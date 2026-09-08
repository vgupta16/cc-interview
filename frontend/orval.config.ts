import { defineConfig } from 'orval';

// Generates a typed client from the API's OpenAPI spec.
//
// Reads the committed snapshot (./openapi.json) so `pnpm gen:api` works on a fresh clone
// without the backend running. The snapshot is refreshed from the live API whenever the
// endpoints change:
//
//   cd backend/Api && dotnet run
//   curl -s http://localhost:5080/openapi/v1.json -o frontend/openapi.json
//   cd frontend && pnpm gen:api
//
// Note this is a manual step. See DESIGN.md ("Deferred work") on what it would take to stop
// the committed client from silently drifting out of sync with the backend.
export default defineConfig({
  amenities: {
    input: './openapi.json',
    output: {
      mode: 'single',
      target: './src/api/generated/api.ts',
      client: 'fetch',
      clean: true,
      override: {
        // CHANGED from the scaffold's `false`.
        //
        // With `false`, the generated functions return only the parsed body and throw away the
        // status code. That makes a 409 indistinguishable from success at the type level: a
        // conflict response is parsed and handed back typed as a `Reservation`, so the UI has no
        // honest way to tell a booking from a rejection. Since surfacing the double-booking
        // conflict is the whole point of this feature, we keep the status and unwrap `.data`
        // at the call sites instead.
        fetch: { includeHttpResponseReturnType: true },
      },
    },
  },
});
