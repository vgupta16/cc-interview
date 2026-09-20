using Api.Application;
using Api.Domain;

namespace Api.Store;

/// <summary>
/// In-memory reservations with a per-amenity lock. Only correct for a single process; see the
/// design doc for the database version of the same seam.
/// </summary>
public sealed class InMemoryReservationRepository : IReservationRepository
{
    public Task<T> ExecuteExclusiveAsync<T>(Guid buildingId, Guid amenityId, Func<IAmenityBookings, Task<T>> work)
        => throw new NotImplementedException();

    public Task<Reservation?> FindAsync(Guid buildingId, Guid reservationId)
        => throw new NotImplementedException();

    public Task<IReadOnlyList<Reservation>> ListActiveAsync(Guid buildingId, Guid amenityId, DateTimeOffset? from, DateTimeOffset? to)
        => throw new NotImplementedException();
}
