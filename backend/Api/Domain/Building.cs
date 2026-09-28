namespace Api.Domain;

/// <summary>
/// A building whose residents and amenities form one tenant.
/// </summary>
public record Building
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
}
