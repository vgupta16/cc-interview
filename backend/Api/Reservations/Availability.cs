using Api.Domain;

namespace Api.Reservations;

/// <summary>
/// Pure availability arithmetic. No store, no clock, no HTTP — everything here is a function of
/// its arguments, which is what makes the interesting cases cheap to test exhaustively.
/// </summary>
public static class Availability
{
    /// <summary>
    /// Reservations sharing at least one instant with <paramref name="candidate"/>. Cancelled
    /// bookings are filtered out here rather than being the caller's responsibility, so passing
    /// an unfiltered collection is a no-op instead of a way to double-book a freed slot.
    /// </summary>
    public static IReadOnlyList<Reservation> Overlapping(
        IEnumerable<Reservation> reservations,
        TimeWindow candidate) =>
        reservations.Where(r => r.IsActive && r.Window.Overlaps(candidate))
                    .OrderBy(r => r.StartUtc)
                    .ToList();

    /// <summary>
    /// The largest number of reservations held simultaneously at any instant inside
    /// <paramref name="candidate"/>, counting the candidate itself as one.
    ///
    /// <para>
    /// Counting overlapping reservations is <i>not</i> the same thing and gives wrong answers
    /// above capacity 1. With existing bookings 9–11 and 11–13, a candidate of 10–12 overlaps
    /// both, but peak concurrency is only 2: at 10:30 it is the candidate plus 9–11, and at 11:30
    /// the candidate plus 11–13. A capacity-2 amenity must accept it.
    /// </para>
    /// </summary>
    public static int PeakConcurrency(IEnumerable<Reservation> active, TimeWindow candidate)
    {
        var overlapping = Overlapping(active, candidate);
        if (overlapping.Count == 0)
        {
            return 1;
        }

        // Concurrency over time is a right-continuous step function: it only ever rises at the
        // start of some interval. Its maximum inside the candidate window is therefore attained
        // at the candidate's own start or at the start of one of the overlapping reservations,
        // so sampling those instants is exact — no continuous sweep needed.
        var instants = overlapping
            .Select(r => r.StartUtc)
            .Append(candidate.StartUtc)
            .Where(candidate.Contains)
            .Distinct();

        var peak = 1;
        foreach (var instant in instants)
        {
            // +1 for the candidate, which by construction spans every instant sampled.
            var concurrent = 1 + overlapping.Count(r => r.Window.Contains(instant));
            if (concurrent > peak)
            {
                peak = concurrent;
            }
        }

        return peak;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> can be added without exceeding
    /// <paramref name="capacity"/> at any instant. At capacity 1 this reduces to "overlaps
    /// nothing".
    /// </summary>
    public static bool FitsCapacity(IEnumerable<Reservation> active, TimeWindow candidate, int capacity) =>
        PeakConcurrency(active, candidate) <= capacity;

    /// <summary>
    /// The occupied windows a rejected candidate ran into, for the conflict response. Windows
    /// only — never who holds them.
    /// </summary>
    public static IReadOnlyList<TimeWindow> ConflictingWindows(
        IEnumerable<Reservation> active,
        TimeWindow candidate) =>
        Overlapping(active, candidate).Select(r => r.Window).ToList();
}
