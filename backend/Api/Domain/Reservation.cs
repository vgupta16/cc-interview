namespace Api.Domain;

public enum ReservationStatus
{
    Active,
    Cancelled,
}

/// <summary>
/// A resident's booking of an amenity for a time window.
///
/// Soft-deleted via <see cref="Status"/> rather than removed on cancel, so history is
/// preserved (e.g. for "one active booking per resident" checks and future auditing).
/// </summary>
public record Reservation
{
    public required Guid Id { get; init; }
    public required Guid AmenityId { get; init; }

    /// <summary>Identifies the resident who made the booking. No auth in this slice — see design doc.</summary>
    public required string ResidentId { get; init; }

    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }

    public ReservationStatus Status { get; init; } = ReservationStatus.Active;
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Request body for creating a reservation.</summary>
public record CreateReservationRequest
{
    public required Guid AmenityId { get; init; }
    public required string ResidentId { get; init; }
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
}
