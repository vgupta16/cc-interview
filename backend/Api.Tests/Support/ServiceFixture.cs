using Api.Application;
using Api.Domain;
using Api.Store;
using Microsoft.Extensions.Time.Testing;

namespace Api.Tests.Support;

/// <summary>
/// A real <see cref="ReservationService"/> wired the way Program.cs wires it: in-memory store,
/// the four rules in registration order, and a fake clock frozen at <see cref="TestTime.Now"/>.
/// One fixture == one fresh, empty repository.
/// </summary>
public sealed class ServiceFixture
{
    public FakeTimeProvider Clock { get; } = new(TestTime.Now);
    public IAmenityCatalog Catalog { get; }
    public IReservationRepository Repository { get; }
    public ReservationService Service { get; }

    public ServiceFixture(IAmenityCatalog? catalog = null)
    {
        Catalog = catalog ?? new InMemoryAmenityCatalog();
        Repository = new InMemoryReservationRepository();

        // Same order as Program.cs.
        IBookingRule[] rules =
        [
            new EndAfterStartRule(),
            new NotInPastRule(),
            new MaxDurationRule(),
            new OneActivePerResidentRule(),
        ];
        Service = new ReservationService(Catalog, Repository, rules, Clock);
    }

    public Task<Result<Reservation>> Book(
        string resident, Guid amenityId, DateTimeOffset start, DateTimeOffset end,
        Guid? building = null, bool staff = false) =>
        Service.CreateAsync(building ?? SeedData.BuildingA, new Actor(resident, staff),
            new CreateReservationCommand(amenityId, start, end));

    /// <summary>Books and asserts success (for arranging state).</summary>
    public async Task<Reservation> BookOk(
        string resident, Guid amenityId, DateTimeOffset start, DateTimeOffset end, Guid? building = null) =>
        Assertions.AssertSuccess(await Book(resident, amenityId, start, end, building));

    public Task<Failure?> Cancel(string resident, Guid reservationId, Guid? building = null, bool staff = false) =>
        Service.CancelAsync(building ?? SeedData.BuildingA, new Actor(resident, staff), reservationId);

    public async Task<IReadOnlyList<Reservation>> ListOk(Guid amenityId, Guid? building = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        Assertions.AssertSuccess(await Service.ListAsync(building ?? SeedData.BuildingA, amenityId, from, to));
}

/// <summary>Amenity catalog over an explicit list, for cases the seed data can't express (e.g. capacity 2 with a 2h limit).</summary>
public sealed class StubAmenityCatalog(params Amenity[] amenities) : IAmenityCatalog
{
    public Task<IReadOnlyList<Amenity>> ListAsync(Guid buildingId) =>
        Task.FromResult<IReadOnlyList<Amenity>>(amenities.Where(a => a.BuildingId == buildingId).ToList());

    public Task<Amenity?> FindAsync(Guid buildingId, Guid amenityId) =>
        Task.FromResult(amenities.FirstOrDefault(a => a.BuildingId == buildingId && a.Id == amenityId));
}
