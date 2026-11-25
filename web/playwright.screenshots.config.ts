import { defineConfig, devices } from '@playwright/test';

/** README screenshots (docs/img). Not part of `npm run e2e`: run `npm run e2e:screenshots`. */
export default defineConfig({
  testDir: './e2e/screenshots',
  testMatch: '**/*.shots.ts',
  fullyParallel: false,
  workers: 1,
  reporter: 'list',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:8080',
    locale: 'fr-FR',
    viewport: { width: 1440, height: 900 },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } },
    },
  ],
});
