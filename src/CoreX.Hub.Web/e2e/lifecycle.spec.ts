import { expect, test } from './fixtures';
import {
  acceptance,
  actions,
  answerHumanCriteria,
  confirm,
  createTask,
  governance,
  hardReload,
  markReady,
  run,
  summary,
  timeline,
  waitForOutcome,
} from './hub';

/**
 * A task managed from nothing to Accepted, entirely through the browser.
 *
 * No CLI command runs here, no request is posted from the test, and nothing is written into
 * the database. Every transition is a person clicking something, which is the only way this
 * proves what it claims: that the product is usable by an operator who has only the UI.
 */
test.describe('Clean lifecycle', () => {
  test('creates, readies, runs, watches, and accepts a task', async ({ page }) => {
    // --- Create ---------------------------------------------------------------------------
    const key = await createTask(page, 'Add the feature the fixture applies');

    await expect(page.getByRole('heading', { name: key, level: 1 })).toBeVisible();
    await expect(page.locator('.header .badge')).toHaveText('Draft');

    // --- Ready ----------------------------------------------------------------------------
    await markReady(page);
    await expect(page.locator('.header .badge')).toHaveText('Ready');

    // Nothing to decide yet, and the panel says nothing rather than offering something grey.
    await expect(governance(page)).toBeHidden();

    // --- Run ------------------------------------------------------------------------------
    const executionId = await run(page);

    await expect(summary(page)).toBeVisible();

    // The live stream is what a browser sees while a run is in progress. This is a real
    // EventSource against a real endpoint, and the page updates without being reloaded.
    await expect(timeline(page)).toBeVisible();

    await waitForOutcome(page, 'Passed');

    // The steps a real run walked, read from durable state rather than from the stream.
    await expect(timeline(page)).toContainText('Prepare workspace');
    await expect(timeline(page)).toContainText('Agent');
    await expect(timeline(page)).toContainText('Verify');

    // --- Back to the task -----------------------------------------------------------------
    await page.goto(`/tasks/${key}`);

    await expect(page.locator('.header .badge')).toHaveText('Awaiting acceptance');
    await expect(governance(page)).toBeVisible();

    // Machine truth is shown and cannot be answered here.
    const machine = governance(page).getByRole('region', { name: 'Machine criteria' });

    await expect(machine).toContainText('Met');
    await expect(machine.getByRole('radio')).toHaveCount(0);

    // --- Accept ---------------------------------------------------------------------------
    await answerHumanCriteria(page, 'Met');

    await governance(page).getByRole('button', { name: 'Accept', exact: true }).click();

    const dialog = page.getByRole('dialog');

    await expect(dialog).toContainText('does not merge, push or deploy');

    await confirm(page, 'Accept');

    await expect(page.locator('.header .badge')).toHaveText('Accepted', { timeout: 30_000 });

    // --- The reload proof -------------------------------------------------------------------
    await hardReload(page, `/tasks/${key}`);

    await expect(page.locator('.header .badge')).toHaveText('Accepted');

    const decided = governance(page);

    await expect(decided).toContainText('Accept');
    await expect(decided).toContainText('hub:operator:ui');
    await expect(decided.getByRole('time')).toBeVisible();
    await expect(decided).toContainText('Met');

    // Recorded, and read back from the server: this page has no memory of the click.
    await expect(decided).toContainText('Recorded');

    // And there is nothing left to decide.
    await expect(decided.getByRole('button')).toHaveCount(0);
    await expect(decided.getByRole('radio')).toHaveCount(0);
    await expect(actions(page)).toBeHidden();

    // The execution is untouched by the decision and still says what it was.
    await page.goto(`/executions/${executionId}`);
    await expect(summary(page).getByText('Passed', { exact: true })).toBeVisible();

    // The acceptance projection is still a projection.
    await page.goto(`/tasks/${key}`);
    await expect(acceptance(page).getByRole('button')).toHaveCount(0);
  });
});
