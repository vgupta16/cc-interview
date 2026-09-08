namespace Api.Domain;

/// <summary>
/// A resident's booking of an amenity for a time window [StartUtc, EndUtc) — half-open, so
/// back-to-back bookings (10:00-11:00 and 11:00-12:00) do not conflict.
///
/// Note there is deliberately no BuildingId here: it is derivable through AmenityId, and
/// nothing in an in-memory store would enforce that a denormalized copy stayed in agreement.
/// A drifted copy used for authorization is a security hole, not a stale field. See DESIGN.md.
/// </summary>
public record Reservation
{
    public required Guid Id { get; init; }
    public required Guid AmenityId { get; init; }

    /// <summary>Identifies the resident who made the booking.</summary>
    public required string ResidentId { get; init; }

    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>
    /// Null while the booking is active; set when cancelled. Soft-cancel rather than delete so
    /// the history survives, and it records *when* — which a bare status flag would not.
    /// </summary>
    public DateTimeOffset? CancelledUtc { get; init; }
}
