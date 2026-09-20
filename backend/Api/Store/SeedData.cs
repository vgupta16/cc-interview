using Api.Domain;

namespace Api.Store;

/// <summary>
/// Demo data. Two buildings with the same kinds of amenities but different ids, so tenant
/// isolation is visible and testable. Building A keeps the starter's amenity ids.
/// </summary>
public static class SeedData
{
    public static readonly Guid BuildingA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid BuildingB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public static readonly Guid PartyRoomA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid GymA = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid GuestParkingA = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly Guid PartyRoomB = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid GymB = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid GuestParkingB = Guid.Parse("66666666-6666-6666-6666-666666666666");

    public static IReadOnlyList<Building> Buildings { get; } = new[]
    {
        new Building { Id = BuildingA, Name = "Maple Court", TimeZone = "America/Toronto" },
        new Building { Id = BuildingB, Name = "Harbour Tower", TimeZone = "America/Vancouver" },
    };

    public static IReadOnlyList<Amenity> Amenities { get; } = new[]
    {
        PartyRoom(PartyRoomA, BuildingA),
        Gym(GymA, BuildingA),
        GuestParking(GuestParkingA, BuildingA),
        PartyRoom(PartyRoomB, BuildingB),
        Gym(GymB, BuildingB),
        GuestParking(GuestParkingB, BuildingB),
    };

    private static Amenity PartyRoom(Guid id, Guid buildingId) => new()
    {
        Id = id,
        BuildingId = buildingId,
        Name = "Party Room",
        Description = "Bookable event space on the ground floor.",
        Capacity = 1,
        Policy = new AmenityPolicy { MaxBookingMinutes = 240 },
    };

    private static Amenity Gym(Guid id, Guid buildingId) => new()
    {
        Id = id,
        BuildingId = buildingId,
        Name = "Gym",
        Description = "Fitness room. Multiple residents at once.",
        Capacity = 2,
        Policy = new AmenityPolicy { MaxBookingMinutes = 90 },
    };

    private static Amenity GuestParking(Guid id, Guid buildingId) => new()
    {
        Id = id,
        BuildingId = buildingId,
        Name = "Guest Parking",
        Description = "Single visitor parking spot.",
        Capacity = 1,
        Policy = new AmenityPolicy { MaxBookingMinutes = 1440 },
    };
}
