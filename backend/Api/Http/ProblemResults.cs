using Api.Contracts;
using Api.Reservations;

namespace Api.Http;

/// <summary>
/// Maps service errors onto RFC 9457 problem responses. Owning the mapping here rather than in
/// the service keeps HTTP concerns out of the booking logic.
///
/// <para>
/// Every problem carries a machine-readable <c>code</c> extension so the UI can branch on the
/// reason — surfacing a conflict differently from a validation failure — without matching on
/// prose that translation or copy edits would break.
/// </para>
/// </summary>
public static class ProblemResults
{
    public static IResult ResidentRequired() => Results.Problem(
        title: "Resident required",
        detail: $"Send a known resident id in the {RequestContext.ResidentHeaderName} header. "
              + "In a real deployment this would come from the authenticated session.",
        statusCode: StatusCodes.Status401Unauthorized,
        extensions: new Dictionary<string, object?> { ["code"] = "resident_required" });

    public static IResult From(ReservationError error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = WireCode(error.Code) };

        // Conflicts are reported as bare windows so the UI can suggest another time. Never who
        // holds them — see ReservationProjection.
        if (error.Conflicts is { Count: > 0 } conflicts)
        {
            extensions["conflicts"] = conflicts
                .Select(w => new BusyWindow(w.StartUtc, w.EndUtc))
                .ToList();
        }

        return Results.Problem(
            title: Title(error.Code),
            detail: error.Detail,
            statusCode: Status(error.Code),
            extensions: extensions);
    }

    private static int Status(ReservationErrorCode code) => code switch
    {
        // Cross-tenant requests land here too: a 403 would confirm the resource exists.
        ReservationErrorCode.AmenityNotFound or
        ReservationErrorCode.ReservationNotFound => StatusCodes.Status404NotFound,

        ReservationErrorCode.SlotUnavailable => StatusCodes.Status409Conflict,

        _ => StatusCodes.Status400BadRequest,
    };

    private static string Title(ReservationErrorCode code) => code switch
    {
        ReservationErrorCode.AmenityNotFound or
        ReservationErrorCode.ReservationNotFound => "Not found",
        ReservationErrorCode.SlotUnavailable => "Slot unavailable",
        _ => "Invalid booking",
    };

    private static string WireCode(ReservationErrorCode code) => code switch
    {
        ReservationErrorCode.AmenityNotFound => "amenity_not_found",
        ReservationErrorCode.ReservationNotFound => "reservation_not_found",
        ReservationErrorCode.InvalidInterval => "invalid_interval",
        ReservationErrorCode.ExceedsMaxBookingLength => "exceeds_max_booking_length",
        ReservationErrorCode.StartsInPast => "starts_in_past",
        ReservationErrorCode.BeyondBookingHorizon => "beyond_booking_horizon",
        ReservationErrorCode.SlotUnavailable => "slot_unavailable",
        _ => "reservation_error",
    };
}
