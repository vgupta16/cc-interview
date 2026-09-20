import { defineConfig, devices } from '@playwright/test';

// One in-memory backend is shared by every test, so tests run serially (workers: 1) and each
// one uses its own far-future day (see tests/helpers.ts) and cleans up after itself.
export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 30_000,
  expect: { timeout: 5_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: 'http://localhost:5173',
    // datetime-local is interpreted in the browser zone; UTC keeps test times = wire times.
    timezoneId: 'UTC',
    locale: 'en-US',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    actionTimeout: 5_000,
    navigationTimeout: 10_000,
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      // Only the .NET 10 runtime may be installed while the project targets net9.0.
      command: 'dotnet run --project ../backend/Api',
      url: 'http://localhost:5080/health',
      env: { DOTNET_ROLL_FORWARD: 'Major' },
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      stdout: 'ignore',
      stderr: 'pipe',
    },
    {
      // Prefer pnpm (via corepack when not installed); fall back to the local vite binary.
      command: '(command -v pnpm >/dev/null && pnpm dev) || corepack pnpm dev || npx vite',
      cwd: '../frontend',
      url: 'http://localhost:5173',
      env: { COREPACK_ENABLE_DOWNLOAD_PROMPT: '0' },
      reuseExistingServer: !process.env.CI,
      timeout: 60_000,
      stdout: 'ignore',
      stderr: 'pipe',
    },
  ],
});
