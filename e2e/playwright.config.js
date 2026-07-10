import { defineConfig, devices } from '@playwright/test';

// Ports the two dev servers listen on for the E2E. The client talks to the API via
// VITE_API_URL; both are local, no external services.
const API_PORT = 5222;
const CLIENT_PORT = 5173;
const API_URL = `http://127.0.0.1:${API_PORT}`;
const CLIENT_URL = `http://localhost:${CLIENT_PORT}`;

// Compressed round timings (double-underscore = .NET config nesting). A full four-player
// game runs in ~a minute, but the answer window is long enough to actually watch the UI
// move. Mirrors GameApi.Tests/TestAppFactory's "same logic, faster clocks" approach.
const gameTimings = {
  GameTimings__PromptSeconds: '6',
  GameTimings__RevealSeconds: '2',
  GameTimings__AccusationSeconds: '8',
  GameTimings__VetoSeconds: '5',
  GameTimings__PrioritySeconds: '3',
  GameTimings__TickMilliseconds: '200',
  GameTimings__MaxRounds: '8',
};

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 180_000,
  expect: { timeout: 20_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: CLIENT_URL,
    actionTimeout: 20_000,
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
  // Playwright boots both servers, waits for each URL to answer, then runs the tests.
  // The API runs on the Development-only in-memory DB (no Postgres) with the Mock brain
  // (no AI keys) so the whole thing is hermetic.
  webServer: [
    {
      command: 'dotnet run --project ../GameApi/GameApi.csproj --no-launch-profile',
      url: `${API_URL}/api/health`,
      timeout: 180_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: API_URL,
        UseInMemoryDb: 'true',
        Ai__Brain: 'Mock',
        JWT_KEY: 'e2e-only-signing-key-that-is-at-least-64-characters-long-000000000',
        Cors__AllowedOrigins__0: CLIENT_URL,
        ...gameTimings,
      },
    },
    {
      command: `npm run dev -- --port ${CLIENT_PORT} --strictPort`,
      url: CLIENT_URL,
      cwd: '../game-client',
      timeout: 120_000,
      reuseExistingServer: !process.env.CI,
      env: {
        VITE_API_URL: API_URL,
      },
    },
  ],
});
