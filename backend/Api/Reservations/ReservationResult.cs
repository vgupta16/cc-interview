using Api.Domain;

namespace Api.Reservations;

/// <summary>
/// Why a reservation operation failed. Deliberately not HTTP status codes: the service stays
/// transport-agnostic and testable, and <c>Program.cs</c> owns the mapping to status codes.
/// </summary>
public enum ReservationErrorCode
{
    /// <summary>
    /// No such amenity — or one in a different building. Those two cases are intentionally
    /// indistinguishable, so a caller can't probe another building's inventory.
    /// </summary>
    AmenityNotFound,

    /// <summary>No such reservation, or one belonging to another resident. Also intentionally merged.</summary>
    ReservationNotFound,

    /// <summary>Inverted or zero-length window, or a schedule query whose range is backwards.</summary>
    InvalidInterval,

    ExceedsMaxBookingLength,
    StartsInPast,
    BeyondBookingHorizon,

    /// <summary>The amenity is at capacity for some instant in the requested window.</summary>
    SlotUnavailable,
}

/// <param name="Conflicts">
/// Populated only for <see cref="ReservationErrorCode.SlotUnavailable"/>: the occupied windows the
/// request ran into, so the UI can suggest an alternative. Windows only, never their owners.
/// </param>
public sealed record ReservationError(
    ReservationErrorCode Code,
    string Detail,
    IReadOnlyList<TimeWindow>? Conflicts = null);

/// <summary>Either a value or a <see cref="ReservationError"/>, never both.</summary>
public sealed record ReservationResult<T>
{
    private ReservationResult(T? value, ReservationError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public ReservationError? Error { get; }

    public bool Succeeded => Error is null;

    public static ReservationResult<T> Ok(T value) => new(value, null);

    public static ReservationResult<T> Fail(ReservationError error) => new(default, error);

    public static ReservationResult<T> Fail(ReservationErrorCode code, string detail) =>
        new(default, new ReservationError(code, detail));
}

/// <summary>
/// An amenity's bookings over a window, as the service sees them: still domain objects, with
/// resident identity intact. Projecting this to a response is a separate, deliberate step —
/// see <c>Api.Contracts.ReservationProjection</c>.
/// </summary>
public sealed record AmenitySchedule(
    Amenity Amenity,
    TimeWindow Window,
    IReadOnlyList<Reservation> Active);
