using Api.Domain;
using Api.Reservations;
using Api.Store;
using static Api.Tests.Fixtures;

namespace Api.Tests;

/// <summary>
/// The tests that justify the locking design. Everything else in this suite would pass against a
/// naive read-then-write implementation, because the failure mode is an interleaving rather than
/// a wrong answer.
///
/// <para>
/// Note the asymmetry: these tests are deterministic when the gate is in place, but only
/// probabilistic at catching its absence. They are therefore run over several rounds, and the
/// gate was removed once to confirm they do fail without it — a concurrency test that has never
/// been seen to fail is not evidence of anything.
/// </para>
/// </summary>
public class ConcurrencyTests
{
    private const int Rounds = 10;
    private const int Contenders = 12;

    private static readonly Guid PartyRoom = SeedData.Ids.HarborviewPartyRoomMain;   // capacity 1
    private static readonly Guid PartyRoomEast = SeedData.Ids.HarborviewPartyRoomEast; // capacity 1
    private static readonly Guid Gym = SeedData.Ids.HarborviewGym;                   // capacity 2
    private static readonly Guid GuestParking = SeedData.Ids.HarborviewGuestParking; // capacity 1

    /// <summary>
    /// Runs <paramref name="attempt"/> on <paramref name="count"/> dedicated threads that are all
    /// held at a barrier and released together.
    ///
    /// <para>
    /// Real threads rather than <c>Task.Run</c>: a barrier across pooled work items can stall
    /// waiting for tasks the pool hasn't scheduled yet. The barrier is what makes the requests
    /// genuinely simultaneous instead of merely fast.
    /// </para>
    /// </summary>
    private static IReadOnlyList<T> Simultaneously<T>(int count, Func<int, T> attempt)
    {
        var results = new T[count];
        using var barrier = new Barrier(count);

        var threads = Enumerable.Range(0, count)
            .Select(i => new Thread(() =>
            {
                barrier.SignalAndWait();
                results[i] = attempt(i);
            }))
            .ToArray();

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        return results;
    }

    [Fact]
    public void Exactly_one_of_many_simultaneous_requests_wins_an_exclusive_slot()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (service, store, _) = NewService();
            var window = Window(16, 18);

            var results = Simultaneously(Contenders, i =>
                service.Create(i % 2 == 0 ? Riley(store) : Dana(store), PartyRoom, window));

            Assert.Single(results, r => r.Succeeded);
            Assert.All(
                results.Where(r => !r.Succeeded),
                r => Assert.Equal(ReservationErrorCode.SlotUnavailable, r.Error!.Code));

            // The count that would expose a torn or lost write, not just a wrong decision.
            Assert.Single(store.SnapshotForAmenity(PartyRoom));
        }
    }

    [Fact]
    public void Exactly_capacity_many_requests_win_a_shared_slot()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (service, store, _) = NewService();
            var window = Window(9, 10);

            var results = Simultaneously(Contenders, i =>
                service.Create(i % 2 == 0 ? Riley(store) : Dana(store), Gym, window));

            // The gym holds two at once, so two must win — no more, and no fewer.
            Assert.Equal(2, results.Count(r => r.Succeeded));
            Assert.Equal(2, store.SnapshotForAmenity(Gym).Count);
        }
    }

    [Fact]
    public void Simultaneous_bookings_of_non_overlapping_slots_all_persist()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (service, store, _) = NewService();

            // Every request targets the same amenity — so the same gate — but a different hour.
            // All of them are legal, so any shortfall here is a lost write rather than a refusal.
            var results = Simultaneously(Contenders, i =>
                service.Create(Riley(store), GuestParking, Window(8 + i, 9 + i)));

            Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.Detail));
            Assert.Equal(Contenders, store.SnapshotForAmenity(GuestParking).Count);
        }
    }

    [Fact]
    public void Booking_different_amenities_at_once_never_interferes()
    {
        var (service, store, _) = NewService();
        Guid[] amenities = [PartyRoom, PartyRoomEast, Gym, GuestParking];

        // Two party rooms in one building are separate amenities, so the identical window is
        // legal on both. This is the instance-over-type decision holding up under contention.
        var results = Simultaneously(amenities.Length, i =>
            service.Create(Riley(store), amenities[i], Window(16, 17)));

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.Detail));
    }

    [Fact]
    public void Simultaneous_bookings_in_different_buildings_both_succeed()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (service, store, _) = NewService();
            var window = Window(16, 18);

            var results = Simultaneously(2, i => i == 0
                ? service.Create(Riley(store), PartyRoom, window)
                : service.Create(Sam(store), SeedData.Ids.MapleCourtPartyRoom, window));

            Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.Detail));
        }
    }

    [Fact]
    public void Cancelling_while_others_race_for_the_slot_never_yields_two_holders()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (service, store, _) = NewService();
            var window = Window(16, 18);
            var existing = service.Create(Dana(store), PartyRoom, window).Value!;

            // One thread releases the slot while the rest try to take it. Whichever order the
            // scheduler picks, the amenity must never end up doubly booked.
            Simultaneously(Contenders, i => i == 0
                ? service.Cancel(Dana(store), existing.Id)
                : service.Create(Riley(store), PartyRoom, window));

            var active = store.SnapshotForAmenity(PartyRoom).Where(r => r.IsActive).ToList();

            Assert.True(active.Count <= 1, $"{active.Count} active reservations for one exclusive slot");
        }
    }
}
