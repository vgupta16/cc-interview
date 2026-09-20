# Amenity Reservations — Design

Status: **draft, written before the code**. It gets reconciled with what was built before submission (see "Changes from the draft" at the end).

## 1. Assumptions and questions for a PM

**Assumptions I made**

- A request belongs to exactly one building and one resident. Both come from headers (`X-Building-Id`, `X-Resident-Id`, plus `X-Resident-Roles: staff` for staff), standing in for claims in a real auth token. A missing header is a `401`. The client never sends a resident ID in a body.
- A booking is a free-form `[start, end)` interval, not a fixed slot grid. Intervals are half-open, so back-to-back bookings (10–11, then 11–12) don't conflict.
- Times are stored and exchanged as UTC instants. Displaying them in the building's time zone is a UI concern, and this slice shows UTC.
- `Capacity` is how many bookings may overlap at any instant. One booking uses one unit.
- Cancelling is a soft state change (`Cancelled`), not a delete, so there is an audit trail.
- A resident may hold at most one **active** (not cancelled, not in the past) booking per amenity. This limit is a per-amenity policy value, so it is configurable.

**Questions I'd ask a PM**

1. Fixed slots (for example 30-minute blocks) or free-form start/end? This decides the whole availability UX.
2. What are the opening hours per amenity, and in which time zone? Do bookings ever span midnight (guest parking is up to 24h)?
3. Is "one active booking" per amenity or across all amenities? Does a past booking count?
4. Is there a cancellation cutoff (no cancelling within 2h of start), and how far ahead may residents book?
5. Can staff book or cancel on behalf of a resident? Do some amenities (party room) need manager approval?
6. Is a waitlist wanted when a slot is full? Are there fees or deposits?
7. How should DST changes behave for a booking that crosses one?

## 2. Data model

```
Building(Id, TimeZone)                                   -- tenant root
Amenity(Id, BuildingId, Name, Description, Capacity,
        Policy: AmenityPolicy)
AmenityPolicy(MaxBookingMinutes, MaxActivePerResident=1) -- value object, grows with new rules
Reservation(Id, BuildingId, AmenityId, ResidentId,
            StartUtc, EndUtc, Status, CreatedUtc)
ReservationStatus: Active | Cancelled                    -- Status.HoldsCapacity is true only for Active
```

Constraints and invariants:

- `Reservation.BuildingId` always equals its amenity's `BuildingId`. It is denormalized on purpose so every query can filter by tenant without a join.
- `EndUtc > StartUtc` and `EndUtc - StartUtc <= Policy.MaxBookingMinutes`.
- **Core invariant:** for each amenity, at every instant, the number of reservations that hold capacity and cover that instant is `<= Capacity`.
- Occupancy logic asks `Status.HoldsCapacity`, never `== Active`. Adding `Pending` or `NoShow` later does not touch the double-booking code.

## 3. API surface

All routes are under `/api`. Tenant and resident come from headers. Every non-2xx response has the body `{ "code", "message" }` (`ApiError`); clients branch on the stable `code`, never on message text. (In production I'd emit RFC 7807 ProblemDetails with `code` as an extension member; a small typed `ApiError` keeps the generated client simple here.)

| Method and route | Request | Success | Errors |
| --- | --- | --- | --- |
| `GET /api/amenities` | none | `200 Amenity[]` (this building only) | `401` missing building header |
| `GET /api/amenities/{amenityId}/reservations?from=&to=` | optional UTC window | `200 Reservation[]` (Active only) | `404` `amenity_not_found` (unknown, or in another building) |
| `POST /api/reservations` | `{ amenityId, startUtc, endUtc }` | `201 Reservation` | `400` validation (`end_before_start`, `in_past`, `max_duration_exceeded`), `404` `amenity_not_found`, `409` conflict (`slot_full`, `already_has_booking`) |
| `DELETE /api/reservations/{id}` | none | `204` (also when already cancelled) | `404` `reservation_not_found` (unknown or other building), `403` `not_owner` |

`Reservation` response: `{ id, amenityId, residentId, startUtc, endUtc, status, createdUtc }`. `Amenity` response keeps the starter's `maxBookingMinutes` field so the existing client keeps working.

Not built: a computed `/availability` endpoint (free windows for a day). The UI shows existing bookings plus a clear conflict error instead. When we add it, it must reuse the same rules pipeline so the client never re-implements booking rules.

Stable error codes: `missing_building`, `missing_resident` (401); `end_before_start`, `in_past`, `max_duration_exceeded` (400); `amenity_not_found`, `reservation_not_found` (404); `not_owner` (403); `already_has_booking`, `slot_full` (409).

**UI behavior contract** (what the browser tests rely on):

- `building-switcher` and `resident-switcher` are `<select>`s. Building option values are the building GUIDs; resident option values are `alice`, `bob`, `carol`. Defaults: Building A, `alice`. Every API call sends the selected building and resident as `X-Building-Id` and `X-Resident-Id`.
- Clicking `amenity-card-<amenityId>` selects that amenity: its active reservations render as `reservation-row-<reservationId>` (row text includes the resident id and the UTC start/end) and the booking form appears. Switching building reloads the amenity list and clears the selection.
- `book-start` is an `<input type="datetime-local">` (interpreted in the browser's local zone and sent as UTC). `book-duration` is a number input in minutes. `book-submit` submits.
- On success the list refreshes and the form clears. On failure, `booking-error` is shown with the server's message and a `data-code` attribute holding the error `code`; it is absent otherwise.
- `cancel-<reservationId>` exists only on rows owned by the currently selected resident. Cancelling removes the row.

**UI test contract (`data-testid`)**: `building-switcher`, `resident-switcher`, `amenity-card-<amenityId>`, `book-start`, `book-duration`, `book-submit`, `booking-error`, `reservation-row-<reservationId>`, `cancel-<reservationId>`. Seed data: buildings `aaaaaaaa-…` (Maple Court) and `bbbbbbbb-…` (Harbour Tower); Building A amenities keep the starter ids `1111…` Party Room (capacity 1), `2222…` Gym (capacity 2), `3333…` Guest Parking (capacity 1).

## 4. Preventing double-booking

**Failure mode.** A check-then-act race (TOCTOU). Two residents book the last unit of an amenity at the same moment. Both requests read "1 of 2 used", both pass the check, both insert, and the amenity ends up overbooked. The starter's `List<Reservation>` also isn't safe for concurrent writers.

**Mechanism.** The read, the check and the write happen inside one critical section per amenity. `IReservationRepository.ExecuteExclusiveAsync(buildingId, amenityId, work)` runs `work` while holding that amenity's lock (in memory, a per-amenity lock keyed by amenity ID). Inside `work` the service loads the amenity's active reservations, runs the booking rules, checks capacity, and inserts. Different amenities never block each other.

**Capacity check.** Counting "overlapping bookings >= capacity" is wrong: with capacity 2 and bookings 9–10 and 10–11 (which never overlap each other), a request for 9–11 overlaps both, so a naive count of 2 rejects it although occupancy never exceeds 1. `Occupancy.MaxConcurrent` sweeps the start and end events of the overlapping bookings plus the candidate and returns the true peak. The request is rejected only if the peak would exceed `Capacity`.

**Alternative rejected: a fixed slot grid with a unique key** on `(amenityId, slotStart, seatIndex)`. It is trivially correct and maps directly to a database unique constraint, so it would be my choice if the PM answers "fixed slots". I rejected it because free-form durations make it awkward: a 90-minute booking must claim several slot rows atomically, and every capacity or slot-size change becomes a data migration.

**Production path.** With Postgres, the same seam becomes a transaction with `SELECT ... FOR UPDATE` on the amenity row (or serializable isolation), which serializes writers per amenity. A Postgres exclusion constraint on `tstzrange` is a good backstop, but on its own it only covers capacity 1. In-memory locking works for a single process only, so a multi-instance deployment needs the database version.

## 5. Multi-tenancy

- Every tenant-owned entity carries `BuildingId`, and the building is resolved once per request by a single `IRequestContextResolver` (headers here, a validated token claim in production). The building never comes from a request body or route.
- Every repository and catalog method requires `buildingId`, so a query can't be written without scoping.
- A resource in another building returns **404, not 403**, so the API doesn't reveal that an ID exists.
- Locks are keyed by amenity ID, which is globally unique, so one tenant can't block another.
- In production I'd add defense in depth: a global query filter in the data layer and Postgres row-level security, so a missed `WHERE` can't leak data.
- The slice seeds two buildings so isolation is testable and demonstrable.

## 6. Extension points

Abstractions exist only where I expect change, and I deliberately did not build a generic rules engine, plugin loading, CQRS or a generic repository.

- **Rules pipeline (`IBookingRule`).** Each rule inspects a `BookingContext` and returns a `Failure` (kind, code, message; kind maps to 400 or 409) or nothing. Rules are registered in DI. In the slice: end after start, not in the past, max duration, max active per resident. A new rule needs no change to endpoints, service or UI. Capacity is deliberately not a rule: it's the core invariant and can't be skipped by mis-registration.
- **Opening hours slot in here.** Add an optional `AvailabilityWindow` to `AmenityPolicy`, resolve the building time zone through an `IBuildingTimeZoneProvider`, add one `OpeningHoursRule` and one DI line.
- **Storage** sits behind `IReservationRepository`, so the in-memory store can be replaced without touching rules or service.
- **Authorization** is a single `CanCancel(actor, reservation)` method (owner, or staff).
- **Time** comes from `TimeProvider`, so time-dependent rules are testable.
- **Events** (created, cancelled) are the place to hang notifications, audit and waitlist promotion.

## 7. What I left out, and why

| Left out | Why |
| --- | --- |
| Opening hours, booking horizon, cancellation cutoff | Need time-zone and DST decisions from the PM. The seams above make each one additive. |
| Real auth and building/resident management | Headers stand in for identity. Not where the design risk is. |
| Persistence | In-memory, as the brief expects. Storage is behind an interface. |
| Waitlist, approval flow, fees, recurring bookings | Product questions. Recurring bookings would need a series entity. |
| Idempotency keys | A retried or double-clicked POST is a different failure from a double-booking: the same resident sending the same request twice. In this slice the "one active booking" rule happens to reject the duplicate, which masks the problem. Real fix: an `Idempotency-Key` header. |
| Capacity units per booking | A booking that takes several units (a party of 4, a 3-car block) is a small extension: `Occupancy` would sum units instead of counting. |
| Pagination, `/availability`, notifications | Unneeded for a slice, and the seams above leave room for them. |

## 8. Changes from the draft

_To be filled in when the implementation is finished._
