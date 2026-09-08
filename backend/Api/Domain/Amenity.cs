namespace Api.Domain;

/// <summary>
/// A shared amenity in a building that residents can reserve (gym, party room, guest parking, ...).
/// </summary>
public record Amenity
{
    public required Guid Id { get; init; }

    /// <summary>
    /// The building this amenity belongs to. One-to-many: an amenity lives in exactly one
    /// building. A genuinely shared facility (e.g. a pool serving two towers of one complex)
    /// would need a many-to-many join instead — deferred, see DESIGN.md.
    /// </summary>
    public required Guid BuildingId { get; init; }

    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>How many concurrent reservations a single time slot can hold. 1 == exclusive.</summary>
    public int Capacity { get; init; } = 1;

    /// <summary>Upper bound on a single booking length, in minutes.</summary>
    public int MaxBookingMinutes { get; init; } = 120;
}
