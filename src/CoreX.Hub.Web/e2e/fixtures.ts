import { test as base, expect } from '@playwright/test';

/**
 * The test entry point every browser scenario uses.
 *
 * It adds one thing: nothing runs until the Hub says it is healthy. A freshly started host
 * migrates its database, seeds the catalogue from configuration and reconciles stale
 * executions on the first request that needs it, and while that is happening health honestly
 * answers "the database did not answer". Playwright's readiness probe is satisfied by any
 * response at all, so without this a scenario could open a page against a Hub that had not
 * finished waking up and fail for a reason that has nothing to do with what it tests.
 *
 * Worker-scoped, so it is paid once - and there is one worker, because these share a single
 * installation with a machine-wide execution lock.
 */
export const test = base.extend<Record<string, never>, { hub: void }>({
  hub: [
    async ({ }, use, workerInfo) => {
      const base = workerInfo.project.use.baseURL!;

      await expect
        .poll(
          async () => {
            try {
              const response = await fetch(`${base}/api/v1/health`);

              return response.ok ? ((await response.json()) as { status: string }).status : 'down';
            } catch {
              return 'unreachable';
            }
          },
          {
            message: 'the Hub never became healthy',
            timeout: 120_000,
            intervals: [250, 500, 1000],
          },
        )
        .toBe('healthy');

      await use();
    },
    { scope: 'worker', auto: true },
  ],
});

export { expect } from '@playwright/test';
