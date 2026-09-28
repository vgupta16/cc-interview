import { defineConfig } from 'orval';

// Generates a typed client from the API's OpenAPI spec.
//
// The input is the committed snapshot (./openapi.json), so `pnpm gen:api` works on a fresh
// clone without the backend running. After an API change, refresh the snapshot from the
// backend and commit it with the regenerated client (AGENTS.md, "Adding or changing an endpoint").
export default defineConfig({
  amenities: {
    input: './openapi.json',
    output: {
      mode: 'single',
      target: './src/api/generated/api.ts',
      client: 'fetch',
      clean: true,
      override: {
        // Return { data, status, headers } typed per declared status, so the UI can
        // branch on 401/404/409 instead of treating an error body as success.
        fetch: {
          includeHttpResponseReturnType: true
        }
      }
    }
  }
});
