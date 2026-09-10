namespace Api.Domain;

/// <summary>
/// A building whose residents share a set of amenities. Buildings are the tenancy boundary:
/// reservations in one building never affect availability in another.
/// </summary>
public record Building
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// IANA time zone the building operates in (e.g. "America/New_York"). Instants are stored in
    /// UTC; this is what turns them back into the local times residents actually think in.
    /// </summary>
    public required string TimeZoneId { get; init; }
}
