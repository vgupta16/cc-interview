using System.Diagnostics.CodeAnalysis;

namespace Api.Application;

public enum ErrorKind
{
    /// <summary>Request is malformed or breaks a rule (HTTP 400).</summary>
    Validation,
    /// <summary>No identity was supplied (HTTP 401).</summary>
    Unauthorized,
    /// <summary>Caller is known but not allowed (HTTP 403).</summary>
    Forbidden,
    /// <summary>Resource doesn't exist in the caller's building (HTTP 404).</summary>
    NotFound,
    /// <summary>Request is valid but conflicts with current state (HTTP 409).</summary>
    Conflict,
}

/// <summary>A business or request failure with a stable, machine-readable <see cref="Code"/>.</summary>
public sealed record Failure(ErrorKind Kind, string Code, string Message);

public sealed record Result<T>
{
    public T? Value { get; private init; }
    public Failure? Failure { get; private init; }

    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => Failure is null;

    public static Result<T> Ok(T value) => new() { Value = value };
    public static Result<T> Fail(Failure failure) => new() { Failure = failure };
}

/// <summary>Stable error codes. Clients and tests key off these, never off message text.</summary>
public static class ErrorCodes
{
    public const string MissingBuilding = "missing_building";
    public const string MissingResident = "missing_resident";
    public const string AmenityNotFound = "amenity_not_found";
    public const string ReservationNotFound = "reservation_not_found";
    public const string NotOwner = "not_owner";
    public const string EndBeforeStart = "end_before_start";
    public const string InPast = "in_past";
    public const string MaxDurationExceeded = "max_duration_exceeded";
    public const string AlreadyHasBooking = "already_has_booking";
    public const string SlotFull = "slot_full";
}
