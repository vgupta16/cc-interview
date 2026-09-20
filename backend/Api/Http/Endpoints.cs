using Api.Application;

namespace Api.Http;

public static class Endpoints
{
    public static IEndpointRouteBuilder MapAmenityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/amenities", async Task<IResult> (
                HttpRequest http, IRequestContextResolver resolver, IAmenityCatalog catalog) =>
            {
                var ctx = resolver.Resolve(http, requireResident: false);
                if (!ctx.IsSuccess) return ctx.Failure.ToHttpResult();

                var amenities = await catalog.ListAsync(ctx.Value!.BuildingId);
                return Results.Ok(amenities.Select(AmenityResponse.From).ToList());
            })
            .WithName("getAmenities")
            .WithTags("Amenities")
            .Produces<List<AmenityResponse>>()
            .Produces<ApiError>(StatusCodes.Status401Unauthorized);

        return app;
    }

    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/amenities/{amenityId:guid}/reservations", async Task<IResult> (
                Guid amenityId, DateTimeOffset? from, DateTimeOffset? to,
                HttpRequest http, IRequestContextResolver resolver, ReservationService service) =>
            {
                var ctx = resolver.Resolve(http, requireResident: false);
                if (!ctx.IsSuccess) return ctx.Failure.ToHttpResult();

                var result = await service.ListAsync(ctx.Value!.BuildingId, amenityId, from, to);
                return result.IsSuccess
                    ? Results.Ok(result.Value!.Select(ReservationResponse.From).ToList())
                    : result.Failure.ToHttpResult();
            })
            .WithName("getAmenityReservations")
            .WithTags("Reservations")
            .Produces<List<ReservationResponse>>()
            .Produces<ApiError>(StatusCodes.Status401Unauthorized)
            .Produces<ApiError>(StatusCodes.Status404NotFound);

        app.MapPost("/api/reservations", async Task<IResult> (
                CreateReservationRequest body,
                HttpRequest http, IRequestContextResolver resolver, ReservationService service) =>
            {
                var ctx = resolver.Resolve(http, requireResident: true);
                if (!ctx.IsSuccess) return ctx.Failure.ToHttpResult();

                var result = await service.CreateAsync(
                    ctx.Value!.BuildingId, ctx.Value.Actor!,
                    new CreateReservationCommand(body.AmenityId, body.StartUtc, body.EndUtc));

                return result.IsSuccess
                    ? Results.Created($"/api/reservations/{result.Value!.Id}", ReservationResponse.From(result.Value))
                    : result.Failure.ToHttpResult();
            })
            .WithName("createReservation")
            .WithTags("Reservations")
            .Produces<ReservationResponse>(StatusCodes.Status201Created)
            .Produces<ApiError>(StatusCodes.Status400BadRequest)
            .Produces<ApiError>(StatusCodes.Status401Unauthorized)
            .Produces<ApiError>(StatusCodes.Status404NotFound)
            .Produces<ApiError>(StatusCodes.Status409Conflict);

        app.MapDelete("/api/reservations/{id:guid}", async Task<IResult> (
                Guid id,
                HttpRequest http, IRequestContextResolver resolver, ReservationService service) =>
            {
                var ctx = resolver.Resolve(http, requireResident: true);
                if (!ctx.IsSuccess) return ctx.Failure.ToHttpResult();

                var failure = await service.CancelAsync(ctx.Value!.BuildingId, ctx.Value.Actor!, id);
                return failure is null ? Results.NoContent() : failure.ToHttpResult();
            })
            .WithName("cancelReservation")
            .WithTags("Reservations")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiError>(StatusCodes.Status401Unauthorized)
            .Produces<ApiError>(StatusCodes.Status403Forbidden)
            .Produces<ApiError>(StatusCodes.Status404NotFound);

        return app;
    }
}
