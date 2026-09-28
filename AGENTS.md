# AGENTS.md

Instructions for AI coding agents working in this repo. How the system works today is in
[ARCHITECTURE.md](ARCHITECTURE.md); the design decisions behind it, including what was deferred,
are in [DESIGN.md](DESIGN.md); the exercise brief and deliverables are in [TASK.md](TASK.md).

## Context

This repo implements a deliberately narrow amenity-reservation slice for a take-home exercise: a
resident picks a sample identity, sees availability for an amenity in their building, reserves a
window, and sees their own reservations. Capacity and building isolation are the two core
guarantees; [DESIGN.md](DESIGN.md) explains them ([Overview & Scope](DESIGN.md#overview--scope)).

Preserve [DESIGN.md](DESIGN.md)'s product assumptions ([A1–A19](DESIGN.md#assumptions--pm-questions)) and deferred scope unless the user has
authorized changing them. If a fix needs a different assumption, more scope, or a new dependency,
explain the need and the trade-off before proceeding. Make ordinary implementation choices
yourself, within the authorized scope.

## Core invariants

Don't break these. [DESIGN.md](DESIGN.md) explains why they hold, and
[ARCHITECTURE.md](ARCHITECTURE.md#where-correctness-lives) how the code enforces them.

- Never expose another building's amenities or availability, or another resident's reservation
  details.
- Never let reservations exceed an amenity's capacity, including under concurrent requests. Check
  capacity and insert only inside `InMemoryStore.TryCreate`'s lock.
- Derive the resident and building from `X-RESIDENT-ID`. Never trust a resident or building ID
  from a request body.
- Keep the check order 401, 404, 400, 409, so another building's amenity looks the same as an
  unknown one.
- In the UI, never show one resident's data under another. Changing residents clears the previous
  resident's data and resets the form at once, and a response that arrives after its inputs
  changed is dropped.

## Commands

| Task                                                                 | Run in        | Command                                                         |
| -------------------------------------------------------------------- | ------------- | --------------------------------------------------------------- |
| Run the backend on http://localhost:5080                             | `backend/Api` | `dotnet run`                                                    |
| Build the backend                                                    | `backend/Api` | `dotnet build`                                                  |
| Install frontend dependencies                                        | `frontend`    | `pnpm install`                                                  |
| Run the frontend on http://localhost:5173                            | `frontend`    | `pnpm dev`                                                      |
| Type-check and build the frontend                                    | `frontend`    | `pnpm build`                                                    |
| Regenerate the API client from `openapi.json`                        | `frontend`    | `pnpm gen:api`                                                  |
| Refresh `openapi.json` from the running backend                      | `frontend`    | `curl -s http://localhost:5080/openapi/v1.json -o openapi.json` |
| Check the API end to end (backend running)                           | repo root     | `node scripts/verify-api.mjs`                                   |
| Check the screen in headless Chrome (backend and `pnpm dev` running) | repo root     | `node scripts/verify-ui.mjs`                                    |

- `frontend/pnpm-workspace.yaml` lets `pnpm install` succeed on every pnpm version tested: 9.15,
  10.0, 10.34, 11.28, and 12.5. Without its `allowBuilds` entries (`esbuild: true`,
  `'@scarf/scarf': false`), 11.28 and 12.5 fail with `ERR_PNPM_IGNORED_BUILDS`; without its
  `packages: ['.']` entry, 9.15 and 10.0 reject the file ("packages field missing or empty").
  Keep both.
- Stop servers with Ctrl+C. Ctrl+Z suspends them, and a suspended server keeps its port without
  answering requests.
- Reach the frontend at `localhost`, not `127.0.0.1`. Vite may listen on IPv6 only.
- Both verify scripts assume an empty reservation store, so run each against a freshly started
  backend; if one is already running, see [Working safely](#working-safely). They need Node 20+; `verify-ui.mjs`
  also needs Chrome, Chromium, or Edge (set `CHROME` if it isn't found) and saves screenshots when
  `SCREENSHOT_DIR` is set. They've been run on macOS only.

## Working safely

- Leave alone working-tree changes you didn't make and servers you didn't start. The user may have
  the backend and `pnpm dev` running, and restarting the backend wipes its reservations. Stop only
  the servers you started.
- If the user's servers are running, verify against your own on other ports. In `backend/Api`,
  run `dotnet run --urls http://localhost:5081`; in `frontend`, run
  `API_URL=http://localhost:5081 pnpm dev --port 5174 --strictPort` (`API_URL` sets the proxy
  target, which defaults to port 5080). Then run `node scripts/verify-api.mjs http://localhost:5081`
  and `UI_URL=http://localhost:5174 API_URL=http://localhost:5081 node scripts/verify-ui.mjs`,
  restarting your backend between the two.
- Refresh the `openapi.json` snapshot from your own backend, not the user's older one. The spec's
  `servers` URL is the port the backend listens on, so keep the committed value while fetching:
  `curl -s http://localhost:5081/openapi/v1.json | sed 's#localhost:5081#localhost:5080#' > openapi.json`
  (run in `frontend`).

Match the checks to the change. Run everything under [Before calling a change done](#before-calling-a-change-done) before
submission, or when the user asks.

| Change            | Checks                                                                              |
| ----------------- | ----------------------------------------------------------------------------------- |
| Docs only         | Each claim against the code, and that links and commands work                       |
| Frontend behavior | `pnpm build` and `verify-ui.mjs`                                                    |
| Backend behavior  | `dotnet build` and `verify-api.mjs`; `verify-ui.mjs` too if a screen's flow changes |
| API contract      | The endpoint steps below, then both builds and both verify scripts                  |

## Adding or changing an endpoint

1. Add it to `Program.cs` under a `// --- Section ---` banner, with `.WithName("camelCaseName")`
   and `.WithTags(...)`.
2. Resolve the caller first with `RequestRules.FindRequestingResident` and scope every store call
   to `resident.BuildingId`. Return the shared `UnknownResident()`, `AmenityNotFound()`, and
   `ReservationConflict()` responses, and keep the check order 401, 404, 400, 409, so another
   building's data can't be detected.
3. Declare every response. Use a `Results<...>` return type with `TypedResults` for success, and
   `.ProducesProblem(status)` for each `ProblemHttpResult` status, because a `ProblemHttpResult`
   doesn't put its status in the spec. `ValidationProblem` declares its own 400. Otherwise the client
   is typed `void` or lacks the error types.
4. Run `dotnet build`, start a fresh backend, and `curl` the endpoint, including its failure cases.
   Add the cases to `scripts/verify-api.mjs` and run it against a freshly started backend.
5. Refresh `openapi.json` from the backend you just built and tested (your own on 5081 if the
   user's is running; see [Working safely](#working-safely)), then run `pnpm gen:api`.
6. Review the `api.ts` diff. Expect only your changes.
7. Call the new function from the UI with `asResident(residentId)` as its request options, branch
   on `response.status`, then run `pnpm build`.

Refreshing the committed snapshot keeps `pnpm gen:api` working on a fresh clone without the
backend running. The [README](README.md) suggests pointing orval's `input` at the live URL instead, which
loses that.

## Conventions

Backend:

- Records with `required` / `init` properties and `/// <summary>` doc comments. Records aren't
  `sealed`; classes are.
- File-scoped namespaces that match folders (`Api.Contracts`, `Api.Domain`, `Api.Store`).
- All endpoints live in `Program.cs`, grouped under `// --- Section ---` banners, with the shared
  ProblemDetails responses at the bottom of the file.
- API routes start with `/api/`, because only `/api/*` and `/health` are proxied.
- `InMemoryStore` owns scoping and locking. Every amenity and reservation method is scoped to a
  building (`TryCreate` checks that the resident's and amenity's buildings match), its
  collections are private, and `TryCreate` is the only way to insert a reservation.
- Request timestamps are strings that `RequestRules` parses. Don't bind `DateTimeOffset` from a
  request (see [Things that will catch you out](#things-that-will-catch-you-out)).
- Private instance fields are `_camelCase`.
- Trailing commas in multi-line initializers, collection expressions, and `switch` expressions.

Frontend:

- Named function components (`export function App()`) and function declarations for helpers.
  Match the existing hooks and inline `style` objects, and don't add state-management,
  data-fetching, or styling libraries without a demonstrated need.
- Import from the generated client (`import { fn, type T } from './api/generated/api'`). Never
  call `fetch` directly from `src/`.
- Identify the resident with `asResident(residentId)` from `residents.ts`, passed as the generated
  function's request options.
- The picker includes C1 in Building C (no amenities) and an intentionally unknown resident ID
  to demonstrate empty and error states. Keep the unknown ID out of the backend seed data;
  every seeded resident must reference an existing building.
- Branch on `response.status`. Show anything else, and any rejection, as an error where it
  applies: in the failing list's section, beside the Reserve action for a submission, or once
  under the resident picker when both lists fail with the same message. A list's error waits until
  both lists finish loading, so a shared error never shows in a section first.
- Each read keeps its own error. Form actions clear only submission feedback: an amenity or
  reservation list's error clears only when that read succeeds or the resident changes, and a
  failed list never shows its empty-state text.
- Reads run in effects with an ignore flag, so a response that arrives after its inputs changed is
  dropped. Writes run only from event handlers and are never retried automatically.
- `strict`, `noUnusedLocals` and `noUnusedParameters` are on, so unused code fails `pnpm build`.
- `<StrictMode>` runs effects twice in development, so never send a write request from an effect.

## Things that will catch you out

1. **The generated client never throws on HTTP errors.** Each function resolves with
   `{ data, status, headers }`, and `data` is typed by status only for declared statuses. An
   undeclared status, such as a 500, is still cast to one of them, an empty body becomes `{}`, and
   a non-JSON body makes the function reject.
2. **`X-RESIDENT-ID` must be a plain object in the request options.** The generated `POST`
   spreads `options.headers` into its own headers, so a `Headers` instance is dropped without an
   error; use a plain object everywhere. The header isn't in the OpenAPI spec, so no generated
   function has a parameter for it.
3. **Binding `DateTimeOffset` reads a timestamp without an offset as server-local time.** That's
   why request timestamps are strings. In a query string, a `+` must be sent as `%2B`, or it
   arrives as a space; the generated client encodes it, but hand-written `curl` requests must.
4. **`required` doesn't reject `null`.** Validate new request fields explicitly, as
   `RequestRules` does for `startUtc`.
5. **Enums serialize as integers** and reach TypeScript as `number`, without their names. The API
   has no enums today; `CreateOutcome` stays inside the backend.
6. **Capacity must be checked and the reservation inserted inside `InMemoryStore.TryCreate`'s
   lock.** A check anywhere else can pass for two requests at once and double-book.
7. **`src/api/generated/` is deleted on every regeneration.** Never edit or add files there.
8. **Binding runs before endpoint code, so not every 400 is the application's.** Malformed JSON, a
   wrong type in the body or query (such as `durationMinutes=abc`), or a missing `required` body
   field fails before `FindRequestingResident` or the shared ProblemDetails helpers run, so it
   returns 400 even when `X-RESIDENT-ID` is missing. In Development, `AddProblemDetails()` in
   `Program.cs` makes that body ProblemDetails JSON (in Production it's empty), but not the
   application's shape: the title is the exception type and there's no `errors` map. Keep that
   line, and don't add `UseExceptionHandler()`, which turns these 400s into 500s. Without
   `AddProblemDetails()`, the Development body is a `text/plain` stack trace that makes the
   generated client reject instead of resolving.
9. **Every ProblemDetails body carries a random per-request `traceId`.** Two otherwise identical
   errors never match byte for byte, so compare bodies without it, as the cross-building `404`
   check in `verify-api.mjs` does.

## Constraints

- Keep persistence in memory. Don't add a database.
- Keep `frontend/openapi.json` committed and in step with the backend.
- Note every change to the scaffold so it can be listed in the PR.
- Don't commit `node_modules/`, `bin/`, `obj/`, or `dist/`. Commit `pnpm-lock.yaml` changes only
  for an intentional dependency change, not ones an install made on its own.

## Before calling a change done

Run the checks that match the change (see [Working safely](#working-safely)), and all of them before submission.
Report which checks you ran and which you skipped, and why.

- [ ] `dotnet build` and `pnpm build` pass.
- [ ] `node scripts/verify-api.mjs` passes against a freshly started backend, and `curl` shows the
      expected status and body for any new cases.
- [ ] The `api.ts` diff contains only the expected changes.
- [ ] `node scripts/verify-ui.mjs` passes, and the UI flow works when clicked through at
      http://localhost:5173.
- [ ] `git status` shows no stray files.
- [ ] [ARCHITECTURE.md](ARCHITECTURE.md), [DESIGN.md](DESIGN.md), and this file still match the code.
