namespace Api.Domain;

/// <summary>
/// Links a resident to a building they belong to. Many-to-many: a resident can hold units in
/// more than one building, and a building has many residents.
///
/// This is a relationship entity rather than a bare join table. In production it would carry
/// UnitId, Role (owner vs. renter), Status, and move-in/move-out dates — the real path is
/// resident -> unit -> building. Collapsed to direct membership here because nothing in this
/// slice needs unit-level data.
/// </summary>
public record ResidentBuilding
{
    public required string ResidentId { get; init; }
    public required Guid BuildingId { get; init; }
}
