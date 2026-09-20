namespace Api.Domain;

/// <summary>
/// Per-amenity booking rules expressed as data, so a new rule adds a field here plus an
/// <c>IBookingRule</c> instead of another column on <see cref="Amenity"/>.
/// </summary>
public record AmenityPolicy
{
    /// <summary>Upper bound on a single booking length, in minutes.</summary>
    public int MaxBookingMinutes { get; init; } = 120;

    /// <summary>How many active bookings one resident may hold on this amenity at once.</summary>
    public int MaxActivePerResident { get; init; } = 1;
}

/// <summary>
/// A shared amenity in a building that residents can reserve (gym, party room, guest parking, ...).
/// </summary>
public record Amenity
{
    public required Guid Id { get; init; }

    /// <summary>The tenant this amenity belongs to.</summary>
    public required Guid BuildingId { get; init; }

    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>How many concurrent reservations a single time slot can hold. 1 == exclusive.</summary>
    public int Capacity { get; init; } = 1;

    public AmenityPolicy Policy { get; init; } = new();
}
