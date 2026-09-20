using Api.Application;
using Api.Domain;
using Api.Tests.Support;

namespace Api.Tests;

public class OccupancyTests
{
    private static DateTimeOffset H(int hour, int minute = 0) => TestTime.At(hour, minute);

    private static Reservation R(int startHour, int endHour, ReservationStatus status = ReservationStatus.Active) =>
        Make.Reservation("r", H(startHour), H(endHour), status);

    private static Reservation R(DateTimeOffset start, DateTimeOffset end) => Make.Reservation("r", start, end);

    // ---- MaxConcurrent -------------------------------------------------------------------

    [Fact]
    public void MaxConcurrent_NoReservations_IsZero() =>
        Assert.Equal(0, Occupancy.MaxConcurrent([], H(9), H(11)));

    [Fact]
    public void MaxConcurrent_SingleReservationInsideWindow_IsOne() =>
        Assert.Equal(1, Occupancy.MaxConcurrent([R(9, 10)], H(9), H(11)));

    [Fact]
    public void MaxConcurrent_SingleReservationCoveringWholeWindow_IsOne() =>
        Assert.Equal(1, Occupancy.MaxConcurrent([R(8, 12)], H(9), H(11)));

    [Fact]
    public void MaxConcurrent_BackToBackReservations_NeverOverlap()
    {
        var peak = Occupancy.MaxConcurrent([R(10, 11), R(11, 12)], H(10), H(12));
        Assert.Equal(1, peak);
    }

    [Fact]
    public void MaxConcurrent_TwoBackToBackBookingsUnderOneWiderWindow_IsOneNotTwo()
    {
        // The reason this function exists: 9-10 and 10-11 both overlap the window 9-11,
        // but never at the same instant, so the peak is 1.
        var peak = Occupancy.MaxConcurrent([R(9, 10), R(10, 11)], H(9), H(11));
        Assert.Equal(1, peak);
    }

    [Fact]
    public void MaxConcurrent_TwoTrulyOverlappingReservations_IsTwo() =>
        Assert.Equal(2, Occupancy.MaxConcurrent([R(9, 11), R(10, 12)], H(9), H(12)));

    [Fact]
    public void MaxConcurrent_IdenticalReservations_CountEachOne() =>
        Assert.Equal(3, Occupancy.MaxConcurrent([R(9, 10), R(9, 10), R(9, 10)], H(9), H(10)));

    [Fact]
    public void MaxConcurrent_PartialOverlapsThatNeverAllMeet_ReportsPeakNotTotal()
    {
        // 9-10:30, 10-11:30, 11-12:30: at most two at once (10-10:30 and 11-11:30), never three.
        var reservations = new[] { R(H(9), H(10, 30)), R(H(10), H(11, 30)), R(H(11), H(12, 30)) };
        Assert.Equal(2, Occupancy.MaxConcurrent(reservations, H(9), H(13)));
    }

    [Fact]
    public void MaxConcurrent_InputOrderDoesNotMatter()
    {
        var reservations = new[] { R(11, 12), R(9, 10), R(10, 11), R(9, 12) };
        Assert.Equal(2, Occupancy.MaxConcurrent(reservations, H(9), H(12)));
        Assert.Equal(2, Occupancy.MaxConcurrent(reservations.Reverse(), H(9), H(12)));
    }

    [Fact]
    public void MaxConcurrent_OnlyCountsTimeInsideTheWindow()
    {
        // 8-10 and 9-12 overlap each other during 9-10, but the window 10-12 only sees the second.
        Assert.Equal(1, Occupancy.MaxConcurrent([R(8, 10), R(9, 12)], H(10), H(12)));
        // Widen the window to include 9-10 and the overlap is visible.
        Assert.Equal(2, Occupancy.MaxConcurrent([R(8, 10), R(9, 12)], H(9), H(12)));
    }

    [Fact]
    public void MaxConcurrent_CancelledReservationsAreIgnored()
    {
        var reservations = new[] { R(9, 10, ReservationStatus.Cancelled), R(9, 10) };
        Assert.Equal(1, Occupancy.MaxConcurrent(reservations, H(9), H(10)));
    }

    [Fact]
    public void MaxConcurrent_OnlyCancelledReservations_IsZero() =>
        Assert.Equal(0, Occupancy.MaxConcurrent([R(9, 10, ReservationStatus.Cancelled)], H(9), H(10)));

    [Fact]
    public void MaxConcurrent_ReservationsEntirelyOutsideTheWindow_AreIgnored()
    {
        var reservations = new[] { R(6, 7), R(14, 15) };
        Assert.Equal(0, Occupancy.MaxConcurrent(reservations, H(9), H(11)));
    }

    [Fact]
    public void MaxConcurrent_ReservationEndingExactlyWhenWindowStarts_IsIgnored() =>
        Assert.Equal(0, Occupancy.MaxConcurrent([R(8, 9)], H(9), H(11)));

    [Fact]
    public void MaxConcurrent_ReservationStartingExactlyWhenWindowEnds_IsIgnored() =>
        Assert.Equal(0, Occupancy.MaxConcurrent([R(11, 12)], H(9), H(11)));

    [Fact]
    public void MaxConcurrent_ReservationOverlappingByOneMinuteAtTheEdge_Counts()
    {
        Assert.Equal(1, Occupancy.MaxConcurrent([R(H(8), H(9, 1))], H(9), H(11)));
        Assert.Equal(1, Occupancy.MaxConcurrent([R(H(10, 59), H(12))], H(9), H(11)));
    }

    // ---- Overlaps ------------------------------------------------------------------------

    [Theory]
    [InlineData(9, 11, 10, 12, true)]   // partial overlap
    [InlineData(10, 12, 9, 11, true)]   // partial overlap, other order
    [InlineData(9, 12, 10, 11, true)]   // containment
    [InlineData(10, 11, 9, 12, true)]   // contained
    [InlineData(9, 10, 9, 10, true)]    // identical
    [InlineData(9, 10, 10, 11, false)]  // back-to-back: a ends when b starts
    [InlineData(10, 11, 9, 10, false)]  // back-to-back: b ends when a starts
    [InlineData(9, 10, 11, 12, false)]  // disjoint
    [InlineData(11, 12, 9, 10, false)]  // disjoint, other order
    public void Overlaps_UsesHalfOpenIntervals(int aStart, int aEnd, int bStart, int bEnd, bool expected) =>
        Assert.Equal(expected, Occupancy.Overlaps(H(aStart), H(aEnd), H(bStart), H(bEnd)));

    [Fact]
    public void Overlaps_OneMinuteOfSharedTime_Overlaps() =>
        Assert.True(Occupancy.Overlaps(H(9), H(10, 1), H(10), H(11)));
}
