namespace Api.Domain;

/// <summary>
/// A building whose residents share a set of amenities. This is the isolation boundary:
/// every amenity belongs to exactly one building, and a resident may only see or book
/// amenities in buildings they belong to (see <see cref="ResidentBuilding"/>).
///
/// Named "Building" to match the exercise brief. A real Condo Control schema would likely
/// call this a property or community.
/// </summary>
public record Building
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
}
