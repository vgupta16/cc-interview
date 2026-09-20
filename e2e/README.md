# Browser end-to-end tests

Playwright (Chromium) against the real backend + Vite frontend. Selectors come from the
`data-testid` attributes in `frontend/src/components` and `frontend/src/App.tsx`.

    cd e2e && npm install && npx playwright install chromium
    npx playwright test            # starts backend (:5080) and frontend (:5173) if not running
    npx playwright test --ui       # debug; report: npx playwright show-report

- Backend runs with `DOTNET_ROLL_FORWARD=Major`; frontend needs `pnpm install` done in `../frontend`.
- One shared in-memory backend: workers=1, each test (`S<n>` in its title) books on its own
  day (today + 40 + n) and cancels its reservations afterwards through the API.
- The browser time zone is pinned to UTC so `datetime-local` input equals the UTC wire time.
