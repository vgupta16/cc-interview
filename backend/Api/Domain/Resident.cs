namespace Api.Domain;

/// <summary>
/// The only kind of user in this slice. Seeded; there is no sign-up flow (see Design.md).
/// </summary>
public record Resident
{
    public required Guid Id { get; init; }

    /// <summary>Tenancy boundary. Determines which amenities this resident can see or book.</summary>
    public required Guid BuildingId { get; init; }

    public required string Name { get; init; }
    public required string Email { get; init; }
    public string? Phone { get; init; }
    public required string UnitNumber { get; init; }
}
