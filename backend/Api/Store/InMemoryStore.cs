using Api.Domain;

namespace Api.Store;

/// <summary>
/// In-memory store for seeded buildings, residents, and amenities, and for reservations.
/// Registered as a singleton. Reads and writes are scoped to a building, and reservations are
/// checked for capacity and inserted under one store-wide lock.
/// </summary>
public sealed class InMemoryStore
{
    /// <summary>Reservations start and end on 30-minute boundaries (A8).</summary>
    public static readonly TimeSpan SlotLength = TimeSpan.FromMinutes(30);

    private static readonly Guid BuildingAId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid BuildingBId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid BuildingCId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly IReadOnlyList<Building> _buildings =
    [
        new Building { Id = BuildingAId, Name = "Building A" },
        new Building { Id = BuildingBId, Name = "Building B" },
        new Building { Id = BuildingCId, Name = "Building C" },
    ];

    private readonly IReadOnlyList<Resident> _residents =
    [
        new Resident { Id = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1"), BuildingId = BuildingAId },
        new Resident { Id = Guid.Parse("a2a2a2a2-a2a2-a2a2-a2a2-a2a2a2a2a2a2"), BuildingId = BuildingAId },
        new Resident { Id = Guid.Parse("b1b1b1b1-b1b1-b1b1-b1b1-b1b1b1b1b1b1"), BuildingId = BuildingBId },
        new Resident { Id = Guid.Parse("b2b2b2b2-b2b2-b2b2-b2b2-b2b2b2b2b2b2"), BuildingId = BuildingBId },
        new Resident { Id = Guid.Parse("c1c1c1c1-c1c1-c1c1-c1c1-c1c1c1c1c1c1"), BuildingId = BuildingCId },
    ];

    private readonly IReadOnlyList<Amenity> _amenities =
    [
        new Amenity
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            BuildingId = BuildingAId,
            Name = "Party Room, Building A",
            Description = "A bookable event space on the ground floor of Building A.",
            Capacity = 1,
            MaxBookingMinutes = 240,
        },
        new Amenity
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            BuildingId = BuildingAId,
            Name = "Gym, Building A",
            Description = "A fitness center in Building A, where multiple residents can exercise at once.",
            Capacity = 2,
            MaxBookingMinutes = 90,
        },
        new Amenity
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            BuildingId = BuildingAId,
            Name = "Guest Parking, Building A",
            Description = "A single visitor parking spot for Building A guests.",
            Capacity = 1,
            MaxBookingMinutes = 1440,
        },
        new Amenity
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            BuildingId = BuildingBId,
            Name = "Party Room, Building B",
            Description = "A bookable event space on the ground floor of Building B.",
            Capacity = 1,
            MaxBookingMinutes = 240,
        },
        new Amenity
        {
            Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            BuildingId = BuildingBId,
            Name = "Guest Parking, Building B",
            Description = "A single visitor parking spot for Building B guests.",
            Capacity = 1,
            MaxBookingMinutes = 1440,
        },
    ];

    private readonly List<Reservation> _reservations = new();
    private readonly Lock _lock = new();

    /// <summary>Checks that every seeded resident and amenity belongs to a seeded building.</summary>
    public InMemoryStore()
    {
        var buildingIds = _buildings.Select(building => building.Id).ToHashSet();
        var residentReferencesInvalidBuilding = _residents.Any(resident => !buildingIds.Contains(resident.BuildingId));
        var amenityReferencesInvalidBuilding = _amenities.Any(amenity => !buildingIds.Contains(amenity.BuildingId));

        if (residentReferencesInvalidBuilding || amenityReferencesInvalidBuilding)
        {
            throw new InvalidOperationException("Seed data references a building that doesn't exist.");
        }
    }

    /// <summary>Finds a seeded resident, or returns null for an unknown ID.</summary>
    public Resident? FindResident(Guid residentId) => _residents.FirstOrDefault(resident => resident.Id == residentId);

    /// <summary>Returns the amenities in one building.</summary>
    public IReadOnlyList<Amenity> GetAmenities(Guid buildingId) =>
        _amenities.Where(amenity => amenity.BuildingId == buildingId).ToList();

    /// <summary>
    /// Finds an amenity by ID within a building. An amenity in another building returns null,
    /// the same as an unknown ID.
    /// </summary>
    public Amenity? FindAmenity(Guid buildingId, Guid amenityId) =>
        _amenities.FirstOrDefault(amenity => amenity.BuildingId == buildingId && amenity.Id == amenityId);

    /// <summary>
    /// Copies an amenity's reservations under the lock, so availability can be calculated from
    /// the copy after the lock is released.
    /// </summary>
    public IReadOnlyList<Reservation> SnapshotReservations(Guid buildingId, Guid amenityId)
    {
        lock (_lock)
        {
            return _reservations
                .Where(reservation => reservation.BuildingId == buildingId && reservation.AmenityId == amenityId)
                .ToList();
        }
    }

    /// <summary>
    /// Checks capacity across every slot of the window and inserts the reservation, as one atomic
    /// step. The caller has already validated the window's alignment and length.
    /// </summary>
    public CreateResult TryCreate(Resident resident, Amenity amenity, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (resident.BuildingId != amenity.BuildingId)
        {
            throw new ArgumentException("The resident and the amenity must belong to the same building.");
        }

        lock (_lock)
        {
            // start may have passed while this request waited for the lock
            if (startUtc <= DateTimeOffset.UtcNow)
            {
                return new CreateResult { Outcome = CreateOutcome.StartPassed };
            }

            if (RemainingCapacity(amenity, _reservations, startUtc, endUtc) < 1)
            {
                return new CreateResult { Outcome = CreateOutcome.Full };
            }

            var reservation = new Reservation
            {
                Id = Guid.NewGuid(),
                BuildingId = amenity.BuildingId,
                AmenityId = amenity.Id,
                ResidentId = resident.Id,
                StartUtc = startUtc,
                EndUtc = endUtc,
            };

            _reservations.Add(reservation);

            return new CreateResult { Outcome = CreateOutcome.Created, Reservation = reservation };
        }
    }

    /// <summary>
    /// Returns a resident's reservations that haven't ended (A19), ordered by start time, then ID.
    /// </summary>
    public IReadOnlyList<Reservation> GetMyReservations(Guid buildingId, Guid residentId)
    {
        var now = DateTimeOffset.UtcNow;

        List<Reservation> mine;

        lock (_lock)
        {
            mine = _reservations
                .Where(reservation =>
                    reservation.BuildingId == buildingId
                    && reservation.ResidentId == residentId
                    && reservation.EndUtc > now
                )
                .ToList();
        }

        return mine.OrderBy(reservation => reservation.StartUtc).ThenBy(reservation => reservation.Id).ToList();
    }

    /// <summary>
    /// The fewest free spots across every 30-minute slot in <c>[startUtc, endUtc)</c> (A18). A window
    /// can be reserved only if this is at least 1. Only reservations for this amenity are counted.
    /// </summary>
    public static int RemainingCapacity(
        Amenity amenity,
        IEnumerable<Reservation> reservations,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endUtc, startUtc);

        var existing = reservations
            .Where(reservation => reservation.BuildingId == amenity.BuildingId && reservation.AmenityId == amenity.Id)
            .ToList();

        var remaining = amenity.Capacity;

        for (var slotStart = startUtc; slotStart < endUtc; slotStart += SlotLength)
        {
            var slotEnd = slotStart + SlotLength;

            // strict comparisons: a reservation ending at slotStart doesn't occupy this slot (A11).
            var occupied = existing.Count(reservation =>
                reservation.StartUtc < slotEnd && reservation.EndUtc > slotStart
            );

            remaining = Math.Min(remaining, amenity.Capacity - occupied);
        }

        return remaining;
    }
}
