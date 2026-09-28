namespace Api.Contracts;

/// <summary>
/// One candidate window in an availability response. Other residents' reservations appear only
/// as reduced capacity, never as IDs (A5).
/// </summary>
public record AvailabilityWindow
{
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>Exclusive end: a window ending at 10:00 doesn't occupy 10:00.</summary>
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>The fewest free spots across the window's 30-minute slots; 0 means full (A18).</summary>
    public required int RemainingCapacity { get; init; }
}
