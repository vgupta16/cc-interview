using Api.Store;
using Api.Tests.Support;

namespace Api.Tests;

/// <summary>
/// The building-wide listing (design section 3): every active reservation in the caller's
/// building across all amenities, ordered by start, never another building's.
/// </summary>
public class BuildingListingTests
{
    private readonly ServiceFixture _f = new();
    private static readonly Guid A = SeedData.BuildingA;
    private static readonly Guid B = SeedData.BuildingB;

    private static DateTimeOffset At(int hour, int minute = 0) => TestTime.At(hour, minute);

    [Fact]
    public async Task ListForBuilding_ReturnsEveryAmenitysBookings_OrderedByStart()
    {
        var gym = await _f.BookOk("alice", SeedData.GymA, At(12), At(13));
        var party = await _f.BookOk("bob", SeedData.PartyRoomA, At(9), At(10));
        var parking = await _f.BookOk("carol", SeedData.GuestParkingA, At(10), At(11));

        var listed = await _f.Service.ListForBuildingAsync(A, null, null);

        Assert.Equal([party.Id, parking.Id, gym.Id], listed.Select(r => r.Id));
    }

    [Fact]
    public async Task ListForBuilding_OmitsCancelledReservations()
    {
        var kept = await _f.BookOk("alice", SeedData.PartyRoomA, At(10), At(11));
        var gone = await _f.BookOk("bob", SeedData.GymA, At(10), At(11));
        Assert.Null(await _f.Cancel("bob", gone.Id));

        var listed = await _f.Service.ListForBuildingAsync(A, null, null);

        Assert.Equal([kept.Id], listed.Select(r => r.Id));
    }

    [Fact]
    public async Task ListForBuilding_NeverIncludesAnotherBuildingsData()
    {
        var inA = await _f.BookOk("alice", SeedData.PartyRoomA, At(10), At(11), building: A);
        var inB = await _f.BookOk("alice", SeedData.PartyRoomB, At(10), At(11), building: B);

        Assert.Equal([inA.Id], (await _f.Service.ListForBuildingAsync(A, null, null)).Select(r => r.Id));
        Assert.Equal([inB.Id], (await _f.Service.ListForBuildingAsync(B, null, null)).Select(r => r.Id));
    }

    [Fact]
    public async Task ListForBuilding_AppliesTheFromToWindow()
    {
        await _f.BookOk("alice", SeedData.PartyRoomA, At(9), At(10));
        var noon = await _f.BookOk("bob", SeedData.GymA, At(12), At(13));

        var listed = await _f.Service.ListForBuildingAsync(A, At(11), At(14));

        Assert.Equal([noon.Id], listed.Select(r => r.Id));
    }

    [Fact]
    public async Task ListForBuilding_WhenNothingIsBooked_IsEmpty() =>
        Assert.Empty(await _f.Service.ListForBuildingAsync(A, null, null));
}
