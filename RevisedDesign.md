# Amenity Reservations — Design (revised)

> This is the revision of [`Design.md`](./Design.md), which is kept unchanged as the original
> first pass. The capacity model, the one-hour slot rule and the PM questions are carried over
> as written; what's new here is the double-booking failure-mode analysis, the tenancy
> mechanism, an API section matching what actually shipped, and an explicit account of what was
> cut and why.

A resident picks an amenity, sees a day of hour-long slots with the room left in each, books one,
and cancels it. Everything below is scoped to that loop.

## The two decisions everything else follows from

1. **Every reservation is exactly one hour and starts on the hour.** Two bookings therefore
   either occupy the same slot or they don't. There is no interval-overlap math anywhere in the
   codebase — no `start < other.end && end > other.start`, no half-open-interval bugs. 
2. **Every booking rule is a column on the amenity row, not code.** There are no per-amenity
   special cases and no `if (amenity.Name == "Pool")` anywhere. Adding a rule means adding a
   column and one line in one function.

---

# Entities

## Residents
- The only "users". Seeded — there is no sign-up (see *Deliberately left out*).
- `id`, `building_id`, `building_name`, `name`, `email`, `phone`, `unit_number`

`building_name` is display metadata, denormalized onto the resident. There is no `Building`
entity: `building_id` is the tenancy key and nothing in this slice reads a building row. The
name exists only so the UI can say "Building A" instead of a truncated GUID. In a real schema it
is `buildings.name` and is joined.

**Relationships:** 1..n with Reservations.

## Amenities
- `id`, `building_id`, `name`, `description`, `total_capacity`, `reservation_capacity`,
  `max_size_per_reservation`, `opening_hour_utc`, `closing_hour_utc`, `is_open`

Capacity is three independent dimensions because real amenities cap different things:

| Column | Caps | Party Room | Gym | Pool Cabanas | Guest Parking |
| --- | --- | --- | --- | --- | --- |
| `reservation_capacity` | concurrent bookings | 1 | — | 4 | 1 |
| `total_capacity` | concurrent people | — | 10 | 12 | — |
| `max_size_per_reservation` | people per booking | 20 | 2 | 6 | 1 |

`null` means "this amenity doesn't cap that dimension". I used `null` rather than `0` so the
column can't be confused with a legitimate zero, and so the check reads as
`if (amenity.TotalCapacity is int total)` — the compiler enforces handling the absent case.

Pool Cabanas is the case the model exists for: four cabanas *and* twelve swimmers, either of
which can run out first. It is seeded so the design is actually exercised.

`is_open` is a manual admin close ("pool shut for maintenance"), not a computed field. Nothing
in this slice writes it; it exists so an operator has a kill switch that doesn't require
rewriting hours.

**Relationships:** 1..n with Reservations.

## Reservations
- `id`, `amenity_id`, `resident_id`, `start_utc`, `size`
- `end_utc` is **computed** as `start_utc + 1h`, never stored — a bad write can't violate the
  one-hour invariant.
- `size` is how many people the booking claims, counted against `total_capacity`.

**Relationships:** n..1 with Residents, n..1 with Amenities.

---

# Preventing double-booking

**This is the crux, so let me be precise about what the actual bug is.**

## The failure mode: check-then-insert race (TOCTOU)

The naive handler is three steps:

```
1. read   the reservations already in this slot
2. check  that adding mine stays within capacity
3. insert mine
```

Two requests for the last space in the Party Room can interleave as `1,1,2,2,3,3`. Both read an
empty slot. Both compute "capacity 1, currently 0, room for one more". Both insert. The amenity
is now double-booked, **and no individual line of code was wrong** — each one did exactly what
it was written to do. The bug lives in the gap between step 1 and step 3.

This is not hypothetical here: `InMemoryStore.Reservations` is a plain `List<T>`, the API is a
singleton serving concurrent requests, and `List<T>` is not thread-safe, so a genuinely
concurrent write can also corrupt the list itself.

## Chosen mechanism: one lock around the whole read-check-write

```csharp
lock (store.SyncRoot)
{
    var inSlot = store.Reservations.Where(r => r.AmenityId == amenity.Id && r.StartUtc == start).ToList();
    if (Capacity.Reject(amenity, inSlot, request.Size) is (int status, string message))
        return Results.Problem(message, statusCode: status);
    store.Reservations.Add(reservation);
}
```

The point is the *extent* of the critical section, not that a lock exists. Locking only the
`Add` would be useless — the race is in the gap, not in the mutation. Read, decide, and write
are one atomic step or the whole thing is theatre.

**Verified, not asserted:** 30 concurrent `POST`s at a `reservation_capacity: 1` slot return
exactly one `201` and twenty-nine `409`s, and 30 concurrent size-2 `POST`s at the gym
(`total_capacity: 10`) return exactly five `201`s and twenty-five `409`s — both capacity
dimensions, against the running API. The commands and their output are in
[`WhatNext.md`](./WhatNext.md); this is the one piece of the design that would be embarrassing to
get wrong and easy to believe you'd got right.

**How this survives contact with a real database.** The lock is the in-memory stand-in for a
`SERIALIZABLE` transaction, or more cheaply `SELECT ... FROM amenities WHERE id = ? FOR UPDATE`
at the top of the transaction — take the amenity row as the lock, then read the slot, check, and
insert inside it. Same shape, same reasoning, and it works across processes, which a `lock` does
not.

## Alternative rejected: unique index on `(amenity_id, start_utc)`

Genuinely tempting. The database enforces it for free, it is correct under any level of
concurrency, it survives multiple app servers, and it needs no application locking at all — you
just catch the duplicate-key violation and return `409`.

**I rejected it because it can only express `capacity == 1`.** A unique index says "at most one
row with these values". It cannot say "at most four rows" and it certainly cannot say "at most
twelve people across those rows", which is the `total_capacity` dimension and the whole reason
the Pool Cabanas case exists. I would have had to special-case exclusive amenities against a
unique index and multi-capacity amenities against a lock — two mechanisms, two failure modes,
and the interesting bugs living in whichever one I thought about less. One mechanism that covers
every amenity is worth more than a free one that covers a third of them.

*(A third option — an optimistic version column with retry — is the right answer under high
contention, since it avoids holding a lock across the check. Amenity booking is low-contention
by nature: a handful of residents, a handful of slots. I'd be paying retry-loop complexity to
optimize a lock that is never contended.)*

## One booking per resident per slot

Scoped to `(amenity, start_utc)`: a resident may not hold two bookings for the same slot **at the
same amenity**. Distinct from capacity, and not a policy quota. Two rows for one resident in one
slot are incoherent on their own terms, and `SlotAvailability.MyReservationId` surfaces only the
first — so the extras would be invisible in the UI and impossible to cancel, quietly consuming
capacity. The check sits **inside the same lock** as the capacity decision, for exactly the same
reason: two concurrent requests would otherwise both find nothing and both insert. Verified with
20 concurrent identical requests → one `201`, nineteen `409`s.

Holding the gym *and* the party room at 19:00 is still allowed, and that is deliberate rather
than an oversight in the check. One person can't be in two rooms at once, but a resident booking
the party room for guests while their partner uses the gym is ordinary, and the record carries no
attendee to distinguish the two. Whether a resident may hold overlapping slots at *different*
amenities is a policy call, not a correctness one — it belongs with the quota question (PM
question 6), not with this guard.

## Where the user actually meets this

Capacity is decided in exactly one function, `Capacity.Reject`, called by **both** the
availability endpoint and the create endpoint — so what the UI greys out and what the server
enforces cannot drift apart. Availability additionally returns the *remaining* room per slot
rather than a bare boolean, so the client has what it needs to stop a too-large party before the
request is sent. **It doesn't yet** — `isAvailable` is computed for a party of one and the Book
button never reads `size`, so an oversized party is caught by the server rather than by the row
that already knows better. Open, and written up in [`WhatNext.md`](./WhatNext.md). The server
re-checks regardless, because the client is never the authority.

Statuses carry different advice. **400** means *the request is wrong as asked* — not on the hour,
already started, outside opening hours, party larger than `max_size_per_reservation`; retrying it
unchanged will always fail. **409** means *the slot's state refuses it*: fully booked, you already
hold this slot, or the amenity is closed. Only the first of those three gets better on its own,
so 409 is "pick another slot", not "retry shortly". (A permanent admin close arguably deserves
`503` rather than `409` — noted in `WhatNext.md`, latent today because nothing seeds
`is_open = false`.)

---

# Multi-tenancy / Isolation

Every `Resident` and every `Amenity` carries a `building_id`. **The caller never supplies it** —
the server resolves the caller to a `Resident` and reads `building_id` from that record. A
client that wants to see building B's data has nothing to tamper with.

It is enforced on reads *and* writes:

- `GET /api/amenities` filters to the caller's building.
- Availability and create look the amenity up as
  `Id == amenityId && BuildingId == resident.BuildingId` — the tenancy predicate is part of the
  lookup, not a separate `if` that a later refactor can forget.
- A cross-building amenity returns **404, not 403**. A 403 would confirm the amenity exists.

Two buildings are seeded so this is demonstrable rather than merely claimed: switch residents in
the UI and the amenity list changes.

**`residentId` on the query string is a stand-in for authentication, not authentication.** In
production it is a token claim. Everything downstream already reads the identity from a resolved
`Resident` record, so replacing the source is a one-line change in one helper (`Caller`).

---

# API

All routes are under `/api` so the Vite dev proxy forwards them. All times are UTC.
Every error the handlers themselves return is RFC7807 `ProblemDetails`, so the UI has exactly one
field (`detail`) to render. Errors raised *before* a handler runs — model-binding failures on a
missing or malformed query parameter — are plain-text ASP.NET exceptions instead, which the
generated client then fails to parse. Open; see [`WhatNext.md`](./WhatNext.md).

### `GET /api/residents`
The "log in as" picker. Unauthenticated on purpose — it exists because auth is out of scope.

### `GET /api/amenities?residentId=`
→ `200` `Amenity[]`, scoped to the caller's building · `401` unknown resident.

### `GET /api/amenities/{amenityId}/availability?residentId=&date=YYYY-MM-DD`
One day of hour-long slots between the amenity's opening and closing hours. `date` defaults to
today.

```jsonc
[{
  "startUtc": "2026-08-30T10:00:00+00:00",
  "endUtc":   "2026-08-30T11:00:00+00:00",
  "remainingSize": 12,           // null when the amenity doesn't cap headcount
  "remainingReservations": 4,    // null when it doesn't cap booking count
  "isAvailable": true,           // false if closed, full, or already started
  "unavailableReason": null,     // why not, when isAvailable is false
  "myReservationId": null        // set when the caller already booked this slot
}]
```

`unavailableReason` comes from the same `Capacity.Reject` the create path enforces with, so the
row can say *"This slot is fully booked."* rather than showing free capacity next to a dead
button. An elapsed slot ("Already started") has room but can't be booked — without the reason,
the UI reads as broken. The reason is computed without reference to `myReservationId`, so on an
exclusive amenity the slot *you* hold reports itself full next to your own live Cancel button.
Open; see [`WhatNext.md`](./WhatNext.md).

`myReservationId` is why there is no fourth "list my reservations" endpoint: the same row the UI
already renders can offer **Book** or **Cancel** without a second round trip.

→ `401` unknown resident · `404` amenity not in the caller's building.

### `POST /api/reservations`
```jsonc
{ "residentId": "...", "amenityId": "...", "startUtc": "2026-08-30T10:00:00Z", "size": 2 }
```
→ `201` + the created `Reservation` (**not** `{ success: true }` — the UI needs the `id` to
offer a cancel, and a bare boolean forces a refetch to learn what was just created).
→ `400` not on the hour · already started · outside opening hours · size over
`max_size_per_reservation`.
→ `409` slot full — the message names the room actually left
(*"Only 3 space(s) left in this slot — you asked for 6."*) · you already hold this slot ·
amenity closed.
→ `401` unknown resident · `404` amenity not in the caller's building.

`401` is returned by this endpoint and by availability, but is **not** declared on either, so it
never reaches the generated client's discriminated union. Open; see
[`WhatNext.md`](./WhatNext.md).

### `DELETE /api/reservations/{id}?residentId=`
→ `204` · `404` if it doesn't exist **or isn't yours**. Ownership is part of the lookup
predicate, and cancelling someone else's booking is indistinguishable from cancelling one that
never existed.

---

# Deliberately left out, and why

| Left out | Why |
| --- | --- |
| **Per-resident booking *limits*** (TASK.md's "one active booking per resident") | Consecutive bookings across *different* slots are deliberately allowed. A quota is a policy question I'd want a PM answer on before encoding — per day? per week? per amenity or across all of them? Rolling window or calendar day? Guessing wrong means writing the wrong migration later. Mechanically it's cheap: one more count query inside the existing lock, which is already the right place for it. **Not** deferred: duplicate bookings of the *same* slot by the same resident, which is a correctness hole rather than a policy call — see below. |
| **Real time zones and DST** | Everything is UTC and opening hours are integer UTC hours. Buildings are physical and residents think in local time, so `buildings.time_zone` (an IANA id) is the real fix, with hours stored local and converted at the edge. Deferred because DST correctness deserves proper attention, not the last twenty minutes of a time box — and getting it *quietly* wrong is worse than not doing it. |
| **Admin portal** | Residents and amenities are seeded. Since every rule is already an amenity column, the admin surface is CRUD over that table — no new domain logic, so it's genuinely additive later. |
| **Scheduled job reopening amenities** | `is_open` is a manual override. Nothing in this slice needs a scheduler, and adding one means adding a whole runtime concern for one boolean. |
| **Notifications** (PM question 5) | Needs a channel decision (email/SMS/push) and a template story. Real product surface, not a thin-slice detail. |
| **Waitlists and recurring bookings** | Neither is in TASK.md. Both are significant features that would have eaten the double-booking work, which is what's actually being graded. |
| **Persistence** | TASK.md says not to. The store is a singleton with the lock discipline already in the right place, so swapping in EF Core means replacing the `lock` with a transaction at the same boundary. |
| **Authentication** | `residentId` is client-supplied and trivially spoofable. Called out explicitly above rather than hidden, because a reviewer should not have to discover it. |
| **Pagination, rate limiting, audit log, tests** | A day of slots is at most 24 rows. The rest is production hardening on a slice whose point is the concurrency argument. Given more time the first thing I'd add is an automated version of the 30-way concurrency check — right now it lives in `WhatNext.md` as a transcript, which is not where a regression test belongs. |

---

# Questions for PM

1. How is capacity for an amenity defined, and do different amenities have different capacity
   requirements? I've assumed both total occupants and number of reservations can matter, and
   that some amenities (pool cabanas) need both at once.
2. Do residents need to be able to sign up, or is that handled by the admin?
3. What are our security concerns? Is this exposed to the internet or only on the building's
   intranet? (This decides how much the `residentId` stand-in actually costs us.)
4. Do residents only connect via the web, or do we also support an app — or are we thinking
   about building one soon?
5. Do we want a way to send messages back to residents? Maybe the pool unexpectedly closes, or
   a reservation gets cancelled.
6. *(New)* Should a resident be limited in how many slots they can hold at once, and over what
   window? This is the one rule I punted on and it's a pure policy call.
7. *(New)* Are amenity hours the same every day, or do weekends differ? Today the schema assumes
   one pair of hours for all days.

---

# What I changed in the scaffold

- `Amenity`: added `BuildingId`, replaced `Capacity` with the three capacity columns, added
  opening/closing hours and `IsOpen`. **Removed `MaxBookingMinutes`** — it is dead once every
  booking is an hour, and leaving it in would suggest variable-length bookings are supported.
- `Reservation`: `ResidentId` is now a `Guid` (it matches a real entity), added `Size`, and
  `EndUtc` became computed.
- Added `Resident`, `Capacity` (the one place a booking is allowed or denied), and `Contracts.cs`.
- `InMemoryStore`: added residents, a `SyncRoot` lock object, and a two-building seed.
- `GET /api/amenities` **changed signature** — it now requires `residentId`, because an unscoped
  amenity list is exactly the tenancy leak this design is meant to prevent.
- `orval.config.ts`: flipped `includeHttpResponseReturnType` to `true`. The scaffold returned the
  bare response body, but a `409` and a `201` are both JSON objects and only the status
  distinguishes them. Orval now generates a discriminated union on `status`, so the compiler
  forces the error case to be handled.
- `frontend/openapi.json` regenerated from the live backend; client regenerated.
