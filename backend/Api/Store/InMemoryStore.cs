using Api.Domain;

namespace Api.Store;

/// <summary>
/// Tiny in-memory store so you don't have to stand up a database. Registered as a singleton.
///
/// The lists are plain List&lt;T&gt; and are NOT thread-safe. Every read-check-write on
/// Reservations must be performed under <see cref="SyncRoot"/> — see the booking handler in
/// Program.cs and the double-booking section of Design.md.
/// </summary>
public sealed class InMemoryStore
{
    /// <summary>Guards the read-check-write sequence that enforces capacity.</summary>
    public object SyncRoot { get; } = new();

    // Two buildings, so tenancy isolation is something you can demo rather than assert.
    public static readonly Guid BuildingA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    public static readonly Guid BuildingB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    public List<Resident> Residents { get; } = new()
    {
        new Resident
        {
            Id = Guid.Parse("11111111-aaaa-0000-0000-000000000001"),
            BuildingId = BuildingA,
            Name = "Ada Lovelace",
            Email = "ada@example.com",
            Phone = "555-0101",
            UnitNumber = "101",
        },
        new Resident
        {
            Id = Guid.Parse("11111111-aaaa-0000-0000-000000000002"),
            BuildingId = BuildingA,
            Name = "Grace Hopper",
            Email = "grace@example.com",
            Phone = "555-0202",
            UnitNumber = "202",
        },
        new Resident
        {
            Id = Guid.Parse("22222222-bbbb-0000-0000-000000000001"),
            BuildingId = BuildingB,
            Name = "Alan Turing",
            Email = "alan@example.com",
            Phone = "555-0012",
            UnitNumber = "12B",
        },
    };

    public List<Amenity> Amenities { get; } = new()
    {
        // Exclusive booking: one party at a time, but a big one.
        new Amenity
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            BuildingId = BuildingA,
            Name = "Party Room",
            Description = "Bookable event space on the ground floor.",
            ReservationCapacity = 1,
            MaxSizePerReservation = 20,
            OpeningHourUtc = 9,
            ClosingHourUtc = 23,
        },
        // Headcount only: any number of bookings, up to 10 bodies in the room.
        new Amenity
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            BuildingId = BuildingA,
            Name = "Gym",
            Description = "Fitness room. Multiple residents at once.",
            TotalCapacity = 10,
            MaxSizePerReservation = 2,
            OpeningHourUtc = 5,
            ClosingHourUtc = 23,
        },
        // Both dimensions at once — the case the capacity model exists for.
        new Amenity
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            BuildingId = BuildingA,
            Name = "Pool Cabanas",
            Description = "Four cabanas, twelve swimmers total.",
            TotalCapacity = 12,
            ReservationCapacity = 4,
            MaxSizePerReservation = 6,
            OpeningHourUtc = 8,
            ClosingHourUtc = 20,
        },
        // Different building: must never appear for a Building A resident.
        new Amenity
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            BuildingId = BuildingB,
            Name = "Guest Parking",
            Description = "Single visitor parking spot.",
            ReservationCapacity = 1,
            MaxSizePerReservation = 1,
            OpeningHourUtc = 0,
            ClosingHourUtc = 24,
        },
    };

    public List<Reservation> Reservations { get; } = new();
}
