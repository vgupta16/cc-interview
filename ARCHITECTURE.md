# Architecture

How this repo works **today**: the amenity-reservation slice built on the take-home scaffold. This
file describes current behavior only; the reasons behind it are in [DESIGN.md](DESIGN.md). For the
exercise brief see [TASK.md](TASK.md); for how to work in the repo see [AGENTS.md](AGENTS.md).
Update this file when the code changes.

## Repository layout

```text
.
├── README.md, TASK.md                  Setup instructions, exercise brief (from the scaffold)
├── DESIGN.md                           Design doc: guarantees, assumptions, model, concurrency, API
├── AGENTS.md                           Instructions for coding agents
├── nuget.config                        Restricts NuGet to nuget.org
├── scripts/
│   ├── verify-api.mjs                  85 HTTP checks against the API (Node 20+)
│   └── verify-ui.mjs                   45 checks of the screen in headless Chrome (Node 20+)
├── backend/Api/                        .NET 9 minimal API (single project)
│   ├── Program.cs                      Host setup, every endpoint, shared error responses
│   ├── Contracts/                      Request and response types; RequestRules (identity, validation)
│   ├── Domain/                         Building, Resident, Amenity, and Reservation records
│   ├── Store/                          InMemoryStore (seed data, lock, capacity rule); CreateResult
│   └── Properties/launchSettings.json  http://localhost:5080, Development environment
└── frontend/                           React + TypeScript + Vite
    ├── openapi.json                    Committed API spec snapshot (orval's input)
    ├── orval.config.ts                 Client generation config
    ├── pnpm-workspace.yaml             Build-script allowlist that pnpm 11.28 and 12.5 require
    ├── vite.config.ts                  Dev server on :5173, proxy to :5080 (or to API_URL if set)
    └── src/
        ├── main.tsx                    Entry point, wraps App in StrictMode
        ├── App.tsx                     The reservation screen
        ├── residents.ts                Sample resident fixtures and the X-RESIDENT-ID request option
        ├── time.ts                     UTC input and display helpers
        └── api/generated/api.ts        Generated client. Don't edit
```

## Stack

| Layer        | Technology                                                                                                                                   |
| ------------ | -------------------------------------------------------------------------------------------------------------------------------------------- |
| Backend      | .NET 9 (`net9.0`) minimal API on Kestrel. Nullable reference types and implicit usings on. One package: `Microsoft.AspNetCore.OpenApi` 9.0.0 |
| Frontend     | React 18.3, TypeScript 5.9 (`strict`), Vite 5.4                                                                                              |
| API client   | orval 7.21 generating a `fetch` client from the OpenAPI 3.0.1 spec, with `includeHttpResponseReturnType: true`                               |
| Packages     | NuGet (nuget.org only), pnpm (lockfile format 9.0)                                                                                           |
| Verification | Two Node scripts in `scripts/` with no dependencies                                                                                          |

Not present: database, real authentication, CORS, HTTPS, `appsettings.json`, exception-handling or
status-code-page middleware, router, data-fetching library, CSS framework, test framework or test
project, linter, CI.

## Request flow

```text
App.tsx ─▶ generated function (relative URL; X-RESIDENT-ID in the request options)
   ▲              │ fetch
   │              ▼
   │   Vite dev server :5173 ── proxies /api/* and /health ──▶ Kestrel :5080
   │                                                              │
   │             endpoint in Program.cs: resident (401) ─▶ amenity (404) ─▶ validation (400)
   │                                                              │
   └── { data, status } ◀── JSON or ProblemDetails ◀── InMemoryStore (building-scoped, one lock)
```

The proxy targets :5080 unless the `API_URL` environment variable names another backend, as for
verification against a separately started one. The browser only talks to :5173, so CORS never
comes up, and Vite forwards `X-RESIDENT-ID`
unchanged. Paths outside `/api/*` and `/health` never reach the backend: through Vite, a GET
returns the app's `index.html` (`200 text/html`) and a POST returns 404.

## Where correctness lives

Each guarantee, the code that enforces it, and the verify-script sections that check it.

| Guarantee                                                 | Enforced by                                                                                                                             | Checked by (script sections)                                                                                     |
| --------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| Capacity, even under concurrent requests                  | `InMemoryStore.TryCreate` holds the store lock across the capacity check and the insert; `RemainingCapacity` counts each 30-minute slot | verify-api "Capacity boundary" and "Concurrency"; verify-ui "Reserve the first Gym window twice" and "Conflict"  |
| Building isolation                                        | `RequestRules.FindRequestingResident`, then building-scoped store lookups; another building's amenity gets the unknown-amenity `404`    | verify-api "Identity", "Scoping", "Resident in a building with no amenities", and "Error order across buildings" |
| Ownership                                                 | `GetMyReservations` filters by building and resident; availability returns only counts                                                  | verify-api "Ownership"                                                                                           |
| Time rules and the 90-day limit                           | `RequestRules.ValidateWindow`, and the availability cutoff in `Program.cs`                                                              | verify-api "Timestamp rules", "Booking limit", and "Duration rules"; verify-ui "Booking limit"                   |
| The UI never shows one resident's data under another      | `App.tsx` clears the previous resident's data on a switch, and each read effect has an ignore flag                                      | verify-ui "Switching residents" and "Switching residents while requests are pending or failing"                  |
| A failed read stays visibly failed                        | Separate error state for each read in `App.tsx`                                                                                         | verify-ui "API returning 500" and "Only My Reservations fails"                                                   |
| Errors reach the UI as responses, not as network failures | `AddProblemDetails()` in `Program.cs` makes framework errors JSON in Development (empty in Production); `App.tsx` branches on status    | verify-api "Framework errors"                                                                                    |

## Domain model

All four records in `Api.Domain` are immutable, with `required` / `init` properties:

| Record        | Properties                                                                                                                                        |
| ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Building`    | `Id` (`Guid`), `Name`                                                                                                                             |
| `Resident`    | `Id` (`Guid`), `BuildingId`                                                                                                                       |
| `Amenity`     | `Id` (`Guid`), `BuildingId`, `Name`, `Description` (`string?`, the only optional one), `Capacity` (spots; 1 means exclusive), `MaxBookingMinutes` |
| `Reservation` | `Id`, `BuildingId`, `AmenityId`, `ResidentId` (all `Guid`), `StartUtc`, `EndUtc` (`DateTimeOffset`, UTC; the end is exclusive)                    |

There's no status field: every stored reservation is confirmed. Not modeled: reservation status
or cancellation, opening hours, the one-active-reservation rule, building time zones, units or
households, and resident names (the picker's labels are frontend fixtures).

Seed data in [InMemoryStore.cs](backend/Api/Store/InMemoryStore.cs) has fixed IDs and never
changes at runtime. When the store is first created, on the first request that uses it, its
constructor checks that every resident and amenity references a seeded building.

| Building                  | ID                                     | Residents                        |
| ------------------------- | -------------------------------------- | -------------------------------- |
| Building A                | `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa` | A1 `a1a1a1a1-…`, A2 `a2a2a2a2-…` |
| Building B                | `bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb` | B1 `b1b1b1b1-…`, B2 `b2b2b2b2-…` |
| Building C (no amenities) | `cccccccc-cccc-cccc-cccc-cccccccccccc` | C1 `c1c1c1c1-…`                  |

The picker's "Unknown resident (not registered)" choice, `d1d1d1d1-…`, is deliberately absent from
the seed data, so it gets `401` and demonstrates the page-wide error message.

| Amenity ID                             | Name                      | Building | Capacity | MaxBookingMinutes |
| -------------------------------------- | ------------------------- | -------- | -------- | ----------------- |
| `11111111-1111-1111-1111-111111111111` | Party Room, Building A    | A        | 1        | 240               |
| `22222222-2222-2222-2222-222222222222` | Gym, Building A           | A        | 2        | 90                |
| `33333333-3333-3333-3333-333333333333` | Guest Parking, Building A | A        | 1        | 1440              |
| `44444444-4444-4444-4444-444444444444` | Party Room, Building B    | B        | 1        | 240               |
| `55555555-5555-5555-5555-555555555555` | Guest Parking, Building B | B        | 1        | 1440              |

## API

Every `/api/*` endpoint requires `X-RESIDENT-ID`. Every generated function takes optional
`RequestInit` options last and returns `{ data, status, headers }`.

| Method and path                                                               | Generated TS function                                 | Success                                                                              | Errors             |
| ----------------------------------------------------------------------------- | ----------------------------------------------------- | ------------------------------------------------------------------------------------ | ------------------ |
| `GET /health`                                                                 | `getHealth(options?)`                                 | `200 {"status":"ok"}`, typed `void` because the body isn't in the spec               | none               |
| `GET /api/amenities`                                                          | `getAmenities(options?)`                              | `200` `Amenity[]` for the resident's building                                        | 401                |
| `GET /api/amenities/{amenityId:guid}/availability?startUtc=&durationMinutes=` | `getAmenityAvailability(amenityId, params, options?)` | `200` up to five `AvailabilityWindow`s: `{ startUtc, endUtc, remainingCapacity }`    | 400, 401, 404      |
| `POST /api/reservations`                                                      | `createReservation(body, options?)`                   | `201` the created `Reservation`, with no `Location` header                           | 400, 401, 404, 409 |
| `GET /api/reservations/mine`                                                  | `getMyReservations(options?)`                         | `200` the resident's `Reservation[]` with `EndUtc` after now, by start time, then ID | 401                |
| `GET /openapi/v1.json`                                                        | none                                                  | The live OpenAPI 3.0.1 document, always served                                       | none               |

The create body is `CreateReservationRequest`: `{ amenityId, startUtc, durationMinutes }`. The
server calculates `EndUtc`, and takes the resident and building from the header; unknown body
fields, including a `residentId`, are ignored.

### Identity and scoping

- `RequestRules.FindRequestingResident` reads `X-RESIDENT-ID` as a raw header, parses it as a
  `Guid`, and looks up the resident. A missing, malformed, or unknown value returns `401`. The
  header isn't a bound parameter, so a bad value gets the application's 401 rather than the
  framework's 400, and it isn't in the OpenAPI spec.
- The resident's `BuildingId` scopes the rest of the request. Amenity and reservation lookups take
  the building ID, and an amenity is found by ID and building together, so another building's
  amenity returns the same `404` body as an unknown one, apart from its per-request `traceId`.
- For requests that pass routing and binding, checks run in this order: resident (401), amenity
  (404), validation (400), capacity (409).
- The server trusts the header, so any caller can act as any resident. It stands in for a bearer
  token.

### Validation and time

- Request timestamps are strings. `RequestRules` accepts only ISO 8601 with a `Z` or `+00:00`
  suffix; seconds and fractional seconds are optional but must be zero. A timestamp without an
  offset, or with another offset, returns `400`.
- The start must fall on a 30-minute UTC boundary, be in the future, and be no more than
  `RequestRules.MaxDaysAhead` (90) days ahead. The duration must be a positive multiple of 30, up
  to the amenity's `MaxBookingMinutes`. In availability, a missing `startUtc` or `durationMinutes`
  also returns `400` with an `errors` map, and so does a `null` `startUtc` in a create body. A
  create body that omits either field, or sends a `null` duration, fails binding first: `400`
  without an `errors` map (see [Error handling](#error-handling)).
- Availability stops at the first later window that starts past the 90-day limit, measured with
  the same clock reading as validation, so it excludes every window the 90-day check would reject.
  Creation can still fail with `409` if a window fills, or `400` if its start passes.
  The limit also keeps every window far from `DateTimeOffset.MaxValue`, so date arithmetic can't
  overflow. The frontend copies the limit into the start date picker's `max`; the API enforces it.
- Times are stored in UTC and returned with `+00:00`. Windows include their start and exclude
  their end, `[StartUtc, EndUtc)`, and a reservation may cross midnight.
- In a query string, `+00:00` must be sent as `%2B00:00`. The generated client encodes it through
  `URLSearchParams`.

### Capacity and concurrency

- `InMemoryStore.RemainingCapacity` counts, for each 30-minute slot of a window, the amenity's
  reservations that overlap it, and returns `Capacity` minus the highest count. A window can be
  reserved when that's at least 1. Availability and creation share this one method.
- Availability returns the selected window and up to four more, 30 minutes apart with the same
  duration, calculated from one snapshot copied under the lock. Full windows stay in the response
  with `remainingCapacity: 0`. Availability doesn't hold a spot.
- Creation validates before taking the lock. `TryCreate` then holds one store-wide
  `System.Threading.Lock` while it rechecks that the start is still in the future, checks every
  slot, and inserts. It returns a `CreateResult` of `Created`, `Full`, or `StartPassed`, which the
  endpoint maps to `201`, `409`, and `400`.
- Reads, such as availability and "My Reservations", copy what they need under the same lock and
  work on the copy.

### JSON responses

Endpoint responses are JSON whatever the `Accept` header asks for (System.Text.Json web defaults):
camelCase, with `DateTimeOffset` written as ISO 8601 with an offset, for example
`2026-09-27T10:00:00+00:00`. For binding gotchas such as `null` passing `required`, see
[AGENTS.md](AGENTS.md), [Things that will catch you out](AGENTS.md#things-that-will-catch-you-out).

## Error handling

Application errors (400, 401, 404, 409; see [API](#api)) are ProblemDetails,
`application/problem+json`. Framework failures come before endpoint code: a malformed
amenity ID in the availability path gets an empty 404 from the route constraint, a binding failure
(malformed JSON, a wrong type in the body or query, or a missing `required` body field) gets a
400, and an unhandled exception gets a 500. In Development, because `Program.cs` registers
`AddProblemDetails()`, those 400 and 500 bodies are ProblemDetails JSON from the developer exception
page, for `Accept: */*` (what `fetch` sends) or `application/json`; a request that prefers
`text/html`, such as a browser tab, gets an HTML page. Their title is the exception type, there's
no `errors` map, and an `exception` extension carries the stack trace and request headers. In
Production they're empty. The generated client resolves both, so neither is mistaken for a network
failure. `UseExceptionHandler()` isn't used, because it turns the binding 400s into 500s. Every
ProblemDetails body, the application's included, carries a random per-request `traceId`.

The generated client doesn't throw on HTTP error statuses. It resolves `{ data, status, headers }`
whenever it can parse the body, and an empty body, such as Vite's 500 when the backend is down,
becomes `data: {}`. A non-JSON body or a network failure makes it reject. The details are in
[AGENTS.md](AGENTS.md), [Things that will catch you out](AGENTS.md#things-that-will-catch-you-out), #1.

`App.tsx` branches on `status`. A `201` shows a confirmation, then refreshes availability and the
reservation list; a `409` shows the conflict as a notice and refreshes availability; anything else
becomes an error message (`role="alert"`) through `describeProblem`, which uses the validation
errors, the ProblemDetails detail or title, or a generic message that suggests checking the
backend for a 5xx. A rejection shows "Couldn't reach the API"; with every backend error body JSON or
empty, that now means a network failure. Each message appears where it applies: a list's read
error in that list's section, and the reservation notice, submission error, and availability
error in the availability section. When both lists fail with the same message, such as an
unknown resident or an unreachable API, it shows once under the resident picker instead. Neither
list's error shows until both lists finish loading, so a shared error goes straight to the banner;
until then, a list that already failed keeps its loading text. When the
backend is down, the page stays usable, and lists that failed to load don't show their
empty-state text. Read errors are tracked separately from submission errors. Changing amenity,
start, or duration clears submission feedback, but preserves errors from the amenity and
reservation lists until their read succeeds or the resident changes.

## Frontend screen

- `App.tsx` is the only screen. Its state covers the resident, the amenities, "My Reservations",
  the selected amenity, start, and duration, the availability windows, a submitting flag, a notice, and
  separate errors for each read and the submission.
- Three effects read data: amenities per resident; "My Reservations" per resident and refresh
  count; availability per resident, amenity, start, duration, and refresh count. Each uses an
  ignore flag, so a response that arrives after its inputs changed is dropped. Changing the
  resident also clears the amenities, reservations, and windows at once, so the previous
  resident's data never shows while the new requests load or after they fail. Loading flags
  change with the resident so pending requests show loading text immediately.
- The only write, `createReservation`, runs from the Reserve button's click handler and is never
  retried. While it's pending, the picker, inputs, and buttons are disabled.
- The resident picker lists `SAMPLE_RESIDENTS` from `residents.ts`: the five seeded residents plus
  the unknown ID described under [Domain model](#domain-model). C1 shows the empty states.
  `asResident()` builds the `X-RESIDENT-ID` request option as a plain object.
- The start is a date picker (today to the last date within 90 days, shown in the browser's date
  format) plus a dropdown of the 48 half-hour times, so only a valid boundary can be chosen.
  The date and time are kept as separate fields and combined into a UTC timestamp only for the
  request (`startUtcIso`), so clearing the date never disturbs the time. `formatRange` shows UTC
  ranges, with both dates when a window crosses midnight. Durations run from 30 minutes
  to the amenity's maximum in 30-minute steps. The start defaults to the next half hour at least
  5 minutes away, and the duration to 30 minutes, the shortest any amenity allows. Selecting a
  different amenity or resident resets both to those defaults, so one resident's choices never
  carry over to another.

## Persistence

`InMemoryStore` is registered with `AddSingleton`. Its seed lists are private `IReadOnlyList<T>`s
and its reservations a private `List<Reservation>`, guarded as described under
[Capacity and concurrency](#capacity-and-concurrency). Reservation IDs come from `Guid.NewGuid()`,
and everything is lost on restart.

## Generated API client

```text
Program.cs endpoint metadata ─▶ GET /openapi/v1.json ─▶ frontend/openapi.json (committed)
                             ─▶ pnpm gen:api (orval) ─▶ frontend/src/api/generated/api.ts
```

- `.WithName("getAmenities")` sets the operationId, which becomes the TS function name.
  `.WithTags()` groups endpoints.
- A success type reaches the spec through a `TypedResults` / `Results<...>` return type. A
  `ProblemHttpResult` doesn't carry its status into the spec, so each error status is declared
  with `.ProducesProblem(status)`; `ValidationProblem` declares its own 400.
- `includeHttpResponseReturnType: true` makes each function return `{ data, status, headers }`,
  typed as a union over the declared statuses. Undeclared statuses aren't covered.
- `X-RESIDENT-ID` isn't in the spec, so the functions have no header parameter; callers pass it in
  the request options. The generated `POST` merges `...options?.headers` after its own
  `Content-Type`, and query parameters are built with `URLSearchParams`.
- `clean: true` deletes `src/api/generated/` before each run. URLs stay relative, because the
  spec's `servers` entry isn't used.
