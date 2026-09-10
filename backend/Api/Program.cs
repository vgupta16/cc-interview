using Api.Contracts;
using Api.Domain;
using Api.Http;
using Api.Reservations;
using Api.Store;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InMemoryStore>();
builder.Services.AddSingleton<ReservationService>();

var app = builder.Build();

// OpenAPI is always on so the frontend can regenerate its typed client:
//   openapi json -> http://localhost:5080/openapi/v1.json
app.MapOpenApi();

// Endpoints below are deliberately thin: resolve the caller, delegate to the service, map the
// result onto a status code. All booking behaviour lives in ReservationService so it can be
// tested without a web host.

// --- System ---------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("getHealth")
   .WithTags("System");

// --- Identity (development shim) -------------------------------------------
// There is no auth in this exercise. Callers identify themselves with an X-Resident-Id header,
// and this endpoint exists only so the UI can offer a "you are…" picker. A real deployment would
// take identity from the session and would not expose a roster of residents.
app.MapGet("/api/dev/residents", (InMemoryStore store) =>
    {
        var residents = store.Residents
            .Select(r =>
            {
                var building = store.FindBuilding(r.BuildingId)!;
                return new ResidentSummary(
                    r.Id, r.DisplayName, r.UnitNumber, building.Id, building.Name, building.TimeZoneId);
            })
            .OrderBy(r => r.BuildingName)
            .ThenBy(r => r.DisplayName)
            .ToList();

        return Results.Ok(residents);
    })
   .WithName("getResidents")
   .WithTags("Dev")
   .Produces<IReadOnlyList<ResidentSummary>>();

// --- Amenities --------------------------------------------------------------
// Scoped to the caller's building. The tenant comes from the caller, never from the request, so
// there is no code path that can forget to filter.
app.MapGet("/api/amenities", (
        InMemoryStore store,
        [FromHeader(Name = RequestContext.ResidentHeaderName)] Guid? xResidentId) =>
    {
        if (!store.TryResolveResident(xResidentId, out var caller))
        {
            return ProblemResults.ResidentRequired();
        }

        return Results.Ok(store.AmenitiesInBuilding(caller.BuildingId));
    })
   .WithName("getAmenities")
   .WithTags("Amenities")
   .Produces<IReadOnlyList<Amenity>>()
   .ProducesProblem(StatusCodes.Status401Unauthorized);

// --- Reservations -----------------------------------------------------------

// An amenity's bookings over a window. Other residents' bookings come back as anonymous busy
// windows; only the caller's own carry an id. See Api.Contracts.ReservationProjection.
app.MapGet("/api/amenities/{amenityId:guid}/reservations", (
        Guid amenityId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        InMemoryStore store,
        ReservationService reservations,
        [FromHeader(Name = RequestContext.ResidentHeaderName)] Guid? xResidentId) =>
    {
        if (!store.TryResolveResident(xResidentId, out var caller))
        {
            return ProblemResults.ResidentRequired();
        }

        var result = reservations.GetSchedule(caller, amenityId, fromUtc, toUtc);

        return result.Succeeded
            ? Results.Ok(ReservationProjection.Schedule(result.Value!, caller.Id))
            : ProblemResults.From(result.Error!);
    })
   .WithName("getAmenitySchedule")
   .WithTags("Reservations")
   .Produces<AmenityScheduleResponse>()
   .ProducesProblem(StatusCodes.Status400BadRequest)
   .ProducesProblem(StatusCodes.Status401Unauthorized)
   .ProducesProblem(StatusCodes.Status404NotFound);

// The caller's own active bookings, across every amenity in their building.
app.MapGet("/api/reservations/mine", (
        InMemoryStore store,
        ReservationService reservations,
        [FromHeader(Name = RequestContext.ResidentHeaderName)] Guid? xResidentId) =>
    {
        if (!store.TryResolveResident(xResidentId, out var caller))
        {
            return ProblemResults.ResidentRequired();
        }

        return Results.Ok(ReservationProjection.Mine(reservations.ActiveReservationsFor(caller), caller.Id));
    })
   .WithName("getMyReservations")
   .WithTags("Reservations")
   .Produces<IReadOnlyList<MyReservation>>()
   .ProducesProblem(StatusCodes.Status401Unauthorized);

// Create a booking. Returns 409 with a `slot_unavailable` code when the window would exceed the
// amenity's capacity at any instant.
app.MapPost("/api/reservations", (
        CreateReservationRequest request,
        InMemoryStore store,
        ReservationService reservations,
        [FromHeader(Name = RequestContext.ResidentHeaderName)] Guid? xResidentId) =>
    {
        if (!store.TryResolveResident(xResidentId, out var caller))
        {
            return ProblemResults.ResidentRequired();
        }

        var result = reservations.Create(
            caller, request.AmenityId, new TimeWindow(request.StartUtc, request.EndUtc));

        if (!result.Succeeded)
        {
            return ProblemResults.From(result.Error!);
        }

        var created = result.Value!;
        return Results.Created($"/api/reservations/{created.Id}", ReservationProjection.One(created));
    })
   .WithName("createReservation")
   .WithTags("Reservations")
   .Produces<MyReservation>(StatusCodes.Status201Created)
   .ProducesProblem(StatusCodes.Status400BadRequest)
   .ProducesProblem(StatusCodes.Status401Unauthorized)
   .ProducesProblem(StatusCodes.Status404NotFound)
   .ProducesProblem(StatusCodes.Status409Conflict);

// Cancel is a POST rather than a DELETE because it is a transition to Canceled, not a deletion:
// the record is kept for audit, and the operation is idempotent.
app.MapPost("/api/reservations/{reservationId:guid}/cancel", (
        Guid reservationId,
        InMemoryStore store,
        ReservationService reservations,
        [FromHeader(Name = RequestContext.ResidentHeaderName)] Guid? xResidentId) =>
    {
        if (!store.TryResolveResident(xResidentId, out var caller))
        {
            return ProblemResults.ResidentRequired();
        }

        var result = reservations.Cancel(caller, reservationId);

        return result.Succeeded
            ? Results.Ok(ReservationProjection.One(result.Value!))
            : ProblemResults.From(result.Error!);
    })
   .WithName("cancelReservation")
   .WithTags("Reservations")
   .Produces<MyReservation>()
   .ProducesProblem(StatusCodes.Status401Unauthorized)
   .ProducesProblem(StatusCodes.Status404NotFound);

app.Run();
