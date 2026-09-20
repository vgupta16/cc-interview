using Api.Application;
using Api.Domain;

namespace Api.Store;

/// <summary>Amenities are read-only seed data in this slice.</summary>
public sealed class InMemoryAmenityCatalog : IAmenityCatalog
{
    public Task<IReadOnlyList<Amenity>> ListAsync(Guid buildingId) =>
        Task.FromResult<IReadOnlyList<Amenity>>(SeedData.Amenities.Where(a => a.BuildingId == buildingId).ToList());

    public Task<Amenity?> FindAsync(Guid buildingId, Guid amenityId) =>
        Task.FromResult(SeedData.Amenities.FirstOrDefault(a => a.BuildingId == buildingId && a.Id == amenityId));
}
