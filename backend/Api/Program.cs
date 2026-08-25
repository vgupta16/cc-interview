using Api.Domain;
using Api.Store;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<InMemoryStore>();

var app = builder.Build();

// OpenAPI is always on so the frontend can regenerate its typed client:
//   openapi json -> http://localhost:5080/openapi/v1.json
app.MapOpenApi();

// --- System ---------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("getHealth")
   .WithTags("System");

// --- Amenities (worked example — read this, then do the same for reservations) ---
app.MapGet("/api/amenities", (InMemoryStore store) => Results.Ok(store.Amenities))
   .WithName("getAmenities")
   .WithTags("Amenities")
   .Produces<IReadOnlyList<Amenity>>();

// --- Reservations -----------------------------------------------------------
app.MapGet("/api/amenities/{amenityId:guid}/reservations", (Guid amenityId, InMemoryStore store) =>
{
    var amenity = store.Amenities.FirstOrDefault(a => a.Id == amenityId);
    if (amenity is null)
    {
        return Results.NotFound(new { error = "amenity_not_found" });
    }

    return Results.Ok(store.GetReservationsForAmenity(amenityId));
})
   .WithName("getAmenityReservations")
   .WithTags("Reservations")
   .Produces<IReadOnlyList<Reservation>>()
   .Produces(StatusCodes.Status404NotFound);

app.MapPost("/api/reservations", (CreateReservationRequest request, InMemoryStore store) =>
{
    var amenity = store.Amenities.FirstOrDefault(a => a.Id == request.AmenityId);
    if (amenity is null)
    {
        return Results.NotFound(new { error = "amenity_not_found", message = "Amenity not found." });
    }

    if (string.IsNullOrWhiteSpace(request.ResidentId))
    {
        return Results.BadRequest(new { error = "resident_id_required", message = "Resident id is required." });
    }

    if (request.StartUtc >= request.EndUtc)
    {
        return Results.BadRequest(new { error = "start_must_be_before_end", message = "Start time must be before end time." });
    }

    if (request.StartUtc < DateTimeOffset.UtcNow)
    {
        return Results.BadRequest(new { error = "slot_in_the_past", message = "That time slot has already passed." });
    }

    var durationMinutes = (request.EndUtc - request.StartUtc).TotalMinutes;
    if (durationMinutes > amenity.MaxBookingMinutes)
    {
        return Results.BadRequest(new
        {
            error = "exceeds_max_booking_length",
            message = $"Bookings for this amenity can't exceed {amenity.MaxBookingMinutes} minutes.",
            maxBookingMinutes = amenity.MaxBookingMinutes,
        });
    }

    var result = store.TryCreateReservation(amenity, request.ResidentId, request.StartUtc, request.EndUtc);
    return result.Error switch
    {
        CreateReservationError.None => Results.Created($"/api/reservations/{result.Reservation!.Id}", result.Reservation),
        CreateReservationError.SlotFull => Results.Conflict(new
        {
            error = "slot_full",
            message = "This amenity is fully booked for the requested time.",
        }),
        CreateReservationError.ResidentAlreadyBooked => Results.Conflict(new
        {
            error = "resident_already_booked",
            message = "You already have an active booking that overlaps this time for this amenity.",
        }),
        _ => Results.Problem("Unexpected error creating reservation."),
    };
})
   .WithName("createReservation")
   .WithTags("Reservations")
   .Produces<Reservation>(StatusCodes.Status201Created)
   .Produces(StatusCodes.Status400BadRequest)
   .Produces(StatusCodes.Status404NotFound)
   .Produces(StatusCodes.Status409Conflict);

app.MapDelete("/api/reservations/{id:guid}", (Guid id, string residentId, InMemoryStore store) =>
{
    var result = store.CancelReservation(id, residentId);
    return result switch
    {
        CancelReservationResult.Cancelled => Results.NoContent(),
        CancelReservationResult.NotFound => Results.NotFound(),
        CancelReservationResult.Forbidden => Results.Json(new { error = "not_owner" }, statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Problem(),
    };
})
   .WithName("cancelReservation")
   .WithTags("Reservations")
   .Produces(StatusCodes.Status204NoContent)
   .Produces(StatusCodes.Status403Forbidden)
   .Produces(StatusCodes.Status404NotFound);

app.Run();
