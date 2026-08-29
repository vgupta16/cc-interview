import { defineConfig } from 'orval';

// Generates a typed client from the API's OpenAPI spec.
//
// By default we read a committed snapshot (./openapi.json) so `pnpm gen:api` works
// even before the backend is running. To regenerate from the LIVE backend instead
// (recommended once you add endpoints), start the API and point `input` at:
//   http://localhost:5080/openapi/v1.json
export default defineConfig({
  amenities: {
    input: './openapi.json',
    output: {
      mode: 'single',
      target: './src/api/generated/api.ts',
      client: 'fetch',
      clean: true,
      override: {
        // Return { status, data } rather than the bare body. The scaffold defaulted to the
        // bare body, but booking needs the status code: a 409 (slot taken) and a 201 (booked)
        // both come back as JSON objects, and only the status tells them apart.
        fetch: { includeHttpResponseReturnType: true },
      },
    },
  },
});
