namespace Api.Domain;

/// <summary>
/// A shared amenity in a building that residents can reserve (gym, party room, guest parking, ...).
///
/// Every booking rule lives here as a column rather than as per-amenity code. That keeps the
/// booking check uniform: there is one code path, and an amenity that doesn't care about a
/// given dimension leaves it null.
/// </summary>
public record Amenity
{
    public required Guid Id { get; init; }

    /// <summary>Tenancy boundary. A resident only ever sees amenities in their own building.</summary>
    public required Guid BuildingId { get; init; }

    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// Max total headcount across all reservations in one slot (pool = 12 bodies in the water).
    /// null = not tracked for this amenity.
    /// </summary>
    public int? TotalCapacity { get; init; }

    /// <summary>
    /// Max number of concurrent reservations in one slot (4 cabanas, 1 parking spot).
    /// null = not tracked for this amenity.
    /// </summary>
    public int? ReservationCapacity { get; init; }

    /// <summary>Largest party size a single reservation may claim.</summary>
    public int MaxSizePerReservation { get; init; } = 1;

    /// <summary>First bookable hour, UTC, inclusive.</summary>
    public int OpeningHourUtc { get; init; } = 6;

    /// <summary>Last bookable hour, UTC, exclusive. A slot starting at ClosingHourUtc is rejected.</summary>
    public int ClosingHourUtc { get; init; } = 22;

    /// <summary>Manual admin close (e.g. "pool shut for maintenance"). Blocks all booking.</summary>
    public bool IsOpen { get; init; } = true;
}
