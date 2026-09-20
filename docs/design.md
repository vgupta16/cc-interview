# Amenity Reservations — Design

A thin slice: residents view upcoming bookings, book a window on an amenity, and cancel their own. .NET 9 API with an in-memory store; React UI on the generated client.

## 1. Assumptions and questions for a PM

**Assumptions**

- One building and one resident per request, from headers (`X-Building-Id`, `X-Resident-Id`, `X-Resident-Roles: staff`) standing in for token claims. A missing header is `401`; a resident id is never read from a body.
- A booking is a free-form half-open interval `[start, end)`, so 10–11 then 11–12 don't conflict. Times travel as UTC and display in the viewer's local zone.
- `Capacity` is how many bookings may overlap at once. Cancelling is a soft state change. One active booking per resident per amenity (a policy value).

**Questions for a PM**

1. Fixed slots or free-form start/end? It changes the availability UX and the locking design (section 4).
2. Opening hours per amenity, in which time zone (midnight, DST)? Is "one active booking" per amenity or global?
3. Cancellation cutoff, staff overrides, approval flow, waitlist?

## 2. Data model

```
Building(Id, Name, TimeZone)                                  -- tenant root
Amenity(Id, BuildingId, Name, Description, Capacity, Policy{MaxBookingMinutes, MaxActivePerResident=1})
Reservation(Id, BuildingId, AmenityId, ResidentId, StartUtc, EndUtc, Status: Active|Cancelled, CreatedUtc)
```

`Reservation.BuildingId` equals its amenity's (denormalized, so queries filter by tenant without a join); `EndUtc > StartUtc`; duration `<= MaxBookingMinutes`. **Invariant:** for each amenity, at every instant, active bookings covering it number `<= Capacity`.

## 3. API surface

Under `/api`; tenant and resident come from headers. Every non-2xx body is `{ "code", "message" }`.

| Route | Request | Success | Errors |
| --- | --- | --- | --- |
| `GET /amenities` | | `200 Amenity[]` | `401` |
| `GET /reservations`, `GET /amenities/{id}/reservations` | `?from&to` | `200 Reservation[]` (building-wide, or one amenity) | `401`; `404 amenity_not_found` |
| `POST /reservations` | `{ amenityId, startUtc, endUtc }` | `201 Reservation` | `400 end_before_start, in_past, max_duration_exceeded` · `404` · `409 slot_full, already_has_booking` |
| `DELETE /reservations/{id}` | | `204` (idempotent) | `404 reservation_not_found` · `403 not_owner` |

`Amenity`: `{ id, name, description?, capacity, maxBookingMinutes }`. `Reservation`: `{ id, amenityId, residentId, startUtc, endUtc, status, createdUtc }`.

## 4. Preventing double-booking

**Failure mode.** A check-then-act race: two residents book the last unit at once, both read "1 of 2 used", both pass the check, both insert, and the amenity is overbooked.

**Mechanism.** Read, check and write run in one critical section per amenity: `IReservationRepository.ExecuteExclusiveAsync` holds a `SemaphoreSlim` per `(building, amenity)` while it loads active bookings, runs the rules, checks capacity and inserts. In Postgres the same seam is a transaction with `SELECT … FOR UPDATE` on the amenity row; the in-memory lock is correct for one process only.

**Capacity check.** Counting overlapping bookings is wrong: with capacity 2 and bookings 9–10 and 10–11, a 9–11 request overlaps both, so a naive count rejects it though occupancy never exceeds 1. `Occupancy.MaxConcurrent` sweeps start/end events clipped to the request window and returns the true peak; it must be below `Capacity`.

**Rejected alternative: a fixed slot grid with a unique key** on `(amenityId, slotStart, seatIndex)`. It is trivially correct and maps onto a database constraint, so I'd pick it if the PM says "fixed slots". I rejected it because free-form durations make it awkward: a 90-minute booking must claim several rows atomically.

## 5. Multi-tenancy

- Every tenant-owned entity carries `BuildingId`, resolved once per request by `IRequestContextResolver` (headers here, a token claim in production), never from a body or route. Every repository and catalog method requires `buildingId`, so a query can't be written unscoped.
- Another building's resource returns **404, not 403**, so ids aren't revealed. Production adds row-level security.

## 6. Left out, and why

- **Opening hours, booking horizon, cancellation cutoff:** they need time-zone and DST answers from a PM. Each is a new policy field, one `IBookingRule` class and one DI line, with no endpoint or UI change.
- **Real auth and persistence:** headers stand in for identity (the UI's demo dropdown switches residents); in-memory storage is expected. Both sit behind interfaces.
- **Waitlist, approval, fees, recurring bookings, pagination, notifications, an `/availability` endpoint:** product decisions or unneeded for a slice. The UI shows existing bookings and a clear conflict error instead.
- **Hiding other residents' names** (production would show "Booked" to others) **and idempotency keys** (a retried POST is a different failure from double-booking, masked here by the one-active-booking rule).

## 7. Scaffold changes and verification

- **Changed:** `InMemoryStore` became `SeedData` plus an in-memory catalog and repository, and domain records gained tenancy, status and policy. orval now returns `{ data, status }` because the default never throws on non-2xx, so a `409` would look like success. Errors are a typed `ApiError`, not RFC 7807. `App.tsx` was rewritten; `backend/Api.Tests` and `e2e/` were added.
- **Known gap:** a body that fails framework binding gets the framework's own `400`, not an `ApiError`.
- **Verify:** with only a newer .NET runtime, prefix dotnet commands with `DOTNET_ROLL_FORWARD=Major`. `cd backend/Api.Tests && dotnet test` (146 tests, including concurrent races) and `cd e2e && npx playwright test` (13 browser scenarios).
