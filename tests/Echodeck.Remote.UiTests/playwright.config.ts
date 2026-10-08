import { defineConfig, devices } from '@playwright/test';
import { appUrl, controlPort, controlUrl, port } from './support/env';

/**
 * Phone UI tests: the real phone page and server (Echodeck.Remote), with a fake PC behind it
 * (TestHost/). Playwright starts the host itself; tests talk to its control API to set up
 * each scenario. See README.md in this folder.
 */
export default defineConfig({
  testDir: './specs',
  // One shared fake backend: tests reset it, so they run one at a time.
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 30_000,
  expect: { timeout: 5_000 },
  reporter: process.env.CI
    ? [['list'], ['html', { open: 'never' }], ['github']]
    : [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: appUrl,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  // Real users are on iPhones/iPads (Safari) and Android phones (Chrome).
  projects: [
    { name: 'android-chrome', use: { ...devices['Pixel 7'] } },
    { name: 'iphone-safari', use: { ...devices['iPhone 13'] } },
    { name: 'ipad-safari', use: { ...devices['iPad (gen 7)'] } },
  ],
  webServer: {
    command: `dotnet run --project TestHost -c Release -- --port ${port} --control ${controlPort}`,
    url: `${controlUrl}/health`,
    reuseExistingServer: !process.env.CI,
    timeout: 180_000,
    stdout: 'ignore',
    stderr: 'pipe',
  },
});
