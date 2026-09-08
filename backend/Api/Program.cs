using Api.Contracts;
using Api.Domain;
using Api.Services;
using Api.Store;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<InMemoryStore>();
builder.Services.AddSingleton<ReservationService>();

var app = builder.Build();

// OpenAPI is always on so the frontend can regenerate its typed client:
//   openapi json -> http://localhost:5080/openapi/v1.json
app.MapOpenApi();

// Identity is faked for this exercise: the caller asserts who they are via X-Resident-Id.
// Under real auth this becomes a claim on the authenticated principal and nothing else changes
// about the shape of these endpoints — which is why it's a header rather than a body field.
const string ResidentHeader = "X-Resident-Id";

// --- System ---------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("getHealth")
   .WithTags("System");

// --- Buildings ------------------------------------------------------------
// Note: the scaffold shipped an unscoped GET /api/amenities that returned every amenity in the
// system regardless of who asked. Once amenities belong to buildings that is a cross-building
// leak, so it has been replaced by the building-scoped route below.

app.MapGet("/api/buildings", (
    [FromHeader(Name = ResidentHeader)] string? residentId,
    InMemoryStore store) =>
{
    if (string.IsNullOrWhiteSpace(residentId))
    {
        return Results.BadRequest(new { error = $"An {ResidentHeader} header is required." });
    }

    return Results.Ok(store.GetBuildingsForResident(residentId.Trim()));
})
   .WithName("getBuildings")
   .WithTags("Buildings")
   .Produces<IReadOnlyList<Building>>();

app.MapGet("/api/buildings/{buildingId:guid}/amenities", (
    Guid buildingId,
    [FromHeader(Name = ResidentHeader)] string? residentId,
    InMemoryStore store) =>
{
    if (string.IsNullOrWhiteSpace(residentId))
    {
        return Results.BadRequest(new { error = $"An {ResidentHeader} header is required." });
    }

    if (!store.ResidentBelongsToBuilding(residentId.Trim(), buildingId))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Ok(store.GetAmenitiesForBuilding(buildingId));
})
   .WithName("getBuildingAmenities")
   .WithTags("Amenities")
   .Produces<IReadOnlyList<Amenity>>();

// --- Reservations ---------------------------------------------------------

app.MapGet("/api/amenities/{amenityId:guid}/reservations", (
    Guid amenityId,
    [FromHeader(Name = ResidentHeader)] string? residentId,
    InMemoryStore store,
    ReservationService reservations) =>
{
    if (string.IsNullOrWhiteSpace(residentId))
    {
        return Results.BadRequest(new { error = $"An {ResidentHeader} header is required." });
    }

    var amenity = store.FindAmenity(amenityId);
    if (amenity is null)
    {
        return Results.NotFound(new { error = "No such amenity." });
    }

    if (!reservations.CanAccess(residentId.Trim(), amenity))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Ok(store.GetActiveReservations(amenityId));
})
   .WithName("getAmenityReservations")
   .WithTags("Reservations")
   .Produces<IReadOnlyList<Reservation>>();

app.MapGet("/api/reservations/{reservationId:guid}", (
    Guid reservationId,
    [FromHeader(Name = ResidentHeader)] string? residentId,
    InMemoryStore store,
    ReservationService reservations) =>
{
    if (string.IsNullOrWhiteSpace(residentId))
    {
        return Results.BadRequest(new { error = $"An {ResidentHeader} header is required." });
    }

    var reservation = store.FindReservation(reservationId);
    if (reservation is null)
    {
        return Results.NotFound(new { error = "No such reservation." });
    }

    var amenity = store.FindAmenity(reservation.AmenityId);
    if (amenity is null || !reservations.CanAccess(residentId.Trim(), amenity))
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    return Results.Ok(reservation);
})
   .WithName("getReservation")
   .WithTags("Reservations")
   .Produces<Reservation>();

app.MapPost("/api/amenities/{amenityId:guid}/reservations", (
    Guid amenityId,
    [FromHeader(Name = ResidentHeader)] string? residentId,
    CreateReservationRequest request,
    ReservationService reservations) =>
{
    var result = reservations.Book(amenityId, residentId, request.StartUtc, request.EndUtc);

    return result.Outcome switch
    {
        BookingOutcome.Created =>
            Results.Created($"/api/reservations/{result.Reservation!.Id}", result.Reservation),

        BookingOutcome.AmenityNotFound =>
            Results.NotFound(new { error = result.Error }),

        BookingOutcome.Forbidden =>
            Results.StatusCode(StatusCodes.Status403Forbidden),

        // 409 rather than 400: the request is well-formed and would have been valid a moment
        // ago. It conflicts with the current state of the resource, which is what 409 means.
        BookingOutcome.Conflict =>
            Results.Conflict(new { error = result.Error }),

        _ =>
            Results.BadRequest(new { error = result.Error }),
    };
})
   .WithName("createReservation")
   .WithTags("Reservations")
   .Produces<Reservation>(StatusCodes.Status201Created);

app.MapDelete("/api/reservations/{reservationId:guid}", (
    Guid reservationId,
    [FromHeader(Name = ResidentHeader)] string? residentId,
    InMemoryStore store) =>
{
    if (string.IsNullOrWhiteSpace(residentId))
    {
        return Results.BadRequest(new { error = $"An {ResidentHeader} header is required." });
    }

    // Results.Forbid() is deliberately avoided throughout: it expects a configured
    // authentication scheme to challenge against, and this scaffold has none.
    return store.CancelReservation(reservationId, residentId.Trim()) switch
    {
        CancelReservationOutcome.Cancelled => Results.NoContent(),
        CancelReservationOutcome.NotOwner => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.NotFound(new { error = "No such active reservation." }),
    };
})
   .WithName("cancelReservation")
   .WithTags("Reservations");

app.Run();

// Exposed so the test project can reference the app's types.
public partial class Program;
