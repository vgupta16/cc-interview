import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The dev server proxies API calls to the .NET backend so the browser sees a single
// origin (no CORS) and the generated client can use relative URLs like `/api/amenities`.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5080',
      '/health': 'http://localhost:5080',
    },
  },
  test: {
    // Only the pure helpers are tested. Component tests would need a DOM environment and
    // React Testing Library; the logic worth asserting lives in src/lib.
    include: ['src/lib/**/*.test.ts'],
  },
});
