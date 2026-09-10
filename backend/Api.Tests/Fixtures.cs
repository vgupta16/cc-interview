using Api.Domain;
using Api.Reservations;
using Api.Store;

namespace Api.Tests;

/// <summary>A clock the tests move by hand, so "starts in the past" and horizon rules are testable.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Fixtures
{
    /// <summary>Midnight UTC on an arbitrary but fixed day. All test times hang off this.</summary>
    public static readonly DateTimeOffset Day = new(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

    /// <summary>An instant on the fixed day, in hours: <c>At(10.5)</c> is 10:30.</summary>
    public static DateTimeOffset At(double hour) => Day.AddHours(hour);

    public static TimeWindow Window(double startHour, double endHour) => new(At(startHour), At(endHour));

    /// <summary>A bare reservation for the pure availability tests, unattached to any store.</summary>
    public static Reservation Booking(
        double startHour,
        double endHour,
        ReservationStatus status = ReservationStatus.Active) =>
        new()
        {
            Id = Guid.NewGuid(),
            BuildingId = SeedData.Ids.Harborview,
            AmenityId = SeedData.Ids.HarborviewGym,
            ResidentId = SeedData.Ids.Dana,
            StartUtc = At(startHour),
            EndUtc = At(endHour),
            Status = status,
            CreatedAtUtc = Day,
        };

    /// <summary>
    /// A service over the seeded store, with the clock parked at 08:00 so the working hours used
    /// throughout these tests are in the future and inside the booking horizon.
    /// </summary>
    public static (ReservationService Service, InMemoryStore Store, FixedClock Clock) NewService()
    {
        var store = new InMemoryStore();
        var clock = new FixedClock(At(8));
        return (new ReservationService(store, clock), store, clock);
    }

    public static Resident Riley(InMemoryStore store) => store.FindResident(SeedData.Ids.Riley)!;

    public static Resident Dana(InMemoryStore store) => store.FindResident(SeedData.Ids.Dana)!;

    public static Resident Sam(InMemoryStore store) => store.FindResident(SeedData.Ids.Sam)!;
}
