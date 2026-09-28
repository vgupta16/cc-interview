# Design: Amenity Reservations

## Overview & Scope

This slice lets a resident see an amenity's availability, reserve an open window, and view their own reservations, on one React screen using the generated client. It rests on two guarantees:

1. **Capacity:** an amenity never holds more simultaneous reservations than its capacity, even when two residents reserve the last spot at once.
2. **Isolation:** residents reach only their own building's amenities and availability, and only their own reservation details; others' reservations appear only as occupied capacity.

The slice enforces maximum booking length and adds a 90-day advance booking limit ([A15](#a15)). Cancellation, one active reservation per resident, opening hours, and building-local time zones are [deferred](#deliberate-omissions--next-steps). Implementation details are covered in [ARCHITECTURE.md](ARCHITECTURE.md).

## Assumptions & PM Questions

*Italics* are PM questions; → shows what a different answer changes.

- <a id="a1"></a><a id="a2"></a><a id="a3"></a>**A1–A3:** Residents and amenities each belong to one building, and residents reserve only in their own. *Can either span buildings?* → Requests name a building the server verifies, and amenity scoping becomes many-to-many.
- <a id="a4"></a>**A4:** Residents may use all of their building's amenities. *Are any restricted?* → Every read and reservation checks eligibility.
- <a id="a5"></a>**A5:** Residents see their own reservations in full and others' only as occupied capacity. *What may they see of others'?* → Showing identities changes the isolation guarantee.
- <a id="a6"></a>**A6:** Reservations belong to residents, not units. *Resident or unit?* → Ownership and "My Reservations" key on the unit.
- <a id="a7"></a>**A7:** A trusted `X-RESIDENT-ID` header stands in for a bearer token.
- <a id="a8"></a>**A8:** Reservations start on a UTC half hour and span whole 30-minute slots, up to the amenity's `MaxBookingMinutes`. *Any start and end?* → The check must find peak overlap instead of counting slots.
- <a id="a9"></a>**A9:** Each reservation takes one spot, whatever its party size. *Does capacity count people?* → The check sums party sizes.
- <a id="a10"></a>**A10:** Reservations are confirmed at once, without approval, deposit, or payment. *Do any need these?* → A pending status, and a rule for whether it holds a spot.
- <a id="a11"></a>**A11:** A reservation may start exactly when another ends.
- <a id="a12"></a>**A12:** `MaxBookingMinutes` applies per reservation.
- <a id="a13"></a>**A13:** Residents may hold overlapping reservations, even for the same amenity, within capacity. *Should they?* → Creation also checks the resident's own reservations inside the lock.
- <a id="a14"></a>**A14:** Times are entered and shown in UTC. *Which time zone defines slots?* → Buildings need time zones and daylight saving conversions.
- <a id="a15"></a>**A15:** A reservation starts in the future and at most 90 days ahead, to limit how far ahead residents can claim capacity; it may end past the limit. No minimum notice applies. *Should the limit vary by amenity?* → It becomes an amenity field.
- <a id="a16"></a>**A16:** Reservations may cross midnight. *May they?* → Creation rejects them, and availability stops at the end of the day.
- <a id="a17"></a>**A17:** Availability returns the requested window plus the next four half-hour starts at the same duration, including full ones; starts past the 90-day limit are omitted. *Browse a whole day or week?* → A grid or per-day summary.
- <a id="a18"></a>**A18:** A window's remaining capacity is the minimum across its slots; it's reservable only if every slot has a spot.
- <a id="a19"></a>**A19:** "My Reservations" lists the resident's own upcoming and in-progress reservations for every amenity in their building.

## Data Model

A singleton in-memory store holds immutable seeded buildings, residents, and amenities; reservations are lost on restart. Building A keeps the scaffold's amenity IDs, B has its own, and C has a resident but no amenities, for the empty state. IDs are `Guid`s, and times are UTC `DateTimeOffset`s.

- **Building** (`Id`, `Name`): the tenant; has many residents and amenities. New.
- **Resident** (`Id`, `BuildingId`): belongs to one building and owns many reservations. New.
- **Amenity** (`Id`, `BuildingId`, `Name`, `Description?`, `Capacity`, `MaxBookingMinutes`): belongs to one building. Adds `BuildingId`, and `Capacity` and `MaxBookingMinutes` no longer have defaults.
- **Reservation** (`Id`, `BuildingId`, `AmenityId`, `ResidentId`, `StartUtc`, `EndUtc`): one resident's window on one amenity in their building. Adds `BuildingId`, and `ResidentId` changes from `string` to `Guid`.

Each reservation stores one half-open window, `[StartUtc, EndUtc)`, not per-slot records. Remaining capacity is calculated, never stored, and there's no status field: every reservation is confirmed ([A10](#a10)), and its times show whether it's upcoming, in progress, or ended.

**Constraints**

- IDs are unique per entity, every reference exists, and a reservation's building matches its resident's and its amenity's.
- The server generates reservation IDs, takes the resident and building from the requester (never the body), and calculates the end.
- `Capacity` is positive, and `MaxBookingMinutes` is at least 30.
- New windows follow [A8](#a8) and [A15](#a15): the start falls in `(now, now + 90 days]` and has no seconds or fractional seconds.
- No slot holds more reservations than capacity, and checking and inserting is one atomic step.

Request timestamps are strings that must include `Z` or `+00:00`, so their meaning never depends on the server's time zone.

## API Surface

The four endpoints below require `X-RESIDENT-ID`. JSON is camelCase, and empty lists are `[]`.

| Endpoint | Input | Success | Errors |
|---|---|---|---|
| `GET /api/amenities` | — | `200 Amenity[]` in the resident's building | `401` |
| `GET /api/amenities/{amenityId}/availability` | Query `startUtc`, `durationMinutes` | `200` up to five `{ startUtc, endUtc, remainingCapacity }`, by start | `400`, `401`, `404` |
| `POST /api/reservations` | `{ amenityId, startUtc, durationMinutes }` | `201 Reservation` | `400`, `401`, `404`, `409` |
| `GET /api/reservations/mine` | — | `200 Reservation[]` with `EndUtc > now`, by start, then ID | `401` |

Application errors are ProblemDetails: a `400` lists field messages in `errors`, and a `409` never identifies another resident's reservation. Availability applies creation's validation. Instead of the scaffold's suggested `GET /api/amenities/{amenityId}/reservations` (an amenity's individual bookings), availability returns only counts and "My Reservations" only the requester's ([A5](#a5)).

**UI.** A resident picker, standing in for login, sets `X-RESIDENT-ID` on every generated-client call. Reads discard responses that arrive after the resident or inputs change, and a failed list shows its error, never an empty state. The start is a date picker up to the 90-day limit plus a dropdown of UTC half-hour times, so only a valid boundary can be chosen. Submitting disables the form and never retries. A `201` confirms before refreshing availability and "My Reservations", so a refresh failure never looks like a failed reservation; a `409` keeps the selection and refreshes availability. Other failures show where they apply: in the failing list's section, by the Reserve action, or once under the resident picker when both lists fail the same way.

## Concurrency: Preventing Double-Booking

**Failure mode: a check-then-insert race.** Two requests both read the last free spot, and both insert, exceeding capacity. Locking only the insert doesn't help; the decision must use capacity that can't change before the reservation is saved.

**Capacity rule.** For each 30-minute slot `[start, end)` of the window, count the amenity's reservations where `StartUtc < end && EndUtc > start`, and accept only if every count is below `Capacity`. Strict comparisons allow back-to-back reservations. Counting everything that overlaps the whole window would be too strict: with capacity two, 10:00–10:30 and 10:30–11:00 still leave a spot free for all of 10:00–11:00.

**Chosen mechanism: one store-wide lock.** Lookups and validation run first, against immutable seed data. Inside the lock, creation rechecks that the start hasn't passed while it waited (the 90-day limit only moves later, so it needs no recheck), checks every slot, and inserts. A passed start returns `400` and a full slot `409`; neither inserts. No I/O or serialization happens inside the lock, and the reservation collection is private, so no endpoint can bypass it. Reads copy records under the lock, and each availability response is calculated outside it from one snapshot. Availability is advisory, not a hold: creation always rechecks.

- **Store-wide lock (chosen):** one rule for writes and snapshots, and the simplest fit. It costs throughput: capacity scans grow with reservation volume and delay unrelated buildings and amenities, and it protects one process only.
- **Per-amenity locks (rejected):** amenities proceed in parallel, but stable lock management and safe shared-collection access add complexity without demonstrated demand, and it still protects one process.
- **Database row lock per amenity (production option):** coordinates writers across servers in a transaction, but needs the persistence that the in-memory constraint excludes.

## Multitenancy & Isolation

For requests that pass framework routing and binding, a missing, malformed, or unknown `X-RESIDENT-ID` gets `401`. After identity resolution, the resident's building scopes the rest of the request, and the store enforces it for every amenity and reservation operation.

- **Lookups:** amenities are found by ID and building together, so another building's amenity gets the same `404` as an unknown one. `GET /api/amenities` (open in the scaffold) now returns only the resident's building.
- **Check order:** application checks run identity (`401`), amenity (`404`), validation (`400`), then capacity (`409`), so another building's amenity never reaches validation or capacity.
- **Ownership:** "My Reservations" returns only the requester's reservations, availability returns only counts, and no endpoint reads a reservation by ID.

## Deliberate Omissions & Next Steps

- **Real authentication:** the header permits impersonation; production needs an authenticated identity and verified building membership.
- **Canceling your own reservation:** deferred because its absence breaks neither guarantee. It's next: `DELETE /api/reservations/{id}` removes the requester's reservation under the lock and returns `204`; another resident's or an unknown ID gets the same `404`. Until then, accidental reservations can't be released.
- **One active reservation per resident:** must share the capacity check's atomic step, or two quick requests both succeed; neither guarantee needs it.
- **Opening hours and building time zones:** need new per-building data and affect neither guarantee.
- **Request deduplication:** a retried `POST` could create a second reservation, so the UI never retries writes.
- **Staff tools, recurring reservations, waitlists, and notifications:** outside the core problem.

## Verification

`scripts/verify-api.mjs` (85 HTTP checks) and `scripts/verify-ui.mjs` (45 headless-Chrome checks) cover identity on every endpoint, isolation and cross-building `404`s, JSON errors when binding fails, time, duration, and 90-day boundaries, adjacent and midnight-crossing reservations, the [capacity example](#concurrency-preventing-double-booking) above, ownership, 20 parallel creates for one spot (one `201`, nineteen `409`s), and the screen's reserve, conflict, resident-switching, and failure-recovery flows. Both run locally, not in CI, and don't replace a manual click-through.