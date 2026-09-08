using Api.Domain;

namespace Api.Store;

public enum CreateReservationOutcome
{
    Created,
    Conflict,
}

public sealed record CreateReservationResult(CreateReservationOutcome Outcome, Reservation? Reservation);

public enum CancelReservationOutcome
{
    Cancelled,
    NotFound,
    NotOwner,
}

/// <summary>
/// Tiny in-memory store so you don't have to stand up a database. Registered as a singleton,
/// which means every request shares this one instance across thread-pool threads.
///
/// Buildings, amenities and memberships are seeded once and never mutated, so they are safe to
/// read without synchronization. Reservations are mutable, so <see cref="_reservations"/> is
/// private and every access to it — reads included — goes through a method holding
/// <see cref="_gate"/>. It is deliberately not a public List: a public mutable collection would
/// let a caller Add() straight past the lock and silently defeat the capacity guarantee.
/// </summary>
public sealed class InMemoryStore
{
    private static readonly Guid OakStreetId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MainStreetId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public List<Building> Buildings { get; } = new()
    {
        new Building { Id = OakStreetId, Name = "Oak Street" },
        new Building { Id = MainStreetId, Name = "Main Street" },
    };

    public List<Amenity> Amenities { get; } = new()
    {
        new Amenity
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            BuildingId = OakStreetId,
            Name = "Party Room",
            Description = "Bookable event space on the ground floor.",
            Capacity = 1,
            MaxBookingMinutes = 240,
        },
        new Amenity
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            BuildingId = OakStreetId,
            Name = "Gym",
            Description = "Fitness room. Multiple residents at once.",
            Capacity = 2,
            MaxBookingMinutes = 90,
        },
        new Amenity
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            BuildingId = MainStreetId,
            Name = "Guest Parking",
            Description = "Single visitor parking spot.",
            Capacity = 1,
            MaxBookingMinutes = 1440,
        },
        // Same name as Oak Street's gym, different building, separate calendar — booking one
        // has no effect on the other.
        new Amenity
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            BuildingId = MainStreetId,
            Name = "Gym",
            Description = "Fitness room with a squat rack.",
            Capacity = 3,
            MaxBookingMinutes = 60,
        },
    };

    /// <summary>
    /// Seeded stand-in for what an auth system would tell us. "resident-jeremy" belongs to both
    /// buildings (so the multi-building flow has something to show); "resident-dana" belongs to
    /// only one (so a cross-building attempt can be demonstrated returning 403).
    /// </summary>
    public List<ResidentBuilding> ResidentBuildings { get; } = new()
    {
        new ResidentBuilding { ResidentId = "resident-jeremy", BuildingId = OakStreetId },
        new ResidentBuilding { ResidentId = "resident-jeremy", BuildingId = MainStreetId },
        new ResidentBuilding { ResidentId = "resident-dana", BuildingId = MainStreetId },
    };

    private readonly object _gate = new();
    private readonly List<Reservation> _reservations = new();

    // --- Reads over immutable seed data (no lock needed) ----------------------------------

    public Amenity? FindAmenity(Guid amenityId) =>
        Amenities.FirstOrDefault(a => a.Id == amenityId);

    public IReadOnlyList<Building> GetBuildingsForResident(string residentId)
    {
        var ids = ResidentBuildings
            .Where(rb => rb.ResidentId == residentId)
            .Select(rb => rb.BuildingId)
            .ToHashSet();

        return Buildings.Where(b => ids.Contains(b.Id)).ToList();
    }

    public IReadOnlyList<Amenity> GetAmenitiesForBuilding(Guid buildingId) =>
        Amenities.Where(a => a.BuildingId == buildingId).ToList();

    public bool ResidentBelongsToBuilding(string residentId, Guid buildingId) =>
        ResidentBuildings.Any(rb => rb.ResidentId == residentId && rb.BuildingId == buildingId);

    // --- Reservation access (all of it under the lock) -------------------------------------

    public IReadOnlyList<Reservation> GetActiveReservations(Guid amenityId)
    {
        lock (_gate)
        {
            // ToList() inside the lock: hand back a snapshot, never the live list. Enumerating
            // the live list outside the lock could throw if another thread adds concurrently.
            return _reservations
                .Where(r => r.AmenityId == amenityId && r.CancelledUtc is null)
                .OrderBy(r => r.StartUtc)
                .ToList();
        }
    }

    public Reservation? FindReservation(Guid reservationId)
    {
        lock (_gate)
        {
            return _reservations.FirstOrDefault(r => r.Id == reservationId);
        }
    }

    /// <summary>
    /// The whole double-booking guarantee lives here. The capacity check and the insert are one
    /// atomic operation: if they were split — even across two methods on this class, let alone
    /// across a service layer — two concurrent callers could both observe a free slot and both
    /// insert, exceeding capacity. Nothing else may add to <see cref="_reservations"/>.
    ///
    /// Callers are expected to have already validated the request (see ReservationService);
    /// this method's single job is to make check-then-insert indivisible.
    /// </summary>
    public CreateReservationResult TryCreateReservation(
        Amenity amenity,
        string residentId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        lock (_gate)
        {
            // Half-open intervals [start, end): A and B overlap iff A.Start < B.End && B.Start < A.End.
            // Back-to-back bookings share an instant but do not overlap.
            var overlapping = _reservations.Count(r =>
                r.AmenityId == amenity.Id
                && r.CancelledUtc is null
                && r.StartUtc < endUtc
                && startUtc < r.EndUtc);

            if (overlapping >= amenity.Capacity)
            {
                return new CreateReservationResult(CreateReservationOutcome.Conflict, null);
            }

            var reservation = new Reservation
            {
                Id = Guid.NewGuid(),
                AmenityId = amenity.Id,
                ResidentId = residentId,
                StartUtc = startUtc,
                EndUtc = endUtc,
            };

            _reservations.Add(reservation);
            return new CreateReservationResult(CreateReservationOutcome.Created, reservation);
        }
    }

    /// <summary>
    /// Soft-cancels a reservation the caller owns. Replaces the record rather than mutating it,
    /// so <see cref="Reservation"/> stays immutable and anyone holding an earlier reference is
    /// not surprised by a value changing underneath them.
    /// </summary>
    public CancelReservationOutcome CancelReservation(Guid reservationId, string residentId)
    {
        lock (_gate)
        {
            var index = _reservations.FindIndex(r => r.Id == reservationId && r.CancelledUtc is null);
            if (index < 0)
            {
                return CancelReservationOutcome.NotFound;
            }

            if (_reservations[index].ResidentId != residentId)
            {
                return CancelReservationOutcome.NotOwner;
            }

            _reservations[index] = _reservations[index] with { CancelledUtc = DateTimeOffset.UtcNow };
            return CancelReservationOutcome.Cancelled;
        }
    }
}
