using Api.Domain;

namespace Api.Application;

/// <summary>Tenant-scoped amenity reads.</summary>
public interface IAmenityCatalog
{
    Task<IReadOnlyList<Amenity>> ListAsync(Guid buildingId);

    /// <summary>Null when the amenity doesn't exist <b>in this building</b>.</summary>
    Task<Amenity?> FindAsync(Guid buildingId, Guid amenityId);
}

/// <summary>
/// The scoped view a caller gets inside <see cref="IReservationRepository.ExecuteExclusiveAsync{T}"/>.
/// It is only valid for the duration of the callback.
/// </summary>
public interface IAmenityBookings
{
    /// <summary>Snapshot of this amenity's reservations whose status holds capacity.</summary>
    IReadOnlyList<Reservation> ActiveReservations { get; }

    Reservation? Find(Guid reservationId);
    void Add(Reservation reservation);

    /// <summary>Replaces the stored reservation that has the same id (used for cancel).</summary>
    void Update(Reservation reservation);
}

public interface IReservationRepository
{
    /// <summary>
    /// Runs <paramref name="work"/> while holding exclusive access to one amenity's bookings.
    /// Load, check and write all happen inside the callback, which is what makes the booking
    /// atomic. In memory this is a per-amenity lock; a database implementation would be a
    /// transaction with a row lock. Different amenities never block each other.
    /// </summary>
    Task<T> ExecuteExclusiveAsync<T>(Guid buildingId, Guid amenityId, Func<IAmenityBookings, Task<T>> work);

    /// <summary>Tenant-scoped lookup, without locking. Null for another building's reservation.</summary>
    Task<Reservation?> FindAsync(Guid buildingId, Guid reservationId);

    /// <summary>Active reservations of an amenity overlapping [from, to) (open-ended when null).</summary>
    Task<IReadOnlyList<Reservation>> ListActiveAsync(Guid buildingId, Guid amenityId, DateTimeOffset? from, DateTimeOffset? to);
}
