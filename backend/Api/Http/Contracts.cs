using Api.Application;
using Api.Domain;

namespace Api.Http;

// Wire types, kept separate from the domain records so the API contract can evolve on its own.
// `required` members are what make the OpenAPI schema (and the generated client) non-optional.

public sealed record AmenityResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required int Capacity { get; init; }

    /// <summary>Kept at the top level (as in the starter) even though it now lives on the policy.</summary>
    public required int MaxBookingMinutes { get; init; }

    public static AmenityResponse From(Amenity a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        Description = a.Description,
        Capacity = a.Capacity,
        MaxBookingMinutes = a.Policy.MaxBookingMinutes,
    };
}

public sealed record CreateReservationRequest
{
    public required Guid AmenityId { get; init; }
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
}

public sealed record ReservationResponse
{
    public required Guid Id { get; init; }
    public required Guid AmenityId { get; init; }
    public required string ResidentId { get; init; }
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
    public required ReservationStatus Status { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }

    public static ReservationResponse From(Reservation r) => new()
    {
        Id = r.Id,
        AmenityId = r.AmenityId,
        ResidentId = r.ResidentId,
        StartUtc = r.StartUtc,
        EndUtc = r.EndUtc,
        Status = r.Status,
        CreatedUtc = r.CreatedUtc,
    };
}

/// <summary>Error body for every non-2xx response. Clients branch on <c>Code</c>, not on message text.</summary>
public sealed record ApiError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public static class FailureHttpExtensions
{
    public static IResult ToHttpResult(this Failure failure)
    {
        var status = failure.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };
        return Results.Json(new ApiError { Code = failure.Code, Message = failure.Message }, statusCode: status);
    }
}
