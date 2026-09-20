using System.Text.Json.Serialization;
using Api.Application;
using Api.Http;
using Api.Store;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Enums travel as strings ("Active"), both on the wire and in the OpenAPI schema.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Persistence and tenancy/identity sit behind interfaces so they can be swapped without touching the rest.
builder.Services.AddSingleton<IAmenityCatalog, InMemoryAmenityCatalog>();
builder.Services.AddSingleton<IReservationRepository, InMemoryReservationRepository>();
builder.Services.AddSingleton<IRequestContextResolver, HeaderRequestContextResolver>();
builder.Services.AddSingleton(TimeProvider.System);

// Booking rules run in registration order: validation first, then conflicts.
// A new rule is one class plus one line here.
builder.Services.AddSingleton<IBookingRule, EndAfterStartRule>();
builder.Services.AddSingleton<IBookingRule, NotInPastRule>();
builder.Services.AddSingleton<IBookingRule, MaxDurationRule>();
builder.Services.AddSingleton<IBookingRule, OneActivePerResidentRule>();
builder.Services.AddSingleton<ReservationService>();

var app = builder.Build();

// OpenAPI is always on so the frontend can regenerate its typed client:
//   openapi json -> http://localhost:5080/openapi/v1.json
app.MapOpenApi();

// --- System ---------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("getHealth")
   .WithTags("System");

// --- Amenities and reservations (see Http/Endpoints.cs) ---------------------
app.MapAmenityEndpoints();
app.MapReservationEndpoints();

app.Run();

// Lets integration tests use WebApplicationFactory<Program>.
public partial class Program;
