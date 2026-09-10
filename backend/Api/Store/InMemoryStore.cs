using System.Collections.Concurrent;
using Api.Domain;

namespace Api.Store;

/// <summary>
/// In-memory persistence, registered as a singleton. Buildings, residents and amenities are
/// immutable fixtures; reservations are mutable and therefore the only thing that needs
/// concurrency control.
///
/// <para>
/// <b>Concurrency.</b> ASP.NET Core serves requests on a multi-threaded thread pool against this
/// single instance, so two simultaneous booking requests genuinely interleave. The failure mode is
/// a time-of-check-to-time-of-use race: both requests read the reservations, both independently
/// decide the slot is free, and both insert. Neither request is wrong on its own — the invariant
/// is broken by the interleaving.
/// </para>
/// <para>
/// The fix is to make check-and-insert one indivisible step, serialized per amenity via
/// <see cref="WithAmenityLock{T}"/>. Reservations are <i>partitioned</i> by amenity id and the
/// partition key is the same as the lock key. That matters: a single shared list guarded by
/// per-amenity locks would still be a data race, because amenity X's read could overlap amenity
/// Y's write on the same list. Partitioning by the lock key makes it structurally impossible to
/// touch a partition without holding its gate.
/// </para>
/// <para>
/// Per-amenity granularity is chosen because that is the exact scope of the invariant, and since
/// an amenity belongs to exactly one building, it gives building isolation for free: booking
/// Harborview's gym never blocks booking Maple Court's party room.
/// </para>
/// </summary>
public sealed class InMemoryStore
{
    private sealed class Partition
    {
        public readonly object Gate = new();
        public readonly List<Reservation> Items = new();
    }

    private readonly ConcurrentDictionary<Guid, Partition> _partitions = new();

    /// <summary>
    /// Reservation id -> amenity id, so cancellation can find the right partition (and therefore
    /// the right gate) from a bare reservation id without scanning every amenity.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Guid> _partitionOfReservation = new();

    public IReadOnlyList<Building> Buildings { get; }
    public IReadOnlyList<Resident> Residents { get; }
    public IReadOnlyList<Amenity> Amenities { get; }

    public InMemoryStore()
        : this(SeedData.Buildings, SeedData.Residents, SeedData.Amenities)
    {
    }

    private InMemoryStore(
        IReadOnlyList<Building> buildings,
        IReadOnlyList<Resident> residents,
        IReadOnlyList<Amenity> amenities)
    {
        Buildings = buildings;
        Residents = residents;
        Amenities = amenities;
    }

    /// <summary>Builds a store over custom fixtures. For tests that need a specific arrangement.</summary>
    public static InMemoryStore WithFixtures(
        IReadOnlyList<Building> buildings,
        IReadOnlyList<Resident> residents,
        IReadOnlyList<Amenity> amenities) => new(buildings, residents, amenities);

    // --- Fixture lookups -------------------------------------------------------------------

    public Building? FindBuilding(Guid id) => Buildings.FirstOrDefault(b => b.Id == id);

    public Resident? FindResident(Guid id) => Residents.FirstOrDefault(r => r.Id == id);

    public Amenity? FindAmenity(Guid id) => Amenities.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// Amenities visible to a building. Every amenity read goes through a building filter so no
    /// caller can accidentally list another tenant's amenities.
    /// </summary>
    public IReadOnlyList<Amenity> AmenitiesInBuilding(Guid buildingId) =>
        Amenities.Where(a => a.BuildingId == buildingId).ToList();

    // --- Reservations ----------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="work"/> with exclusive access to one amenity's reservations. The
    /// delegate gets a live view plus <see cref="AmenityReservations.Add"/>, so validating
    /// availability and inserting happen inside the same critical section.
    ///
    /// <para>
    /// Callers should confirm the amenity exists (and belongs to the caller's building) first;
    /// an unknown id simply creates an empty partition.
    /// </para>
    /// </summary>
    public T WithAmenityLock<T>(Guid amenityId, Func<AmenityReservations, T> work)
    {
        var partition = _partitions.GetOrAdd(amenityId, _ => new Partition());
        lock (partition.Gate)
        {
            return work(new AmenityReservations(this, amenityId, partition.Items));
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> with exclusive access to the partition holding
    /// <paramref name="reservationId"/>, or returns <paramref name="notFound"/> if no such
    /// reservation exists. Used by cancellation, which is addressed by reservation id alone.
    /// </summary>
    public T WithReservationLock<T>(Guid reservationId, Func<AmenityReservations, T> work, T notFound)
    {
        if (!_partitionOfReservation.TryGetValue(reservationId, out var amenityId))
        {
            return notFound;
        }

        return WithAmenityLock(amenityId, work);
    }

    /// <summary>A point-in-time copy of one amenity's reservations, taken under its gate.</summary>
    public IReadOnlyList<Reservation> SnapshotForAmenity(Guid amenityId) =>
        WithAmenityLock(amenityId, slot => (IReadOnlyList<Reservation>)slot.All.ToList());

    /// <summary>
    /// A resident's reservations across every amenity. Each partition is copied under its own
    /// gate, so this is not a single consistent instant across amenities — which is fine for a
    /// "my bookings" list, and is the price of not holding a global lock.
    /// </summary>
    public IReadOnlyList<Reservation> SnapshotForResident(Guid residentId)
    {
        var results = new List<Reservation>();
        foreach (var (amenityId, _) in _partitions)
        {
            WithAmenityLock(amenityId, slot =>
            {
                results.AddRange(slot.All.Where(r => r.ResidentId == residentId));
                return true;
            });
        }

        return results.OrderBy(r => r.StartUtc).ToList();
    }

    /// <summary>
    /// Exclusive handle on one amenity's reservations, only obtainable from inside
    /// <see cref="WithAmenityLock{T}"/>. Reservations are never exposed as a mutable list, because
    /// a public list invites callers to read and write outside the gate — which would silently
    /// reintroduce the exact race the gate exists to prevent.
    /// </summary>
    public sealed class AmenityReservations
    {
        private readonly InMemoryStore _store;
        private readonly Guid _amenityId;
        private readonly List<Reservation> _items;

        internal AmenityReservations(InMemoryStore store, Guid amenityId, List<Reservation> items)
        {
            _store = store;
            _amenityId = amenityId;
            _items = items;
        }

        /// <summary>Every reservation for this amenity, cancelled ones included.</summary>
        public IReadOnlyList<Reservation> All => _items;

        /// <summary>Reservations that still hold their slot. This is what availability is computed from.</summary>
        public IEnumerable<Reservation> Active => _items.Where(r => r.IsActive);

        public Reservation? Find(Guid reservationId) => _items.FirstOrDefault(r => r.Id == reservationId);

        public void Add(Reservation reservation)
        {
            _items.Add(reservation);
            _store._partitionOfReservation[reservation.Id] = _amenityId;
        }

        /// <summary>
        /// Replaces a reservation with an updated copy (records are immutable, so state changes
        /// are replacements). Returns the new value, or null if the id isn't in this partition.
        /// </summary>
        public Reservation? Update(Guid reservationId, Func<Reservation, Reservation> update)
        {
            var index = _items.FindIndex(r => r.Id == reservationId);
            if (index < 0)
            {
                return null;
            }

            var updated = update(_items[index]);
            _items[index] = updated;
            return updated;
        }
    }
}
