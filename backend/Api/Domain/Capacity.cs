namespace Api.Domain;

/// <summary>
/// The single place a booking is allowed or denied. Both the availability endpoint and the
/// create endpoint call in here, so what the UI shows and what the server enforces can never
/// drift apart.
/// </summary>
public static class Capacity
{
    /// <summary>Headcount still free in this slot, or null when the amenity doesn't track it.</summary>
    public static int? RemainingSize(Amenity amenity, IReadOnlyList<Reservation> slot) =>
        amenity.TotalCapacity is int total
            ? Math.Max(0, total - slot.Sum(r => r.Size))
            : null;

    /// <summary>Booking slots still free, or null when the amenity doesn't track it.</summary>
    public static int? RemainingReservations(Amenity amenity, IReadOnlyList<Reservation> slot) =>
        amenity.ReservationCapacity is int max
            ? Math.Max(0, max - slot.Count)
            : null;

    /// <summary>
    /// null == this booking is allowed. Otherwise the HTTP status and the message to show the
    /// resident. 400 means "your request is wrong" (fixable by asking for less); 409 means
    /// "someone got here first" (fixable by picking another slot).
    /// </summary>
    public static (int Status, string Message)? Reject(
        Amenity amenity, IReadOnlyList<Reservation> slot, int size)
    {
        if (!amenity.IsOpen)
            return (409, $"{amenity.Name} is currently closed.");

        if (size < 1 || size > amenity.MaxSizePerReservation)
            return (400, $"Reservation size must be between 1 and {amenity.MaxSizePerReservation}.");

        if (RemainingReservations(amenity, slot) is 0)
            return (409, "This slot is fully booked.");

        if (RemainingSize(amenity, slot) is int remaining && size > remaining)
            return remaining == 0
                ? (409, "This slot is fully booked.")
                : (409, $"Only {remaining} space(s) left in this slot — you asked for {size}.");

        return null;
    }
}
