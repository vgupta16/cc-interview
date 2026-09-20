using Api.Application;
using Api.Domain;
using Api.Tests.Support;
using static Api.Tests.Support.Assertions;

namespace Api.Tests;

public class EndAfterStartRuleTests
{
    private readonly EndAfterStartRule _rule = new();

    [Fact]
    public void EndAfterStart_Passes() =>
        Assert.Null(_rule.Evaluate(Make.Context(TestTime.At(10), TestTime.At(11))));

    [Fact]
    public void OneMinuteLong_Passes() =>
        Assert.Null(_rule.Evaluate(Make.Context(TestTime.At(10), TestTime.At(10, 1))));

    [Fact]
    public void EndEqualToStart_IsRejected() =>
        AssertFailure(_rule.Evaluate(Make.Context(TestTime.At(10), TestTime.At(10))),
            ErrorKind.Validation, ErrorCodes.EndBeforeStart);

    [Fact]
    public void EndBeforeStart_IsRejected() =>
        AssertFailure(_rule.Evaluate(Make.Context(TestTime.At(11), TestTime.At(10))),
            ErrorKind.Validation, ErrorCodes.EndBeforeStart);
}

public class NotInPastRuleTests
{
    private readonly NotInPastRule _rule = new();

    [Fact]
    public void StartInTheFuture_Passes() =>
        Assert.Null(_rule.Evaluate(Make.Context(TestTime.Now.AddHours(1), TestTime.Now.AddHours(2))));

    [Fact]
    public void StartExactlyNow_Passes() =>
        Assert.Null(_rule.Evaluate(Make.Context(TestTime.Now, TestTime.Now.AddHours(1))));

    [Fact]
    public void StartOneMinuteAgo_IsRejected() =>
        AssertFailure(_rule.Evaluate(Make.Context(TestTime.Now.AddMinutes(-1), TestTime.Now.AddHours(1))),
            ErrorKind.Validation, ErrorCodes.InPast);

    [Fact]
    public void WholeWindowInThePast_IsRejected() =>
        AssertFailure(_rule.Evaluate(Make.Context(TestTime.Now.AddHours(-3), TestTime.Now.AddHours(-2))),
            ErrorKind.Validation, ErrorCodes.InPast);

    [Fact]
    public void UsesTheContextsNow_NotTheWallClock()
    {
        var later = TestTime.Now.AddDays(10);
        AssertFailure(_rule.Evaluate(Make.Context(TestTime.Now.AddHours(1), TestTime.Now.AddHours(2), now: later)),
            ErrorKind.Validation, ErrorCodes.InPast);
    }
}

public class MaxDurationRuleTests
{
    private readonly MaxDurationRule _rule = new();
    private static readonly Amenity Ninety = Make.Amenity(maxBookingMinutes: 90);

    [Fact]
    public void ShorterThanMax_Passes() =>
        Assert.Null(_rule.Evaluate(Make.Context(TestTime.At(10), TestTime.At(10, 30), Ninety)));

    [Fact]
    public void ExactlyMax_IsAllowed() =>
        Assert.Null(_rule.Evaluate(Make.Context(TestTime.At(10), TestTime.At(11, 30), Ninety)));

    [Fact]
    public void OneMinuteOverMax_IsRejected() =>
        AssertFailure(_rule.Evaluate(Make.Context(TestTime.At(10), TestTime.At(11, 31), Ninety)),
            ErrorKind.Validation, ErrorCodes.MaxDurationExceeded);

    [Fact]
    public void FarOverMax_IsRejected() =>
        AssertFailure(_rule.Evaluate(Make.Context(TestTime.At(8), TestTime.At(20), Ninety)),
            ErrorKind.Validation, ErrorCodes.MaxDurationExceeded);

    [Fact]
    public void UsesThisAmenitysPolicy()
    {
        var day = Make.Amenity(maxBookingMinutes: 1440);
        Assert.Null(_rule.Evaluate(Make.Context(TestTime.At(0), TestTime.At(0, day: 3), day)));
    }
}

public class OneActivePerResidentRuleTests
{
    private readonly OneActivePerResidentRule _rule = new();

    // "now" is 2030-01-01 08:00; these windows are all still in the future at that point.
    private static Reservation Future(string resident, ReservationStatus status = ReservationStatus.Active) =>
        Make.Reservation(resident, TestTime.At(10), TestTime.At(11), status);

    private static BookingContext Ctx(params Reservation[] active) => CtxWith(null, active);

    private static BookingContext CtxWith(Amenity? amenity, params Reservation[] active) =>
        Make.Context(TestTime.At(14), TestTime.At(15), amenity, "alice", active);

    [Fact]
    public void NoExistingBookings_Passes() =>
        Assert.Null(_rule.Evaluate(Ctx()));

    [Fact]
    public void ResidentAlreadyHasAFutureBooking_IsRejected() =>
        AssertFailure(_rule.Evaluate(Ctx(Future("alice"))),
            ErrorKind.Conflict, ErrorCodes.AlreadyHasBooking);

    [Fact]
    public void ResidentCurrentlyInProgressBooking_IsRejected()
    {
        // Started at 07:00, ends at 09:00, now is 08:00: still active.
        var inProgress = Make.Reservation("alice", TestTime.Now.AddHours(-1), TestTime.Now.AddHours(1));
        AssertFailure(_rule.Evaluate(Ctx(inProgress)), ErrorKind.Conflict, ErrorCodes.AlreadyHasBooking);
    }

    [Fact]
    public void AnotherResidentsBooking_DoesNotCount() =>
        Assert.Null(_rule.Evaluate(Ctx(Future("bob"))));

    [Fact]
    public void ACancelledBooking_DoesNotCount() =>
        Assert.Null(_rule.Evaluate(Ctx(Future("alice", ReservationStatus.Cancelled))));

    [Fact]
    public void AnAlreadyEndedBooking_DoesNotCount()
    {
        var ended = Make.Reservation("alice", TestTime.Now.AddHours(-3), TestTime.Now.AddHours(-2));
        Assert.Null(_rule.Evaluate(Ctx(ended)));
    }

    [Fact]
    public void OnlyTheActorsOwnBookingsAreCounted_AmongMany() =>
        Assert.Null(_rule.Evaluate(Ctx(Future("bob"), Future("carol"), Future("alice", ReservationStatus.Cancelled))));

    [Fact]
    public void MaxActiveTwo_AllowsASecondBooking() =>
        Assert.Null(_rule.Evaluate(CtxWith(Make.Amenity(maxActivePerResident: 2), Future("alice"))));

    [Fact]
    public void MaxActiveTwo_RejectsAThirdBooking() =>
        AssertFailure(_rule.Evaluate(CtxWith(Make.Amenity(maxActivePerResident: 2), Future("alice"), Future("alice"))),
            ErrorKind.Conflict, ErrorCodes.AlreadyHasBooking);

    [Fact]
    public void MaxActiveTwo_CancelledAndEndedBookingsStillLeaveRoom()
    {
        var ended = Make.Reservation("alice", TestTime.Now.AddHours(-3), TestTime.Now.AddHours(-2));
        var amenity = Make.Amenity(maxActivePerResident: 2);
        Assert.Null(_rule.Evaluate(CtxWith(amenity, Future("alice"), Future("alice", ReservationStatus.Cancelled), ended)));
    }
}

public class ReservationAuthorizationTests
{
    private static readonly Reservation AlicesBooking = Make.Reservation("alice", TestTime.At(10), TestTime.At(11));

    [Fact]
    public void Owner_CanCancel() =>
        Assert.True(ReservationAuthorization.CanCancel(new Actor("alice"), AlicesBooking));

    [Fact]
    public void OtherResident_CannotCancel() =>
        Assert.False(ReservationAuthorization.CanCancel(new Actor("bob"), AlicesBooking));

    [Fact]
    public void Staff_CanCancelSomeoneElsesBooking() =>
        Assert.True(ReservationAuthorization.CanCancel(new Actor("bob", IsStaff: true), AlicesBooking));

    [Fact]
    public void Staff_CanCancelTheirOwnBooking() =>
        Assert.True(ReservationAuthorization.CanCancel(new Actor("alice", IsStaff: true), AlicesBooking));
}
