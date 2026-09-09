using Api.Domain;
using Api.Store;

namespace Api.Services;

public enum BookingOutcome
{
    Created,
    AmenityNotFound,
    Forbidden,
    Invalid,
    Conflict,
}

public sealed record BookingResult(BookingOutcome Outcome, Reservation? Reservation, string? Error);

/// <summary>
/// Booking rules live here rather than in the endpoints, so they don't depend on the caller
/// having arrived over HTTP.
///
/// Note carefully what this class does *not* do: it never checks capacity. Capacity has to be
/// checked in the same indivisible step as the insert, so that check lives inside
/// <see cref="InMemoryStore.TryCreateReservation"/>. Hoisting it up here for tidier layering
/// would reintroduce exactly the check-then-act race the design exists to prevent — the rules
/// below are all stateless, which is precisely why they are safe to run outside the lock.
/// </summary>
public sealed class ReservationService
{
    private readonly InMemoryStore _store;

    public ReservationService(InMemoryStore store) => _store = store;

    /// <summary>
    /// True when the resident belongs to the building that owns this amenity. Every read and
    /// write path funnels through this one method: the BuildingId column alone prevents nothing,
    /// and a set membership test copy-pasted to four call sites is four chances to get the
    /// security-critical path wrong.
    /// </summary>
    public bool CanAccess(string residentId, Amenity amenity) =>
        _store.ResidentBelongsToBuilding(residentId, amenity.BuildingId);

    public BookingResult Book(
        Guid amenityId,
        string? residentId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        if (string.IsNullOrWhiteSpace(residentId))
        {
            return new BookingResult(BookingOutcome.Invalid, null, "An X-Resident-Id header is required.");
        }

        residentId = residentId.Trim();

        var amenity = _store.FindAmenity(amenityId);
        if (amenity is null)
        {
            return new BookingResult(BookingOutcome.AmenityNotFound, null, "No such amenity.");
        }

        // Authorization before the detailed validation below, so a non-member can't probe
        // another building's booking rules by reading our error messages.
        //
        // Trade-off worth naming: distinguishing 403 from 404 confirms that an amenity ID exists
        // to someone who can't access it. A stricter posture returns 404 for both. Kept distinct
        // here because it makes the isolation behaviour legible in a demo.
        if (!CanAccess(residentId, amenity))
        {
            return new BookingResult(BookingOutcome.Forbidden, null, "You don't have access to this amenity.");
        }

        if (endUtc <= startUtc)
        {
            return new BookingResult(BookingOutcome.Invalid, null, "End time must be after start time.");
        }

        // Comparing two absolute instants, so this needs no timezone knowledge — unlike opening
        // hours, which are local wall-clock times and were deferred for exactly that reason.
        if (startUtc < DateTimeOffset.UtcNow)
        {
            return new BookingResult(BookingOutcome.Invalid, null, "That start time has already passed.");
        }

        if ((endUtc - startUtc).TotalMinutes > amenity.MaxBookingMinutes)
        {
            return new BookingResult(
                BookingOutcome.Invalid,
                null,
                $"{amenity.Name} can be booked for at most {amenity.MaxBookingMinutes} minutes at a time.");
        }

        // Everything above is stateless. The capacity check and the insert happen together,
        // under the store's lock.
        var result = _store.TryCreateReservation(amenity, residentId, startUtc, endUtc);

        return result.Outcome switch
        {
            CreateReservationOutcome.Created =>
                new BookingResult(BookingOutcome.Created, result.Reservation, null),
            _ =>
                new BookingResult(BookingOutcome.Conflict, null, "That time is no longer available."),
        };
    }
}
