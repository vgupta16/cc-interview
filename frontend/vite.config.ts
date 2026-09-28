import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

// The dev server proxies API calls to the .NET backend so the browser sees a single
// origin (no CORS) and the generated client can use relative URLs like `/api/amenities`.
// API_URL points the proxy at another backend, such as one started on a separate port for
// verification; without it, the proxy targets the backend that `dotnet run` starts.
export default defineConfig(({ mode }) => {
  const apiUrl = loadEnv(mode, '.', '').API_URL || 'http://localhost:5080';

  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': apiUrl,
        '/health': apiUrl,
      },
    },
  };
});
