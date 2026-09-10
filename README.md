# Amenity Reservations — Take-Home Exercise

A small, runnable starter. Your job is to **design and build a thin slice** of an amenity
reservation feature. This is a **design + AI-usage** exercise, time-boxed to **1–2 hours** —
we do **not** expect you to finish everything. See [`TASK.md`](./TASK.md) for the full brief and
what we grade.

**→ The design write-up is in [`DESIGN.md`](./DESIGN.md).**

What's here:

- **Backend** — .NET 9 minimal API, in-memory store, OpenAPI on. Buildings, amenities and
  reservations, with double-booking prevented by an atomic capacity-check-and-insert.
- **Frontend** — React + TypeScript + Vite, calling the API through an **orval-generated typed
  client**. Three screens: your buildings → that building's amenities → book a slot.
- **Tests** — `backend/Api.Tests`, including a concurrency test that races 16 threads at a
  capacity-1 amenity and asserts exactly one wins.

Seeded residents: `resident-jeremy` is in both buildings, `resident-sam` in Oak Street, and
`resident-varun` and `resident-brian` in Main Street. Because each building has at least two
residents, you can watch one take a slot the other then can't book. Switching between them in the
UI also shows the isolation working — each sees only their own buildings.

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

Open http://localhost:5173. The Vite dev server proxies `/api/*` to the backend, so there's no
CORS to deal with.

**3) Tests**

```bash
cd backend
dotnet test
```

## Regenerating the API client

The typed client lives in `frontend/src/api/generated/api.ts`, generated from the committed
snapshot at `frontend/openapi.json` so it works on a fresh clone with no backend running.

```bash
cd frontend && pnpm gen:api
```

When the backend's endpoints change, refresh the snapshot first:

```bash
# with the API running
curl -s http://localhost:5080/openapi/v1.json -o frontend/openapi.json
cd frontend && pnpm gen:api
```

This is a manual step — see the note in `DESIGN.md` on what would keep it from drifting.

## Where things are

| Looking for... | Look at |
| --- | --- |
| The design write-up | [`DESIGN.md`](./DESIGN.md) |
| Double-booking prevention | `backend/Api/Store/InMemoryStore.cs` (`TryCreateReservation`) |
| Booking rules / access checks | `backend/Api/Services/ReservationService.cs` |
| Endpoints | `backend/Api/Program.cs` |
| Domain shape | `backend/Api/Domain/` |
| The UI | `frontend/src/App.tsx` |
| The typed client | `frontend/src/api/generated/api.ts` (generated) |
| Tests | `backend/Api.Tests/ReservationTests.cs` |

## Submitting

When you're done, **push your work to a branch and open a pull request against this repository.**
Include in the PR:

1. **A short design doc** (1–3 pages, Markdown is fine, committed in the repo) — see `TASK.md` for
   what to cover.
2. **2–3 sentences** in the PR description on what you'd do next with another 4 hours.
