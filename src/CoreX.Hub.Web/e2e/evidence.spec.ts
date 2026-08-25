import { join } from 'node:path';
import { expect, test } from './fixtures';
import {
  actions,
  answerHumanCriteria,
  confirm,
  createTask,
  governance,
  markReady,
  run,
  slowImplementer,
  summary,
  waitForOutcome,
} from './hub';

/**
 * A small, bounded set of pictures of the real thing.
 *
 * Taken from the production-hosted SPA, so what is in them is what an operator sees: the same
 * bundle, the same origin, the same database. They live beside the other validation artifacts
 * and are not committed - a screenshot in source control is a file that is wrong within a week
 * and that nobody notices is wrong.
 */
const shots = join(import.meta.dirname, '.artifacts', 'screenshots');

const capture = (page: import('@playwright/test').Page, name: string) =>
  page.screenshot({ path: join(shots, `${name}.png`), fullPage: true });

test.describe('Screenshot evidence', () => {
  test('captures the screens Phase 9 is accepted on', async ({ page }) => {
    // 1. The task list - the screen an operator opens the Hub to look at.
    await page.goto('/tasks');
    await expect(page.getByRole('region', { name: 'Tasks' })).toBeVisible();
    await capture(page, '1-tasks');

    // 2. A run in progress, followed live. The slow agent is used so there is something to see.
    const running = await createTask(page, 'A run captured while it is still running');

    await markReady(page);
    await run(page, slowImplementer);

    await expect(page.getByRole('region', { name: 'Timeline' })).toContainText('Agent', {
      timeout: 60_000,
    });

    await capture(page, '2-running-execution');

    // Left as it was found: this task is stopped rather than abandoned mid-run.
    await page.goto(`/tasks/${running}`);
    await actions(page).getByRole('button', { name: 'Cancel task' }).click();
    await confirm(page, 'Cancel task');

    // 3. A passing attempt waiting for a person, with the governance panel open.
    const key = await createTask(page, 'A run captured while it waits for a person');

    await markReady(page);
    await run(page);
    await waitForOutcome(page, 'Passed');
    await capture(page, '3-passed-execution');

    await page.goto(`/tasks/${key}`);
    await expect(governance(page)).toBeVisible();
    await capture(page, '4-awaiting-acceptance');

    // 4. The same task once a decision has been recorded, read back from the server.
    await answerHumanCriteria(page, 'Met');
    await governance(page).getByRole('button', { name: 'Accept', exact: true }).click();
    await confirm(page, 'Accept');

    await expect(page.locator('.header .badge')).toHaveText('Accepted', { timeout: 30_000 });

    await page.goto('about:blank');
    await page.goto(`/tasks/${key}`);

    await expect(governance(page)).toContainText('Recorded');
    await capture(page, '5-accepted-readback');

    // 5. The execution behind it, unchanged by the decision, reached the way a person
    //    reaches it: by following the attempt.
    await page.getByRole('region', { name: 'Attempts' }).getByRole('link').first().click();

    await expect(summary(page)).toBeVisible();
    await capture(page, '6-execution-after-acceptance');
  });
});
