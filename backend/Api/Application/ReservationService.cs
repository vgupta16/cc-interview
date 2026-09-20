using Api.Domain;

namespace Api.Application;

public sealed record CreateReservationCommand(Guid AmenityId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>The one place that decides who may cancel a reservation.</summary>
public static class ReservationAuthorization
{
    /// <summary>The owner, or staff.</summary>
    public static bool CanCancel(Actor actor, Reservation reservation) =>
        actor.IsStaff || reservation.ResidentId == actor.ResidentId;
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
    private readonly IReadOnlyList<IBookingRule> _rules = rules.ToList();

    /// <summary>
    /// Failures: amenity_not_found (NotFound), any rule violation (Validation/Conflict), and
    /// slot_full (Conflict) when peak occupancy over the window has already reached capacity.
    /// </summary>
    public async Task<Result<Reservation>> CreateAsync(Guid buildingId, Actor actor, CreateReservationCommand command)
    {
        var amenity = await amenities.FindAsync(buildingId, command.AmenityId);
        if (amenity is null) return Result<Reservation>.Fail(AmenityNotFound());

        // Everything from here to the insert is one atomic step per amenity: without it, two
        // requests could both see a free unit and both book it (check-then-act race).
        return await reservations.ExecuteExclusiveAsync(buildingId, amenity.Id, bookings =>
        {
            var now = clock.GetUtcNow();
            var active = bookings.ActiveReservations;
            var context = new BookingContext(amenity, actor, command.StartUtc, command.EndUtc, now, active);

            foreach (var rule in _rules)
            {
                var violation = rule.Evaluate(context);
                if (violation is not null) return Task.FromResult(Result<Reservation>.Fail(violation));
            }

            // Adding this booking must not push peak occupancy over capacity anywhere in its window.
            if (Occupancy.MaxConcurrent(active, command.StartUtc, command.EndUtc) >= amenity.Capacity)
            {
                return Task.FromResult(Result<Reservation>.Fail(new Failure(
                    ErrorKind.Conflict, ErrorCodes.SlotFull, $"{amenity.Name} is fully booked for that time.")));
            }

            var reservation = new Reservation
            {
                Id = Guid.NewGuid(),
                BuildingId = buildingId,
                AmenityId = amenity.Id,
                ResidentId = actor.ResidentId,
                StartUtc = command.StartUtc,
                EndUtc = command.EndUtc,
                Status = ReservationStatus.Active,
                CreatedUtc = now,
            };
            bookings.Add(reservation);
            return Task.FromResult(Result<Reservation>.Ok(reservation));
        });
    }

    /// <summary>
    /// Cancels (soft): status becomes Cancelled so the slot frees up. Returns null on success.
    /// Failures: reservation_not_found (NotFound, including another building's), not_owner (Forbidden).
    /// Cancelling an already-cancelled reservation succeeds (idempotent).
    /// </summary>
    public async Task<Failure?> CancelAsync(Guid buildingId, Actor actor, Guid reservationId)
    {
        var found = await reservations.FindAsync(buildingId, reservationId);
        if (found is null) return ReservationNotFound();

        return await reservations.ExecuteExclusiveAsync(buildingId, found.AmenityId, bookings =>
        {
            // Re-read inside the exclusive section: the copy above may be stale.
            var current = bookings.Find(reservationId);
            if (current is null) return Task.FromResult<Failure?>(ReservationNotFound());

            if (!ReservationAuthorization.CanCancel(actor, current))
            {
                return Task.FromResult<Failure?>(new Failure(
                    ErrorKind.Forbidden, ErrorCodes.NotOwner, "You can only cancel your own reservations."));
            }

            if (current.Status.HoldsCapacity())
            {
                bookings.Update(current with { Status = ReservationStatus.Cancelled });
            }
            return Task.FromResult<Failure?>(null);
        });
    }

    /// <summary>Active reservations of an amenity overlapping [from, to). Failure: amenity_not_found.</summary>
    public async Task<Result<IReadOnlyList<Reservation>>> ListAsync(
        Guid buildingId, Guid amenityId, DateTimeOffset? from, DateTimeOffset? to)
    {
        var amenity = await amenities.FindAsync(buildingId, amenityId);
        if (amenity is null) return Result<IReadOnlyList<Reservation>>.Fail(AmenityNotFound());

        return Result<IReadOnlyList<Reservation>>.Ok(
            await reservations.ListActiveAsync(buildingId, amenityId, from, to));
    }

    /// <summary>
    /// Every active reservation in the building, across all amenities, overlapping [from, to),
    /// ordered by start time. Cannot fail: the building scope is the only filter.
    /// </summary>
    public Task<IReadOnlyList<Reservation>> ListForBuildingAsync(Guid buildingId, DateTimeOffset? from, DateTimeOffset? to)
        => reservations.ListActiveForBuildingAsync(buildingId, from, to);

    private static Failure AmenityNotFound() =>
        new(ErrorKind.NotFound, ErrorCodes.AmenityNotFound, "That amenity doesn't exist.");

    private static Failure ReservationNotFound() =>
        new(ErrorKind.NotFound, ErrorCodes.ReservationNotFound, "That reservation doesn't exist.");
}
