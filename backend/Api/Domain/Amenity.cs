using System.Text.Json.Serialization;

namespace Api.Domain;

/// <summary>
/// The kind of amenity, used for grouping and filtering. A building may hold several amenities of
/// the same kind (two party rooms), each with its own rules.
///
/// Serialized as a string so the generated TypeScript client gets a union type instead of ints.
/// A production system would promote this to a lookup table so new kinds don't need a deploy.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AmenityKind>))]
public enum AmenityKind
{
    PartyRoom,
    Gym,
    GuestParking,
}

/// <summary>
/// A single bookable amenity in a building (gym, party room, guest parking, ...).
///
/// This models the *instance*, not the type: "Party Room (East)" and "Party Room (Main)" are two
/// Amenity records that share a Kind. Rules therefore live per instance, which makes both
/// per-instance and per-building divergence free. See DESIGN.md §4 for why that direction is the
/// one that avoids a painful migration later.
/// </summary>
public record Amenity
{
    public required Guid Id { get; init; }

    /// <summary>
    /// The building this amenity belongs to. Assumed immutable — an amenity never moves between
    /// buildings. Reservation.BuildingId is denormalized on the strength of that assumption, so
    /// anything that lets amenities move must revisit it.
    /// </summary>
    public required Guid BuildingId { get; init; }

    public required string Name { get; init; }
    public required AmenityKind Kind { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// How many reservations may overlap at any single instant. 1 == exclusive.
    /// Note this is a concurrency limit on *reservations*, not on people (open PM question).
    /// </summary>
    public int Capacity { get; init; } = 1;

    /// <summary>Upper bound on a single booking length, in minutes.</summary>
    public int MaxBookingMinutes { get; init; } = 120;

    /// <summary>
    /// Earliest local time a booking may start. Not enforced in this slice — the requirement is
    /// underspecified (see DESIGN.md §2, question 5) and guest parking's 24-hour bookings
    /// contradict a naive open/close window. Present so enforcing it later is purely additive.
    /// </summary>
    public TimeOnly? OpeningLocalTime { get; init; }

    /// <summary>Latest local time a booking may end. Not enforced in this slice; see above.</summary>
    public TimeOnly? ClosingLocalTime { get; init; }
}
