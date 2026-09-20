namespace Api.Domain;

public enum ReservationStatus
{
    Active,
    Cancelled,
}

public static class ReservationStatusExtensions
{
    /// <summary>
    /// Whether a reservation in this status occupies capacity. Occupancy and rules ask this rather
    /// than comparing to <see cref="ReservationStatus.Active"/>, so adding Pending/NoShow later
    /// doesn't touch the double-booking logic.
    /// </summary>
    public static bool HoldsCapacity(this ReservationStatus status) => status == ReservationStatus.Active;
}

/// <summary>
/// A resident's booking of an amenity for the half-open window [StartUtc, EndUtc).
/// </summary>
public record Reservation
{
    public required Guid Id { get; init; }

    /// <summary>Denormalized from the amenity so every query can filter by tenant without a join.</summary>
    public required Guid BuildingId { get; init; }

    public required Guid AmenityId { get; init; }

    /// <summary>Identifies the resident who made the booking (taken from the request context, never the body).</summary>
    public required string ResidentId { get; init; }

    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }

    public ReservationStatus Status { get; init; } = ReservationStatus.Active;
    public required DateTimeOffset CreatedUtc { get; init; }
}
