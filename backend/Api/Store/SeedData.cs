using Api.Domain;

namespace Api.Store;

/// <summary>
/// Fixture data for the in-memory store. Deliberately shaped to make the interesting cases
/// demonstrable in the UI rather than only in tests:
///
///   - two buildings that each have a party room, so building isolation is visible;
///   - two party rooms in the same building on divergent rules, so the instance-over-type
///     modeling decision is visible;
///   - gyms with different capacities per building, so per-building rule divergence is visible;
///   - guest parking with a 24-hour max, the case that breaks naive opening hours.
/// </summary>
public static class SeedData
{
    /// <summary>Stable ids so tests and the dev-only resident picker can refer to fixtures by name.</summary>
    public static class Ids
    {
        public static readonly Guid Harborview = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        public static readonly Guid MapleCourt = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        public static readonly Guid HarborviewPartyRoomMain = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid HarborviewGym = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public static readonly Guid HarborviewGuestParking = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public static readonly Guid HarborviewPartyRoomEast = Guid.Parse("44444444-4444-4444-4444-444444444444");

        public static readonly Guid MapleCourtPartyRoom = Guid.Parse("55555555-5555-5555-5555-555555555555");
        public static readonly Guid MapleCourtGym = Guid.Parse("66666666-6666-6666-6666-666666666666");

        public static readonly Guid Riley = Guid.Parse("a1a1a1a1-1111-1111-1111-111111111111");
        public static readonly Guid Dana = Guid.Parse("a2a2a2a2-2222-2222-2222-222222222222");
        public static readonly Guid Sam = Guid.Parse("b1b1b1b1-1111-1111-1111-111111111111");
    }

    public static IReadOnlyList<Building> Buildings { get; } = new List<Building>
    {
        new() { Id = Ids.Harborview, Name = "Harborview Tower", TimeZoneId = "America/New_York" },
        new() { Id = Ids.MapleCourt, Name = "Maple Court", TimeZoneId = "America/Chicago" },
    };

    public static IReadOnlyList<Resident> Residents { get; } = new List<Resident>
    {
        new() { Id = Ids.Riley, BuildingId = Ids.Harborview, DisplayName = "Riley Chen", UnitNumber = "502" },
        new() { Id = Ids.Dana, BuildingId = Ids.Harborview, DisplayName = "Dana Whitfield", UnitNumber = "1104" },
        new() { Id = Ids.Sam, BuildingId = Ids.MapleCourt, DisplayName = "Sam Okafor", UnitNumber = "205" },
    };

    public static IReadOnlyList<Amenity> Amenities { get; } = new List<Amenity>
    {
        new()
        {
            Id = Ids.HarborviewPartyRoomMain,
            BuildingId = Ids.Harborview,
            Name = "Party Room (Main)",
            Kind = AmenityKind.PartyRoom,
            Description = "Bookable event space on the ground floor.",
            Capacity = 1,
            MaxBookingMinutes = 240,
        },
        new()
        {
            // Same building, same kind, shorter maximum booking. Exists to prove that rules live
            // on the instance: nothing here is shared with Party Room (Main).
            Id = Ids.HarborviewPartyRoomEast,
            BuildingId = Ids.Harborview,
            Name = "Party Room (East)",
            Kind = AmenityKind.PartyRoom,
            Description = "Smaller east-wing event space.",
            Capacity = 1,
            MaxBookingMinutes = 180,
        },
        new()
        {
            Id = Ids.HarborviewGym,
            BuildingId = Ids.Harborview,
            Name = "Gym",
            Kind = AmenityKind.Gym,
            Description = "Fitness room. Two reservations may overlap.",
            Capacity = 2,
            MaxBookingMinutes = 90,
        },
        new()
        {
            Id = Ids.HarborviewGuestParking,
            BuildingId = Ids.Harborview,
            Name = "Guest Parking",
            Kind = AmenityKind.GuestParking,
            Description = "Single visitor parking spot. Overnight bookings are normal here.",
            Capacity = 1,
            MaxBookingMinutes = 1440,
        },
        new()
        {
            Id = Ids.MapleCourtPartyRoom,
            BuildingId = Ids.MapleCourt,
            Name = "Party Room",
            Kind = AmenityKind.PartyRoom,
            Description = "Shared lounge. Unaffected by Harborview's bookings.",
            Capacity = 1,
            MaxBookingMinutes = 240,
        },
        new()
        {
            Id = Ids.MapleCourtGym,
            BuildingId = Ids.MapleCourt,
            Name = "Gym",
            Kind = AmenityKind.Gym,
            Description = "Larger fitness room; three reservations may overlap.",
            Capacity = 3,
            MaxBookingMinutes = 60,
        },
    };
}
