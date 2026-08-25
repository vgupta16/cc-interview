# Amenity Reservations — Design Doc

## Assumptions & questions for a PM

Assumptions made to keep the slice tight:

- **One residence per resident-amenity interaction.** A resident is identified by an opaque
  `residentId` string; there's no real auth in this slice (see below).
- **No opening-hours enforcement.** `Amenity` has no opening-hours field in the scaffold; adding
  and enforcing it is mechanical but out of scope for the time box.
- **"One active booking per resident" is scoped per amenity, not building-wide.** The scaffold's
  wording is ambiguous ("one active booking per resident") — I read it as "you can't hold two
  overlapping bookings for the *same* amenity," not "a resident may only ever have one booking
  across the whole building." The latter seems too restrictive for real usage (why would booking
  the gym block booking guest parking?), but I'd confirm with a PM.
- **A booking's start time must be in the future.** `POST /api/reservations` rejects a slot whose
  `startUtc` has already passed (`400 slot_in_the_past`). No other minimum-notice window (e.g.
  "must book at least 1 hour ahead") is enforced — just "not in the past."
- **Cancellation is self-service and immediate**, no cancellation window/fee, no notice period.
- **Reservations are soft-deleted** (status flips to `Cancelled`) rather than removed, to keep
  history for support/abuse investigation.
- **No overbooking waitlist.** A full slot just fails; no queue.

Questions I'd ask a PM:

1. Should residents see *who else* booked a slot (privacy), or just that it's unavailable?
2. Is there a per-resident booking quota (e.g. max 3 active reservations building-wide) to stop
   one resident from hoarding the gym?
3. Do property managers need an override to cancel/edit any resident's booking?
4. Time zones: buildings presumably have a local time zone for "opening hours" — is that
   per-building or per-amenity?
5. Is there a minimum notice period beyond "not in the past" (e.g. must book at least an hour
   ahead), or a cancellation cutoff (can't cancel within 1 hour of start)?

## Data model

```
Building
  Id
  Name

Amenity
  Id
  BuildingId        (FK -> Building)   // not implemented in this slice, see Multi-tenancy
  Name
  Description
  Capacity           // concurrent bookings a single time slot allows
  MaxBookingMinutes   // longest a single booking may run

Reservation
  Id
  AmenityId          (FK -> Amenity)
  ResidentId          // opaque string identity, no auth in this slice
  StartUtc
  EndUtc
  Status              // Active | Cancelled
  CreatedAtUtc
```

Key constraints:

- `Reservation.StartUtc < Reservation.EndUtc`.
- `(EndUtc - StartUtc) <= Amenity.MaxBookingMinutes`.
- For any point in time, the count of `Active` reservations on an amenity whose interval
  contains that point must be `<= Amenity.Capacity`. Enforced as an interval-overlap check on
  every write, not a DB constraint (in-memory store — see below).
- A resident may not hold two overlapping `Active` reservations on the *same* amenity.

## API surface

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/amenities` | List amenities (worked example, unchanged) |
| `GET` | `/api/amenities/{amenityId}/reservations` | List all reservations (active + cancelled) for an amenity, sorted by start time |
| `POST` | `/api/reservations` | Create a reservation |
| `DELETE` | `/api/reservations/{id}?residentId=...` | Cancel a reservation (owner only) |

**`POST /api/reservations`**

```json
// request
{ "amenityId": "guid", "residentId": "string", "startUtc": "ISO-8601", "endUtc": "ISO-8601" }

// 201 Created
{ "id": "guid", "amenityId": "guid", "residentId": "string",
  "startUtc": "...", "endUtc": "...", "status": 0, "createdAtUtc": "..." }

// 400 { "error": "start_must_be_before_end" | "resident_id_required" | "exceeds_max_booking_length" | "slot_in_the_past" }
// 404 { "error": "amenity_not_found" }
// 409 { "error": "slot_full" | "resident_already_booked" }
```

**`DELETE /api/reservations/{id}?residentId=...`** — `204` on success (idempotent-ish: cancelling
an already-cancelled reservation still requires the caller to be the owner), `403` if
`residentId` doesn't match the reservation's owner, `404` if the id doesn't exist. `residentId`
is a query param rather than a body because `DELETE` requests conventionally carry no body and
minimal API model-binds query params cleanly.

I did **not** implement `PATCH` (editing a reservation's time). Editing is really
"cancel + recreate" from a conflict-checking standpoint, and exposing it as its own endpoint
adds a second code path to keep in sync with the same invariants for no real UX win in an MVP.

## Preventing double-booking — the crux

**Failure mode:** classic check-then-act race (TOCTOU / lost update). Two requests for the same
amenity and overlapping time both read "capacity available," both pass validation, both write —
capacity is exceeded. This is a real risk here: multiple residents plausibly hit "book" on a
popular Saturday evening slot within milliseconds of each other.

**Chosen mechanism:** a single in-process lock (`lock (_gate)`) around the entire
"count overlapping active reservations for this amenity, compare to capacity, insert" sequence
in `InMemoryStore.TryCreateReservation`. This makes the check-and-write atomic — no interleaving
is possible, so no two callers can both observe free capacity and both commit. I verified this
under load: firing 10 truly concurrent `POST` requests at the same capacity-1 slot produced
exactly **one** `201` and nine `409 slot_full`, with the store left holding exactly one active
reservation.

This works because the store is in-memory and single-process. It does **not** generalize to
multiple API instances behind a load balancer — the lock is per-process.

**Alternative considered and rejected (for now): a database-level constraint.** In production
with a real database, I'd drop the application lock and instead rely on the database to
serialize this — e.g. Postgres `EXCLUDE USING gist (amenity_id WITH =, tstzrange(start_utc,
end_utc) WITH &&)` so an overlapping-and-over-capacity insert is rejected by the DB itself, or a
`SELECT ... FOR UPDATE` on a per-amenity row before counting overlaps. That's the mechanism I'd
actually ship for a multi-instance API — it doesn't depend on there being exactly one process
holding the lock. I didn't build it here because the task explicitly scopes out a real database
("in-memory persistence is expected; do not spend time on a real database"), so an app-level
lock is the right-sized answer for *this* slice, with the DB constraint noted as the production
path.

**Alternative also considered: optimistic concurrency (version/ETag + retry).** Better under low
contention or when writes are otherwise expensive to serialize, but it adds client-visible retry
logic for a case (booking a shared amenity) where losing a race should just be a clear "that slot
just filled up" — pessimistic locking gives a simpler, more honest UX here.

## Multi-tenancy / isolation

**Not implemented in this slice** — flagged in the data model above with `Amenity.BuildingId`
sketched but unused. What I'd build:

- Every `Amenity` (and transitively every `Reservation`, via its `AmenityId`) carries a
  `BuildingId`.
- The authenticated resident's building comes from their session/JWT claim, never from a
  client-supplied field — a resident should not be able to pass `buildingId` on a request and
  see another building's data.
- Every store query is filtered by that building id as a mandatory parameter — not an optional
  one — so it's structurally impossible to write a handler that forgets the filter. In EF Core
  terms, a global query filter on `BuildingId` is the standard way to make this the default
  rather than something each endpoint has to remember.
- The double-booking lock and overlap check are already scoped by `AmenityId`, and amenities
  don't span buildings, so tenancy isolation and the concurrency mechanism don't interact badly.

I cut the implementation because this slice has no auth system to derive a trusted building id
from — bolting on a client-supplied `buildingId` query param would demonstrate the *wiring* but
not the actual security property (nothing stops a client from lying about it), so it'd be a
false signal rather than a real one.

## What I left out, and why

- **Opening hours / business-hours enforcement** — no field for it on `Amenity` in the scaffold;
  mechanical to add (`OpensAt`/`ClosesAt` + a range check) but not where the interesting design
  problem is.
- **Auth / real resident identity** — `residentId` is a free-text string the client asserts, with
  no verification. The frontend stores a random id in `localStorage` per browser profile so you
  can simulate two residents by opening two profiles. Swapping in real auth (session cookie or
  JWT) means deriving `residentId` server-side from the token instead of trusting the request
  body — the store/lock logic doesn't change.
- **Multi-tenancy enforcement** — see above.
- **Pagination on the reservations list** — fine at demo scale, would matter once an amenity
  accumulates thousands of historical bookings.
- **Editing a reservation's time** — see API surface note above.
- **Waitlists / notifications** — no queue for a full slot; a resident who's blocked just retries
  a different time.
- **Rate limiting / abuse prevention** on booking creation.

## Testing note

I don't have an interactive browser available in this environment to click through the UI
manually. I verified correctness via: `dotnet build` + running the API and exercising every
endpoint (success, every 4xx/409 branch, and a 10-way concurrent race on a capacity-1 slot) with
curl; `tsc -b` typechecking the frontend against the regenerated client; and confirming the Vite
dev server serves the page and proxies `/api/*` to the backend correctly. I did not get a
human-eyes screenshot of the booking form in a real browser before writing this — worth doing
before calling the slice done.

## Next steps (with another 4 hours)

I'd add opening-hours enforcement and a real `buildingId`-scoped tenancy filter (both are
mechanical given the patterns above), then get actual browser-based verification of the UI flow
including the double-booking error message. I'd also add a small test project
(`dotnet test` on `InMemoryStore`, especially the concurrency case, plus a couple of
`WebApplicationFactory` integration tests for the endpoints) since right now correctness is
demonstrated by ad hoc curl commands rather than anything that runs in CI.

Beyond the 4-hour horizon, a longer backlog:

- **Real authentication, with 2FA.** Replace the self-asserted `residentId` with actual login
  (session or JWT-based) and two-factor auth on sign-in. This is also the prerequisite for
  multi-tenancy actually being a security boundary rather than a data-modeling sketch — see
  above.
- **Automated tests**, as noted above — unit tests on the store (especially the concurrency
  path) and integration tests on the endpoints, running in CI rather than verified ad hoc.
- **A resident-facing "my reservations" view** showing that resident's current, past, and
  cancelled bookings across all amenities, not just the per-amenity list the current screen
  shows.
- **Admin tooling to remove or ban residents** from creating new reservations, either
  temporarily (e.g. a suspension window) or permanently. Needs an admin role distinct from
  "resident" and an enforcement check at reservation-creation time (`resident.status == Banned`
  → reject before the amenity/capacity checks even run).
- **Admin-initiated cancellation of any resident's reservation**, with an email notification to
  the affected resident when it happens. This needs (a) an admin-only cancel path that skips the
  ownership check `CancelReservation` currently enforces, and (b) an outbound email/notification
  side effect — I'd fire that as an event rather than inline in the request handler, so a slow or
  failing mail provider can't block the cancellation itself.
- **Admin-approved sign-up**, via either a one-time invite link (admin generates a link scoped
  to a building/unit, resident redeems it once) or a request-then-approve flow (resident
  submits a sign-up request that sits `Pending` until an admin approves it). The invite-link
  approach is simpler to build and harder to abuse (no open sign-up surface to spam); the
  request-then-approve flow is more flexible (residents can self-serve initiate) but needs an
  admin queue/notification so requests don't sit unnoticed — I'd lean toward invite links first
  and add the request queue only if property managers actually want self-serve sign-up.
