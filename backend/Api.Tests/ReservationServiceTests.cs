using Api.Domain;
using Api.Reservations;
using Api.Store;
using static Api.Tests.Fixtures;

namespace Api.Tests;

/// <summary>
/// Booking behaviour end to end through the service, against the seeded store. Cross-building
/// isolation and concurrency have their own suites.
/// </summary>
public class ReservationServiceTests
{
    private static readonly Guid PartyRoom = SeedData.Ids.HarborviewPartyRoomMain;  // capacity 1, max 240 min
    private static readonly Guid Gym = SeedData.Ids.HarborviewGym;                  // capacity 2, max 90 min

    // --- Creating ---------------------------------------------------------------------------

    [Fact]
    public void A_free_slot_can_be_booked()
    {
        var (service, store, _) = NewService();

        var result = service.Create(Riley(store), PartyRoom, Window(16, 18));

        Assert.True(result.Succeeded);
        var reservation = result.Value!;
        Assert.Equal(PartyRoom, reservation.AmenityId);
        Assert.Equal(SeedData.Ids.Riley, reservation.ResidentId);
        Assert.Equal(SeedData.Ids.Harborview, reservation.BuildingId);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
    }

    [Fact]
    public void An_overlapping_slot_is_refused_on_an_exclusive_amenity()
    {
        var (service, store, _) = NewService();
        service.Create(Dana(store), PartyRoom, Window(16, 18));

        // The scenario from the brief: 17:30 falls inside an existing 16:00–18:00 booking.
        var result = service.Create(Riley(store), PartyRoom, Window(17.5, 19));

        Assert.False(result.Succeeded);
        Assert.Equal(ReservationErrorCode.SlotUnavailable, result.Error!.Code);
        Assert.Equal(new[] { Window(16, 18) }, result.Error.Conflicts);
    }

    [Fact]
    public void Back_to_back_bookings_are_allowed()
    {
        var (service, store, _) = NewService();
        service.Create(Dana(store), PartyRoom, Window(16, 18));

        var result = service.Create(Riley(store), PartyRoom, Window(18, 20));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void A_shared_amenity_accepts_bookings_up_to_its_capacity()
    {
        var (service, store, _) = NewService();
        var dana = Dana(store);

        // Adjacent rather than overlapping, so the candidate below never sees three at once.
        Assert.True(service.Create(dana, Gym, Window(9, 10)).Succeeded);
        Assert.True(service.Create(dana, Gym, Window(10, 11)).Succeeded);

        // Overlaps both, but peak concurrency is only 2. Counting overlaps would wrongly refuse this.
        var result = service.Create(Riley(store), Gym, Window(9.5, 10.5));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void A_shared_amenity_refuses_a_booking_past_its_capacity()
    {
        var (service, store, _) = NewService();
        var dana = Dana(store);
        service.Create(dana, Gym, Window(9, 10));
        service.Create(dana, Gym, Window(9.25, 10.25));

        // At 09:30 both existing bookings are held, so this would make three on a capacity of two.
        var result = service.Create(Riley(store), Gym, Window(9.5, 10.5));

        Assert.False(result.Succeeded);
        Assert.Equal(ReservationErrorCode.SlotUnavailable, result.Error!.Code);
    }

    [Fact]
    public void An_unknown_amenity_is_not_found()
    {
        var (service, store, _) = NewService();

        var result = service.Create(Riley(store), Guid.NewGuid(), Window(16, 18));

        Assert.Equal(ReservationErrorCode.AmenityNotFound, result.Error!.Code);
    }

    // --- Rules ------------------------------------------------------------------------------

    [Fact]
    public void A_booking_longer_than_the_amenity_allows_is_refused()
    {
        var (service, store, _) = NewService();

        // The party room caps bookings at 240 minutes.
        var result = service.Create(Riley(store), PartyRoom, Window(12, 17));

        Assert.Equal(ReservationErrorCode.ExceedsMaxBookingLength, result.Error!.Code);
    }

    [Fact]
    public void A_booking_of_exactly_the_maximum_length_is_allowed()
    {
        var (service, store, _) = NewService();

        var result = service.Create(Riley(store), PartyRoom, Window(12, 16));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(18, 16)]
    public void A_degenerate_window_is_refused(double start, double end)
    {
        var (service, store, _) = NewService();

        var result = service.Create(Riley(store), PartyRoom, Window(start, end));

        Assert.Equal(ReservationErrorCode.InvalidInterval, result.Error!.Code);
    }

    [Fact]
    public void A_booking_in_the_past_is_refused()
    {
        var (service, store, _) = NewService();  // clock sits at 08:00

        var result = service.Create(Riley(store), PartyRoom, Window(6, 7));

        Assert.Equal(ReservationErrorCode.StartsInPast, result.Error!.Code);
    }

    [Fact]
    public void A_booking_starting_within_the_clock_skew_grace_is_allowed()
    {
        var (service, store, clock) = NewService();

        // Thirty seconds ago: a client whose clock runs slightly fast shouldn't be rejected.
        var start = clock.Now.AddSeconds(-30);
        var result = service.Create(Riley(store), PartyRoom, new TimeWindow(start, start.AddHours(1)));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void A_booking_beyond_the_horizon_is_refused()
    {
        var (service, store, _) = NewService();
        var start = At(16).AddDays(ReservationPolicy.BookingHorizon.TotalDays + 1);

        var result = service.Create(Riley(store), PartyRoom, new TimeWindow(start, start.AddHours(2)));

        Assert.Equal(ReservationErrorCode.BeyondBookingHorizon, result.Error!.Code);
    }

    // --- Cancelling -------------------------------------------------------------------------

    [Fact]
    public void Cancelling_releases_the_slot_for_someone_else()
    {
        var (service, store, _) = NewService();
        var booked = service.Create(Dana(store), PartyRoom, Window(16, 18)).Value!;

        var canceled = service.Cancel(Dana(store), booked.Id);

        Assert.True(canceled.Succeeded);
        Assert.Equal(ReservationStatus.Canceled, canceled.Value!.Status);
        Assert.NotNull(canceled.Value.CanceledAtUtc);
        Assert.True(service.Create(Riley(store), PartyRoom, Window(16, 18)).Succeeded);
    }

    [Fact]
    public void Cancelling_someone_elses_booking_is_indistinguishable_from_a_missing_one()
    {
        var (service, store, _) = NewService();
        var danas = service.Create(Dana(store), PartyRoom, Window(16, 18)).Value!;

        var byRiley = service.Cancel(Riley(store), danas.Id);
        var byNobody = service.Cancel(Riley(store), Guid.NewGuid());

        Assert.Equal(ReservationErrorCode.ReservationNotFound, byRiley.Error!.Code);
        Assert.Equal(ReservationErrorCode.ReservationNotFound, byNobody.Error!.Code);

        // And Dana's booking is untouched.
        Assert.Equal(ReservationStatus.Active, service.ActiveReservationsFor(Dana(store)).Single().Status);
    }

    [Fact]
    public void Cancelling_twice_is_idempotent()
    {
        var (service, store, _) = NewService();
        var booked = service.Create(Dana(store), PartyRoom, Window(16, 18)).Value!;
        var first = service.Cancel(Dana(store), booked.Id);

        var second = service.Cancel(Dana(store), booked.Id);

        Assert.True(second.Succeeded);
        Assert.Equal(ReservationStatus.Canceled, second.Value!.Status);
        Assert.Equal(first.Value!.CanceledAtUtc, second.Value.CanceledAtUtc);
    }

    // --- Reading ----------------------------------------------------------------------------

    [Fact]
    public void A_schedule_covers_only_bookings_overlapping_the_requested_range()
    {
        var (service, store, _) = NewService();
        var dana = Dana(store);
        service.Create(dana, PartyRoom, Window(9, 11));
        service.Create(dana, PartyRoom, Window(20, 22));

        var schedule = service.GetSchedule(Riley(store), PartyRoom, At(8), At(12)).Value!;

        Assert.Equal(Window(9, 11), Assert.Single(schedule.Active).Window);
    }

    [Fact]
    public void A_schedule_omits_cancelled_bookings()
    {
        var (service, store, _) = NewService();
        var booked = service.Create(Dana(store), PartyRoom, Window(9, 11)).Value!;
        service.Cancel(Dana(store), booked.Id);

        var schedule = service.GetSchedule(Riley(store), PartyRoom, At(8), At(12)).Value!;

        Assert.Empty(schedule.Active);
    }

    [Fact]
    public void A_backwards_schedule_range_is_refused()
    {
        var (service, store, _) = NewService();

        var result = service.GetSchedule(Riley(store), PartyRoom, At(12), At(8));

        Assert.Equal(ReservationErrorCode.InvalidInterval, result.Error!.Code);
    }

    [Fact]
    public void An_oversized_schedule_range_is_clamped_rather_than_refused()
    {
        var (service, store, _) = NewService();

        var result = service.GetSchedule(Riley(store), PartyRoom, At(0), At(0).AddDays(365));

        Assert.True(result.Succeeded);
        Assert.Equal(ReservationPolicy.MaxScheduleWindow, result.Value!.Window.Duration);
    }

    [Fact]
    public void My_reservations_span_amenities_and_exclude_cancelled_ones()
    {
        var (service, store, _) = NewService();
        var riley = Riley(store);
        service.Create(riley, PartyRoom, Window(16, 18));
        service.Create(riley, Gym, Window(9, 10));
        var dropped = service.Create(riley, Gym, Window(11, 12)).Value!;
        service.Cancel(riley, dropped.Id);
        service.Create(Dana(store), PartyRoom, Window(20, 22));

        var mine = service.ActiveReservationsFor(riley);

        Assert.Equal(new[] { Gym, PartyRoom }, mine.Select(r => r.AmenityId));
        Assert.All(mine, r => Assert.Equal(SeedData.Ids.Riley, r.ResidentId));
    }
}
