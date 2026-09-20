using Api.Application;
using Api.Domain;
using Api.Store;

namespace Api.Tests.Support;

/// <summary>Fixed points in time so tests never depend on the wall clock.</summary>
public static class TestTime
{
    /// <summary>"Now" for service and rule tests: 2030-01-01 08:00 UTC.</summary>
    public static readonly DateTimeOffset Now = new(2030, 1, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A time on 2030-01-<paramref name="day"/> (default the 2nd, i.e. tomorrow relative to <see cref="Now"/>).</summary>
    public static DateTimeOffset At(int hour, int minute = 0, int day = 2) =>
        new(2030, 1, day, hour, minute, 0, TimeSpan.Zero);
}

public static class Make
{
    public static Amenity Amenity(
        int capacity = 1, int maxBookingMinutes = 120, int maxActivePerResident = 1,
        Guid? id = null, Guid? buildingId = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        BuildingId = buildingId ?? SeedData.BuildingA,
        Name = "Test amenity",
        Capacity = capacity,
        Policy = new AmenityPolicy { MaxBookingMinutes = maxBookingMinutes, MaxActivePerResident = maxActivePerResident },
    };

    public static Reservation Reservation(
        string resident, DateTimeOffset start, DateTimeOffset end,
        ReservationStatus status = ReservationStatus.Active, Guid? amenityId = null, Guid? buildingId = null) => new()
    {
        Id = Guid.NewGuid(),
        BuildingId = buildingId ?? SeedData.BuildingA,
        AmenityId = amenityId ?? Guid.NewGuid(),
        ResidentId = resident,
        StartUtc = start,
        EndUtc = end,
        Status = status,
        CreatedUtc = TestTime.Now,
    };

    /// <summary>A rule context with sensible defaults: Now = <see cref="TestTime.Now"/>, no existing bookings.</summary>
    public static BookingContext Context(
        DateTimeOffset start, DateTimeOffset end,
        Amenity? amenity = null, string resident = "alice",
        IReadOnlyList<Reservation>? active = null, DateTimeOffset? now = null) =>
        new(amenity ?? Amenity(), new Actor(resident), start, end, now ?? TestTime.Now, active ?? []);
}

public static class Assertions
{
    public static void AssertFailure(Failure? failure, ErrorKind kind, string code)
    {
        Assert.NotNull(failure);
        Assert.Equal(kind, failure.Kind);
        Assert.Equal(code, failure.Code);
    }

    public static void AssertFailure<T>(Result<T> result, ErrorKind kind, string code)
    {
        Assert.False(result.IsSuccess, $"Expected failure {code} but the operation succeeded.");
        AssertFailure(result.Failure, kind, code);
    }

    public static T AssertSuccess<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, $"Expected success but got {result.Failure?.Code}: {result.Failure?.Message}");
        return result.Value!;
    }
}
