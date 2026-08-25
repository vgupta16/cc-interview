using Api.Domain;

namespace Api.Store;

public enum CreateReservationError
{
    None,
    SlotFull,
    ResidentAlreadyBooked,
}

public sealed record CreateReservationResult(Reservation? Reservation, CreateReservationError Error)
{
    public bool Success => Error == CreateReservationError.None;
    public static CreateReservationResult Ok(Reservation r) => new(r, CreateReservationError.None);
    public static CreateReservationResult Fail(CreateReservationError e) => new(null, e);
}

public enum CancelReservationResult
{
    Cancelled,
    NotFound,
    Forbidden,
}

/// <summary>
/// Tiny in-memory store so you don't have to stand up a database.
/// Registered as a singleton.
/// </summary>
public sealed class InMemoryStore
{
    // Single coarse-grained lock guarding all reservation reads/writes. At this scale
    // (in-memory, single process, a handful of amenities) contention is a non-issue, and
    // it makes the "check availability, then write" step atomic without per-amenity
    // lock bookkeeping. See design doc for the production alternative (DB-level
    // constraint instead of an application lock).
    private readonly object _gate = new();
    private readonly List<Reservation> _reservations = new();

    public List<Amenity> Amenities { get; } = new()
    {
        new Amenity
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Party Room",
            Description = "Bookable event space on the ground floor.",
            Capacity = 1,
            MaxBookingMinutes = 240,
        },
        new Amenity
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Gym",
            Description = "Fitness room. Multiple residents at once.",
            Capacity = 2,
            MaxBookingMinutes = 90,
        },
        new Amenity
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Name = "Guest Parking",
            Description = "Single visitor parking spot.",
            Capacity = 1,
            MaxBookingMinutes = 1440,
        },
    };

    public IReadOnlyList<Reservation> GetReservationsForAmenity(Guid amenityId)
    {
        lock (_gate)
        {
            return _reservations
                .Where(r => r.AmenityId == amenityId)
                .OrderBy(r => r.StartUtc)
                .ToList();
        }
    }

    public Reservation? GetReservation(Guid id)
    {
        lock (_gate)
        {
            return _reservations.FirstOrDefault(r => r.Id == id);
        }
    }

    private static bool Overlaps(Reservation r, DateTimeOffset start, DateTimeOffset end) =>
        r.Status == ReservationStatus.Active && r.StartUtc < end && start < r.EndUtc;

    /// <summary>
    /// Atomically checks capacity and inserts. This check-then-write pair is the crux of
    /// double-booking prevention: everything happens inside one lock so no two callers can
    /// both observe "capacity available" and both insert.
    /// </summary>
    public CreateReservationResult TryCreateReservation(Amenity amenity, string residentId, DateTimeOffset start, DateTimeOffset end)
    {
        lock (_gate)
        {
            var overlappingCount = _reservations.Count(r => r.AmenityId == amenity.Id && Overlaps(r, start, end));
            if (overlappingCount >= amenity.Capacity)
            {
                return CreateReservationResult.Fail(CreateReservationError.SlotFull);
            }

            var residentAlreadyBooked = _reservations.Any(r =>
                r.AmenityId == amenity.Id && r.ResidentId == residentId && Overlaps(r, start, end));
            if (residentAlreadyBooked)
            {
                return CreateReservationResult.Fail(CreateReservationError.ResidentAlreadyBooked);
            }

            var reservation = new Reservation
            {
                Id = Guid.NewGuid(),
                AmenityId = amenity.Id,
                ResidentId = residentId,
                StartUtc = start,
                EndUtc = end,
            };
            _reservations.Add(reservation);
            return CreateReservationResult.Ok(reservation);
        }
    }

    public CancelReservationResult CancelReservation(Guid id, string residentId)
    {
        lock (_gate)
        {
            var index = _reservations.FindIndex(r => r.Id == id);
            if (index < 0)
            {
                return CancelReservationResult.NotFound;
            }

            var existing = _reservations[index];
            if (existing.ResidentId != residentId)
            {
                return CancelReservationResult.Forbidden;
            }

            _reservations[index] = existing with { Status = ReservationStatus.Cancelled };
            return CancelReservationResult.Cancelled;
        }
    }
}
