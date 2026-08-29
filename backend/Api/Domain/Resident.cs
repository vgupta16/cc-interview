namespace Api.Domain;

/// <summary>
/// The only kind of user in this slice. Seeded; there is no sign-up flow (see Design.md).
/// </summary>
public record Resident
{
    public required Guid Id { get; init; }

    /// <summary>Tenancy boundary. Determines which amenities this resident can see or book.</summary>
    public required Guid BuildingId { get; init; }

    /// <summary>
    /// Display label for the building. Denormalized onto the resident because there is no
    /// Building entity in this slice — BuildingId is the tenancy key and nothing else reads a
    /// building row. In a real schema this lives on `buildings.name` and is joined.
    /// </summary>
    public required string BuildingName { get; init; }

    public required string Name { get; init; }
    public required string Email { get; init; }
    public string? Phone { get; init; }
    public required string UnitNumber { get; init; }
}
