using Api.Domain;

namespace Api.Contracts;

/// <summary>
/// An occupied window. This type exists to make anonymity structural rather than remembered: it
/// has no field capable of identifying who holds the slot, so no code path can leak one.
/// </summary>
public sealed record BusyWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);

/// <summary>
/// One of the caller's own bookings, carrying the id needed to cancel it.
///
/// <para>
/// There is deliberately no resident id here either. The caller already knows who they are, so
/// echoing it back buys nothing and would create a field that a future change could accidentally
/// populate for someone else.
/// </para>
/// </summary>
public sealed record MyReservation(
    Guid Id,
    Guid AmenityId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    ReservationStatus Status);

/// <summary>
/// What one amenity looks like over a window.
///
/// <para>
/// <see cref="Busy"/> is the complete occupancy picture — every active booking, the caller's own
/// included — so availability is computed from a single array with no client-side merging.
/// <see cref="Mine"/> is the actionable subset. The overlap between them is intentional.
/// </para>
/// </summary>
public sealed record AmenityScheduleResponse(
    Guid AmenityId,
    string AmenityName,
    int Capacity,
    int MaxBookingMinutes,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    IReadOnlyList<BusyWindow> Busy,
    IReadOnlyList<MyReservation> Mine);

/// <summary>
/// Body of a create request. Note the absence of a resident id: identity comes from the caller's
/// credentials, never the payload, or anyone could book as anyone.
/// </summary>
public sealed record CreateReservationRequest(
    Guid AmenityId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

/// <summary>
/// A resident the dev-only identity picker can act as. Exists solely because there is no auth in
/// this exercise; a real deployment would have no such endpoint.
/// </summary>
public sealed record ResidentSummary(
    Guid Id,
    string DisplayName,
    string? UnitNumber,
    Guid BuildingId,
    string BuildingName,
    string BuildingTimeZoneId);
