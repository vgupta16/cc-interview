using System.Text.Json.Serialization;

namespace Api.Domain;

/// <summary>
/// Lifecycle of a reservation. Cancellation is a state transition rather than a delete: it is an
/// event the business cares about (audit, no-show policy, notifications), and keeping the row
/// makes availability a pure filter on Active.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReservationStatus>))]
public enum ReservationStatus
{
    Active,
    Canceled,
}

/// <summary>
/// A resident's booking of an amenity for a time window.
///
/// The window is half-open: [StartUtc, EndUtc). Back-to-back bookings (4–6 and 6–8) therefore do
/// not conflict. This convention is applied everywhere availability is computed.
/// </summary>
public record Reservation
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Denormalized from the amenity. Derivable, and normally we wouldn't store it — it is safe
    /// only because an amenity never changes buildings, which makes this immutable and drift-free.
    /// It buys a composite (building_id, amenity_id, start_utc) index, natural partitioning, and
    /// row-level security keyed on a column that is physically present.
    /// </summary>
    public required Guid BuildingId { get; init; }

    public required Guid AmenityId { get; init; }

    /// <summary>The resident who made the booking. Taken from the caller's identity, never the body.</summary>
    public required Guid ResidentId { get; init; }

    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }

    public ReservationStatus Status { get; init; } = ReservationStatus.Active;

    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? CanceledAtUtc { get; init; }

    /// <summary>The booked interval as a half-open window. Convenience for availability checks.</summary>
    [JsonIgnore]
    public TimeWindow Window => new(StartUtc, EndUtc);

    [JsonIgnore]
    public TimeSpan Duration => EndUtc - StartUtc;

    [JsonIgnore]
    public bool IsActive => Status == ReservationStatus.Active;
}
