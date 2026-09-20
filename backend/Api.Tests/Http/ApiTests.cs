using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Application;
using Api.Store;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Api.Tests.Http;

/// <summary>
/// End-to-end through the real pipeline (routing, header resolution, JSON, service, store).
/// xUnit creates one instance of this class per test, so every test gets its own factory and
/// therefore its own empty in-memory repository. Each test also books on its own future day
/// with its own resident ids, so tests could safely share state even if that changed.
/// </summary>
public class ApiTests : IDisposable
{
    private static readonly Guid A = SeedData.BuildingA;
    private static readonly Guid B = SeedData.BuildingB;

    private static int _dayCounter = 30;
    private readonly DateTime _day = DateTime.UtcNow.Date.AddDays(Interlocked.Increment(ref _dayCounter));

    private readonly WebApplicationFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>An hour on this test's private future day, always on the hour.</summary>
    private DateTimeOffset Hour(int hour, int minute = 0) => new(_day.AddHours(hour).AddMinutes(minute), TimeSpan.Zero);

    private HttpClient Client(Guid? building = null, string? resident = null, string? roles = null)
    {
        var client = _factory.CreateClient();
        if (building is { } b) client.DefaultRequestHeaders.Add("X-Building-Id", b.ToString());
        if (resident is not null) client.DefaultRequestHeaders.Add("X-Resident-Id", resident);
        if (roles is not null) client.DefaultRequestHeaders.Add("X-Resident-Roles", roles);
        return client;
    }

    private static Task<HttpResponseMessage> PostReservation(HttpClient client, Guid amenityId, DateTimeOffset start, DateTimeOffset end) =>
        client.PostAsJsonAsync("/api/reservations", new { amenityId, startUtc = start, endUtc = end });

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    private async Task<Guid> BookOk(HttpClient client, Guid amenityId, DateTimeOffset start, DateTimeOffset end)
    {
        var response = await PostReservation(client, amenityId, start, end);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetGuid();
    }

    // ---- identity headers ----------------------------------------------------------------

    [Fact]
    public async Task Get_Amenities_WithoutBuildingHeader_Is401MissingBuilding()
    {
        var response = await Client().GetAsync("/api/amenities");

        await AssertError(response, HttpStatusCode.Unauthorized, ErrorCodes.MissingBuilding);
    }

    [Fact]
    public async Task Get_Amenities_WithMalformedBuildingHeader_Is401MissingBuilding()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Building-Id", "not-a-guid");

        await AssertError(await client.GetAsync("/api/amenities"), HttpStatusCode.Unauthorized, ErrorCodes.MissingBuilding);
    }

    [Fact]
    public async Task Get_Amenities_DoesNotRequireAResident()
    {
        var response = await Client(A).GetAsync("/api/amenities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_WithoutBuildingHeader_Is401MissingBuilding()
    {
        var response = await PostReservation(Client(resident: "alice-nobuilding"), SeedData.PartyRoomA, Hour(10), Hour(11));

        await AssertError(response, HttpStatusCode.Unauthorized, ErrorCodes.MissingBuilding);
    }

    [Fact]
    public async Task Post_WithoutResidentHeader_Is401MissingResident()
    {
        var response = await PostReservation(Client(A), SeedData.PartyRoomA, Hour(10), Hour(11));

        await AssertError(response, HttpStatusCode.Unauthorized, ErrorCodes.MissingResident);
    }

    [Fact]
    public async Task Delete_WithoutResidentHeader_Is401MissingResident()
    {
        var response = await Client(A).DeleteAsync($"/api/reservations/{Guid.NewGuid()}");

        await AssertError(response, HttpStatusCode.Unauthorized, ErrorCodes.MissingResident);
    }

    // ---- amenities and tenancy -----------------------------------------------------------

    [Fact]
    public async Task Get_Amenities_ReturnsOnlyTheCallersBuildingsAmenities()
    {
        var inA = await Json(await Client(A).GetAsync("/api/amenities"));
        var inB = await Json(await Client(B).GetAsync("/api/amenities"));

        var idsA = inA.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Order().ToList();
        var idsB = inB.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Order().ToList();
        Assert.Equal(new[] { SeedData.PartyRoomA, SeedData.GymA, SeedData.GuestParkingA }.Order(), idsA);
        Assert.Equal(new[] { SeedData.PartyRoomB, SeedData.GymB, SeedData.GuestParkingB }.Order(), idsB);
    }

    [Fact]
    public async Task Get_Amenities_ExposesCapacityAndMaxBookingMinutes()
    {
        var amenities = await Json(await Client(A).GetAsync("/api/amenities"));

        var gym = amenities.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == SeedData.GymA);
        Assert.Equal(2, gym.GetProperty("capacity").GetInt32());
        Assert.Equal(90, gym.GetProperty("maxBookingMinutes").GetInt32());
    }

    [Fact]
    public async Task Get_Reservations_OfAnAmenityFromAnotherBuilding_Is404()
    {
        var response = await Client(B).GetAsync($"/api/amenities/{SeedData.PartyRoomA}/reservations");

        await AssertError(response, HttpStatusCode.NotFound, ErrorCodes.AmenityNotFound);
    }

    [Fact]
    public async Task Get_Reservations_OfAnUnknownAmenity_Is404()
    {
        var response = await Client(A).GetAsync($"/api/amenities/{Guid.NewGuid()}/reservations");

        await AssertError(response, HttpStatusCode.NotFound, ErrorCodes.AmenityNotFound);
    }

    [Fact]
    public async Task Post_WithAnAmenityFromAnotherBuilding_Is404()
    {
        var response = await PostReservation(Client(B, "alice-cross"), SeedData.PartyRoomA, Hour(10), Hour(11));

        await AssertError(response, HttpStatusCode.NotFound, ErrorCodes.AmenityNotFound);
    }

    [Fact]
    public async Task Delete_ReservationOfAnotherBuilding_Is404_AndTheBookingSurvives()
    {
        var id = await BookOk(Client(A, "alice-del-cross"), SeedData.PartyRoomA, Hour(10), Hour(11));

        var response = await Client(B, "alice-del-cross").DeleteAsync($"/api/reservations/{id}");

        await AssertError(response, HttpStatusCode.NotFound, ErrorCodes.ReservationNotFound);
        var listed = await Json(await Client(A).GetAsync($"/api/amenities/{SeedData.PartyRoomA}/reservations"));
        Assert.Equal(1, listed.GetArrayLength());
    }

    // ---- create / list -------------------------------------------------------------------

    [Fact]
    public async Task Post_Reservation_Returns201WithStringStatus_AndItShowsUpInTheAmenitysList()
    {
        var alice = Client(A, "alice-happy");

        var response = await PostReservation(alice, SeedData.PartyRoomA, Hour(10), Hour(11));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Json(response);
        Assert.Equal("Active", created.GetProperty("status").GetString());
        Assert.Equal("alice-happy", created.GetProperty("residentId").GetString());
        Assert.Equal(SeedData.PartyRoomA, created.GetProperty("amenityId").GetGuid());
        Assert.Equal(Hour(10), created.GetProperty("startUtc").GetDateTimeOffset());
        Assert.Equal(Hour(11), created.GetProperty("endUtc").GetDateTimeOffset());
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal($"/api/reservations/{id}", response.Headers.Location?.OriginalString);

        var listed = await Json(await alice.GetAsync($"/api/amenities/{SeedData.PartyRoomA}/reservations"));
        var only = Assert.Single(listed.EnumerateArray());
        Assert.Equal(id, only.GetProperty("id").GetGuid());
        Assert.Equal("Active", only.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Get_Reservations_SupportsFromAndToWindow()
    {
        var alice = Client(A, "alice-window");
        var bob = Client(A, "bob-window");
        await BookOk(alice, SeedData.PartyRoomA, Hour(9), Hour(10));
        var noon = await BookOk(bob, SeedData.PartyRoomA, Hour(12), Hour(13));

        var from = Uri.EscapeDataString(Hour(11).ToString("O"));
        var to = Uri.EscapeDataString(Hour(14).ToString("O"));
        var listed = await Json(await alice.GetAsync($"/api/amenities/{SeedData.PartyRoomA}/reservations?from={from}&to={to}"));

        var only = Assert.Single(listed.EnumerateArray());
        Assert.Equal(noon, only.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Post_OverlappingBookingOnACapacityOneAmenity_Is409SlotFull()
    {
        await BookOk(Client(A, "alice-full"), SeedData.PartyRoomA, Hour(10), Hour(11));

        var response = await PostReservation(Client(A, "bob-full"), SeedData.PartyRoomA, Hour(10, 30), Hour(11, 30));

        await AssertError(response, HttpStatusCode.Conflict, ErrorCodes.SlotFull);
    }

    [Fact]
    public async Task Post_BackToBackBooking_Is201()
    {
        await BookOk(Client(A, "alice-b2b"), SeedData.PartyRoomA, Hour(10), Hour(11));

        var response = await PostReservation(Client(A, "bob-b2b"), SeedData.PartyRoomA, Hour(11), Hour(12));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_ASecondBookingBySameResidentOnTheSameAmenity_Is409AlreadyHasBooking()
    {
        var alice = Client(A, "alice-two");
        await BookOk(alice, SeedData.PartyRoomA, Hour(10), Hour(11));

        var response = await PostReservation(alice, SeedData.PartyRoomA, Hour(14), Hour(15));

        await AssertError(response, HttpStatusCode.Conflict, ErrorCodes.AlreadyHasBooking);
    }

    [Fact]
    public async Task Post_GymHoldsTwoAtOnce_ButNotThree()
    {
        Assert.Equal(HttpStatusCode.Created, (await PostReservation(Client(A, "alice-gym"), SeedData.GymA, Hour(10), Hour(11))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostReservation(Client(A, "bob-gym"), SeedData.GymA, Hour(10), Hour(11))).StatusCode);

        var third = await PostReservation(Client(A, "carol-gym"), SeedData.GymA, Hour(10), Hour(11));

        await AssertError(third, HttpStatusCode.Conflict, ErrorCodes.SlotFull);
    }

    [Fact]
    public async Task Post_EndBeforeStart_Is400()
    {
        var response = await PostReservation(Client(A, "alice-badrange"), SeedData.PartyRoomA, Hour(11), Hour(10));

        await AssertError(response, HttpStatusCode.BadRequest, ErrorCodes.EndBeforeStart);
    }

    [Fact]
    public async Task Post_InThePast_Is400()
    {
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);

        var response = await PostReservation(Client(A, "alice-past"), SeedData.PartyRoomA, yesterday, yesterday.AddHours(1));

        await AssertError(response, HttpStatusCode.BadRequest, ErrorCodes.InPast);
    }

    [Fact]
    public async Task Post_LongerThanTheAmenitysMax_Is400()
    {
        // The Gym allows 90 minutes.
        var response = await PostReservation(Client(A, "alice-long"), SeedData.GymA, Hour(10), Hour(12));

        await AssertError(response, HttpStatusCode.BadRequest, ErrorCodes.MaxDurationExceeded);
    }

    [Fact]
    public async Task Post_Concurrently_ExactlyOneWinsAndTheRestAre409()
    {
        // Create every client BEFORE racing: WebApplicationFactory starts its host lazily on the
        // first CreateClient(), and doing that from many threads at once can start several hosts,
        // each with its own in-memory repository, which would make every request "win".
        var clients = Enumerable.Range(0, 20).Select(i => Client(A, $"racer-{i}")).ToList();

        var statuses = await Task.WhenAll(clients.Select(client =>
            Task.Run(async () => (await PostReservation(client, SeedData.PartyRoomA, Hour(10), Hour(11))).StatusCode)));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(19, statuses.Count(s => s == HttpStatusCode.Conflict));
    }

    // ---- cancel --------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ByOwner_Is204_AndRepeatingItIsStill204()
    {
        var alice = Client(A, "alice-cancel");
        var id = await BookOk(alice, SeedData.PartyRoomA, Hour(10), Hour(11));

        var first = await alice.DeleteAsync($"/api/reservations/{id}");
        var second = await alice.DeleteAsync($"/api/reservations/{id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var listed = await Json(await alice.GetAsync($"/api/amenities/{SeedData.PartyRoomA}/reservations"));
        Assert.Equal(0, listed.GetArrayLength());
    }

    [Fact]
    public async Task Delete_FreesTheSlotForAnotherResident()
    {
        var alice = Client(A, "alice-free");
        var id = await BookOk(alice, SeedData.PartyRoomA, Hour(10), Hour(11));

        await alice.DeleteAsync($"/api/reservations/{id}");

        var response = await PostReservation(Client(A, "bob-free"), SeedData.PartyRoomA, Hour(10), Hour(11));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Delete_ByAnotherResident_Is403NotOwner()
    {
        var id = await BookOk(Client(A, "alice-owner"), SeedData.PartyRoomA, Hour(10), Hour(11));

        var response = await Client(A, "mallory").DeleteAsync($"/api/reservations/{id}");

        await AssertError(response, HttpStatusCode.Forbidden, ErrorCodes.NotOwner);
        var listed = await Json(await Client(A).GetAsync($"/api/amenities/{SeedData.PartyRoomA}/reservations"));
        Assert.Equal(1, listed.GetArrayLength());
    }

    [Fact]
    public async Task Delete_ByStaff_CancelsAnotherResidentsBooking()
    {
        var id = await BookOk(Client(A, "alice-staff"), SeedData.PartyRoomA, Hour(10), Hour(11));

        var response = await Client(A, "front-desk", roles: "staff").DeleteAsync($"/api/reservations/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var listed = await Json(await Client(A).GetAsync($"/api/amenities/{SeedData.PartyRoomA}/reservations"));
        Assert.Equal(0, listed.GetArrayLength());
    }

    [Fact]
    public async Task Delete_ByStaffAmongSeveralRoles_StillWorks()
    {
        var id = await BookOk(Client(A, "alice-roles"), SeedData.PartyRoomA, Hour(10), Hour(11));

        var response = await Client(A, "front-desk", roles: "resident, staff").DeleteAsync($"/api/reservations/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Delete_UnknownReservation_Is404()
    {
        var response = await Client(A, "alice-unknown").DeleteAsync($"/api/reservations/{Guid.NewGuid()}");

        await AssertError(response, HttpStatusCode.NotFound, ErrorCodes.ReservationNotFound);
    }
}
