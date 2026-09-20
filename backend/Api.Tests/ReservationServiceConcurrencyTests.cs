using Api.Application;
using Api.Domain;
using Api.Store;
using Api.Tests.Support;
using static Api.Tests.Support.Assertions;

namespace Api.Tests;

/// <summary>
/// The crux: many residents booking the same amenity at the same instant must never overbook it.
/// A check-then-act implementation (read count, compare, insert) passes single-threaded tests
/// and fails these, so each scenario is repeated on a fresh repository to make a race likely.
/// </summary>
public class ReservationServiceConcurrencyTests
{
    private const int Contenders = 50;
    private const int Iterations = 20;

    private static DateTimeOffset At(int hour, int minute = 0) => TestTime.At(hour, minute);

    /// <summary>Runs every call on the thread pool, released together by a start gate.</summary>
    private static async Task<T[]> Race<T>(int count, Func<int, Task<T>> call)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await call(i);
            }))
            .ToArray();

        await Task.Delay(50); // let the workers reach the gate
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task CapacityOne_ManyResidentsSameWindow_ExactlyOneWins()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var f = new ServiceFixture();

            var results = await Race(Contenders, i => f.Book($"resident-{i}", SeedData.PartyRoomA, At(10), At(11)));

            Assert.Equal(1, results.Count(r => r.IsSuccess));
            Assert.All(results.Where(r => !r.IsSuccess), r => AssertFailure(r, ErrorKind.Conflict, ErrorCodes.SlotFull));
            Assert.Single(await f.ListOk(SeedData.PartyRoomA));
        }
    }

    [Fact]
    public async Task CapacityTwo_ManyResidentsSameWindow_ExactlyTwoWin()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var f = new ServiceFixture();

            var results = await Race(Contenders, i => f.Book($"resident-{i}", SeedData.GymA, At(10), At(11)));

            Assert.Equal(2, results.Count(r => r.IsSuccess));
            Assert.All(results.Where(r => !r.IsSuccess), r => AssertFailure(r, ErrorKind.Conflict, ErrorCodes.SlotFull));
            Assert.Equal(2, (await f.ListOk(SeedData.GymA)).Count);
        }
    }

    [Fact]
    public async Task CapacityOne_OverlappingButNotIdenticalWindows_StillExactlyOneWins()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var f = new ServiceFixture();

            // Every window contains 10:30, so all 50 mutually overlap.
            var results = await Race(Contenders, i =>
                f.Book($"resident-{i}", SeedData.PartyRoomA, At(10).AddMinutes(-(i % 30)), At(11).AddMinutes(i % 30)));

            Assert.Equal(1, results.Count(r => r.IsSuccess));
            Assert.Single(await f.ListOk(SeedData.PartyRoomA));
        }
    }

    [Fact]
    public async Task CapacityTwo_RandomOverlappingWindows_NeverExceedCapacityAtAnyMinute()
    {
        var random = new Random(12345);
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var f = new ServiceFixture();
            var windows = Enumerable.Range(0, Contenders)
                .Select(_ =>
                {
                    var start = At(9).AddMinutes(15 * random.Next(0, 13));      // 9:00 .. 12:00
                    return (Start: start, End: start.AddMinutes(15 * random.Next(2, 7))); // 30 .. 90 minutes
                })
                .ToArray();

            await Race(Contenders, i => f.Book($"resident-{i}", SeedData.GymA, windows[i].Start, windows[i].End));

            var accepted = await f.ListOk(SeedData.GymA);
            Assert.NotEmpty(accepted);
            for (var minute = 0; minute < 24 * 60; minute++)
            {
                var instant = At(0).AddMinutes(minute);
                var occupied = accepted.Count(r => r.StartUtc <= instant && instant < r.EndUtc);
                Assert.True(occupied <= 2, $"Gym holds {occupied} bookings at {instant:HH:mm} (iteration {iteration}).");
            }
        }
    }

    [Fact]
    public async Task OneResidentDoubleSubmitting_OnlyOneBookingIsCreated()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var f = new ServiceFixture();

            // Capacity 2, so capacity alone would admit two of these. The per-resident rule must
            // be evaluated inside the same critical section as the insert.
            var results = await Race(Contenders, _ => f.Book("alice", SeedData.GymA, At(10), At(11)));

            Assert.Equal(1, results.Count(r => r.IsSuccess));
            Assert.All(results.Where(r => !r.IsSuccess), r => Assert.Equal(ErrorKind.Conflict, r.Failure!.Kind));
            Assert.Single(await f.ListOk(SeedData.GymA));
        }
    }

    [Fact]
    public async Task ParallelBookingsOnDifferentAmenities_AllSucceed()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var f = new ServiceFixture();
            (Guid Building, Guid Amenity)[] targets =
            [
                (SeedData.BuildingA, SeedData.PartyRoomA), (SeedData.BuildingA, SeedData.GymA), (SeedData.BuildingA, SeedData.GuestParkingA),
                (SeedData.BuildingB, SeedData.PartyRoomB), (SeedData.BuildingB, SeedData.GymB), (SeedData.BuildingB, SeedData.GuestParkingB),
            ];

            var results = await Race(targets.Length, i =>
                f.Book($"resident-{i}", targets[i].Amenity, At(10), At(11), targets[i].Building));

            Assert.All(results, r => AssertSuccess(r));
        }
    }

    [Fact]
    public async Task ManyResidentsOnManyAmenitiesAtOnce_EachAmenityFillsToItsOwnCapacity()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var f = new ServiceFixture();
            var amenities = SeedData.Amenities;
            const int perAmenity = 10;

            var results = await Race(amenities.Count * perAmenity, async i =>
            {
                var amenity = amenities[i % amenities.Count];
                var result = await f.Book($"resident-{i}", amenity.Id, At(10), At(11), amenity.BuildingId);
                return (Amenity: amenity, Result: result);
            });

            foreach (var amenity in amenities)
            {
                var forThisAmenity = results.Where(r => r.Amenity.Id == amenity.Id).ToList();
                Assert.Equal(amenity.Capacity, forThisAmenity.Count(r => r.Result.IsSuccess));
                Assert.Equal(amenity.Capacity, (await f.ListOk(amenity.Id, amenity.BuildingId)).Count);
            }
        }
    }

    [Fact]
    public async Task ConcurrentCancelAndBook_NeverOverbooks()
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var f = new ServiceFixture();
            var incumbent = await f.BookOk("incumbent", SeedData.PartyRoomA, At(10), At(11));

            // One cancel racing with many new bookings for the freed slot.
            var results = await Race(Contenders, async i =>
            {
                if (i == 0)
                {
                    Assert.Null(await f.Cancel("incumbent", incumbent.Id));
                    return (Result<Reservation>?)null;
                }
                return await f.Book($"resident-{i}", SeedData.PartyRoomA, At(10), At(11));
            });

            var wins = results.Count(r => r is { IsSuccess: true });
            Assert.InRange(wins, 0, 1); // 0 if every attempt ran before the cancel, otherwise exactly 1
            Assert.Equal(wins, (await f.ListOk(SeedData.PartyRoomA)).Count);
        }
    }
}
