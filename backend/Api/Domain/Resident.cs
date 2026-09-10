namespace Api.Domain;

/// <summary>
/// A resident who can reserve amenities. Stands in for whatever the real system's identity
/// provider would hand us; see the X-Resident-Id header shim in Program.cs.
/// </summary>
public record Resident
{
    public required Guid Id { get; init; }

    /// <summary>
    /// The building this resident lives in. Assumed to be exactly one: a resident may only book
    /// amenities in their own building.
    /// </summary>
    public required Guid BuildingId { get; init; }

    public required string DisplayName { get; init; }
    public string? UnitNumber { get; init; }
}
