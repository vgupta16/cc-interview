# What Next

Two AI passes were run against this branch: **`/verify`** drove the running app and API,
**`/code-review`** read the diff and then verified its own claims live. Overlapping findings are
merged below and tagged with who found them, keeping each pass's original numbering so they stay
traceable. Ranked by what a user actually hits.

One defect was found by both passes and is fixed. **Everything under "Open" is documented only —
none of it is fixed in the code.**

## With another 4 hours

Close the open findings below (~an hour), then the two things the time box forced me to skip:
1) An automated test suite that exercises the application from request to "db", including the concurrency check.
2) Real per-building time zones, so opening hours mean 9am *there* instead of 9am UTC. 
3) Per-resident booking quotas.

Both passes confirmed the crux holds. `/code-review` measured it: 30 concurrent `POST`s at a
`reservation_capacity: 1` slot → exactly 1×`201` / 29×`409`, and 30 concurrent size-2 `POST`s at
the gym (`total_capacity: 10`) → exactly 5×`201` / 25×`409`, exercising both capacity dimensions.
`/verify` reached the same conclusion from the running app. Tenancy scoping, 404-not-403 on
cross-building access, and ownership-on-cancel all behaved as documented.

---

## Fixed

**No per-resident cap within a slot** — `Program.cs` · *verify #1 · code-review*
Ada booked gym 19:00 three times at size 2 → `201`, `201`, `201`: 6 of 10 capacity to one person.
`MaxSizePerReservation` caps a *row*, not a *person*. Closed with
`inSlot.Any(r => r.ResidentId == resident.Id)` inside the existing lock — 20 concurrent identical
requests now give 1×`201` / 19×`409`.
Still open by design: no daily/weekly quota — deferred as a PM question, not an oversight.

Scope of the guard · *verify*: it covers `(amenity, slot)`, not the hour. One resident can hold
the Gym, the Party Room and the Pool Cabanas all at 19:00 — three `201`s, verified. That is
deliberate: the record carries no attendee, and a resident booking the party room for guests
while their partner uses the gym is ordinary, so restricting it is a policy call belonging with
the quota question. `RevisedDesign.md` originally justified the guard in terms of the *hour*,
which described a rule the code doesn't implement; it now states the `(amenity, start_utc)` scope
and why the cross-amenity case is left open.

---

## Open (documented, not fixed)

**1. Error banner is ~900px above the button you pressed** · *verify #2*
Book at Party Room 22:00 with size 25 → screen appears not to change. The `400` renders in a
banner at the top of a long slot list, off-screen. Reads as a dead button.
→ Inline per-row, or scroll-to / toast.

**2. Your own booking says "This slot is fully booked."** · *verify #3*
`20:00–21:00  This slot is fully booked.  [Cancel]`. `slotLabel` returns `unavailableReason`
without ever consulting `myReservationId` — the row's Book/Cancel branch checks ownership
independently, so the two are computed from different inputs and were never reconciled. Reachable
on any exclusive amenity: book Party Room, and the slot you now hold reports itself full.
**My regression** — introduced when I added `unavailableReason`.
→ Return "Booked by you" (or suppress the reason) when `myReservationId` is set.

**3. Stale responses can cross tenancy** · `App.tsx` · *code-review*
Neither fetch effect has a cleanup guard (`grep -c "return () =>"` → 0). Switch resident A→B
while A's request is in flight; if A resolves last, the UI shows **building A's amenities to a
building B resident** — the leak the design says is impossible — and `setAmenitiesFor` captures
the old `residentId`, so `loadSlots` then gates itself off permanently and the list stays empty
with no error. The same gap in `loadSlots` lets a stale `getAvailability` overwrite `slots`,
after which Book sends a `startUtc` the user never saw.
→ `ignore` flag or `AbortController` in each effect's cleanup.

**4. Party size isn't validated client-side** · *verify #4 · code-review*
Three symptoms, one cause — the client never checks `size` against anything:
- accepts `25` where the header says max 20. *Correction to the finding as filed:* `min={1}` and
  `max={maxSize}` **are** present (`App.tsx:197-198`); HTML number inputs only enforce range on
  form validation, and there is no form — so the behaviour is real, the stated cause isn't;
- `Number('') === 0` when the field is cleared, and the clamp effect depends only on `[maxSize]`,
  so `0` is never corrected;
- Book stays enabled when `size > remainingSize`, because the server computes `IsAvailable` for a
  party of **one** (`Capacity.Reject(amenity, inSlot, 1)`) and the button never reads `size`.

The server rejects all three correctly, so these are preventable round trips rather than
correctness holes. `RevisedDesign.md` used to claim the UI stops a too-large party before the
request is sent; it now says the row has the data and the button doesn't use it, so the doc and
the code agree that this is open.
→ Clamp in `onChange`; add `size > remainingSize` to the button's `disabled`.

**5. Bad query params return raw ASP.NET text, and the client chokes on it** · *verify #6 ·
code-review*
`GET /api/amenities` with no `residentId`, or `date=` from a cleared date input (orval omits only
`undefined`), returns a plain-text `BadHttpRequestException` plus stack frames — not
ProblemDetails. The generated client `JSON.parse`s every non-2xx body, so the user sees
`Unexpected token 'M', "Microsoft."... is not valid JSON`. Every error path the handlers *do* own
returns clean Problem JSON, which is why this sticks out.
→ `date: date || undefined`; stop assuming non-2xx bodies are JSON.

**6. No booking horizon** · *verify #5* — `2027-01-01T10:00Z` → `201`. Nothing stops a resident
reserving next year.

**7. `401` is undeclared, defeating the typed-error change** · `Program.cs` · *code-review*
`createReservation` and `getAvailability` both return `401` for an unknown resident but declare
only `201/400/404/409` and `200/404`. Confirmed against `openapi.json`. So `401` never reaches
the generated discriminated union and the compiler can't force it to be handled — the entire
reason `includeHttpResponseReturnType` was flipped to `true`. (`getAmenities` does declare it.)
→ `.ProducesProblem(StatusCodes.Status401Unauthorized)` on both, regenerate.

**8. `getResidents` skips its status check** · `App.tsx:63` · *code-review*
The only call site destructuring `({ data })` without checking `res.status`. A non-200 JSON body
lands in `residents` and the next `residents.find(...)` throws, blanking the app. Masked only
because the endpoint declares no error responses — see 7.

**9. Closed-amenity message is unreachable in practice** · `Capacity.cs:30` · *code-review*
`!amenity.IsOpen` is the first check *within* `Capacity.Reject`, but `Reject` runs inside the
lock, after the slot-shape validations — so a closed amenity reports "Reservations start on the
hour." before it ever says it is closed. Latent: no seeded amenity sets `IsOpen = false`. Also, a
permanent admin close returning `409` contradicts the doc's own "409 means retrying may succeed."

**10. Everything is UTC end to end** · *verify #7* — consistent and honestly labelled
(`Date (UTC)`, `Open 05:00–23:00 UTC`), but residents read hours that aren't theirs. Deferred
deliberately; see `RevisedDesign.md`.

**11. Garbled comment at `Program.cs:78`** · *verify #8* — "Why not, in the words of the same
helper the create path enforces with". Mine; one line.
