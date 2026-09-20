using Api.Domain;

namespace Api.Application;

public sealed record CreateReservationCommand(Guid AmenityId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>The one place that decides who may cancel a reservation.</summary>
public static class ReservationAuthorization
{
    /// <summary>The owner, or staff.</summary>
    public static bool CanCancel(Actor actor, Reservation reservation) => throw new NotImplementedException();
}

/// <summary>
/// Orchestrates a booking: load amenity, then inside the amenity's exclusive section run the
/// rules, check capacity, and insert. Every method is scoped by <c>buildingId</c>.
/// </summary>
public sealed class ReservationService(
    IAmenityCatalog amenities,
    IReservationRepository reservations,
    IEnumerable<IBookingRule> rules,
    TimeProvider clock)
{
    /// <summary>
    /// Failures: amenity_not_found (NotFound), any rule violation (Validation/Conflict), and
    /// slot_full (Conflict) when peak occupancy over the window has already reached capacity.
    /// </summary>
    public Task<Result<Reservation>> CreateAsync(Guid buildingId, Actor actor, CreateReservationCommand command)
        => throw new NotImplementedException();

    /// <summary>
    /// Cancels (soft): status becomes Cancelled so the slot frees up. Returns null on success.
    /// Failures: reservation_not_found (NotFound, including another building's), not_owner (Forbidden).
    /// Cancelling an already-cancelled reservation succeeds (idempotent).
    /// </summary>
    public Task<Failure?> CancelAsync(Guid buildingId, Actor actor, Guid reservationId)
        => throw new NotImplementedException();

    /// <summary>Active reservations of an amenity overlapping [from, to). Failure: amenity_not_found.</summary>
    public Task<Result<IReadOnlyList<Reservation>>> ListAsync(Guid buildingId, Guid amenityId, DateTimeOffset? from, DateTimeOffset? to)
        => throw new NotImplementedException();
}
