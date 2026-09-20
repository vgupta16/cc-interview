using Api.Domain;

namespace Api.Application;

/// <summary>
/// The capacity invariant, kept as a pure function so it can be tested in isolation and can't be
/// skipped by mis-registering a rule.
/// </summary>
public static class Occupancy
{
    /// <summary>Half-open interval overlap: [aStart, aEnd) and [bStart, bEnd). Back-to-back doesn't overlap.</summary>
    public static bool Overlaps(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd)
        => aStart < bEnd && bStart < aEnd;

    /// <summary>
    /// The highest number of <paramref name="reservations"/> that are simultaneously active at any
    /// instant within [startUtc, endUtc). Only reservations whose status holds capacity count.
    /// This is the peak occupancy, not the count of overlapping bookings: with 9-10 and 10-11
    /// booked, the peak over 9-11 is 1 even though 2 bookings overlap the window.
    /// A new booking fits when this peak is strictly less than the amenity's capacity.
    /// </summary>
    public static int MaxConcurrent(IEnumerable<Reservation> reservations, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        // Sweep line over the reservations clipped to the window. At equal timestamps an end
        // sorts before a start (delta -1 < +1), which is what makes the intervals half-open.
        var events = new List<(DateTimeOffset At, int Delta)>();
        foreach (var r in reservations)
        {
            if (!r.Status.HoldsCapacity() || !Overlaps(r.StartUtc, r.EndUtc, startUtc, endUtc)) continue;
            events.Add((r.StartUtc > startUtc ? r.StartUtc : startUtc, +1));
            events.Add((r.EndUtc < endUtc ? r.EndUtc : endUtc, -1));
        }

        int current = 0, peak = 0;
        foreach (var (_, delta) in events.OrderBy(e => e.At).ThenBy(e => e.Delta))
        {
            current += delta;
            peak = Math.Max(peak, current);
        }
        return peak;
    }
}
