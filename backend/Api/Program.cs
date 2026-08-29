using Api.Contracts;
using Api.Domain;
using Api.Store;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<InMemoryStore>();

var app = builder.Build();

// OpenAPI is always on so the frontend can regenerate its typed client:
//   openapi json -> http://localhost:5080/openapi/v1.json
app.MapOpenApi();

// Identity is a `residentId` passed by the caller. This is a deliberate stand-in for auth so
// the slice stays small — in production it would be a token claim and would never be
// client-supplied. Every handler still resolves it to a Resident and derives BuildingId from
// there, so swapping the source of the id is a one-line change.
static Resident? Caller(InMemoryStore store, Guid residentId) =>
    store.Residents.FirstOrDefault(r => r.Id == residentId);

// --- System ---------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("getHealth")
   .WithTags("System");

// --- Residents ------------------------------------------------------------
// Powers the resident picker in the UI — the "log in as" stand-in. Unauthenticated on purpose.
app.MapGet("/api/residents", (InMemoryStore store) => Results.Ok(store.Residents))
   .WithName("getResidents")
   .WithTags("Residents")
   .Produces<IReadOnlyList<Resident>>();

// --- Amenities ------------------------------------------------------------
// Tenancy: scoped to the caller's building, never to a client-supplied building id.
app.MapGet("/api/amenities", (Guid residentId, InMemoryStore store) =>
{
    var resident = Caller(store, residentId);
    if (resident is null)
        return Results.Problem("Unknown resident.", statusCode: StatusCodes.Status401Unauthorized);

    return Results.Ok(store.Amenities.Where(a => a.BuildingId == resident.BuildingId).ToList());
})
   .WithName("getAmenities")
   .WithTags("Amenities")
   .Produces<IReadOnlyList<Amenity>>()
   .ProducesProblem(StatusCodes.Status401Unauthorized);

// One day of hour-long slots, with the room left in each. The UI renders this directly.
app.MapGet("/api/amenities/{amenityId:guid}/availability",
    (Guid amenityId, Guid residentId, DateOnly? date, InMemoryStore store) =>
{
    var resident = Caller(store, residentId);
    if (resident is null)
        return Results.Problem("Unknown resident.", statusCode: StatusCodes.Status401Unauthorized);

    // Cross-building access is a 404, not a 403: a resident of building A learns nothing about
    // whether an amenity in building B exists.
    var amenity = store.Amenities
        .FirstOrDefault(a => a.Id == amenityId && a.BuildingId == resident.BuildingId);
    if (amenity is null)
        return Results.Problem("Amenity not found.", statusCode: StatusCodes.Status404NotFound);

    var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var now = DateTimeOffset.UtcNow;

    List<Reservation> booked;
    lock (store.SyncRoot)
        booked = store.Reservations.Where(r => r.AmenityId == amenity.Id).ToList();

    var slots = new List<SlotAvailability>();
    for (var hour = amenity.OpeningHourUtc; hour < amenity.ClosingHourUtc; hour++)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue).AddHours(hour), TimeSpan.Zero);
        var inSlot = booked.Where(r => r.StartUtc == start).ToList();

        // Why not, in the words of the same helper the create path enforces with — so the row
        // can say "already started" rather than showing free capacity next to a dead button.
        var reason = start < now
            ? "Already started"
            : Capacity.Reject(amenity, inSlot, 1)?.Message;

        slots.Add(new SlotAvailability
        {
            StartUtc = start,
            EndUtc = start.AddHours(1),
            RemainingSize = Capacity.RemainingSize(amenity, inSlot),
            RemainingReservations = Capacity.RemainingReservations(amenity, inSlot),
            IsAvailable = reason is null,
            UnavailableReason = reason,
            MyReservationId = inSlot.FirstOrDefault(r => r.ResidentId == resident.Id)?.Id,
        });
    }

    return Results.Ok(slots);
})
   .WithName("getAvailability")
   .WithTags("Amenities")
   .Produces<IReadOnlyList<SlotAvailability>>()
   .ProducesProblem(StatusCodes.Status404NotFound);

// --- Reservations ---------------------------------------------------------
app.MapPost("/api/reservations", (CreateReservationRequest request, InMemoryStore store) =>
{
    var resident = Caller(store, request.ResidentId);
    if (resident is null)
        return Results.Problem("Unknown resident.", statusCode: StatusCodes.Status401Unauthorized);

    var amenity = store.Amenities
        .FirstOrDefault(a => a.Id == request.AmenityId && a.BuildingId == resident.BuildingId);
    if (amenity is null)
        return Results.Problem("Amenity not found.", statusCode: StatusCodes.Status404NotFound);

    // Slot shape rules. None of these depend on other reservations, so they need no lock.
    var start = request.StartUtc.ToUniversalTime();
    if (start.UtcDateTime.TimeOfDay.Ticks % TimeSpan.TicksPerHour != 0)
        return Results.Problem("Reservations start on the hour.", statusCode: StatusCodes.Status400BadRequest);
    if (start < DateTimeOffset.UtcNow)
        return Results.Problem("That slot has already started.", statusCode: StatusCodes.Status400BadRequest);
    if (start.Hour < amenity.OpeningHourUtc || start.Hour >= amenity.ClosingHourUtc)
        return Results.Problem(
            $"{amenity.Name} is open {amenity.OpeningHourUtc:00}:00–{amenity.ClosingHourUtc:00}:00 UTC.",
            statusCode: StatusCodes.Status400BadRequest);

    // THE CRUX. Reading the slot, deciding on capacity, and inserting are one atomic step.
    // Split them and two concurrent requests both read "one space left", both pass the check,
    // and both insert — the classic check-then-insert race. See RevisedDesign.md.
    lock (store.SyncRoot)
    {
        var inSlot = store.Reservations
            .Where(r => r.AmenityId == amenity.Id && r.StartUtc == start)
            .ToList();

        // One booking per resident per slot. Not a capacity rule — a resident holding two
        // bookings for the same hour is nonsense on its own terms, and SlotAvailability
        // surfaces only the first via MyReservationId, so the extras would be invisible and
        // uncancellable. Checked inside the lock for the same reason capacity is: two
        // concurrent requests would otherwise both find nothing and both insert.
        if (inSlot.Any(r => r.ResidentId == resident.Id))
            return Results.Problem(
                "You already have a booking for this slot.",
                statusCode: StatusCodes.Status409Conflict);

        if (Capacity.Reject(amenity, inSlot, request.Size) is (int status, string message))
            return Results.Problem(message, statusCode: status);

        var reservation = new Reservation
        {
            Id = Guid.NewGuid(),
            AmenityId = amenity.Id,
            ResidentId = resident.Id,
            StartUtc = start,
            Size = request.Size,
        };
        store.Reservations.Add(reservation);

        return Results.Created($"/api/reservations/{reservation.Id}", reservation);
    }
})
   .WithName("createReservation")
   .WithTags("Reservations")
   .Produces<Reservation>(StatusCodes.Status201Created)
   .ProducesProblem(StatusCodes.Status400BadRequest)
   .ProducesProblem(StatusCodes.Status404NotFound)
   .ProducesProblem(StatusCodes.Status409Conflict);

app.MapDelete("/api/reservations/{id:guid}", (Guid id, Guid residentId, InMemoryStore store) =>
{
    lock (store.SyncRoot)
    {
        // Ownership is part of the lookup. Someone else's reservation is "not found" rather
        // than "forbidden" — a 403 would confirm the booking exists.
        var reservation = store.Reservations
            .FirstOrDefault(r => r.Id == id && r.ResidentId == residentId);
        if (reservation is null)
            return Results.Problem("Reservation not found.", statusCode: StatusCodes.Status404NotFound);

        store.Reservations.Remove(reservation);
        return Results.NoContent();
    }
})
   .WithName("cancelReservation")
   .WithTags("Reservations")
   .Produces(StatusCodes.Status204NoContent)
   .ProducesProblem(StatusCodes.Status404NotFound);

app.Run();
