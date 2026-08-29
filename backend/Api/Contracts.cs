namespace Api.Contracts;

/// <summary>
/// One hour-long slot as the UI needs to render it: how much room is left, and whether the
/// caller already holds a booking here (so the same row can offer Book or Cancel without a
/// second round trip).
/// </summary>
public record SlotAvailability
{
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>Headcount still free. null = this amenity doesn't cap headcount.</summary>
    public int? RemainingSize { get; init; }

    /// <summary>Booking slots still free. null = this amenity doesn't cap booking count.</summary>
    public int? RemainingReservations { get; init; }

    /// <summary>False if closed, already started, or full for even a party of one.</summary>
    public required bool IsAvailable { get; init; }

    /// <summary>Why this slot can't be booked, or null when it can. Rendered verbatim.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>Set when the calling resident already booked this slot.</summary>
    public Guid? MyReservationId { get; init; }
}

public record CreateReservationRequest
{
    /// <summary>Stand-in for an authenticated identity. See Design.md — this is not auth.</summary>
    public required Guid ResidentId { get; init; }
    public required Guid AmenityId { get; init; }

    /// <summary>Must be on the hour. The booking always runs for exactly one hour.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    public int Size { get; init; } = 1;
}
