using Api.Reservations;
using Api.Store;
using static Api.Tests.Fixtures;

namespace Api.Tests;

/// <summary>
/// Each building's reservations are independent, and a resident cannot see or touch another
/// building's. Both halves matter: isolation that only prevents interference, while still
/// leaking existence, is not isolation.
/// </summary>
public class BuildingIsolationTests
{
    private static readonly Guid HarborviewPartyRoom = SeedData.Ids.HarborviewPartyRoomMain;
    private static readonly Guid HarborviewPartyRoomEast = SeedData.Ids.HarborviewPartyRoomEast;
    private static readonly Guid MapleCourtPartyRoom = SeedData.Ids.MapleCourtPartyRoom;

    [Fact]
    public void The_same_window_is_bookable_in_both_buildings()
    {
        var (service, store, _) = NewService();
        var window = Window(16, 18);

        // The scenario from the brief: Building A's party room being taken 4–6 must not stop
        // Building B's party room being taken 4–6 on the same day.
        Assert.True(service.Create(Riley(store), HarborviewPartyRoom, window).Succeeded);
        Assert.True(service.Create(Sam(store), MapleCourtPartyRoom, window).Succeeded);
    }

    [Fact]
    public void Booking_another_buildings_amenity_looks_exactly_like_booking_a_missing_one()
    {
        var (service, store, _) = NewService();

        var crossTenant = service.Create(Riley(store), MapleCourtPartyRoom, Window(16, 18));
        var nonexistent = service.Create(Riley(store), Guid.NewGuid(), Window(16, 18));

        // Not "forbidden" — a 403 would confirm Maple Court's party room exists.
        Assert.Equal(ReservationErrorCode.AmenityNotFound, crossTenant.Error!.Code);
        Assert.Equal(nonexistent.Error!.Code, crossTenant.Error.Code);
        Assert.Equal(nonexistent.Error.Detail, crossTenant.Error.Detail);
    }

    [Fact]
    public void Another_buildings_schedule_cannot_be_read()
    {
        var (service, store, _) = NewService();
        service.Create(Sam(store), MapleCourtPartyRoom, Window(16, 18));

        var result = service.GetSchedule(Riley(store), MapleCourtPartyRoom, At(0), At(24));

        Assert.Equal(ReservationErrorCode.AmenityNotFound, result.Error!.Code);
    }

    [Fact]
    public void Another_buildings_bookings_never_appear_in_my_own_list()
    {
        var (service, store, _) = NewService();
        service.Create(Sam(store), MapleCourtPartyRoom, Window(16, 18));
        service.Create(Riley(store), HarborviewPartyRoom, Window(16, 18));

        var rileys = service.ActiveReservationsFor(Riley(store));
        var sams = service.ActiveReservationsFor(Sam(store));

        Assert.Equal(HarborviewPartyRoom, Assert.Single(rileys).AmenityId);
        Assert.Equal(MapleCourtPartyRoom, Assert.Single(sams).AmenityId);
    }

    [Fact]
    public void Another_buildings_booking_cannot_be_cancelled()
    {
        var (service, store, _) = NewService();
        var sams = service.Create(Sam(store), MapleCourtPartyRoom, Window(16, 18)).Value!;

        var result = service.Cancel(Riley(store), sams.Id);

        Assert.Equal(ReservationErrorCode.ReservationNotFound, result.Error!.Code);
        Assert.True(service.ActiveReservationsFor(Sam(store)).Single().IsActive);
    }

    [Fact]
    public void Amenity_listings_are_scoped_to_the_residents_building()
    {
        var store = new InMemoryStore();

        var harborview = store.AmenitiesInBuilding(SeedData.Ids.Harborview);
        var mapleCourt = store.AmenitiesInBuilding(SeedData.Ids.MapleCourt);

        Assert.All(harborview, a => Assert.Equal(SeedData.Ids.Harborview, a.BuildingId));
        Assert.All(mapleCourt, a => Assert.Equal(SeedData.Ids.MapleCourt, a.BuildingId));
        Assert.Equal(store.Amenities.Count, harborview.Count + mapleCourt.Count);
    }

    // --- Several amenities of one kind, in one building ---------------------------------------

    [Fact]
    public void Two_party_rooms_in_one_building_are_booked_independently()
    {
        var (service, store, _) = NewService();
        var window = Window(16, 18);

        // Modeling amenities as instances rather than types is what makes this work with no
        // extra machinery: they are simply two rows that happen to share a Kind.
        Assert.True(service.Create(Riley(store), HarborviewPartyRoom, window).Succeeded);
        Assert.True(service.Create(Dana(store), HarborviewPartyRoomEast, window).Succeeded);
    }

    [Fact]
    public void Amenities_of_the_same_kind_can_carry_different_rules()
    {
        var (service, store, _) = NewService();
        var fourHours = Window(12, 16);

        // Party Room (Main) allows 240 minutes; Party Room (East) allows 180. Rules live on the
        // instance, so the shorter cap applies to one and not the other.
        Assert.True(service.Create(Riley(store), HarborviewPartyRoom, fourHours).Succeeded);

        var tooLong = service.Create(Dana(store), HarborviewPartyRoomEast, fourHours);
        Assert.Equal(ReservationErrorCode.ExceedsMaxBookingLength, tooLong.Error!.Code);
    }

    [Fact]
    public void Rules_can_differ_between_buildings_for_the_same_kind()
    {
        var (service, store, _) = NewService();

        // Harborview's gym holds two at once; Maple Court's holds three.
        Assert.True(service.Create(Riley(store), SeedData.Ids.HarborviewGym, Window(9, 10)).Succeeded);
        Assert.True(service.Create(Dana(store), SeedData.Ids.HarborviewGym, Window(9, 10)).Succeeded);
        Assert.Equal(
            ReservationErrorCode.SlotUnavailable,
            service.Create(Riley(store), SeedData.Ids.HarborviewGym, Window(9, 10)).Error!.Code);

        var mapleCourtGym = SeedData.Ids.MapleCourtGym;
        Assert.True(service.Create(Sam(store), mapleCourtGym, Window(9, 9.5)).Succeeded);
        Assert.True(service.Create(Sam(store), mapleCourtGym, Window(9, 9.5)).Succeeded);
        Assert.True(service.Create(Sam(store), mapleCourtGym, Window(9, 9.5)).Succeeded);
        Assert.Equal(
            ReservationErrorCode.SlotUnavailable,
            service.Create(Sam(store), mapleCourtGym, Window(9, 9.5)).Error!.Code);
    }
}
