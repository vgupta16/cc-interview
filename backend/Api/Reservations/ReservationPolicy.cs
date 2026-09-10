namespace Api.Reservations;

/// <summary>
/// Tunables that are product decisions rather than domain facts. Gathered here so the numbers a
/// PM would want to change are in one place instead of scattered through the service.
/// </summary>
public static class ReservationPolicy
{
    /// <summary>How far ahead a resident may book. Open PM question; 30 days is the placeholder.</summary>
    public static readonly TimeSpan BookingHorizon = TimeSpan.FromDays(30);

    /// <summary>
    /// Tolerance on the "no bookings in the past" rule. Without it, a client whose clock is a few
    /// seconds fast gets a confusing rejection when booking the current slot.
    /// </summary>
    public static readonly TimeSpan ClockSkewGrace = TimeSpan.FromMinutes(1);

    /// <summary>Window returned by the schedule endpoint when the caller doesn't specify one.</summary>
    public static readonly TimeSpan DefaultScheduleWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Ceiling on a requested schedule window, so one request can't ask for a decade of data.
    /// Oversized requests are clamped rather than rejected — the caller still gets useful data.
    /// </summary>
    public static readonly TimeSpan MaxScheduleWindow = TimeSpan.FromDays(31);
}
