# Amenity Reservations — Design

A thin vertical slice: a resident picks one of their buildings, sees that building's amenities,
and books one if it's available.

---

## Scope & assumptions

**Assumptions**

- **Identity is out of scope, but access control is not.** There's no auth in the scaffold, so the
  caller asserts their identity via an `X-Resident-Id` header. It's a header rather than a body
  field so that swapping in real auth means replacing header-parsing with claims-parsing, and no
  endpoint shapes change.
- **A resident can belong to more than one building.** People own or rent units in more than one
  place, so access depends on membership rather than a single equality.
- **An amenity belongs to exactly one building.** In real life buildings sometimes share one — a
  pool in a large complex — but for this exercise we've tied each amenity to a single building.
- **A booking can start at the exact moment another ends.** 11:00–12:00 and 12:00–1:00 don't
  collide; otherwise every reservation has to start a minute late and the day fills with dead gaps.
- **Cancellations are soft-deleted.** We keep the record in case a dispute arises between a
  resident and a manager, for analytics, and to catch abuse — a resident working around a booking
  limit by holding rooms and cancelling shortly before they start.
- **All times are stored and compared in UTC.**

**Questions we'd ask a PM**

- What does a booking need to record beyond who made it? Guest Parking presumably needs the
  visitor's name or plate for the concierge to check against, and the party room may care about
  headcount rather than just "one booking."
- What limits belong on cancelling, and on how many bookings a resident can hold? Cancelling a
  booking that has already started frees the space for someone else, but repeated
  book-then-cancel is a way to hoard time. And is a holding limit per amenity or per building,
  concurrent or per week?
- Can a manager close an amenity for maintenance, a private event, or a holiday? Today every
  unavailable minute is caused by a resident's booking. A blockout would consume availability the
  same way for a different reason, and the calendar should say "closed" rather than "full." This
  is probably the first thing we'd build next.
- **When an amenity's rules change, what happens to bookings made under the old ones?** The
  capacity limit is enforced *at write time*, so lowering capacity from 3 to 2 while three
  overlapping bookings exist leaves the system in a state the booking path would never have
  allowed. Grandfather them, cancel them, or flag them?
- What happens to a resident's future bookings when they move out? Access is derived from current
  membership, so once their `ResidentBuilding` row goes away they can't see or cancel a booking
  they still hold.

---

## Domain model

```
Resident ──< ResidentBuilding >── Building ──< Amenity ──< Reservation
```

| Entity | Fields | Notes |
| --- | --- | --- |
| `Building` | Id, Name | The isolation boundary. |
| `ResidentBuilding` | ResidentId, BuildingId | Many-to-many. `UNIQUE(ResidentId, BuildingId)`. |
| `Amenity` | Id, **BuildingId**, Name, Description, Capacity, MaxBookingMinutes | Capacity 1 == exclusive. |
| `Reservation` | Id, AmenityId, ResidentId, StartUtc, EndUtc, CancelledUtc? | `CancelledUtc == null` means active. |

**The invariant everything else serves:**

> For a given amenity and time interval, the number of overlapping active reservations must never
> exceed that amenity's capacity.

**`ResidentBuilding` collapses a two-step relationship.** In a real schema there'd be a `Unit`
table, and the resident-to-unit link would carry own-versus-rent and move-in/move-out dates.
Building membership would then be derived by following resident → unit → building rather than
stored. Nothing here needs unit-level data, so we skipped the middle step.

**`Reservation` deliberately has no `BuildingId`.** A reservation points at an amenity, and the
amenity already knows its building. Storing it twice would make building-wide queries faster, but
the copies could drift apart — and since the building is exactly what access control checks, a
stale copy wouldn't be a cosmetic bug, it would be one resident seeing another building's data.
Deriving it costs a lookup; getting it wrong costs a leak.

**Representation choices:** `DateTimeOffset` in UTC, because buildings sit in different time zones
and local clock times break twice a year — an hour repeats in the fall and one goes missing in the
spring. `Guid` ids, needing no central coordination and matching the scaffold. Booking length as
`int` minutes, matching the existing `MaxBookingMinutes` and needing no sub-minute precision.

---

## API

All routes require the `X-Resident-Id` header.

| Method | Route | Returns |
| --- | --- | --- |
| GET | `/api/buildings` | Buildings this resident belongs to |
| GET | `/api/buildings/{buildingId}/amenities` | That building's amenities · 403 if not a member |
| GET | `/api/amenities/{amenityId}/reservations` | Active reservations · 403 / 404 |
| POST | `/api/amenities/{amenityId}/reservations` | **201** + `Location` · 400 / 403 / 404 / **409** |
| GET | `/api/reservations/{id}` | One reservation · 403 / 404 |
| DELETE | `/api/reservations/{id}` | 204 · 403 if not the owner · 404 |

```jsonc
// POST body
{ "startUtc": "2026-09-10T10:00:00Z", "endUtc": "2026-09-10T11:00:00Z" }

// 409 Conflict
{ "error": "That time is no longer available." }
```

**409, not 400**, for a capacity conflict: the request is well-formed and would have succeeded a
moment earlier. It conflicts with the current state of the resource, which is what 409 means.

**POST isn't nested under the building.** The amenity already determines its building, so a
`buildingId` segment would be a client-supplied value we'd then have to validate for agreement —
a new failure mode with no upside. The boundary is enforced by the access check, not the URL.

**Validation order:** amenity exists (404) → resident header present (400) → **access check
(403)** → end after start (400) → start not already passed (400) → duration within the amenity's
max (400) → capacity (409). Authorization runs before the detailed validation so a non-member
can't probe another building's rules by reading error messages.

---

## Preventing double-booking

### Failure mode

Check then act. Two requests arrive at once for a capacity-1 amenity:

```
Request A                        Request B
─────────                        ─────────
count overlapping → 0
                                 count overlapping → 0
   "free!"                          "free!"
insert reservation
                                 insert reservation

Result: 2 reservations, capacity 1.
```

Neither request is wrong on its own — the bug is that the gap between *deciding* and *writing*
lets another writer slip in. `InMemoryStore` is a singleton and ASP.NET Core serves requests on
thread-pool threads, so this is live, not theoretical. Separately, `List<T>` isn't safe for
concurrent mutation at all, so an unsynchronised reader can throw even when capacity isn't at
issue.

### Solution

In order to close that gap, the check and the insert happen as **one indivisible operation**, and
nothing touches the reservation list outside it.

```csharp
lock (_gate)
{
    var overlapping = _reservations.Count(r =>
        r.AmenityId == amenity.Id
        && r.CancelledUtc is null
        && r.StartUtc < endUtc      // strict <, so a booking that ends exactly when
        && startUtc < r.EndUtc);    // this one starts is not counted as a clash

    if (overlapping >= amenity.Capacity)
        return Conflict;

    _reservations.Add(reservation);
    return Created;
}
```

Three supporting decisions. **`_reservations` is private** — the scaffold exposed it publicly,
which only prevents reassignment; the list itself stayed mutable, so any caller could `Add()`
straight past the lock. **Reads take the lock too** and return a `.ToList()` snapshot, since
handing out the live list lets a caller enumerate it mid-write. **One global lock, not
per-amenity**, because contention is irrelevant at this scale.

This constrains the layering. Booking rules live in a `ReservationService`, but capacity checking
deliberately doesn't — it stays in the store, next to the insert. Hoisting it up for tidier
separation would put the check and the write in different layers and reintroduce the exact race
above. Everything the service validates is stateless, which is why that part is safe outside the
lock.

### Unused alternative

**Optimistic concurrency** — version the availability, confirm on write that it hasn't changed,
retry on conflict. This is the right answer for a real deployment, since it survives multiple
server instances where an in-process lock protects nothing. In a database we'd push it further
down still: a Postgres exclusion constraint over a `tsrange` makes overlapping rows
*unrepresentable*, which beats any application-level check.

We didn't use it because the exercise excludes a database, and versioning needs a store that can
atomically compare-and-swap. Hand-rolling that over a `List<T>` adds moving parts without adding
safety over a lock that already serialises correctly in one process.

### Verification

`Api.Tests` has 11 tests. To show the guarantee holds rather than assert it, the one that matters
fires 16 threads at a capacity-1 amenity, released together by a `Barrier`, over 40 fresh rounds.

**We checked that this test can actually fail.** The first version used `Task.Run` and passed —
and kept passing after we deleted the lock, because the thread pool injects workers gradually and
the critical section is microseconds long, so the tasks never really overlapped. It was verifying
nothing. Real threads plus a barrier fail immediately without the lock: *expected 1, actual 2* —
two winners on a capacity-1 amenity, the bug reproduced on demand.

The booking screen's hour grid is **advisory only**. Another resident can take a slot between the
grid rendering and someone clicking, so the server still runs its own atomic check and the UI
handles a conflict on an hour it just drew as open.

---

## Multi-tenancy & isolation

In order to ensure residents can only view and book amenities in buildings they belong to, every
read and write runs the same check first:

```csharp
CanAccess(residentId, amenity) =>
    resident belongs to amenity.BuildingId
```

Recording a `BuildingId` isn't enough on its own — the id only helps if something checks it. So
the check lives in one method rather than being repeated at each of the four call sites, and the
building it compares against always comes from the resident's own membership, never from anything
the caller sent. Skipping a check like this is a well-known vulnerability class; OWASP calls it
Insecure Direct Object Reference: pass someone else's id, get their data.

For the same reason we replaced the scaffold's `GET /api/amenities`, which listed every amenity no
matter who asked, with `GET /api/buildings/{id}/amenities`.

Tenancy and concurrency don't interact — the membership check runs before the lock and doesn't
touch the overlap math.

**Indexing.** There's no real database here, but to keep these checks fast at volume,
`Reservation` would want `(AmenityId, StartUtc)` for the overlap query that runs on every booking,
plus `ResidentId` for a resident's own bookings; `Amenity` would want `BuildingId`. One
consequence of leaving `BuildingId` off `Reservation` is that listing a building's bookings goes
through `Amenity` rather than straight off an index — the accepted cost of not keeping two copies
of one fact.

---

## What we left out, and why

| Cut | Why |
| --- | --- |
| Real authentication | Explicitly not the exercise; the header is a clean seam for it |
| Opening hours | Local wall-clock times, so they need a timezone per building and DST handling that the scaffold has nowhere to store. A naive UTC comparison looks right in a demo and breaks in November |
| Limits on bookings per resident | We weren't sure how those limits should be set — a PM question, not a coding one |
| `Unit` entity | Nothing in this slice needs unit-level data |
| Shared cross-building amenities | The model we'd actually build; one-to-many was enough to show isolation and capacity here |
| Amenity/building admin CRUD | Assumed preconfigured |
| Pagination / date-range filtering | The reservations endpoint returns every booking for an amenity and the client filters by day. Fine at seed scale; a real one needs a server-side range query |
| Notifications, recurring bookings | Orthogonal to the booking invariant |
| Persistence | Excluded by the brief |
| CI check on the generated client | `gen:api` is a manual command, so a backend change can ship without a matching regeneration. What closes that gap is a CI step that regenerates and fails on a diff |

**Rules we did implement:** max booking length, ownership on cancel, and no start times in the
past. That last one is worth contrasting with opening hours — it compares two absolute instants,
so it needs no timezone knowledge at all, and the calendar greys out elapsed hours so the UI never
offers what the server will refuse.

---

## Changes to the scaffold

- `Reservation` gained `CancelledUtc`; `Amenity` gained `BuildingId`; added `Building` and
  `ResidentBuilding`.
- `InMemoryStore.Reservations` went from a public list to a private field behind synchronised
  methods.
- Added `ReservationService` and the `Api.Tests` project; endpoints stay thin.
- Replaced the unscoped `GET /api/amenities` with the building-scoped route.
- `orval.config.ts`: refreshed the committed `openapi.json` snapshot, and flipped
  `includeHttpResponseReturnType` to `true` — with it off, the generated client discards the status
  code and returns a 409's error body typed as a `Reservation`, so the UI can't tell a booking from
  a rejection.
