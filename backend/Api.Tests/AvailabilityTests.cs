using Api.Domain;
using Api.Reservations;
using static Api.Tests.Fixtures;

namespace Api.Tests;

/// <summary>
/// The pure arithmetic underneath every booking decision. Cheap to enumerate exhaustively here,
/// which is the whole reason it lives outside the service.
/// </summary>
public class AvailabilityTests
{
    // Every case is measured against a fixed 10:00–12:00 candidate.
    private static readonly TimeWindow Candidate = Window(10, 12);

    [Theory]
    // Half-open intervals: touching endpoints are not an overlap, so back-to-back bookings fit.
    [InlineData(8, 10, false)]
    [InlineData(12, 14, false)]
    // Clear of the candidate entirely.
    [InlineData(6, 8, false)]
    [InlineData(14, 16, false)]
    // Genuine overlaps.
    [InlineData(10, 12, true)]      // identical
    [InlineData(9, 11, true)]       // straddles the start
    [InlineData(11, 13, true)]      // straddles the end
    [InlineData(9, 13, true)]       // swallows the candidate
    [InlineData(10.5, 11.5, true)]  // sits strictly inside
    [InlineData(11.5, 12.5, true)]  // overlaps by half an hour at the tail
    public void Overlap_is_half_open(double existingStart, double existingEnd, bool expected)
    {
        var existing = Window(existingStart, existingEnd);

        Assert.Equal(expected, existing.Overlaps(Candidate));

        // Overlap is symmetric; a one-sided implementation would pass the line above and still be wrong.
        Assert.Equal(expected, Candidate.Overlaps(existing));
    }

    [Theory]
    [InlineData(10, 10)]   // zero length
    [InlineData(12, 10)]   // inverted
    public void Degenerate_windows_are_not_well_formed(double start, double end) =>
        Assert.False(Window(start, end).IsWellFormed);

    [Fact]
    public void Empty_amenity_has_concurrency_of_one()
    {
        Assert.Equal(1, Availability.PeakConcurrency([], Candidate));
        Assert.True(Availability.FitsCapacity([], Candidate, capacity: 1));
    }

    [Fact]
    public void Capacity_of_one_rejects_any_overlap()
    {
        Reservation[] existing = [Booking(11, 13)];

        Assert.False(Availability.FitsCapacity(existing, Candidate, capacity: 1));
    }

    [Fact]
    public void Capacity_of_one_accepts_a_back_to_back_booking()
    {
        Reservation[] existing = [Booking(8, 10), Booking(12, 14)];

        Assert.True(Availability.FitsCapacity(existing, Candidate, capacity: 1));
    }

    /// <summary>
    /// The case that makes counting overlaps the wrong algorithm. Two existing bookings both
    /// overlap the candidate, but they do not overlap each other, so at no instant are three
    /// reservations held at once. A capacity-2 amenity must accept this.
    /// </summary>
    [Fact]
    public void Two_adjacent_bookings_do_not_saturate_a_capacity_of_two()
    {
        Reservation[] existing = [Booking(9, 11), Booking(11, 13)];

        Assert.Equal(2, Availability.Overlapping(existing, Candidate).Count);
        Assert.Equal(2, Availability.PeakConcurrency(existing, Candidate));
        Assert.True(Availability.FitsCapacity(existing, Candidate, capacity: 2));
    }

    [Fact]
    public void Two_mutually_overlapping_bookings_do_saturate_a_capacity_of_two()
    {
        Reservation[] existing = [Booking(9, 13), Booking(10.5, 11.5)];

        Assert.Equal(3, Availability.PeakConcurrency(existing, Candidate));
        Assert.False(Availability.FitsCapacity(existing, Candidate, capacity: 2));
        Assert.True(Availability.FitsCapacity(existing, Candidate, capacity: 3));
    }

    [Fact]
    public void Peak_is_measured_inside_the_candidate_window_only()
    {
        // Three reservations pile up at 13:00–14:00, well clear of the 10:00–12:00 candidate.
        // Only the 9–11 booking touches it, so peak inside the candidate is 2, not 4.
        Reservation[] existing = [Booking(9, 11), Booking(13, 14), Booking(13, 14), Booking(13, 14)];

        Assert.Equal(2, Availability.PeakConcurrency(existing, Candidate));
    }

    [Fact]
    public void Cancelled_bookings_do_not_occupy_their_slot()
    {
        Reservation[] existing = [Booking(10, 12, ReservationStatus.Canceled)];

        Assert.Empty(Availability.Overlapping(existing, Candidate));
        Assert.True(Availability.FitsCapacity(existing, Candidate, capacity: 1));
    }

    [Fact]
    public void Conflicting_windows_report_the_occupied_ranges_in_order()
    {
        Reservation[] existing = [Booking(11, 13), Booking(9, 11), Booking(20, 21)];

        var conflicts = Availability.ConflictingWindows(existing, Candidate);

        Assert.Equal(new[] { Window(9, 11), Window(11, 13) }, conflicts);
    }
}
