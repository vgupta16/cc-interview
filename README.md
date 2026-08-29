# Amenity Reservations — Take-Home Exercise

A small, runnable starter. Your job is to **design and build a thin slice** of an amenity
reservation feature. This is a **design + AI-usage** exercise, time-boxed to **1–2 hours** —
we do **not** expect you to finish everything. See [`TASK.md`](./TASK.md) for the full brief and
what we grade.

The plumbing is already wired so you can spend your time on design and the feature, not setup:

- **Backend** — .NET 9 minimal API, in-memory store, OpenAPI on. Amenities endpoint works.
- **Frontend** — React + TypeScript + Vite, calling the API through an **orval-generated typed
  client**. One screen lists amenities.
- **Reservations is your task** — backend endpoints and the UI for it are stubbed with `TODO(candidate)`.

## Prerequisites

- [.NET SDK 9.0+](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org) and a package manager (`pnpm` recommended; `npm` works too)

## Run it (two terminals)

**1) Backend** → http://localhost:5080 (OpenAPI doc at `/openapi/v1.json`)

```bash
cd backend/Api
dotnet run
```

**2) Frontend** → http://localhost:5173

```bash
cd frontend
pnpm install      # or: npm install
pnpm dev          # or: npm run dev
```

Open http://localhost:5173 — you should see the seeded amenities. The Vite dev server proxies
`/api/*` to the backend, so there's no CORS to deal with.

## Regenerating the API client

The typed client lives in `frontend/src/api/generated/api.ts` and is generated from an OpenAPI
spec. A committed snapshot (`frontend/openapi.json`) is used by default so it works on a fresh clone.

```bash
cd frontend
pnpm gen:api      # regenerates the client from openapi.json
```

Once you add backend endpoints, regenerate from the **live** backend to pick them up: start the
API, then change `input` in `frontend/orval.config.ts` to
`http://localhost:5080/openapi/v1.json` and rerun `pnpm gen:api`.

## Where to work

| You're doing... | Look at |
| --- | --- |
| Adding reservation endpoints | `backend/Api/Program.cs` (see the `TODO(candidate)` block) |
| Domain shape | `backend/Api/Domain/Reservation.cs`, `Amenity.cs` |
| Persistence | `backend/Api/Store/InMemoryStore.cs` |
| Building the UI | `frontend/src/App.tsx` |
| The typed client | `frontend/src/api/generated/api.ts` (generated) |

## What I changed in the scaffold

See [`RevisedDesign.md`](./RevisedDesign.md) for the reasoning — [`Design.md`](./Design.md) is
the original first pass, kept unchanged. In short:

- `Amenity` gained `BuildingId`, three capacity columns (`TotalCapacity`, `ReservationCapacity`,
  `MaxSizePerReservation`), opening/closing hours and `IsOpen`; `MaxBookingMinutes` was removed
  because every booking is exactly one hour.
- Added `Resident`, `Capacity` (the one place a booking is allowed or denied) and `Contracts.cs`.
- `InMemoryStore` seeds two buildings and exposes a `SyncRoot` lock; every read-check-write on
  reservations happens under it.
- **`GET /api/amenities` now requires `residentId`** and is scoped to that resident's building.
- `orval.config.ts` sets `includeHttpResponseReturnType: true`, so the generated client returns
  `{ status, data }`. Booking needs the status: a `409` and a `201` are both JSON objects.

### Note on `pnpm`

On pnpm 11 the pre-script dependency check exits non-zero over unapproved build scripts, which
breaks `pnpm dev` and `pnpm gen:api`. Either run `pnpm approve-builds` once, or use `npm`
(`npm install && npm run dev`), or invoke the tools directly:

```bash
node node_modules/vite/bin/vite.js                          # dev server
node node_modules/orval/dist/bin/orval.js --config ./orval.config.ts   # regenerate client
```

## Submitting

When you're done, **push your work to a branch and open a pull request against this repository.**
Include in the PR:

1. **A short design doc** (1–3 pages, Markdown is fine, committed in the repo) — see `TASK.md` for
   what to cover.
2. **2–3 sentences** in the PR description on what you'd do next with another 4 hours.
