import { defineConfig, devices } from '@playwright/test';
import { join } from 'node:path';

/**
 * Browser acceptance for the operator UI.
 *
 * What is under test is the whole local product: the built React bundle, served by the shipped
 * API host, on one loopback origin, in front of a real database and a real git repository. Not
 * the Vite dev server, not jsdom, and not a component in isolation - those are covered by the
 * unit suite and none of them can prove that a page survives a refresh or that an EventSource
 * reaches an endpoint.
 *
 * Everything the browser does here goes through the UI. No test writes to the database, flips
 * a state, or posts to the API to move a task along; where a scenario needs a task to be
 * Running, a browser clicks Run.
 *
 * One worker, on purpose. These share a single Hub installation with a machine-wide execution
 * lock, and running them in parallel would prove only that the lock works.
 */
const e2e = join(import.meta.dirname, 'e2e');
const artifacts = join(e2e, '.artifacts');

/** The port the validation host binds. Loopback, and the host refuses anything else. */
const port = 5187;

export const baseURL = `http://127.0.0.1:${port}`;

export default defineConfig({
  testDir: e2e,
  testMatch: '**/*.spec.ts',

  // The committed suite is the e2e directory's own specs. Anything under .artifacts/ is a
  // generated, gitignored validation harness - a build output, a disposable Hub, or the
  // one paid real-reviewer acceptance - and must never be swept into `npm run e2e`.
  testIgnore: '**/.artifacts/**',

  // A real agent process, real git and real verification take real seconds.
  timeout: 120_000,
  expect: { timeout: 20_000 },

  fullyParallel: false,
  workers: 1,
  forbidOnly: true,
  retries: 0,

  reporter: [['list'], ['html', { outputFolder: join(artifacts, 'report'), open: 'never' }]],
  outputDir: join(artifacts, 'runs'),

  globalSetup: join(e2e, 'global-setup.mjs'),

  use: {
    baseURL,
    trace: 'retain-on-failure',
    video: 'off',
    screenshot: 'only-on-failure',
  },

  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],

  webServer: {
    // The published executable, not `dotnet run`: what is being accepted is the thing an
    // operator would start.
    command: `"${join(artifacts, 'host', 'hub-api.exe')}"`,
    url: `${baseURL}/api/v1/health`,
    timeout: 120_000,

    // Never an already-running Hub. Reusing one would point the acceptance at the operator's
    // real database.
    reuseExistingServer: false,
    stdout: 'pipe',
    stderr: 'pipe',
    env: {
      COREX_HUB_DATA_DIR: join(artifacts, 'validation', 'data'),
      COREX_HUB_LISTEN_URL: baseURL,
    },
  },
});
