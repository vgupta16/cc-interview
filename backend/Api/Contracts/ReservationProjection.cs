using Api.Domain;
using Api.Reservations;

namespace Api.Contracts;

/// <summary>
/// The one place domain reservations become responses.
///
/// <para>
/// Domain <see cref="Reservation"/> records carry <c>ResidentId</c> and are never serialized. If
/// an endpoint returned one directly — the way the amenities worked example returns
/// <see cref="Amenity"/> — every caller could read who booked what straight out of the raw
/// response, no matter what the UI chose to display. Routing every reservation response through
/// these functions is what keeps that field inside the process.
/// </para>
/// </summary>
public static class ReservationProjection
{
    public static AmenityScheduleResponse Schedule(AmenitySchedule schedule, Guid callerId) =>
        new(
            AmenityId: schedule.Amenity.Id,
            AmenityName: schedule.Amenity.Name,
            Capacity: schedule.Amenity.Capacity,
            MaxBookingMinutes: schedule.Amenity.MaxBookingMinutes,
            WindowStartUtc: schedule.Window.StartUtc,
            WindowEndUtc: schedule.Window.EndUtc,
            Busy: schedule.Active.Select(Anonymous).ToList(),
            Mine: Mine(schedule.Active, callerId));

    /// <summary>
    /// The caller's own bookings, projected.
    ///
    /// <para>
    /// The filter lives here rather than in the caller on purpose. Handing this an unfiltered
    /// collection is the obvious mistake, and if filtering were the caller's job that mistake
    /// would be a leak; here it is a no-op.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MyReservation> Mine(IEnumerable<Reservation> reservations, Guid callerId) =>
        reservations
            .Where(r => r.ResidentId == callerId)
            .OrderBy(r => r.StartUtc)
            .Select(r => new MyReservation(r.Id, r.AmenityId, r.StartUtc, r.EndUtc, r.Status))
            .ToList();

    /// <summary>
    /// A single reservation the caller owns — the response to creating or cancelling one. Only
    /// ever called with a reservation the service has already confirmed belongs to the caller.
    /// </summary>
    public static MyReservation One(Reservation reservation) =>
        new(reservation.Id, reservation.AmenityId, reservation.StartUtc, reservation.EndUtc, reservation.Status);

    /// <summary>Strips a reservation down to the fact that its window is taken.</summary>
    private static BusyWindow Anonymous(Reservation reservation) =>
        new(reservation.StartUtc, reservation.EndUtc);
}
