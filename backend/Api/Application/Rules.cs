using Api.Domain;

namespace Api.Application;

/// <summary>Who is making the request. Comes from the request context, never from a body.</summary>
public sealed record Actor(string ResidentId, bool IsStaff = false);

/// <summary>Everything a rule needs to judge a booking request. Rules never touch storage.</summary>
public sealed record BookingContext(
    Amenity Amenity,
    Actor Actor,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset NowUtc,
    /// <summary>Reservations of this amenity that hold capacity, as of inside the exclusive section.</summary>
    IReadOnlyList<Reservation> ActiveReservations);

/// <summary>
/// One pluggable booking rule. Stateless and side-effect free: return a <see cref="Failure"/> to
/// reject the request, or null to let it through. Rules are registered in DI and run in order
/// (validation rules first). New rules need no change to endpoints, service or UI.
/// </summary>
public interface IBookingRule
{
    Failure? Evaluate(BookingContext context);
}

/// <summary><c>end_before_start</c> (Validation): the window must have positive length.</summary>
public sealed class EndAfterStartRule : IBookingRule
{
    public Failure? Evaluate(BookingContext context) =>
        context.EndUtc > context.StartUtc
            ? null
            : new Failure(ErrorKind.Validation, ErrorCodes.EndBeforeStart, "The booking must end after it starts.");
}

/// <summary><c>in_past</c> (Validation): the booking must not start before <c>NowUtc</c>.</summary>
public sealed class NotInPastRule : IBookingRule
{
    public Failure? Evaluate(BookingContext context) =>
        context.StartUtc >= context.NowUtc
            ? null
            : new Failure(ErrorKind.Validation, ErrorCodes.InPast, "The booking can't start in the past.");
}

/// <summary><c>max_duration_exceeded</c> (Validation): length must be &lt;= <c>Policy.MaxBookingMinutes</c>.</summary>
public sealed class MaxDurationRule : IBookingRule
{
    public Failure? Evaluate(BookingContext context)
    {
        var max = context.Amenity.Policy.MaxBookingMinutes;
        return (context.EndUtc - context.StartUtc).TotalMinutes <= max
            ? null
            : new Failure(ErrorKind.Validation, ErrorCodes.MaxDurationExceeded,
                $"{context.Amenity.Name} can be booked for at most {max} minutes at a time.");
    }
}

/// <summary>
/// <c>already_has_booking</c> (Conflict): the actor must hold fewer than
/// <c>Policy.MaxActivePerResident</c> reservations that hold capacity and haven't ended yet.
/// </summary>
public sealed class OneActivePerResidentRule : IBookingRule
{
    public Failure? Evaluate(BookingContext context)
    {
        var max = context.Amenity.Policy.MaxActivePerResident;
        var held = context.ActiveReservations.Count(r =>
            r.Status.HoldsCapacity() && r.ResidentId == context.Actor.ResidentId && r.EndUtc > context.NowUtc);

        return held < max
            ? null
            : new Failure(ErrorKind.Conflict, ErrorCodes.AlreadyHasBooking,
                $"You already have {(max == 1 ? "a booking" : $"{max} bookings")} for {context.Amenity.Name}. Cancel it to book another.");
    }
}
