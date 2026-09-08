namespace Api.Contracts;

/// <summary>
/// Body for creating a reservation. The amenity comes from the route and the resident from the
/// X-Resident-Id header, so neither is repeated here — a client-supplied copy of either would
/// just be something to validate for agreement.
/// </summary>
public record CreateReservationRequest(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
