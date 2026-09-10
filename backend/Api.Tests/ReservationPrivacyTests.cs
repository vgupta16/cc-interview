using System.Text.Json;
using Api.Contracts;
using Api.Store;
using static Api.Tests.Fixtures;

namespace Api.Tests;

/// <summary>
/// Anonymizing other residents' bookings has to hold on the wire, not in the UI — anyone can read
/// the raw response in dev tools or curl the endpoint directly.
///
/// <para>
/// These assertions therefore run against serialized JSON rather than the DTO's properties.
/// Asserting on properties would only restate the type definition; asserting on the bytes also
/// catches a field added later, a serializer configured to include extra data, or a projection
/// that starts passing the domain record through.
/// </para>
/// </summary>
public class ReservationPrivacyTests
{
    private static readonly Guid Gym = SeedData.Ids.HarborviewGym;

    [Fact]
    public void A_schedule_never_serializes_another_residents_identifiers()
    {
        var (service, store, _) = NewService();
        var dana = Dana(store);
        var riley = Riley(store);
        var danas = service.Create(dana, Gym, Window(9, 10)).Value!;
        service.Create(riley, Gym, Window(11, 12));

        var schedule = service.GetSchedule(riley, Gym, At(8), At(13)).Value!;
        var json = JsonSerializer.Serialize(ReservationProjection.Schedule(schedule, riley.Id), ApiJson.Options);

        // Guard: Dana's window really is in this payload, so the assertions below can't pass
        // just because the response was empty.
        var danasStart = JsonSerializer.Serialize(danas.StartUtc, ApiJson.Options).Trim('"');
        Assert.Contains(danasStart, json, StringComparison.Ordinal);

        Assert.DoesNotContain(dana.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(danas.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(dana.DisplayName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("residentId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_schedule_shows_every_booking_as_busy_but_only_mine_as_cancellable()
    {
        var (service, store, _) = NewService();
        var riley = Riley(store);
        service.Create(Dana(store), Gym, Window(9, 10));
        var mine = service.Create(riley, Gym, Window(11, 12)).Value!;

        var schedule = service.GetSchedule(riley, Gym, At(8), At(13)).Value!;
        var response = ReservationProjection.Schedule(schedule, riley.Id);

        // Busy is the complete occupancy picture, so availability needs no client-side merging.
        Assert.Equal(2, response.Busy.Count);

        // Mine is the actionable subset, and is the only place an id appears.
        Assert.Equal(mine.Id, Assert.Single(response.Mine).Id);
    }

    /// <summary>
    /// The filter lives inside the projection, so handing it an unfiltered collection — the
    /// obvious mistake — cannot leak anything.
    /// </summary>
    [Fact]
    public void Projecting_my_reservations_filters_even_when_handed_everyone_elses()
    {
        var (service, store, _) = NewService();
        var riley = Riley(store);
        service.Create(Dana(store), Gym, Window(9, 10));
        var mine = service.Create(riley, Gym, Window(11, 12)).Value!;
        var everything = service.GetSchedule(riley, Gym, At(8), At(13)).Value!.Active;

        var projected = ReservationProjection.Mine(everything, riley.Id);

        Assert.Equal(mine.Id, Assert.Single(projected).Id);
    }
}
