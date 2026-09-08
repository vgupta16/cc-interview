using Api.Services;
using Api.Store;

namespace Api.Tests;

/// <summary>
/// Four tests, chosen to cover the invariant the design is built around rather than to pad a
/// coverage number. The concurrency test is the one that matters: everything else asserts that
/// the rules are written correctly, that one asserts they can't be raced.
/// </summary>
public class ReservationTests
{
    // Seeded in InMemoryStore. Oak Street's party room is capacity 1; its gym is capacity 2.
    private static readonly Guid PartyRoomId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OakStreetGymId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid MainStreetParkingId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string BothBuildings = "resident-jeremy";
    private const string MainStreetOnly = "resident-dana";

    private static readonly DateTimeOffset Ten = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Eleven = Ten.AddHours(1);
    private static readonly DateTimeOffset Twelve = Ten.AddHours(2);

    private static ReservationService NewService() => new(new InMemoryStore());

    [Fact]
    public void Booking_a_free_amenity_succeeds()
    {
        var service = NewService();

        var result = service.Book(PartyRoomId, BothBuildings, Ten, Eleven);

        Assert.Equal(BookingOutcome.Created, result.Outcome);
        Assert.NotNull(result.Reservation);
        Assert.Equal(Ten, result.Reservation!.StartUtc);
        Assert.Null(result.Reservation.CancelledUtc);
    }

    [Fact]
    public void Overlapping_booking_beyond_capacity_is_a_conflict()
    {
        var service = NewService();

        // Party room has capacity 1.
        Assert.Equal(BookingOutcome.Created, service.Book(PartyRoomId, BothBuildings, Ten, Eleven).Outcome);

        var second = service.Book(PartyRoomId, BothBuildings, Ten.AddMinutes(30), Eleven.AddMinutes(30));

        Assert.Equal(BookingOutcome.Conflict, second.Outcome);
    }

    [Fact]
    public void Capacity_allows_concurrent_bookings_up_to_the_limit()
    {
        var service = NewService();

        // Oak Street's gym has capacity 2, so two overlapping bookings are fine and a third is not.
        Assert.Equal(BookingOutcome.Created, service.Book(OakStreetGymId, BothBuildings, Ten, Eleven).Outcome);
        Assert.Equal(BookingOutcome.Created, service.Book(OakStreetGymId, BothBuildings, Ten, Eleven).Outcome);
        Assert.Equal(BookingOutcome.Conflict, service.Book(OakStreetGymId, BothBuildings, Ten, Eleven).Outcome);
    }

    [Fact]
    public void Back_to_back_bookings_do_not_conflict()
    {
        var service = NewService();

        // Intervals are half-open [start, end), so 10-11 and 11-12 share an instant but do not
        // overlap. Getting this wrong makes consecutive slots unbookable.
        Assert.Equal(BookingOutcome.Created, service.Book(PartyRoomId, BothBuildings, Ten, Eleven).Outcome);
        Assert.Equal(BookingOutcome.Created, service.Book(PartyRoomId, BothBuildings, Eleven, Twelve).Outcome);
    }

    [Fact]
    public void Booking_longer_than_the_amenity_allows_is_rejected()
    {
        var service = NewService();

        // Oak Street's gym caps a booking at 90 minutes.
        var result = service.Book(OakStreetGymId, BothBuildings, Ten, Ten.AddMinutes(120));

        Assert.Equal(BookingOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void End_before_start_is_rejected()
    {
        var service = NewService();

        var result = service.Book(PartyRoomId, BothBuildings, Eleven, Ten);

        Assert.Equal(BookingOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void A_resident_cannot_book_an_amenity_in_a_building_they_do_not_belong_to()
    {
        var service = NewService();

        // resident-dana belongs to Main Street only; the party room is at Oak Street.
        var result = service.Book(PartyRoomId, MainStreetOnly, Ten, Eleven);

        Assert.Equal(BookingOutcome.Forbidden, result.Outcome);

        // ...and the same resident can book in their own building, so the check is scoping
        // access rather than just refusing everything.
        Assert.Equal(
            BookingOutcome.Created,
            service.Book(MainStreetParkingId, MainStreetOnly, Ten, Eleven).Outcome);
    }

    [Fact]
    public void Cancelling_frees_the_slot()
    {
        var store = new InMemoryStore();
        var service = new ReservationService(store);

        var created = service.Book(PartyRoomId, BothBuildings, Ten, Eleven);
        Assert.Equal(BookingOutcome.Conflict, service.Book(PartyRoomId, BothBuildings, Ten, Eleven).Outcome);

        Assert.Equal(
            CancelReservationOutcome.Cancelled,
            store.CancelReservation(created.Reservation!.Id, BothBuildings));

        Assert.Equal(BookingOutcome.Created, service.Book(PartyRoomId, BothBuildings, Ten, Eleven).Outcome);
    }

    [Fact]
    public void Cancelling_someone_elses_reservation_is_refused()
    {
        var store = new InMemoryStore();
        var service = new ReservationService(store);

        var created = service.Book(MainStreetParkingId, BothBuildings, Ten, Eleven);

        Assert.Equal(
            CancelReservationOutcome.NotOwner,
            store.CancelReservation(created.Reservation!.Id, MainStreetOnly));
    }

    /// <summary>
    /// The one that actually proves the design. Many threads race to book the same slot on a
    /// capacity-1 amenity; exactly one must win.
    ///
    /// Two details matter, both learned the hard way — an earlier version of this test used
    /// Task.Run and passed even with the lock removed, which made it worse than useless:
    ///
    /// 1. Real threads and a Barrier, not thread-pool tasks. The pool injects threads gradually
    ///    and the critical section here is a few microseconds long, so pooled tasks end up
    ///    effectively serialised and never overlap. A Barrier releases every thread at once.
    /// 2. Repeated rounds. A race is probabilistic; a single round can get lucky. Running the
    ///    scenario many times over a fresh store turns "might catch it" into "reliably catches
    ///    it" — verified by deleting the lock and watching this fail.
    /// </summary>
    [Fact]
    public void Concurrent_bookings_for_the_same_slot_produce_exactly_one_winner()
    {
        const int contenders = 16;
        const int rounds = 40;

        for (var round = 0; round < rounds; round++)
        {
            var store = new InMemoryStore();
            var service = new ReservationService(store);

            var barrier = new Barrier(contenders);
            var outcomes = new BookingOutcome[contenders];
            var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var threads = new Thread[contenders];

            for (var i = 0; i < contenders; i++)
            {
                var index = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        // Every thread parks here until the last one arrives, then all resume
                        // together.
                        barrier.SignalAndWait();
                        outcomes[index] = service.Book(PartyRoomId, BothBuildings, Ten, Eleven).Outcome;
                    }
                    catch (Exception ex)
                    {
                        // An unsynchronised List<T> can also throw or corrupt under concurrent
                        // mutation. Capture rather than letting it tear down the test host.
                        failures.Add(ex);
                    }
                });
                threads[i].Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            Assert.Empty(failures);
            Assert.Equal(1, outcomes.Count(o => o == BookingOutcome.Created));
            Assert.Equal(contenders - 1, outcomes.Count(o => o == BookingOutcome.Conflict));

            // And the store agrees — exactly one active reservation exists.
            Assert.Single(store.GetActiveReservations(PartyRoomId));
        }
    }
}
