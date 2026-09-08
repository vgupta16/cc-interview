# Amenity Reservations — Design

A thin vertical slice: a resident picks one of their buildings, sees that building's amenities,
and books a time slot that cannot be double-booked.

---

## Scope & assumptions

The brief is deliberately underspecified, so here is what I assumed and what I'd ask a PM.

**Assumed**

- **Identity is out of scope, but access control is not.** There is no auth in the scaffold, so
  the caller asserts who they are via an `X-Resident-Id` header. It's a header rather than a body
  field or query param specifically so that swapping in real auth means replacing header-parsing
  with claims-parsing — no endpoint shapes change.
- **A resident can belong to more than one building.** This is what makes the isolation
  requirement real rather than decorative, so it's modelled properly (many-to-many) and the UI is
  built around it.
- **An amenity belongs to exactly one building.**
- **Reservation intervals are half-open, `[start, end)`.** 10:00–11:00 and 11:00–12:00 do not
  conflict. Getting this wrong makes consecutive slots unbookable, which is a bad default.
- **Cancellation is a soft delete.** History matters more than row count here.
- **All times are stored and compared in UTC.**

**Questions I'd ask a PM**

- Are guests/visitors ever bookers, or only residents?
- Should a booking that has already started be cancellable? Should past slots be visible?
- Is "one active booking per resident" per amenity, per building, or global — and is it
  concurrent or lifetime? (I deferred this rule precisely because the answer changes the model.)
- Do amenities need buffer time between bookings for setup/cleaning?
- Who administers amenities and their rules — a property manager, a board, support staff?

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

### Three modelling decisions worth stating

**`ResidentBuilding` is a relationship entity, not a bare join.** Today it carries only the two
ids, but the real thing routes through a **unit** — a resident occupies a unit, a unit belongs to
a building — and would carry `UnitId`, role (owner vs. renter), status, and move-in/move-out
dates. I collapsed it because nothing in this slice needs unit-level data, not because the
simpler shape is correct.

**`Reservation` deliberately has no `BuildingId`.** Denormalizing it would make building-scoped
queries a single index scan instead of a join, which is a real benefit. I skipped it anyway: a
real database could enforce agreement with the amenity's building via a foreign key, but an
in-memory `List<T>` enforces nothing, so the two values can silently drift. A drifted copy that
something later authorizes against is a security hole, not a stale field. The cost of deriving it
is one join; the cost of the drift is a cross-building leak.

**Amenity-to-building is one-to-many, not many-to-many.** Many-to-many would let one physical
resource be shared between two buildings — a pool serving two towers of one complex, which is a
real configuration. It's a strict superset of what I built and would be the migration path if
that requirement appeared. Rejected here as more expressiveness than this slice needs.

**Representation choices**, stated rather than assumed:

- **Time — `DateTimeOffset`, UTC.** Buildings span time zones and DST makes local wall-clock times
  ambiguous twice a year. Storing an instant sidesteps both.
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

**Why POST isn't nested under the building.** `/api/buildings/{b}/amenities/{a}/reservations`
would make the tenant boundary look explicit, but the amenity already determines its building, so
the extra segment is a client-supplied value that must then be validated for agreement — a new
failure mode with no upside. The boundary is enforced by the access check, not by the URL.

**Validation order** is: amenity exists (404) → resident header present (400) → **access check
(403)** → end after start (400) → duration within the amenity's max (400) → capacity (409).
Authorization runs before the detailed validation so a non-member can't probe another building's
booking rules by reading error messages.

---

## Preventing double-booking

**This is the crux, so it gets the most space.**

### The failure mode

Check-then-act. Two requests arrive at once for a capacity-1 amenity:

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

### Chosen mechanism

Make the check and the insert **one indivisible operation**, and never touch the reservation list
outside it.

```csharp
lock (_gate)
{
    var overlapping = _reservations.Count(r =>
        r.AmenityId == amenity.Id
        && r.CancelledUtc is null
        && r.StartUtc < endUtc      // half-open overlap:
        && startUtc < r.EndUtc);    // A.Start < B.End && B.Start < A.End

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
   locking is complexity that would need justifying with a measurement I don't have.

**Note this constrains the layering.** Booking rules live in a `ReservationService`, but capacity
checking deliberately does *not* — it stays in the store, next to the insert. Hoisting it into the
service for tidier separation would put the check and the write in different layers and
reintroduce the exact race above. Everything the service validates is stateless, which is why it's
safe outside the lock.

### Alternative rejected

**Optimistic concurrency** — version the amenity's availability, and on write, confirm the version
hasn't changed since the read; retry on conflict. This is the right answer for a real deployment:
it survives multiple server instances, where an in-process lock protects nothing because each
instance has its own. In a database I'd push the guarantee down further — a Postgres exclusion
constraint over a `tsrange` makes overlapping rows *unrepresentable*, which beats any
application-level check.

Rejected here because the exercise explicitly excludes a database, and versioning needs a store
that can atomically compare-and-swap. Layering a hand-rolled version field over a `List<T>` adds
moving parts without adding safety over a lock that already serialises correctly in one process.

I'd also reject **checking then inserting without any synchronisation** — the naive version — for
the reason the diagram above shows. It's worth naming because it's what the code looks like if
you don't think about concurrency at all, and it passes every single-threaded test you write.

### Verification

`Api.Tests` has 10 tests. The one that matters fires 16 threads at a capacity-1 amenity, released
simultaneously by a `Barrier`, repeated over 40 fresh rounds, and asserts exactly one winner.

**I checked that this test can actually fail.** My first version used `Task.Run` and passed —
and kept passing after I deleted the lock, because the thread pool injects workers gradually and
the critical section is only microseconds long, so the tasks never really overlapped. It was
verifying nothing. Real threads plus a barrier fail immediately without the lock:

```
Assert.Equal() Failure: Values differ
Expected: 1
Actual:   2
```

Two winners on a capacity-1 amenity — the double-booking bug, reproduced on demand.

### The UI shows availability, but does not decide it

The booking screen renders an hour grid for a chosen day, marking each hour Open, *n* left,
*n* min free, or Full. That's a **view** over arbitrary reservation intervals rather than a model
of them, and the distinction matters: the API accepts any start/end, so bookings don't respect
hour boundaries.

A 4:00–5:30 reservation leaves 5:30–6:00 genuinely free, and reporting the 5:00 hour as simply
"taken" would be a lie the resident can't act on. So each cell runs a small sweep — split the
hour at every reservation boundary falling inside it, count how many bookings cover each
resulting slice, compare against capacity. The cell then reports how many minutes are actually
free and draws a miniature timeline showing where. Clicking a partly-free hour snaps the form to
that window rather than proposing an hour the server would reject.

This is also why hour granularity is a display choice rather than a domain one: a fixed grid at
any resolution would eventually misreport a booking that doesn't align to it, whereas the sweep
is exact for arbitrary intervals.

Worth being explicit that this display is **advisory**. Between the moment the grid renders and
the moment someone clicks, another resident can take the slot — the client is never the authority
on what's free. That's precisely why the server does its own atomic check and why the UI still has
to handle a 409 on a slot it just drew as open. Showing availability optimistically and enforcing
it pessimistically is the intended split, not a gap.

Selection is a three-click cycle: the first click anchors an hour, the second extends a range
across everything between, the third clears.

Because bookings don't align to hours, a range's **edges** are computed from the free runs at the
boundary rather than from the hour marks. Each cell records the contiguous free time touching its
start and the contiguous free time touching its end. Growing forward from a partly-booked hour
therefore begins at that hour's free tail (5:30, not 5:00), and can finish part-way into a
partly-booked hour by taking its free head. What a partly-booked hour *cannot* do is sit in the
middle of a range, since passing through it would swallow the booked part — so extension stops
there and treats it as the far edge.

All of that is a UI guard, not enforcement; the server still validates whatever is submitted.

A selection fills the form rather than booking outright, so the times stay editable and a resident
can still request something the grid can't express. Editing a time by hand drops the highlight
rather than letting it keep advertising a range the form no longer holds.

Note the grid shows all 24 hours deliberately. Restricting it to plausible daytime hours would
smuggle in the opening-hours rule that was explicitly deferred below, and a UI that quietly
assumes a rule the server doesn't enforce is worse than one that shows everything.

---

## Multi-tenancy & isolation

The `BuildingId` column prevents nothing on its own. What prevents the leak is that **every** read
and write is filtered by the caller's own membership, derived server-side:

```csharp
CanAccess(residentId, amenity) =>
    resident belongs to amenity.BuildingId
```

One method, called by the amenity list, the reservation list, create, and cancel. It's the
security-critical path, so it exists once rather than being copy-pasted to four call sites.

**The failure mode if you skip it:** a resident of Building A passes Building B's `amenityId` and
reads or books its slots. That's cross-building data leakage — OWASP's Insecure Direct Object
Reference, under Broken Access Control — not a cosmetic bug.

The rule that keeps it honest: **the building is never taken from client input.** It's derived
from the resident's membership. Today the resident comes from a header; under real auth it comes
from the authenticated principal's claims, and nothing else about this changes.

I also **removed the scaffold's `GET /api/amenities`**, which returned every amenity in the system
with no notion of who was asking. Harmless before buildings existed; a leak the moment they do.

Tenancy and concurrency are orthogonal here — membership is an authorization gate checked before
the lock, and it doesn't touch the overlap math.

**Indexing**, since there's no real database to put it in: `Reservation` would want
`(AmenityId, StartUtc)` — the overlap query is the hot path on every booking — plus `ResidentId`
for "my bookings". `Amenity` wants `BuildingId`. `ResidentBuilding` wants
`(ResidentId, BuildingId)`, which the uniqueness constraint provides for free, since `CanAccess`
hits it on every request. Because `Reservation` has no `BuildingId`, a building-scoped reservation
query needs a join through `Amenity` — the accepted cost of not denormalizing.

---

## Validation & scheduling rules

The brief says amenities "may have rules" and leaves the scope to me, so the decision is the
artifact:

- **Implemented — max booking length.** `MaxBookingMinutes` already existed on `Amenity`, so
  enforcing it was free.
- **Implemented — ownership on cancel.** You can only cancel your own booking (403 otherwise).
- **Deferred — opening hours.** These are *local wall-clock* times, so they need a timezone per
  building and DST handling, and the scaffold stores no timezone anywhere. A naive UTC hour
  comparison would look correct in a demo and quietly break in November. I'd rather defer it than
  ship something that looks done.
- **Deferred — one active booking per resident.** Genuinely ambiguous: per amenity, per building,
  or global? Concurrent or lifetime? I'd settle that with a PM rather than encode a guess.

---

## Deferred work

| Cut | Why |
| --- | --- |
| Real authentication | Explicitly not the exercise; the header is a clean seam for it |
| Opening hours | Needs per-building timezone + DST; half-building it is worse than not |
| One-booking-per-resident | Ambiguous scope — a PM question, not a coding one |
| `Unit` entity | Nothing in this slice needs unit-level data |
| Shared cross-building amenities | Real but uncommon; one-to-many covers the dominant case |
| Amenity/building admin CRUD | Assumed preconfigured |
| Pagination / date-range filtering | The reservations endpoint returns every booking for an amenity and the client filters to the selected day. Fine at seed scale; a real amenity would need a server-side range query, since a year of bookings should not cross the wire to render one day |
| Past-date booking prevention | Trivial to add, no design content |
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
- `orval.config.ts`: pointed `input` at the live spec, and flipped
  `includeHttpResponseReturnType` to `true`. With it `false`, the generated functions discard the
  status code and return a 409's error body typed as a `Reservation` — success and conflict are
  indistinguishable at the type level. Since surfacing the conflict is the point of the feature,
  the UI needs the status.
