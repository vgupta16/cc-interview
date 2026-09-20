# Amenity Reservations — Design

A thin vertical slice: residents view an amenity's bookings, book a time window, and cancel their own booking. Backend is .NET 9 minimal API with an in-memory store; the UI calls it through the orval-generated client. Run and test commands are in section 7.

## 1. Assumptions and questions for a PM

**Assumptions**

- A request belongs to one building and one resident, both taken from headers (`X-Building-Id`, `X-Resident-Id`, and `X-Resident-Roles: staff`). These stand in for token claims; a missing header is `401`. The client never sends a resident id in a body.
- A booking is a free-form half-open interval `[start, end)`, so back-to-back bookings (10–11, then 11–12) don't conflict. Times travel as UTC instants and are shown in the viewer's local time zone (the building's own zone is the production choice, since `Building.TimeZone` exists).
- `Capacity` is how many bookings may overlap at any instant; one booking uses one unit.
- Cancelling is a soft state change (`Cancelled`), kept for audit.
- A resident may hold one active (not cancelled, not ended) booking per amenity. This is a per-amenity policy value, so it's configurable.

**Questions for a PM**

1. Fixed slots (30-minute blocks) or free-form start/end? It changes the whole availability UX and the locking design (section 4).
2. Opening hours per amenity, and in which time zone? Can bookings span midnight (guest parking is up to 24h)? How should DST behave?
3. Is "one active booking" per amenity or across all amenities?
4. Cancellation cutoff, and how far ahead can residents book?
5. Can staff book or cancel for a resident? Does the party room need manager approval? Waitlist? Fees?

## 2. Data model

```
Building(Id, Name, TimeZone)                              -- tenant root
Amenity(Id, BuildingId, Name, Description, Capacity, Policy)
AmenityPolicy(MaxBookingMinutes, MaxActivePerResident=1)  -- value object; new rules add fields here
Reservation(Id, BuildingId, AmenityId, ResidentId, StartUtc, EndUtc, Status, CreatedUtc)
ReservationStatus: Active | Cancelled                     -- HoldsCapacity() is true only for Active
```

- `Reservation.BuildingId` equals its amenity's `BuildingId`, denormalized so every query can filter by tenant without a join.
- `EndUtc > StartUtc` and duration `<= Policy.MaxBookingMinutes`.
- **Core invariant:** for each amenity, at every instant, the reservations that hold capacity and cover it number `<= Capacity`. Occupancy code asks `HoldsCapacity()`, never `== Active`, so a later `Pending` or `NoShow` status doesn't touch it.

## 3. API surface

Routes are under `/api`; tenant and resident come from headers. Every non-2xx body is `{ "code", "message" }` (`ApiError`); clients branch on `code`, not message text.

| Route | Request | Success | Errors |
| --- | --- | --- | --- |
| `GET /api/amenities` | | `200 Amenity[]` for this building | `401` |
| `GET /api/reservations?from=&to=` | optional UTC window | `200 Reservation[]`: every active booking in this building, all amenities, by start time | `401` |
| `GET /api/amenities/{id}/reservations?from=&to=` | optional UTC window | `200 Reservation[]` (one amenity, active only) | `404 amenity_not_found` |
| `POST /api/reservations` | `{ amenityId, startUtc, endUtc }` | `201 Reservation` | `400 end_before_start, in_past, max_duration_exceeded` · `404 amenity_not_found` · `409 slot_full, already_has_booking` |
| `DELETE /api/reservations/{id}` | | `204` (also if already cancelled) | `404 reservation_not_found` · `403 not_owner` |

`Reservation` is `{ id, amenityId, residentId, startUtc, endUtc, status, createdUtc }`. `Amenity` keeps the starter's `maxBookingMinutes` field. Seed data has two buildings (Maple Court `aaaa…`, Harbour Tower `bbbb…`); Building A keeps the starter amenity ids (Party Room and Guest Parking capacity 1, Gym capacity 2).

## 4. Preventing double-booking

**Failure mode.** A check-then-act race: two residents book the last unit at the same moment, both read "1 of 2 used", both pass the check, both insert, and the amenity is overbooked. The starter's `List<Reservation>` isn't safe for concurrent writers either.

**Mechanism.** Read, check and write happen inside one critical section per amenity. `IReservationRepository.ExecuteExclusiveAsync(buildingId, amenityId, work)` holds that amenity's lock (in memory, a `SemaphoreSlim` per `(building, amenity)`, because a plain `lock` can't span an `await`) while `work` loads active reservations, runs the booking rules, checks capacity and inserts. Different amenities never block each other.

**Capacity check.** Counting "overlapping bookings >= capacity" is wrong. With capacity 2 and bookings 9–10 and 10–11, a request for 9–11 overlaps both, so a naive count of 2 rejects it, yet occupancy never exceeds 1. `Occupancy.MaxConcurrent` sweeps the existing bookings' start and end events, clipped to the requested window, and returns the true peak. The request fits only if that peak is strictly below `Capacity`.

**Rejected alternative: a fixed slot grid with a unique key** on `(amenityId, slotStart, seatIndex)`. It's trivially correct and maps straight onto a database unique constraint, so it's what I'd pick if the PM answers "fixed slots". I rejected it because free-form durations make it awkward: a 90-minute booking must claim several rows atomically, and any capacity or slot-size change becomes a data migration.

**Production path.** Postgres: a transaction with `SELECT … FOR UPDATE` on the amenity row (or serializable isolation) behind the same repository seam. An exclusion constraint on `tstzrange` is a good backstop but only covers capacity 1. The in-memory lock is correct for one process only.

## 5. Multi-tenancy

- Every tenant-owned entity carries `BuildingId`. The building is resolved once per request by `IRequestContextResolver` (headers here, a validated token claim in production) and never comes from a body or route.
- Every repository and catalog method requires `buildingId`, so a query can't be written unscoped. Exclusive-section state is keyed by `(building, amenity)`, so tenants can't share state or block each other.
- A resource in another building returns **404, not 403**, so the API doesn't reveal that an id exists.
- Production defense in depth: an ORM global query filter and Postgres row-level security, so a missed `WHERE` can't leak data.

## 6. Left out, and extension points

Abstractions exist only where I expect change. I deliberately did not build a generic rules engine, plugin loading, CQRS or a generic repository.

- **Rules are pluggable.** `IBookingRule` returns a `Failure` (kind, code, message) or nothing, and rules run in DI order. The slice has four: end after start, not in the past, max duration, max active per resident. **Opening hours, a booking horizon and a cancellation cutoff are left out** (they need time-zone and DST decisions from a PM); each is a new optional `AmenityPolicy` field, one rule class and one DI line, with no change to endpoints, service or UI. Capacity is deliberately not a rule, so it can't be skipped by mis-registration.
- **Storage, identity, time** sit behind `IReservationRepository`, `IRequestContextResolver` and `TimeProvider`, so a database, real auth and fake clocks slot in. Who may cancel is one method, `CanCancel` (owner or staff).
- **Left out:** real auth, persistence, waitlist, approval flow, fees, recurring bookings (would need a series entity), pagination, a computed `/availability` endpoint (it must reuse the rules pipeline so the client never re-implements them), and notifications (hang them off create/cancel events).
- **Privacy.** Every booking shows the resident's name to everyone in the building. In production other residents would see "Booked", with names only for the owner and staff; that changes what the API returns, so it's left out here.
- **Demo sign-in.** The "Demo: sign in as" dropdown stands in for real authentication so a reviewer can act as different residents; a real product takes identity from login.
- **Idempotency keys.** A retried or double-clicked POST is a different failure from double-booking. Here the one-active-booking rule happens to reject the duplicate, which masks the problem; the real fix is an `Idempotency-Key` header.
- **Capacity units** (a party of 4, a 3-car block) would be a `Units` field that `Occupancy` sums instead of counting.

## 7. Deviations from the scaffold, and verification

**Deviations.** `InMemoryStore` became `SeedData` plus in-memory catalog and repository. The orval client now returns `{ data, status }` (`includeHttpResponseReturnType: true`), because the default never throws on non-2xx, so a `409` would look like success. `frontend/openapi.json` was regenerated from the live API. Errors are a small typed `ApiError` rather than RFC 7807 ProblemDetails, so the generated client types them.

**Known gap.** A body that fails framework binding gets the framework's own `400` (not an `ApiError`), and that runs before the header check. The UI always sends a valid body.

**Run and verify.** The project targets net9.0; with only a newer runtime installed, prefix commands with `DOTNET_ROLL_FORWARD=Major`.

- `cd backend/Api.Tests && dotnet test`: 146 tests written from this doc's invariants (occupancy sweep, rules, authorization, tenant isolation, concurrent races, HTTP through `WebApplicationFactory`).
- `cd e2e && npx playwright test`: 13 browser scenarios through the real UI.
- Mutation checks: removing the per-amenity lock turns 7 concurrency tests red, and loosening the capacity comparison turns the browser double-booking scenarios red. Both were reverted.
