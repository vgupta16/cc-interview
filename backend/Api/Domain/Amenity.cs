namespace Api.Domain;

/// <summary>
/// A shared amenity in a building that residents can reserve (gym, party room, guest parking, ...).
/// </summary>
public record Amenity
{
    public required Guid Id { get; init; }
    public required Guid BuildingId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>How many concurrent reservations a single time slot can hold. 1 == exclusive.</summary>
    public required int Capacity { get; init; }

    /// <summary>Upper bound on a single reservation length, in minutes.</summary>
    public required int MaxBookingMinutes { get; init; }
}
