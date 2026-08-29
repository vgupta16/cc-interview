namespace Api.Domain;

/// <summary>
/// A resident's booking of an amenity for one hour-aligned slot.
///
/// Every reservation is exactly one hour long and starts on the hour. That is the single
/// simplifying decision the whole design rests on: two reservations either occupy the same
/// slot or they don't, so there is no interval-overlap math anywhere in the codebase.
/// </summary>
public record Reservation
{
    public required Guid Id { get; init; }
    public required Guid AmenityId { get; init; }
    public required Guid ResidentId { get; init; }

    /// <summary>Always on the hour, UTC. Validated on create.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>How many people this booking claims, counted against Amenity.TotalCapacity.</summary>
    public required int Size { get; init; }

    /// <summary>Computed, not stored — the one-hour invariant can't be violated by a bad write.</summary>
    public DateTimeOffset EndUtc => StartUtc.AddHours(1);
}
