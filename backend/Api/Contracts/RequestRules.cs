using System.Globalization;
using System.Text.RegularExpressions;
using Api.Domain;
using Api.Store;

namespace Api.Contracts;

/// <summary>
/// Rules shared by the endpoints: resolving the requesting resident from its header, and
/// validating a reservation window's start and duration.
/// </summary>
public static partial class RequestRules
{
    /// <summary>The sample identity header, in place of a bearer token (A7).</summary>
    public const string ResidentHeader = "X-RESIDENT-ID";

    /// <summary>
    /// Availability returns the selected window plus up to four more, 30 minutes apart (A17). It
    /// returns fewer only when a later window would start more than <see cref="MaxDaysAhead"/> days ahead.
    /// </summary>
    public const int AvailabilityWindowCount = 5;

    /// <summary>How far ahead of the server's current time a reservation can start (A15).</summary>
    public const int MaxDaysAhead = 90;

    /// <summary>The latest start a window can have at <paramref name="now"/> (A15).</summary>
    public static DateTimeOffset LatestStart(DateTimeOffset now) => now.AddDays(MaxDaysAhead);

    /// <summary>
    /// Reads the header as a raw value, so a missing or malformed header reaches the application
    /// instead of failing framework binding with 400. A missing header, a malformed value, and an
    /// unknown ID all return null, which the endpoint turns into 401.
    /// </summary>
    public static Resident? FindRequestingResident(HttpRequest request, InMemoryStore store)
    {
        return Guid.TryParse(request.Headers[ResidentHeader].ToString(), out var residentId)
            ? store.FindResident(residentId)
            : null;
    }

    /// <summary>
    /// Validates a window's start and duration against an amenity (A8, A12, A14, A15). Returns the
    /// errors keyed by request field; when there are none, <paramref name="windowStart"/> and
    /// <paramref name="duration"/> are set.
    /// </summary>
    public static Dictionary<string, string[]> ValidateWindow(
        string? startUtc,
        int? durationMinutes,
        Amenity amenity,
        DateTimeOffset now,
        out DateTimeOffset windowStart,
        out TimeSpan duration
    )
    {
        var errors = new Dictionary<string, string[]>();
        var slotMinutes = (int)InMemoryStore.SlotLength.TotalMinutes;

        windowStart = default;
        duration = default;

        if (!TryParseUtc(startUtc, out var start))
        {
            errors["startUtc"] =
            [
                "Use an ISO 8601 UTC timestamp ending in Z or +00:00, for example 2026-10-01T10:00:00Z.",
            ];
        }
        else if (start.UtcTicks % InMemoryStore.SlotLength.Ticks != 0)
        {
            errors["startUtc"] = ["Start on a 30-minute boundary, with no seconds."];
        }
        else if (start <= now)
        {
            errors["startUtc"] = ["Start in the future."];
        }
        else if (start > LatestStart(now))
        {
            errors["startUtc"] = [$"Start no more than {MaxDaysAhead} days from now."];
        }
        else
        {
            windowStart = start;
        }

        if (durationMinutes is not { } minutes)
        {
            errors["durationMinutes"] = ["Required."];
        }
        else if (minutes <= 0 || minutes % slotMinutes != 0)
        {
            errors["durationMinutes"] = [$"Use a positive multiple of {slotMinutes} minutes."];
        }
        else if (minutes > amenity.MaxBookingMinutes)
        {
            errors["durationMinutes"] =
            [
                $"Can't exceed {amenity.MaxBookingMinutes} minutes for this amenity.",
            ];
        }
        else
        {
            duration = TimeSpan.FromMinutes(minutes);
        }

        return errors;
    }

    /// <summary>
    /// Accepts only ISO 8601 with an explicit UTC suffix. Default parsing would give a timestamp
    /// without an offset the server's local offset, so the suffix is checked on the raw text.
    /// </summary>
    private static bool TryParseUtc(string? value, out DateTimeOffset utc)
    {
        utc = default;

        return value is not null
            && UtcTimestamp().IsMatch(value)
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out utc
            );
    }

    // date, hours, and minutes are required; seconds and fractions are optional (JavaScript's
    // toISOString adds ".000"). The suffix must be Z or +00:00.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|\+00:00)$")]
    private static partial Regex UtcTimestamp();
}
