using Api.Application;
using Api.Domain;
using Api.Store;
using Api.Tests.Support;
using static Api.Tests.Support.Assertions;

namespace Api.Tests;

/// <summary>
/// Behavior of <see cref="ReservationService"/> over the real in-memory store and the real rules.
/// Seed amenities used: Party Room (capacity 1, max 240 min), Gym (capacity 2, max 90 min),
/// all in Building A. "Now" is 2030-01-01 08:00; bookings are made on 2030-01-02.
/// </summary>
public class ReservationServiceTests
{
    private readonly ServiceFixture _f = new();
    private static readonly Guid PartyRoom = SeedData.PartyRoomA;
    private static readonly Guid Gym = SeedData.GymA;

    private static DateTimeOffset At(int hour, int minute = 0, int day = 2) => TestTime.At(hour, minute, day);

    // ---- create / list / cancel basics ---------------------------------------------------

    [Fact]
    public async Task Create_HappyPath_ReturnsAnActiveReservationOwnedByTheActor()
    {
        var result = await _f.Book("alice", PartyRoom, At(10), At(11));

        var reservation = AssertSuccess(result);
        Assert.NotEqual(Guid.Empty, reservation.Id);
        Assert.Equal(SeedData.BuildingA, reservation.BuildingId);
        Assert.Equal(PartyRoom, reservation.AmenityId);
        Assert.Equal("alice", reservation.ResidentId);
        Assert.Equal(At(10), reservation.StartUtc);
        Assert.Equal(At(11), reservation.EndUtc);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(TestTime.Now, reservation.CreatedUtc);
    }

    [Fact]
    public async Task List_ShowsACreatedReservation()
    {
        var created = await _f.BookOk("alice", PartyRoom, At(10), At(11));

        var listed = await _f.ListOk(PartyRoom);

        var only = Assert.Single(listed);
        Assert.Equal(created.Id, only.Id);
    }

    [Fact]
    public async Task List_WithNoBookings_IsEmpty() =>
        Assert.Empty(await _f.ListOk(PartyRoom));

    [Fact]
    public async Task List_OnlyShowsThatAmenitysReservations()
    {
        await _f.BookOk("alice", PartyRoom, At(10), At(11));

        Assert.Empty(await _f.ListOk(Gym));
    }

    [Fact]
    public async Task List_FiltersByHalfOpenWindow()
    {
        var morning = await _f.BookOk("alice", PartyRoom, At(9), At(10));
        var noon = await _f.BookOk("bob", PartyRoom, At(12), At(13));

        var afternoon = await _f.ListOk(PartyRoom, from: At(11), to: At(14));
        var touchingOnly = await _f.ListOk(PartyRoom, from: At(10), to: At(12));
        var everything = await _f.ListOk(PartyRoom);

        Assert.Equal([noon.Id], afternoon.Select(r => r.Id));
        Assert.Empty(touchingOnly); // 9-10 ends when the window starts, 12-13 starts when it ends
        Assert.Equal(new[] { morning.Id, noon.Id }.Order(), everything.Select(r => r.Id).Order());
    }

    [Fact]
    public async Task Cancel_ByOwner_Succeeds_AndRemovesItFromTheList()
    {
        var created = await _f.BookOk("alice", PartyRoom, At(10), At(11));

        var failure = await _f.Cancel("alice", created.Id);

        Assert.Null(failure);
        Assert.Empty(await _f.ListOk(PartyRoom));
    }

    [Fact]
    public async Task Cancel_FreesTheSlotForSomeoneElse()
    {
        var alices = await _f.BookOk("alice", PartyRoom, At(10), At(11));
        AssertFailure(await _f.Book("bob", PartyRoom, At(10), At(11)), ErrorKind.Conflict, ErrorCodes.SlotFull);

        Assert.Null(await _f.Cancel("alice", alices.Id));

        var bobs = AssertSuccess(await _f.Book("bob", PartyRoom, At(10), At(11)));
        Assert.Equal("bob", bobs.ResidentId);
    }

    [Fact]
    public async Task Cancel_ThenRebookBySameResident_Succeeds()
    {
        var first = await _f.BookOk("alice", PartyRoom, At(10), At(11));
        await _f.Cancel("alice", first.Id);

        AssertSuccess(await _f.Book("alice", PartyRoom, At(10), At(11)));
    }

    // ---- capacity 1 (Party Room) ---------------------------------------------------------

    [Theory]
    [InlineData(10, 0, 11, 0)] // identical
    [InlineData(10, 30, 11, 30)] // overlaps the end of the existing one
    [InlineData(9, 30, 10, 30)] // overlaps the start
    [InlineData(10, 15, 10, 45)] // inside
    [InlineData(9, 0, 12, 0)] // contains
    public async Task CapacityOne_OverlappingBookingByAnotherResident_IsSlotFull(int sh, int sm, int eh, int em)
    {
        await _f.BookOk("alice", PartyRoom, At(10), At(11));

        var result = await _f.Book("bob", PartyRoom, At(sh, sm), At(eh, em));

        AssertFailure(result, ErrorKind.Conflict, ErrorCodes.SlotFull);
        Assert.Single(await _f.ListOk(PartyRoom));
    }

    [Fact]
    public async Task CapacityOne_BackToBackBookings_AreAllowed()
    {
        await _f.BookOk("alice", PartyRoom, At(10), At(11));

        AssertSuccess(await _f.Book("bob", PartyRoom, At(11), At(12)));   // starts as alice ends
        AssertSuccess(await _f.Book("carol", PartyRoom, At(9), At(10)));  // ends as alice starts
        Assert.Equal(3, (await _f.ListOk(PartyRoom)).Count);
    }

    // ---- capacity 2 (Gym) ----------------------------------------------------------------

    [Fact]
    public async Task CapacityTwo_TwoResidentsMayOverlap_ButAThirdIsRejected()
    {
        AssertSuccess(await _f.Book("alice", Gym, At(10), At(11)));
        AssertSuccess(await _f.Book("bob", Gym, At(10), At(11)));

        var third = await _f.Book("carol", Gym, At(10), At(11));

        AssertFailure(third, ErrorKind.Conflict, ErrorCodes.SlotFull);
    }

    [Fact]
    public async Task CapacityTwo_ThirdBookingAfterTheFullOneEnds_IsAllowed()
    {
        await _f.BookOk("alice", Gym, At(10), At(11));
        await _f.BookOk("bob", Gym, At(10), At(11));

        AssertSuccess(await _f.Book("carol", Gym, At(11), At(12)));
    }

    [Fact]
    public async Task CapacityTwo_BookingSpanningTwoBackToBackBookings_IsAcceptedBecausePeakIsOne()
    {
        // 9-10 (alice) and 10-11 (bob) never overlap each other. Carol's 9:30-10:30 overlaps both
        // but occupancy never exceeds 1, so a capacity-2 amenity has room. A naive "count of
        // overlapping bookings >= capacity" check would wrongly reject this (2 >= 2).
        await _f.BookOk("alice", Gym, At(9), At(10));
        await _f.BookOk("bob", Gym, At(10), At(11));

        var carol = await _f.Book("carol", Gym, At(9, 30), At(10, 30));

        AssertSuccess(carol);
    }

    [Fact]
    public async Task CapacityTwo_AfterThePeakScenarioIsBooked_TheSameWindowIsFullForTheNextResident()
    {
        await _f.BookOk("alice", Gym, At(9), At(10));
        await _f.BookOk("bob", Gym, At(10), At(11));
        await _f.BookOk("carol", Gym, At(9, 30), At(10, 30));

        // Occupancy is now 2 during 9:30-10:30, so nobody else fits there.
        AssertFailure(await _f.Book("dave", Gym, At(9, 30), At(10, 30)), ErrorKind.Conflict, ErrorCodes.SlotFull);
        AssertFailure(await _f.Book("dave", Gym, At(10, 15), At(10, 45)), ErrorKind.Conflict, ErrorCodes.SlotFull);
        // ...but the edges, where only one booking is active, still have room.
        AssertSuccess(await _f.Book("erin", Gym, At(9), At(9, 30)));
    }

    [Fact]
    public async Task CapacityTwo_TwoHourWindowSpanningBackToBackBookings_IsAcceptedWhenPolicyAllowsTwoHours()
    {
        // Exactly the 9-10 / 10-11 / 9-11 scenario from the design doc. The seeded Gym only allows
        // 90 minute bookings, so use a capacity-2 amenity with a 2 hour limit.
        var amenity = Make.Amenity(capacity: 2, maxBookingMinutes: 120);
        var f = new ServiceFixture(new StubAmenityCatalog(amenity));
        await f.BookOk("alice", amenity.Id, At(9), At(10));
        await f.BookOk("bob", amenity.Id, At(10), At(11));

        var carol = await f.Book("carol", amenity.Id, At(9), At(11));

        AssertSuccess(carol);
    }

    [Fact]
    public async Task CapacityOne_WindowSpanningTwoBackToBackBookings_IsRejected()
    {
        // Same shape on capacity 1: peak would be 2 > 1.
        var amenity = Make.Amenity(capacity: 1, maxBookingMinutes: 120);
        var f = new ServiceFixture(new StubAmenityCatalog(amenity));
        await f.BookOk("alice", amenity.Id, At(9), At(10));
        await f.BookOk("bob", amenity.Id, At(10), At(11));

        AssertFailure(await f.Book("carol", amenity.Id, At(9), At(11)), ErrorKind.Conflict, ErrorCodes.SlotFull);
    }

    // ---- one active booking per resident -------------------------------------------------

    [Fact]
    public async Task ResidentWithAnActiveBooking_CannotBookTheSameAmenityAgain()
    {
        await _f.BookOk("alice", PartyRoom, At(10), At(11));

        // A different, free window, so only the per-resident rule can be what rejects it.
        var second = await _f.Book("alice", PartyRoom, At(14), At(15));

        AssertFailure(second, ErrorKind.Conflict, ErrorCodes.AlreadyHasBooking);
    }

    [Fact]
    public async Task OneActiveLimit_IsPerAmenity()
    {
        await _f.BookOk("alice", PartyRoom, At(10), At(11));

        AssertSuccess(await _f.Book("alice", Gym, At(10), At(11)));
    }

    [Fact]
    public async Task OneActiveLimit_DoesNotBlockOtherResidents()
    {
        await _f.BookOk("alice", PartyRoom, At(10), At(11));

        AssertSuccess(await _f.Book("bob", PartyRoom, At(14), At(15)));
    }

    [Fact]
    public async Task OneActiveLimit_ACancelledBookingDoesNotCount()
    {
        var first = await _f.BookOk("alice", PartyRoom, At(10), At(11));
        await _f.Cancel("alice", first.Id);

        AssertSuccess(await _f.Book("alice", PartyRoom, At(14), At(15)));
    }

    [Fact]
    public async Task OneActiveLimit_ABookingThatHasEndedDoesNotCount()
    {
        await _f.BookOk("alice", PartyRoom, At(10), At(11));

        _f.Clock.SetUtcNow(At(12)); // the 10-11 booking is now over

        AssertSuccess(await _f.Book("alice", PartyRoom, At(14), At(15)));
    }

    [Fact]
    public async Task OneActiveLimit_UsesThePolicyValue()
    {
        var amenity = Make.Amenity(capacity: 3, maxBookingMinutes: 120, maxActivePerResident: 2);
        var f = new ServiceFixture(new StubAmenityCatalog(amenity));

        AssertSuccess(await f.Book("alice", amenity.Id, At(9), At(10)));
        AssertSuccess(await f.Book("alice", amenity.Id, At(11), At(12)));
        AssertFailure(await f.Book("alice", amenity.Id, At(13), At(14)), ErrorKind.Conflict, ErrorCodes.AlreadyHasBooking);
    }

    // ---- validation ----------------------------------------------------------------------

    [Fact]
    public async Task Create_EndEqualToStart_IsValidationEndBeforeStart() =>
        AssertFailure(await _f.Book("alice", PartyRoom, At(10), At(10)), ErrorKind.Validation, ErrorCodes.EndBeforeStart);

    [Fact]
    public async Task Create_EndBeforeStart_IsValidationEndBeforeStart() =>
        AssertFailure(await _f.Book("alice", PartyRoom, At(11), At(10)), ErrorKind.Validation, ErrorCodes.EndBeforeStart);

    [Fact]
    public async Task Create_StartInThePast_IsValidationInPast() =>
        AssertFailure(
            await _f.Book("alice", PartyRoom, TestTime.Now.AddMinutes(-1), TestTime.Now.AddHours(1)),
            ErrorKind.Validation, ErrorCodes.InPast);

    [Fact]
    public async Task Create_StartExactlyNow_IsAllowed() =>
        AssertSuccess(await _f.Book("alice", PartyRoom, TestTime.Now, TestTime.Now.AddHours(1)));

    [Fact]
    public async Task Create_UsesTheInjectedClock()
    {
        _f.Clock.SetUtcNow(At(12));

        AssertFailure(await _f.Book("alice", PartyRoom, At(10), At(11)), ErrorKind.Validation, ErrorCodes.InPast);
    }

    [Fact]
    public async Task Create_LongerThanTheAmenitysMax_IsValidationMaxDurationExceeded()
    {
        // Party Room max is 240 minutes.
        AssertFailure(await _f.Book("alice", PartyRoom, At(10), At(14, 1)), ErrorKind.Validation, ErrorCodes.MaxDurationExceeded);
        // Gym max is 90 minutes.
        AssertFailure(await _f.Book("alice", Gym, At(10), At(11, 31)), ErrorKind.Validation, ErrorCodes.MaxDurationExceeded);
    }

    [Fact]
    public async Task Create_ExactlyTheAmenitysMax_IsAllowed()
    {
        AssertSuccess(await _f.Book("alice", PartyRoom, At(10), At(14)));
        AssertSuccess(await _f.Book("bob", Gym, At(10), At(11, 30)));
    }

    [Fact]
    public async Task Create_ValidationFailure_DoesNotStoreAnything()
    {
        await _f.Book("alice", PartyRoom, At(11), At(10));
        await _f.Book("alice", PartyRoom, At(10), At(15));

        Assert.Empty(await _f.ListOk(PartyRoom));
    }

    [Fact]
    public async Task Create_UnknownAmenity_IsNotFoundAmenityNotFound() =>
        AssertFailure(await _f.Book("alice", Guid.NewGuid(), At(10), At(11)), ErrorKind.NotFound, ErrorCodes.AmenityNotFound);

    [Fact]
    public async Task List_UnknownAmenity_IsNotFoundAmenityNotFound() =>
        AssertFailure(await _f.Service.ListAsync(SeedData.BuildingA, Guid.NewGuid(), null, null),
            ErrorKind.NotFound, ErrorCodes.AmenityNotFound);

    // ---- cancel: authorization and errors ------------------------------------------------

    [Fact]
    public async Task Cancel_ByAnotherResident_IsForbiddenNotOwner_AndLeavesTheBookingActive()
    {
        var alices = await _f.BookOk("alice", PartyRoom, At(10), At(11));

        var failure = await _f.Cancel("bob", alices.Id);

        AssertFailure(failure, ErrorKind.Forbidden, ErrorCodes.NotOwner);
        Assert.Single(await _f.ListOk(PartyRoom));
    }

    [Fact]
    public async Task Cancel_ByStaff_SucceedsForAnotherResidentsBooking()
    {
        var alices = await _f.BookOk("alice", PartyRoom, At(10), At(11));

        var failure = await _f.Cancel("manager", alices.Id, staff: true);

        Assert.Null(failure);
        Assert.Empty(await _f.ListOk(PartyRoom));
    }

    [Fact]
    public async Task Cancel_UnknownReservation_IsNotFoundReservationNotFound() =>
        AssertFailure(await _f.Cancel("alice", Guid.NewGuid()), ErrorKind.NotFound, ErrorCodes.ReservationNotFound);

    [Fact]
    public async Task Cancel_AlreadyCancelled_IsIdempotent()
    {
        var alices = await _f.BookOk("alice", PartyRoom, At(10), At(11));

        Assert.Null(await _f.Cancel("alice", alices.Id));
        Assert.Null(await _f.Cancel("alice", alices.Id));
    }
}
