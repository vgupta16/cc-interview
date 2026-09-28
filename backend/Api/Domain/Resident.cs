namespace Api.Domain;

/// <summary>
/// A seeded resident who belongs to exactly one building.
/// </summary>
public record Resident
{
    public required Guid Id { get; init; }
    public required Guid BuildingId { get; init; }
}
