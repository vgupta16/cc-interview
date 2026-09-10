namespace Api.Domain;

/// <summary>
/// A half-open interval of time, <c>[StartUtc, EndUtc)</c>. The end instant is excluded, so
/// back-to-back windows (4–6 and 6–8) do not overlap. Every availability calculation goes through
/// this type so the convention is applied in exactly one place.
/// </summary>
public readonly record struct TimeWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public TimeSpan Duration => EndUtc - StartUtc;

    /// <summary>False for inverted and zero-length windows, both of which are rejected.</summary>
    public bool IsWellFormed => StartUtc < EndUtc;

    /// <summary>
    /// Half-open overlap: true when the two windows share at least one instant. Touching
    /// endpoints do not count.
    /// </summary>
    public bool Overlaps(TimeWindow other) => StartUtc < other.EndUtc && other.StartUtc < EndUtc;

    public bool Contains(DateTimeOffset instant) => StartUtc <= instant && instant < EndUtc;

    /// <summary>
    /// Re-expresses both endpoints in UTC. Absolute-time comparisons are already offset-correct,
    /// but normalizing keeps stored values and serialized output unambiguous.
    /// </summary>
    public TimeWindow ToUtc() => new(StartUtc.ToUniversalTime(), EndUtc.ToUniversalTime());
}
