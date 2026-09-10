# Amenity Reservations — Design

This is a time-boxed slice, not a finished product. The goal is a correct, well-tested vertical
slice through the part of the problem that is actually hard (never double-booking a shared
amenity, per building), plus an honest account of what was cut and why.

---

## 1. Assumptions

**Persistence and runtime.** Storage is in-memory, single-process, and non-durable; a restart
loses all reservations. This does *not* make concurrency a non-issue. ASP.NET Core serves
requests on a multi-threaded thread pool against a singleton store, so two simultaneous `POST`s
genuinely interleave. There are two distinct hazards: a logical race on the "is this slot free?"
check, and a raw data race on `List<T>` itself. Both are addressed in §5.

**Time.** All instants are stored in UTC as `DateTimeOffset` (as scaffolded). Intervals are
**half-open, `[start, end)`** — a 4:00–6:00 booking does not block a 6:00–8:00 booking. Adopting
one convention explicitly and applying it everywhere removes a whole class of boundary bugs.
Display is building-local via `Building.TimeZoneId` (IANA).

**Identity.** There is no auth in this exercise. An `X-Resident-Id` request header stands in for
an authenticated principal, resolved in exactly one place so it can be replaced with real auth
without touching booking logic. Resident identity is **never** read from the request body —
otherwise any caller could book as anyone.

**Tenancy.** An amenity belongs to exactly one building and never moves between buildings. A
resident belongs to exactly one building and may only book amenities in that building.

**Booking semantics.** A reservation is for a whole amenity instance (no sub-resources like
"treadmill 3"), made by a resident for themselves (no book-on-behalf-of), and always
forward-dated.

**Scale.** Hundreds of reservations per amenity, so linear scans are fine. Queries are
nonetheless written in the shape a real index would serve: `(building_id, amenity_id, start_utc)`.

---

## 2. Questions for the Product Manager

Each has the default shipped in its absence, so an unanswered question never blocks the slice.

1. **Is `Capacity` concurrent *reservations* or concurrent *people*?** A gym with capacity 2
   could mean two bookings or two bodies. If bodies, a booking needs a party size and capacity
   becomes a sum rather than a count. *Default: concurrent reservations.*
2. **Is there any reason why non-admins (residents) should see who booked a slot?** Showing names 
   is a privacy decision. *Default: opaque busy blocks; identity only on your own bookings.*
3. **Must bookings align to a grid** (:00/:30), or are arbitrary minutes allowed? This decides
   whether availability is a computed slot list or a free-form range. *Default: arbitrary minutes.*
4. **"One active booking per resident" — active how?** Per amenity, or building-wide? Currently in
   progress, simultaneous, or any future booking? Does cancelling immediately restore the allowance 
   (inviting cancel-and-rebook churn)? *Default: not enforced.*
5. **Opening hours — per weekday? Holidays? Which time zone governs?** Does guest parking ship with
   `MaxBookingMinutes = 1440`, i.e. a legitimate 24-hour booking that *must* cross midnight and
   therefore cannot respect a naive open/close window? Are overnight amenities exempt? *Default: 
   not enforced.*
6. **Can a resident ever book another building's amenity?** (sister buildings in a portfolio,
   amenities shared between two towers)? *Default: strict isolation.*
7. **Cancellation policy.** Cutoff before start? Can you cancel a booking already in progress?
   No-show consequences? *Default: cancel any active booking whose window hasn't ended.*
8. **Booking horizon** — how far ahead can residents book? *Default: 30 days.*
9. **Is there a staff/manager role?** Maintenance blackouts are really "a reservation owned by the
   building," which is nearly free if modeled that way now. *Default: out of scope, but the model
   accommodates it.*
10. **With multiple same-kind amenities, does the resident pick "Party Room (East)" explicitly, or
    request "a party room" and get assigned one?** Pooled assignment is a substantially larger
    feature — allocation policy, fairness, re-assignment. *Default: explicit pick.*

Questions 1, 4, 5, and 10 are the ones whose answers would change the code. That clustering drives
the scope cuts below.

---

## 3. Scope

### In scope

- `Building` and `Resident` entities; tenant scope derived from the caller.
- Four endpoints: create, list-for-amenity, list-mine, cancel.
- Overlap **and** capacity enforcement, correct under concurrent requests.
- The two unambiguous rules from feature 5: `MaxBookingMinutes` and valid-future-interval.
- One React screen: amenity picker, day view of busy blocks, booking form with a legible conflict
  error, and cancel.
- xUnit tests including a genuine concurrency test; Vitest tests over the frontend's pure helpers.
- Regenerated Orval client and committed OpenAPI snapshot.

### Deferred, and why

**Opening hours (feature 5).** The guest-parking contradiction in question 5 shows the
requirement isn't yet self-consistent. Doing it properly means per-weekday schedules, holiday
exceptions, and DST-correct local-time conversion. Nullable `OpeningLocalTime` / `ClosingLocalTime`
on the amenity plus `Building.TimeZoneId` make it purely additive later.

**One active booking per resident (feature 5).** Roughly fifteen lines once defined, and currently
undefined along four separate dimensions. Cutting it costs almost nothing to resume; guessing wrong
means a significant re-write of its semantics.

**Capacity as headcount.** The literal reading (concurrent reservations) is implemented in concept,
though for now, we assume everything has a maximum capacity of 1 in practice. Party size is out.

**A server-side availability/slot endpoint.** The reservation list plus `Capacity` is sufficient to
render availability client-side, and a server slot grid forces the time-granularity decision that is
question 3. Deriving on the client keeps the API honest about what's actually specified.

**Reschedule.** Cancel-and-rebook covers it.

**Also out:** waitlists, recurring bookings, notifications, no-show tracking, quotas, blackout
dates, fees, pagination, logging, real auth.

### Not deferred, because it's already free

**Multiple amenities of the same kind in one building.** See §4.

---

## 4. Data model

```
Building    { Id, Name, TimeZoneId }
Resident    { Id, BuildingId, DisplayName, UnitNumber? }
Amenity     { Id, BuildingId, Name, Kind, Description?, Capacity,
              MaxBookingMinutes, OpeningLocalTime?, ClosingLocalTime? }
Reservation { Id, BuildingId, AmenityId, ResidentId,
              StartUtc, EndUtc, Status, CreatedAtUtc, CanceledAtUtc? }
```

### Amenity is an instance, not a type

The scaffold's `Amenity` quietly conflates the *kind* of amenity with the *bookable instance*.
Resolving that in favor of the instance is the decision that avoids a painful future migration,
and it is essentially free: two party rooms in one building are simply two `Amenity` rows sharing
`Kind = PartyRoom`. Because rules live on the instance, per-instance divergence and per-building
divergence both fall out automatically with no new tables. The seed data exercises both.

The direction that *would* hurt is the opposite one — putting rules on a shared type row and later
discovering instances need overrides. That migration needs nullable override columns on every
instance, a fallback resolution layer, and a backfill of anything that had drifted. Instance-first
is strictly more general: adding an `AmenityType` defaults table later is purely additive, with
nullable instance columns falling back to type defaults and no backfill of existing rows.

`Kind` is modeled as a string-serialized enum so the generated TypeScript client gets a union
type. A real system would promote this to a lookup table so new kinds don't require a deploy.

### Why `Reservation.BuildingId` is denormalized

`AmenityId` alone would suffice, and normally derivable data shouldn't be stored. It is safe here
precisely because of the assumption that an amenity never changes buildings, which makes the field
immutable at creation and drift-proof. In exchange it buys the composite index
`(building_id, amenity_id, start_utc)`, natural partitioning, and Postgres row-level security keyed
on a column that is physically present. That assumption will be written into the code comment so 
anyone who later wants amenities to move buildings knows exactly what they are breaking.

### Cancellation is a state transition, not a delete

`Status ∈ {Active, Canceled}` plus `CanceledAtUtc`. Cancellation is an event the business cares
about — audit trail, no-show policy, notifications — and availability stays a pure filter on
`Status == Active`. The cost is remembering that filter everywhere, contained by routing all reads
through the store's accessors.

### Invariants

These are exactly what the tests assert.

| | Invariant |
|---|---|
| **I1** | `StartUtc < EndUtc` (zero-length and inverted rejected) |
| **I2** | duration ≤ `Amenity.MaxBookingMinutes` |
| **I3** | max concurrency over `[start, end)` among active reservations ≤ `Amenity.Capacity` |
| **I4** | booking resident's `BuildingId` == amenity's `BuildingId` |
| **I5** | `Reservation.BuildingId` == amenity's `BuildingId`, immutable |
| **I6** | `StartUtc` ≥ now, within the booking horizon |
| **I7** | *(deferred)* within opening hours |
| **I8** | *(deferred)* one active booking per resident |

---

## 5. Preventing double-booking

### The failure mode

**A time-of-check-to-time-of-use (TOCTOU) race producing write skew.** Both requests read the
reservation set, both independently evaluate "does this overlap anything?" against a snapshot that
is already stale, both conclude no, and both insert. Neither request is individually wrong — the
invariant is violated by the *interleaving*, which is what makes it invisible to single-request
testing and to manual clicking. Underneath sits a second, cruder failure: concurrent
`List<T>.Add` can lose items or corrupt internal state outright.

### The mechanism

**An atomic check-and-insert inside a critical section striped per amenity.** Reservation storage
is partitioned by amenity id, and each partition has its own gate. The booking path takes that
amenity's gate, re-validates I3 against the live partition, inserts, and releases — so no window
exists between check and insert.

Three properties make this right here:

- **Lock granularity matches the invariant's scope.** I3 is scoped to a single amenity instance,
  so serializing within one amenity is both sufficient for correctness and sufficient for
  throughput. Booking Building A's gym never blocks booking Building B's party room.
- **Building isolation comes for free.** An amenity belongs to exactly one building, so per-amenity
  gates are strictly finer-grained than per-building gates. No separate mechanism needed.
- **Striping is bounded** by the amenity count, so the gate map cannot grow without limit.

Critically, **the partition key and the lock key are the same**. A single shared list guarded by
per-amenity locks would still be a data race — amenity X's read can overlap amenity Y's write on
the same list. Partitioning by the lock key makes it structurally impossible to touch a partition
without holding its gate, and it mirrors how the data would be sharded in a real database.

The overlap predicate and the capacity check are pure functions defined once and used by both the
validator and the inserter — one source of truth, unit-testable without a web host.

### The capacity subtlety

The obvious check — *count overlapping reservations, reject if ≥ capacity* — is **wrong**. With
capacity 2 and existing bookings 9:00–11:00 and 11:00–13:00, a candidate 10:00–12:00 overlaps
both, so a naive count rejects it. But at no instant are three reservations concurrent: at 10:30
it's two, at 11:30 it's two. The correct question is the **maximum concurrency over the candidate's
window**, which is a sweep over interval boundaries:

```csharp
static bool FitsCapacity(IEnumerable<Reservation> active,
                         DateTimeOffset start, DateTimeOffset end, int capacity)
{
    // Only reservations overlapping [start, end) can affect concurrency inside it.
    var overlapping = active.Where(r => r.StartUtc < end && start < r.EndUtc).ToList();
    if (overlapping.Count < capacity) return true;

    // Concurrency is a right-continuous step function: its maximum over the window is
    // attained at the window's start or at some reservation's start boundary.
    var boundaries = overlapping.Select(r => r.StartUtc)
        .Append(start)
        .Where(t => t >= start && t < end)
        .Distinct()
        .OrderBy(t => t);

    foreach (var t in boundaries)
        if (1 + overlapping.Count(r => r.StartUtc <= t && t < r.EndUtc) > capacity)
            return false;

    return true;
}
```

For capacity 1 this degenerates to plain overlap rejection.

### Alternatives considered and rejected

**A single global lock over the whole store.** Correct, and the least code. Rejected because the
granularity is conceptually wrong rather than merely slow: it implies the invariant spans all
buildings. That misdirection would carry into a SQL port as an unnecessary table-level lock.

**Optimistic concurrency on an immutable snapshot** — replace the list with an `ImmutableList` and
loop on `Interlocked.CompareExchange`, re-validating per attempt. Genuinely correct and lock-free,
and the closest analogue to how a database would do this with row versions. Rejected because the
retry loop must re-run the whole capacity sweep on every attempt, degrades precisely under
same-amenity contention (the case that matters), and is materially harder for a reviewer to verify
at a glance. With no throughput requirement, readability wins.

**A discrete slot-inventory table** — pre-materialize fixed buckets (ex. 30 minutes) per amenity
per day, so booking is an insert against a unique key and the *database* enforces
no-double-booking with no application lock at all. This is very likely the production choice, and
it is rejected *for now* rather than on merit: it hardcodes a granularity the PM hasn't specified
(question 3), forces bookings onto a grid, and multiplies row count by amenities × days × buckets.

---

## 6. Building isolation

The mechanism is **deriving the tenant from the principal.** `X-Resident-Id` resolves to a 
`Resident`, whose `BuildingId` is authoritative. Every read filters on it; every write 
cross-checks `amenity.BuildingId == currentResident.BuildingId`.

This fails closed in a way path-parameter tenancy does not: there is no code path that can forget
the tenant filter, because the filter's input arrives with the request context instead of being
supplied by the caller. A nested route
(`/api/buildings/{buildingId}/amenities/{amenityId}/reservations`) is the reasonable alternative
and self-documents nicely, but it means trusting a caller-supplied tenant, which then needs its own
authorization check anyway.

A cross-tenant request returns **404, not 403** — confirming that Building B's party room exists is
itself a leak.

One consequence to declare: `GET /api/amenities` becomes tenant-scoped, a behavior change to the
worked example under the assumption that amenities are building-specific.

Seed data includes two buildings that each have a party room, and one building with *two* party
rooms on divergent rules — so both the isolation requirement and the multiple-same-kind case are
demonstrable in the UI, not only in tests.

---

## 7. API surface

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/amenities` | Caller's building only *(modified from scaffold)* |
| `GET` | `/api/amenities/{amenityId}/reservations?fromUtc=&toUtc=` | Busy windows; defaults to now → +7d, range-capped |
| `GET` | `/api/reservations/mine` | The caller's active bookings |
| `POST` | `/api/reservations` | Create → `201` + `Location` |
| `POST` | `/api/reservations/{id}/cancel` | Soft cancel |

Create takes `{ amenityId, startUtc, endUtc }` — no `residentId`, per §1.

**Cancel is `POST .../cancel` rather than the scaffold's suggested `DELETE`,** because it is a
transition to `Canceled` rather than a deletion, it is naturally idempotent, and it leaves room for
a `reason` body without redefining the verb. `DELETE` is the defensible alternative had we chosen
hard deletes.

### List response and privacy

Anonymization is a **server-side** guarantee, not a UI convention. Filtering in the client would be
no protection at all — anyone can read the raw response in dev tools or curl the endpoint
directly — so the identifying fields must never leave the process.

Three things enforce that:

**Domain records are never serialized.** `Reservation` is internal to the backend; every
reservation endpoint returns a dedicated contract type. This is a deliberate departure from the
scaffold's `Results.Ok(store.Amenities)` pattern, which returns the domain record directly.

**No reservation response carries a resident identifier — including the caller's own.** There is
nowhere for one to leak from, because no such field exists on any response type. The caller
already knows who they are, so echoing it back buys nothing.

**The two audiences are separate types, not one type with nullable fields.** A single DTO with an
optional `residentId` would rely on remembering to null it out on every code path; one missed
branch is a silent leak. Instead:

```jsonc
GET /api/amenities/{amenityId}/reservations
{
  "amenityId": "...", "capacity": 2, "maxBookingMinutes": 90,
  "windowStartUtc": "...", "windowEndUtc": "...",

  // Every active reservation in the window, from anyone. Start and end only —
  // this type has no field capable of identifying a resident.
  "busy": [ { "startUtc": "...", "endUtc": "..." } ],

  // The caller's own subset, carrying the id needed to cancel.
  "mine": [ { "id": "...", "startUtc": "...", "endUtc": "...", "status": "Active" } ]
}
```

`busy` is the complete occupancy picture including the caller's own bookings, so availability is
computed from one array with no client-side merging. `mine` is the actionable subset. The
duplication is intentional.

Note that reservation **ids** are also withheld for other residents' bookings. Cancellation is
authorized server-side regardless, so leaking them wouldn't permit anything — but an opaque
identifier a resident has no use for is one more thing to correlate across responses.

What necessarily *is* disclosed is that a window is occupied. That is the feature.

**Errors are RFC 9457 `ProblemDetails`** (built into ASP.NET Core via `AddProblemDetails`) with a
machine-readable `code` extension so the UI branches on the reason rather than string-matching:

| Status | `code` | When |
|---|---|---|
| 409 | `slot_unavailable` | capacity would be exceeded; carries the conflicting **windows**, not who holds them |
| 404 | `amenity_not_found`, `reservation_not_found` | also the cross-tenant response |
| 401 | `resident_required` | missing or unknown resident header |
| 400 | `invalid_interval`, `exceeds_max_booking_length`, `starts_in_past`, `beyond_booking_horizon` | one code per rule |

The 400s are split by rule rather than collapsed into a single `validation_failed`, so the UI can
point at the field that's wrong — a booking that's too long and one that's too far ahead need
different corrections.

---

## 8. Testing

xUnit at `backend/Api.Tests`, testing the service layer directly rather than over HTTP — the payoff
for extracting booking logic out of `Program.cs`.

- **Overlap predicate:** back-to-back allowed on both sides, identical window rejected, full
  containment in both directions, partial overlap leading and trailing, zero-length and inverted
  rejected.
- **Capacity sweep:** the trap case above (9–11 and 11–13 existing, 10–12 candidate, capacity 2)
  must **succeed**; a true three-way overlap must fail at capacity 2; capacity 1 rejects any
  overlap.
- **Building isolation:** an identical window on Building B's party room succeeds; a cross-tenant
  booking returns not-found; Building B's reservations never appear in Building A's list.
- **Privacy, asserted against serialized JSON rather than object shape:** build a schedule
  containing another resident's booking, serialize the response with the API's own JSON options,
  and assert that neither their resident id nor their reservation id appears anywhere in the
  payload. Asserting on the DTO's properties would only re-state the type definition; asserting on
  the bytes catches a field added later, a `JsonExtensionData` bag, or a projection that starts
  passing the domain record through.
- **Concurrency — the test that proves the mechanism:** N tasks released simultaneously off a
  `Barrier`, all booking the identical slot on a capacity-1 amenity, asserting **exactly one**
  success and N−1 conflicts; then capacity 2 asserting exactly two. Note the asymmetry: this test
  is deterministic with the gate in place but only probabilistic at catching its absence, so it is
  verified to fail when the gate is removed rather than trusted to.
- **Cancel:** own booking frees the slot for rebooking; someone else's returns not-found;
  cancelling twice is idempotent.
- **Frontend (Vitest):** slot derivation and error mapping live in pure helper modules and are
  tested directly. React Testing Library component tests are out — setup cost against a UI this
  small — and end-to-end is out of scope with in-memory state.

---

## 9. Changes to the scaffold

- **Added:** `Building.cs`, `Resident.cs`, `AmenityKind`, `ReservationStatus`; an xUnit project and
  an `amenity.sln` so `dotnet test` runs from the root; `AddProblemDetails()`; Vitest on the
  frontend.
- **Extended:** `Amenity` gains `BuildingId`, `Kind`, and nullable opening hours; `Reservation`
  gains `BuildingId`, `Status`, `CreatedAtUtc`, `CanceledAtUtc`. `Reservation.ResidentId` changes
  from `string` to `Guid` for referential consistency with the new `Resident` entity.
- **Extracted:** booking logic into a service with pure predicates alongside it. Endpoints in
  `Program.cs` stay thin — parse, delegate, map the result to a status code.
- **Added a contracts layer.** Reservation endpoints return dedicated response types built by a
  single projection function, rather than serializing domain records the way the amenities worked
  example does. See §7 for why this is the mechanism that keeps resident identity server-side.
- **Encapsulated:** `InMemoryStore.Reservations` is no longer a public mutable `List`. This is not
  tidiness — a public list invites callers to bypass the gate, silently reintroducing the exact
  race being prevented. Access goes through methods that hold it.
- **Added a dev-only `GET /api/dev/residents`,** so the UI can offer a "you are…" picker. It
  exists only because there is no auth; a real deployment would not publish a resident roster.
- **Orval now generates through a custom `mutator`** (`src/api/http.ts`) rather than calling
  `fetch` directly. Two defects in the stock fetch client forced this, and both mattered:

  Non-2xx bodies were parsed into the success type, so a 409 conflict came back typed as a
  `MyReservation`. Surfacing double-booking clearly is the point of the feature, so failures have
  to be distinguishable. The wrapper throws a typed `ApiError` carrying the problem `code` and
  the conflicting windows.

  Header parameters were dropped from the generated signatures even though `X-Resident-Id` is
  correctly documented in the spec. The wrapper attaches it centrally, which is also where a real
  app would attach a bearer token.

  `includeHttpResponseReturnType: false` is kept so call sites still receive the parsed body
  rather than a `{ data, status, headers }` envelope.
- **Orval input** stays the committed `openapi.json` snapshot (deterministic, works offline).
  `pnpm gen:api:live` refreshes it from a running backend and regenerates.

---

## 10. TBD

Answer PM questions 1, 4, 5, and 10, then land opening hours and the per-resident booking limit —
both are small once defined, and the data model already accommodates them. Replace the in-memory
store with Postgres, moving the no-double-book invariant from the application gate into a
`tstzrange` exclusion constraint for capacity-1 amenities and serializable-isolation counting for
capacity > 1. Swap the `X-Resident-Id` shim for real authentication behind the single resolver it
already lives in.
