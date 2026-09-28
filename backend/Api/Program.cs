using Api.Contracts;
using Api.Domain;
using Api.Store;
using Microsoft.AspNetCore.Http.HttpResults;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<InMemoryStore>();

// Development-only: framework failures (e.g., failed bindings, unhandled exceptions) return ProblemDetails
// JSON like the application's own errors (instead of text/plain stack traces), so the generated client resolves
// rather than rejects. In Production, they return an empty body, which it also resolves. UseExceptionHandler()
// is deliberately absent because it turns binding 400s into 500s.
builder.Services.AddProblemDetails();

var app = builder.Build();

// OpenAPI is always on so the frontend can regenerate its typed client:
//   openapi json -> http://localhost:5080/openapi/v1.json
app.MapOpenApi();

// --- System ---------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("getHealth")
    .WithTags("System");

// Every endpoint below resolves the requesting resident from X-RESIDENT-ID first (401), then
// scopes all data to that resident's building. For requests that pass framework routing and
// binding, checks run in this order: resident (401), amenity in the resident's building (404),
// request validation (400), and capacity (409).

// --- Amenities ------------------------------------------------------------
app.MapGet(
        "/api/amenities",
        Results<Ok<IReadOnlyList<Amenity>>, ProblemHttpResult> (
            HttpRequest request,
            InMemoryStore store
        ) =>
        {
            var resident = RequestRules.FindRequestingResident(request, store);

            if (resident is null)
            {
                return UnknownResident();
            }

            return TypedResults.Ok(store.GetAmenities(resident.BuildingId));
        }
    )
    .WithName("getAmenities")
    .WithTags("Amenities")
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.MapGet(
        "/api/amenities/{amenityId:guid}/availability",
        Results<Ok<List<AvailabilityWindow>>, ValidationProblem, ProblemHttpResult> (
            Guid amenityId,
            string? startUtc,
            int? durationMinutes,
            HttpRequest request,
            InMemoryStore store
        ) =>
        {
            var resident = RequestRules.FindRequestingResident(request, store);

            if (resident is null)
            {
                return UnknownResident();
            }

            var amenity = store.FindAmenity(resident.BuildingId, amenityId);

            if (amenity is null)
            {
                return AmenityNotFound();
            }

            // one clock reading for validation and the 90-day cutoff, so the selected window is always kept
            var now = DateTimeOffset.UtcNow;

            var errors = RequestRules.ValidateWindow(
                startUtc,
                durationMinutes,
                amenity,
                now,
                out var start,
                out var duration
            );

            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors);
            }

            // the selected window plus up to four more, all from one snapshot; full windows stay in (A17, A18).
            // stop at the first window that starts past the 90-day limit, so availability excludes every window
            // the 90-day check would reject; creation measures its limit later, so it's never earlier (A15).
            var latestStart = RequestRules.LatestStart(now);
            var reservations = store.SnapshotReservations(resident.BuildingId, amenity.Id);

            var windows = Enumerable
                .Range(0, RequestRules.AvailabilityWindowCount)
                .Select(i => start + i * InMemoryStore.SlotLength)
                .TakeWhile(windowStart => windowStart <= latestStart)
                .Select(windowStart => new AvailabilityWindow
                {
                    StartUtc = windowStart,
                    EndUtc = windowStart + duration,
                    RemainingCapacity = Math.Max(
                        0,
                        InMemoryStore.RemainingCapacity(
                            amenity,
                            reservations,
                            windowStart,
                            windowStart + duration
                        )
                    ),
                })
                .ToList();

            return TypedResults.Ok(windows);
        }
    )
    .WithName("getAmenityAvailability")
    .WithTags("Amenities")
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound);

// --- Reservations ---------------------------------------------------------
app.MapPost(
        "/api/reservations",
        Results<Created<Reservation>, ValidationProblem, ProblemHttpResult> (
            CreateReservationRequest body,
            HttpRequest request,
            InMemoryStore store
        ) =>
        {
            var resident = RequestRules.FindRequestingResident(request, store);

            if (resident is null)
            {
                return UnknownResident();
            }

            var amenity = store.FindAmenity(resident.BuildingId, body.AmenityId);

            if (amenity is null)
            {
                return AmenityNotFound();
            }

            var errors = RequestRules.ValidateWindow(
                body.StartUtc,
                body.DurationMinutes,
                amenity,
                DateTimeOffset.UtcNow,
                out var start,
                out var duration
            );

            if (errors.Count > 0)
            {
                return TypedResults.ValidationProblem(errors);
            }

            // capacity is checked and the reservation inserted in one locked step
            var result = store.TryCreate(resident, amenity, start, start + duration);

            return result.Outcome switch
            {
                CreateOutcome.Created => TypedResults.Created((string?)null, result.Reservation!),
                CreateOutcome.StartPassed => TypedResults.ValidationProblem(
                    new Dictionary<string, string[]> { ["startUtc"] = ["Start in the future."] }
                ),
                _ => ReservationConflict(),
            };
        }
    )
    .WithName("createReservation")
    .WithTags("Reservations")
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);

app.MapGet(
        "/api/reservations/mine",
        Results<Ok<IReadOnlyList<Reservation>>, ProblemHttpResult> (
            HttpRequest request,
            InMemoryStore store
        ) =>
        {
            var resident = RequestRules.FindRequestingResident(request, store);

            if (resident is null)
            {
                return UnknownResident();
            }

            return TypedResults.Ok(store.GetMyReservations(resident.BuildingId, resident.Id));
        }
    )
    .WithName("getMyReservations")
    .WithTags("Reservations")
    .ProducesProblem(StatusCodes.Status401Unauthorized);

app.Run();

static ProblemHttpResult UnknownResident() =>
    TypedResults.Problem(
        statusCode: StatusCodes.Status401Unauthorized,
        title: "Unknown resident",
        detail: $"Send a known resident ID in the {RequestRules.ResidentHeader} header."
    );

static ProblemHttpResult AmenityNotFound() =>
    TypedResults.Problem(
        statusCode: StatusCodes.Status404NotFound,
        title: "Amenity not found",
        detail: "No amenity with this ID exists in your building."
    );

static ProblemHttpResult ReservationConflict() =>
    TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Reservation conflict",
        detail: "The requested window no longer has enough capacity. Refresh availability and choose another window."
    );
