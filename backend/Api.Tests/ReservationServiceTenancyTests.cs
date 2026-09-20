using Api.Application;
using Api.Store;
using Api.Tests.Support;
using static Api.Tests.Support.Assertions;

namespace Api.Tests;

/// <summary>
/// Tenancy (design section 5): a resource in another building is indistinguishable from one that
/// doesn't exist (404, never 403), and one building's activity never affects another's.
/// </summary>
public class ReservationServiceTenancyTests
{
    private readonly ServiceFixture _f = new();
    private static readonly Guid A = SeedData.BuildingA;
    private static readonly Guid B = SeedData.BuildingB;

    private static DateTimeOffset At(int hour, int minute = 0) => TestTime.At(hour, minute);

    [Fact]
    public async Task Create_WithAnAmenityFromAnotherBuilding_IsNotFound_AndStoresNothing()
    {
        var result = await _f.Book("alice", SeedData.PartyRoomB, At(10), At(11), building: A);

        AssertFailure(result, ErrorKind.NotFound, ErrorCodes.AmenityNotFound);
        Assert.Empty(await _f.ListOk(SeedData.PartyRoomB, building: B));
    }

    [Fact]
    public async Task Create_WithAnAmenityFromAnotherBuilding_IsNotFound_InTheOtherDirectionToo() =>
        AssertFailure(await _f.Book("alice", SeedData.GymA, At(10), At(11), building: B),
            ErrorKind.NotFound, ErrorCodes.AmenityNotFound);

    [Fact]
    public async Task Cancel_ReservationOfAnotherBuilding_IsNotFound_AndTheBookingSurvives()
    {
        var inA = await _f.BookOk("alice", SeedData.PartyRoomA, At(10), At(11), building: A);

        var failure = await _f.Cancel("alice", inA.Id, building: B);

        AssertFailure(failure, ErrorKind.NotFound, ErrorCodes.ReservationNotFound);
        Assert.Single(await _f.ListOk(SeedData.PartyRoomA, building: A));
    }

    [Fact]
    public async Task Cancel_ReservationOfAnotherBuilding_IsNotFoundEvenForStaff()
    {
        var inA = await _f.BookOk("alice", SeedData.PartyRoomA, At(10), At(11), building: A);

        var failure = await _f.Cancel("manager", inA.Id, building: B, staff: true);

        AssertFailure(failure, ErrorKind.NotFound, ErrorCodes.ReservationNotFound);
        Assert.Single(await _f.ListOk(SeedData.PartyRoomA, building: A));
    }

    [Fact]
    public async Task List_ForAnAmenityOfAnotherBuilding_IsNotFound()
    {
        AssertFailure(await _f.Service.ListAsync(B, SeedData.GymA, null, null), ErrorKind.NotFound, ErrorCodes.AmenityNotFound);
        AssertFailure(await _f.Service.ListAsync(A, SeedData.GymB, null, null), ErrorKind.NotFound, ErrorCodes.AmenityNotFound);
    }

    [Fact]
    public async Task ABookingInBuildingA_DoesNotOccupyTheSameSlotInBuildingB()
    {
        await _f.BookOk("alice", SeedData.PartyRoomA, At(10), At(11), building: A);

        // Different tenant, different amenity: same time slot is free, even for the same resident id.
        AssertSuccess(await _f.Book("alice", SeedData.PartyRoomB, At(10), At(11), building: B));
        AssertSuccess(await _f.Book("bob", SeedData.GuestParkingB, At(10), At(11), building: B));
    }

    [Fact]
    public async Task FillingAnAmenityInBuildingA_DoesNotReduceCapacityInBuildingB()
    {
        await _f.BookOk("alice", SeedData.GymA, At(10), At(11), building: A);
        await _f.BookOk("bob", SeedData.GymA, At(10), At(11), building: A);
        AssertFailure(await _f.Book("carol", SeedData.GymA, At(10), At(11), building: A), ErrorKind.Conflict, ErrorCodes.SlotFull);

        AssertSuccess(await _f.Book("carol", SeedData.GymB, At(10), At(11), building: B));
        AssertSuccess(await _f.Book("dave", SeedData.GymB, At(10), At(11), building: B));
        AssertFailure(await _f.Book("erin", SeedData.GymB, At(10), At(11), building: B), ErrorKind.Conflict, ErrorCodes.SlotFull);
    }

    [Fact]
    public async Task EachBuildingsListShowsOnlyItsOwnReservations()
    {
        var inA = await _f.BookOk("alice", SeedData.PartyRoomA, At(10), At(11), building: A);
        var inB = await _f.BookOk("alice", SeedData.PartyRoomB, At(10), At(11), building: B);

        Assert.Equal([inA.Id], (await _f.ListOk(SeedData.PartyRoomA, building: A)).Select(r => r.Id));
        Assert.Equal([inB.Id], (await _f.ListOk(SeedData.PartyRoomB, building: B)).Select(r => r.Id));
        Assert.Equal(B, inB.BuildingId);
    }

    [Fact]
    public async Task CancellingInBuildingA_DoesNotTouchBuildingB()
    {
        var inA = await _f.BookOk("alice", SeedData.PartyRoomA, At(10), At(11), building: A);
        await _f.BookOk("alice", SeedData.PartyRoomB, At(10), At(11), building: B);

        Assert.Null(await _f.Cancel("alice", inA.Id, building: A));

        Assert.Empty(await _f.ListOk(SeedData.PartyRoomA, building: A));
        Assert.Single(await _f.ListOk(SeedData.PartyRoomB, building: B));
    }
}
