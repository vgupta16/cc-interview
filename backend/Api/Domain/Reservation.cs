namespace Api.Domain;

/// <summary>
/// A resident's booking of an amenity for a time window.
/// </summary>
public record Reservation
{
    public required Guid Id { get; init; }

    /// <summary>Identifies the building to which this reservation belongs.</summary>
    public required Guid BuildingId { get; init; }

    /// <summary>Identifies the amenity that was reserved.</summary>
    public required Guid AmenityId { get; init; }

    /// <summary>Identifies the resident who made this reservation.</summary>
    public required Guid ResidentId { get; init; }

    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
}
