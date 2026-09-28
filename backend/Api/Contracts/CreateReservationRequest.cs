namespace Api.Contracts;

/// <summary>
/// The body of <c>POST /api/reservations</c>. The server takes the resident and building from the
/// <c>X-RESIDENT-ID</c> header and calculates the end from the duration.
/// </summary>
public record CreateReservationRequest
{
    public required Guid AmenityId { get; init; }

    /// <summary>
    /// ISO 8601 UTC start ending in <c>Z</c> or <c>+00:00</c>, on a 30-minute boundary, in the future and
    /// no more than 90 days ahead.
    /// </summary>
    public required string StartUtc { get; init; }

    /// <summary>A positive multiple of 30, up to the amenity's <c>MaxBookingMinutes</c>.</summary>
    public required int DurationMinutes { get; init; }
}
