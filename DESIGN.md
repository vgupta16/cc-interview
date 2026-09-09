# Amenity Reservations — Design

A thin vertical slice: a resident picks one of their buildings, sees that building's amenities,
and books an amenity if it's available.

---

## Scope & assumptions

**Assumptions**

- **Identity is out of scope, but access control is not.** There is no auth in the scaffold, so
  the caller asserts their identity via an `X-Resident-Id` header. It's a header rather than a
  body field or query param specifically, so that swapping in real auth means replacing
  header-parsing with claims-parsing.
- **A resident can belong to more than one building.** People own or rent units in more than
  one place, and a portfolio product has to represent that. It also means that access depends on
  membership (e.g. currently rents a unit) rather than a single equality.
- **An amenity belongs to exactly one building.**
  In real life there are cases where multiple buildings share an amenity, like a pool in a large
  complex. For the scope of this exercise we've tied each amenity to a single building.
- **A booking can start at the exact moment another ends.** If someone has the party room from
  11:00 to 12:00, the next resident can book it from 12:00 to 1:00, so one booking's end time and
  the next one's start time are allowed to be the same instant.
- **Cancellations are soft-deleted.** We keep the record in case a dispute arises between a
  resident and a manager, for analytics, and to catch abuse — a resident working around a booking
  limit by holding rooms and cancelling shortly before they start.
- **All times are stored and compared in UTC.**

**Questions we'd ask a PM**

- What does a booking need to record beyond who made it? Guest Parking presumably needs the
  visitor's name or plate for the concierge to check against, and the party room may care about
  headcount rather than just "one booking." If residents are allowed to book for guests, they may
  want to include identifying information here too.
- What limits belong on cancelling? Cancelling a booking that has already started seems fine —
  it frees the space for someone else — but last-minute cancellations on something like the party
  room leave a slot nobody can realistically use, and repeated book-then-cancel is a way to hoard
  time. Is there a notice period, a cap per month, or neither?
- How many bookings should a resident be able to hold, and how do we count them? Per amenity or
  per building, and concurrently or so many per week?
- Do amenities need buffer time between bookings for setup or cleaning?
- Can a manager close an amenity for maintenance, a private event, or a holiday? Today every
  unavailable minute is caused by a resident's booking. A blockout would consume availability the
  same way but for a different reason, and the calendar should say "closed" rather than "full."
  This is probably the first thing we'd build next.
- Opening hours are local clock times, so each building needs its own time zone stored against it
  before that rule can be built at all. The one question we'd actually raise is what a booking's
  length means across a daylight-saving change: 1:00–3:00 AM on the spring-forward day is two
  hours on the clock but one hour of real occupancy. Either we validate against the wall-clock
  span the resident picked, which matches what they think they booked, or against elapsed time,
  which matches how long the room is actually held. We'd lean to wall-clock and flag it in the UI,
  but it's a product call.
- **When an amenity's rules change, what happens to bookings made under the old ones?** If a
  manager lowers capacity or shortens the maximum booking length, are existing reservations
  grandfathered, cancelled, or flagged? Currently, the capacity limit
  is enforced *at write time*, so lowering capacity from 3 to 2 while three overlapping bookings
  exist leaves the system in a state the booking path would never have allowed. And from a more
  user-focused perspective, will residents be upset if rules change after they've already made
  plans?
- What happens to a resident's future bookings when they move out? Access is derived from current
  membership, so the moment their `ResidentBuilding` row goes away they can no longer see or
  cancel a booking they still hold — it would sit there unreachable.

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

### Two modelling decisions worth stating

**`ResidentBuilding` collapses a two-step relationship into one row.** In a real schema there'd be
a `Unit` table, with each unit belonging to a building, and the link from a resident to their unit
would carry whether they own or rent and their move-in and move-out dates. Which buildings a
resident belongs to would then be derived by following resident → unit → building, rather than
stored directly. Nothing in this slice needs unit-level data, so we skipped the middle step.

**`Reservation` deliberately does not store a `BuildingId`.** It doesn't need one — a reservation
points at an amenity, and the amenity already knows its building. Storing it in both places would
make building-wide queries faster, but the two copies could drift apart, and since the building is
exactly what access control checks, a stale copy wouldn't be a cosmetic bug — it would be one
resident seeing another building's data. Deriving it costs a lookup; getting it wrong costs a leak.

**Representation choices**, stated rather than assumed:

- **Time — `DateTimeOffset`, stored in UTC.** Buildings sit in different time zones, and local
  clock times break twice a year in two different ways. In the fall an hour repeats, so 1:30 AM
  happens twice and nothing in the timestamp says which one is meant. In the spring an hour is
  skipped, so 2:30 AM never happens at all. Storing an exact instant has neither problem.
- **Ids — `Guid`.** No central coordination needed to stay unique across buildings, and it matches
  the scaffold's existing convention.
- **Booking length — `int` minutes.** Matches the existing `MaxBookingMinutes` field; the domain
  has no need for sub-minute precision, and minutes are simpler to validate and display than a
  `TimeSpan`.

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
// POST /api/amenities/{amenityId}/reservations
{ "startUtc": "2026-09-10T10:00:00Z", "endUtc": "2026-09-10T11:00:00Z" }

// 201 Created
{ "id": "...", "amenityId": "...", "residentId": "resident-jeremy",
  "startUtc": "2026-09-10T10:00:00+00:00", "endUtc": "2026-09-10T11:00:00+00:00",
  "cancelledUtc": null }

// 409 Conflict
{ "error": "That time is no longer available." }
```

**409, not 400**, for a capacity conflict: the request is well-formed and would have succeeded a
moment earlier. It conflicts with the current state of the resource, which is what 409 means.

**Why POST isn't nested under the building.** A route like
`/api/buildings/{b}/amenities/{a}/reservations` would make the tenant boundary look explicit, but
the amenity already determines its building, so the extra segment is a client-supplied value that
must then be validated for agreement — a new failure mode with no upside. The boundary is enforced
by the access check, not by the URL.

**Validation order** is: amenity exists (404) → resident header present (400) → **access check
(403)** → end after start (400) → start not already passed (400) → duration within the amenity's
max (400) → capacity (409). Authorization runs before the detailed validation so a non-member
can't probe another building's booking rules by reading error messages.

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

Neither request is wrong on its own. The bug is that the gap between *deciding* and *writing*
lets another writer slip in. `InMemoryStore` is registered as a singleton and ASP.NET Core serves
requests on thread-pool threads, so this is a live hazard, not a theoretical one. There's a second
one alongside it: `List<T>` is not safe for concurrent mutation at all, so an unsynchronised
reader can throw or read torn state even when capacity isn't at issue.

### Solution

In order to close the gap between deciding and writing, the check and the insert happen as **one
indivisible operation**, and nothing touches the reservation list outside it.

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

Three supporting decisions:

1. **`_reservations` is private.** The scaffold exposed it as `public List<Reservation> { get; }`.
   That only prevents *reassignment* — the list itself stays mutable, so any caller could
   `store.Reservations.Add(...)` straight past the lock. Making it private forces every access
   through a synchronised method.
2. **Reads take the lock too**, and return a `.ToList()` snapshot. Handing out the live list would
   let a caller enumerate it while another thread writes.
3. **One global lock, not per-amenity.** Contention is irrelevant at this scale, and per-amenity
   locking would introduce complexity beyond the scope of this project.

**Note this constrains the layering.** Booking rules live in a `ReservationService`, but capacity
checking deliberately does *not* — it stays in the store, next to the insert. Hoisting it into the
service for tidier separation would put the check and the write in different layers and
reintroduce the exact race above. Everything the service validates is stateless, which is why it's
safe outside the lock.

### Unused alternative

**Optimistic concurrency** — version the amenity's availability, and on write, confirm the version
hasn't changed since the read; retry on conflict. This is the right answer for a real deployment:
it survives multiple server instances, where an in-process lock protects nothing because each
instance has its own. In a database we'd push the guarantee down further — a Postgres exclusion
constraint over a `tsrange` makes overlapping rows *unrepresentable*, which beats any
application-level check.

We didn't use this method here because the exercise explicitly excludes a database, and versioning
needs a store that can atomically compare-and-swap. Layering a hand-rolled version field over a
`List<T>` adds moving parts without adding safety over a lock that already serialises correctly in
one process.

We'd also reject **checking then inserting without any synchronisation** — the naive version — for
the reason the diagram above shows. It's worth naming because it's what the code looks like if
you don't think about concurrency at all, and it passes every single-threaded test you write.

### Verification

`Api.Tests` has 11 tests. In order to show the guarantee actually holds rather than assert it,
the one that matters fires 16 threads at a capacity-1 amenity, released simultaneously by a
`Barrier`, repeated over 40 fresh rounds, and asserts exactly one winner.

**We checked that this test can actually fail.** The first version used `Task.Run` and passed —
and kept passing after we deleted the lock, because the thread pool injects workers gradually and
the critical section is only microseconds long, so the tasks never really overlapped. It was
verifying nothing. Real threads plus a barrier fail immediately without the lock:

```
Assert.Equal() Failure: Values differ
Expected: 1
Actual:   2
```

Two winners on a capacity-1 amenity — the double-booking bug, reproduced on demand.

---

## Multi-tenancy & isolation

In order to ensure residents can only view and book amenities in buildings they belong to, every
read and write runs the same check before doing anything else:

```csharp
CanAccess(residentId, amenity) =>
    resident belongs to amenity.BuildingId
```

Recording a `BuildingId` on each amenity isn't enough on its own — the id only helps if something
actually checks it. So the check lives in one method rather than being repeated at each of the
four places that need it, and the building it compares against always comes from the resident's
own membership, never from anything the caller sent. Today that's the header; under real auth it
would be the login. Skipping a check like this is a well-known vulnerability class — OWASP calls
it Insecure Direct Object Reference: pass someone else's id, get their data.

For the same reason we replaced the scaffold's `GET /api/amenities`, which listed every amenity no
matter who asked, with `GET /api/buildings/{id}/amenities`. It did no harm before buildings
existed, but afterwards it was the one route not scoped like the others.

Tenancy and concurrency don't interact: the membership check runs before the lock and doesn't
touch the overlap math.

**Indexing.** There's no real database here, but in order to keep these checks fast at real
volume, `Reservation` would want an index on `(AmenityId, StartUtc)`, since the overlap query runs
on every booking, plus one on `ResidentId` for listing a resident's own bookings. `Amenity` would
want one on `BuildingId`, and `ResidentBuilding` on `(ResidentId, BuildingId)` — which its
uniqueness constraint already provides — because the access check reads it on every request.

One consequence of leaving `BuildingId` off `Reservation`: listing every booking in a building has
to go through `Amenity` rather than reading straight from an index. That's the cost of not keeping
two copies of the same fact, and we think it's the right trade.

---

## Validation & scheduling rules

The brief says amenities "may have rules" and leaves the scope to us, so the decision is the
artifact:

- **Implemented — max booking length.** `MaxBookingMinutes` already existed on `Amenity`, so
  enforcing it was free.
- **Implemented — ownership on cancel.** Users can only cancel their own booking (403 otherwise).
- **Implemented — no start times in the past.** Worth contrasting with opening hours below: this
  compares two absolute instants, so it needs no timezone knowledge at all. The calendar also
  greys out elapsed hours, so the UI never offers something the server will refuse.
- **Deferred — opening hours.** These are *local wall-clock* times, so they need a timezone per
  building and DST handling, and the scaffold stores no timezone anywhere. A naive UTC hour
  comparison would look correct in a demo and quietly break in November. We decided to defer this
  feature rather than ship something that looks done.
- **Deferred — limiting how many bookings a resident can hold.** As stated above, we weren't
  sure how those limits should be set, and working that out seemed beyond the scope of this
  project.

---

## Deferred work

| Cut | Why |
| --- | --- |
| Real authentication | Explicitly not the exercise; the header is a clean seam for it |
| Opening hours | Needs per-building timezone + DST; half-building it is worse than not |
| One-booking-per-resident | Ambiguous scope — a PM question, not a coding one |
| `Unit` entity | Nothing in this slice needs unit-level data |
| Shared cross-building amenities (many-to-many) | The model we'd actually build; one-to-many was enough to demonstrate isolation and capacity here without the extra surface |
| Amenity/building admin CRUD | Assumed preconfigured |
| Pagination / date-range filtering | The reservations endpoint returns every booking for an amenity and the client filters to the selected day. Fine at seed scale; a real amenity would need a server-side range query, since a year of bookings should not cross the wire to render one day |
| Notifications, recurring bookings | Orthogonal to the booking invariant |
| Persistence | Excluded by the brief |
| CI check on the generated client | See below |

**On the generated client.** `pnpm gen:api` regenerates the typed client from the live OpenAPI
spec, and both the spec snapshot and the generated file are committed. That makes generation
mechanical — the client can't have a typo or drift from the spec — but the *trigger* is still a
manual command, so a backend change can ship without a matching regeneration. What actually closes
that gap is a CI step that regenerates and fails the build on a diff. Not built here: it's
infrastructure around a feature that didn't exist yet, and it needs branch-protection authority
this repo doesn't grant a PR author.

---

## Changes to the scaffold

- `Reservation` gained `CancelledUtc`; `Amenity` gained `BuildingId`; added `Building` and
  `ResidentBuilding`.
- `InMemoryStore.Reservations` went from a public list to a private field behind synchronised
  methods (see the double-booking section for why).
- Added `ReservationService`; endpoints stay thin.
- Replaced the unscoped `GET /api/amenities` with the building-scoped route.
- Added the `Api.Tests` project.
- `orval.config.ts`: refreshed the committed `openapi.json` snapshot from the running API, and
  flipped `includeHttpResponseReturnType` to `true`. With it `false`, the generated functions
  discard the status code and return a 409's error body typed as a `Reservation` — success and
  conflict are indistinguishable at the type level. Since surfacing the conflict is the point of
  the feature, the UI needs the status.
