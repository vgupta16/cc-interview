using System.Collections.Concurrent;
using Api.Application;
using Api.Domain;

namespace Api.Store;

/// <summary>
/// In-memory reservations with one gate per (building, amenity). Only correct for a single
/// process; see the design doc for the database version of the same seam.
/// </summary>
public sealed class InMemoryReservationRepository : IReservationRepository
{
    private sealed class AmenityState
    {
        // SemaphoreSlim, not lock: the callback is async, and a lock can't span an await.
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Dictionary<Guid, Reservation> Items = new();
    }

    // Keyed by building AND amenity, so tenants can never share state even by mistake.
    private readonly ConcurrentDictionary<(Guid Building, Guid Amenity), AmenityState> _states = new();
    private readonly ConcurrentDictionary<Guid, (Guid Building, Guid Amenity)> _index = new();

    public async Task<T> ExecuteExclusiveAsync<T>(Guid buildingId, Guid amenityId, Func<IAmenityBookings, Task<T>> work)
    {
        var state = _states.GetOrAdd((buildingId, amenityId), _ => new AmenityState());
        await state.Gate.WaitAsync();
        try
        {
            return await work(new Bookings(this, buildingId, amenityId, state));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<Reservation?> FindAsync(Guid buildingId, Guid reservationId)
    {
        // A reservation from another building looks exactly like one that doesn't exist.
        if (!_index.TryGetValue(reservationId, out var key) || key.Building != buildingId) return null;
        if (!_states.TryGetValue(key, out var state)) return null;

        await state.Gate.WaitAsync();
        try
        {
            return state.Items.GetValueOrDefault(reservationId);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<IReadOnlyList<Reservation>> ListActiveAsync(
        Guid buildingId, Guid amenityId, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!_states.TryGetValue((buildingId, amenityId), out var state)) return Array.Empty<Reservation>();

        await state.Gate.WaitAsync();
        try
        {
            var start = from ?? DateTimeOffset.MinValue;
            var end = to ?? DateTimeOffset.MaxValue;
            return state.Items.Values
                .Where(r => r.Status.HoldsCapacity() && Occupancy.Overlaps(r.StartUtc, r.EndUtc, start, end))
                .OrderBy(r => r.StartUtc)
                .ToList();
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private sealed class Bookings(
        InMemoryReservationRepository repo, Guid buildingId, Guid amenityId, AmenityState state) : IAmenityBookings
    {
        public IReadOnlyList<Reservation> ActiveReservations =>
            state.Items.Values.Where(r => r.Status.HoldsCapacity()).ToList();

        public Reservation? Find(Guid reservationId) => state.Items.GetValueOrDefault(reservationId);

        public void Add(Reservation reservation)
        {
            EnsureScope(reservation);
            state.Items.Add(reservation.Id, reservation);
            repo._index[reservation.Id] = (buildingId, amenityId);
        }

        public void Update(Reservation reservation)
        {
            EnsureScope(reservation);
            if (!state.Items.ContainsKey(reservation.Id))
                throw new InvalidOperationException($"Reservation {reservation.Id} does not exist.");
            state.Items[reservation.Id] = reservation;
        }

        private void EnsureScope(Reservation r)
        {
            if (r.BuildingId != buildingId || r.AmenityId != amenityId)
                throw new InvalidOperationException("Reservation is outside the exclusive section's building/amenity.");
        }
    }
}
