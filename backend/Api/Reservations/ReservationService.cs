using Api.Domain;
using Api.Store;

namespace Api.Reservations;

/// <summary>
/// All reservation behaviour. Lives outside <c>Program.cs</c> so it can be unit-tested without a
/// web host, and takes a <see cref="TimeProvider"/> rather than reading the clock directly so
/// "starts in the past" and "beyond the horizon" are testable.
///
/// <para>
/// Every operation takes the calling <see cref="Resident"/>, and the caller's building — never a
/// value from the request — is what scopes the operation. That is what makes tenant isolation
/// fail closed: there is no code path that can forget the filter, because the filter's input
/// arrives with the caller.
/// </para>
/// </summary>
public sealed class ReservationService(InMemoryStore store, TimeProvider clock)
{
    /// <summary>
    /// Books <paramref name="requestedWindow"/> for <paramref name="caller"/>, or explains why not.
    /// </summary>
    public ReservationResult<Reservation> Create(Resident caller, Guid amenityId, TimeWindow requestedWindow)
    {
        var amenity = FindVisibleAmenity(caller, amenityId);
        if (amenity is null)
        {
            return ReservationResult<Reservation>.Fail(
                ReservationErrorCode.AmenityNotFound, "No such amenity in your building.");
        }

        var window = requestedWindow.ToUtc();
        var now = clock.GetUtcNow();

        // Stateless rules are checked before taking the amenity's gate, so the critical section
        // holds only the work that genuinely has to be atomic.
        if (!window.IsWellFormed)
        {
            return ReservationResult<Reservation>.Fail(
                ReservationErrorCode.InvalidInterval, "The booking must end after it starts.");
        }

        var maxLength = TimeSpan.FromMinutes(amenity.MaxBookingMinutes);
        if (window.Duration > maxLength)
        {
            return ReservationResult<Reservation>.Fail(
                ReservationErrorCode.ExceedsMaxBookingLength,
                $"{amenity.Name} allows bookings of at most {amenity.MaxBookingMinutes} minutes.");
        }

        if (window.StartUtc < now - ReservationPolicy.ClockSkewGrace)
        {
            return ReservationResult<Reservation>.Fail(
                ReservationErrorCode.StartsInPast, "Bookings cannot start in the past.");
        }

        if (window.StartUtc > now + ReservationPolicy.BookingHorizon)
        {
            return ReservationResult<Reservation>.Fail(
                ReservationErrorCode.BeyondBookingHorizon,
                $"Bookings can be made up to {ReservationPolicy.BookingHorizon.TotalDays:0} days ahead.");
        }

        // The atomic step. Checking capacity and inserting must happen without releasing the
        // gate: two requests that each checked first and inserted afterwards would both see a
        // free slot and both succeed. See InMemoryStore for the full description.
        return store.WithAmenityLock(amenity.Id, slot =>
        {
            if (!Availability.FitsCapacity(slot.Active, window, amenity.Capacity))
            {
                var detail = amenity.Capacity == 1
                    ? $"{amenity.Name} is already booked during that time."
                    : $"{amenity.Name} is fully booked ({amenity.Capacity} at once) during that time.";

                return ReservationResult<Reservation>.Fail(new ReservationError(
                    ReservationErrorCode.SlotUnavailable,
                    detail,
                    Availability.ConflictingWindows(slot.Active, window)));
            }

            var reservation = new Reservation
            {
                Id = Guid.NewGuid(),
                BuildingId = amenity.BuildingId,
                AmenityId = amenity.Id,
                ResidentId = caller.Id,
                StartUtc = window.StartUtc,
                EndUtc = window.EndUtc,
                Status = ReservationStatus.Active,
                CreatedAtUtc = now,
            };

            slot.Add(reservation);
            return ReservationResult<Reservation>.Ok(reservation);
        });
    }

    /// <summary>
    /// Cancels one of the caller's own reservations. Idempotent: cancelling an already-cancelled
    /// booking succeeds and returns it unchanged, so a double-clicked button isn't an error.
    /// </summary>
    public ReservationResult<Reservation> Cancel(Resident caller, Guid reservationId)
    {
        var notFound = ReservationResult<Reservation>.Fail(
            ReservationErrorCode.ReservationNotFound, "No such reservation.");

        return store.WithReservationLock(reservationId, slot =>
        {
            var existing = slot.Find(reservationId);

            // Another resident's booking is reported exactly like a nonexistent one. Saying
            // "forbidden" would confirm the id is real and tell the caller it belongs elsewhere.
            if (existing is null || existing.ResidentId != caller.Id || existing.BuildingId != caller.BuildingId)
            {
                return notFound;
            }

            if (!existing.IsActive)
            {
                return ReservationResult<Reservation>.Ok(existing);
            }

            var canceled = slot.Update(reservationId, r => r with
            {
                Status = ReservationStatus.Canceled,
                CanceledAtUtc = clock.GetUtcNow(),
            });

            return ReservationResult<Reservation>.Ok(canceled!);
        }, notFound);
    }

    /// <summary>
    /// An amenity's active bookings over a window. <paramref name="fromUtc"/> defaults to now and
    /// <paramref name="toUtc"/> to a week later; oversized ranges are clamped rather than refused.
    /// </summary>
    public ReservationResult<AmenitySchedule> GetSchedule(
        Resident caller,
        Guid amenityId,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null)
    {
        var amenity = FindVisibleAmenity(caller, amenityId);
        if (amenity is null)
        {
            return ReservationResult<AmenitySchedule>.Fail(
                ReservationErrorCode.AmenityNotFound, "No such amenity in your building.");
        }

        var from = (fromUtc ?? clock.GetUtcNow()).ToUniversalTime();
        var to = (toUtc ?? from + ReservationPolicy.DefaultScheduleWindow).ToUniversalTime();

        if (to <= from)
        {
            return ReservationResult<AmenitySchedule>.Fail(
                ReservationErrorCode.InvalidInterval, "The requested range must end after it starts.");
        }

        if (to - from > ReservationPolicy.MaxScheduleWindow)
        {
            to = from + ReservationPolicy.MaxScheduleWindow;
        }

        var window = new TimeWindow(from, to);
        var active = store.SnapshotForAmenity(amenity.Id)
            .Where(r => r.IsActive && r.Window.Overlaps(window))
            .OrderBy(r => r.StartUtc)
            .ToList();

        return ReservationResult<AmenitySchedule>.Ok(new AmenitySchedule(amenity, window, active));
    }

    /// <summary>The caller's own active bookings across every amenity in their building.</summary>
    public IReadOnlyList<Reservation> ActiveReservationsFor(Resident caller) =>
        store.SnapshotForResident(caller.Id)
            .Where(r => r.IsActive && r.BuildingId == caller.BuildingId)
            .OrderBy(r => r.StartUtc)
            .ToList();

    /// <summary>
    /// Resolves an amenity only if it belongs to the caller's building. Returning null for the
    /// cross-tenant case (rather than the amenity) is what keeps every caller of this method
    /// tenant-safe by default.
    /// </summary>
    private Amenity? FindVisibleAmenity(Resident caller, Guid amenityId)
    {
        var amenity = store.FindAmenity(amenityId);
        return amenity is not null && amenity.BuildingId == caller.BuildingId ? amenity : null;
    }
}
