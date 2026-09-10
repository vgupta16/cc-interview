import { defineConfig } from 'orval';

// Generates a typed client from the API's OpenAPI spec.
//
// `input` is a committed snapshot so `pnpm gen:api` works offline and in CI. Refresh it from a
// running backend with `pnpm gen:api:live`, which overwrites the snapshot and regenerates.
export default defineConfig({
  amenities: {
    input: './openapi.json',
    output: {
      mode: 'single',
      target: './src/api/generated/api.ts',
      client: 'fetch',
      clean: true,
      override: {
        // Every generated function calls our fetch wrapper instead of `fetch` directly. The
        // stock client parses error bodies into the success type (so a 409 conflict would look
        // like a successful booking) and omits header parameters, both of which matter here.
        // See src/api/http.ts.
        mutator: {
          path: './src/api/http.ts',
          name: 'apiFetch',
        },
        // Return the parsed body directly rather than a { data, status, headers } envelope —
        // status is already handled by the wrapper, which throws on failure.
        fetch: { includeHttpResponseReturnType: false },
      },
    },
  },
});
