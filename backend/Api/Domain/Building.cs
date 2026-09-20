namespace Api.Domain;

/// <summary>The tenant root. Every amenity and reservation belongs to exactly one building.</summary>
public record Building
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>IANA time zone id. Not used by the slice yet; opening hours will need it.</summary>
    public required string TimeZone { get; init; }
}
