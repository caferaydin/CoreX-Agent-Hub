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
  waitForOutcome,
} from './hub';

/**
 * The decisions a person makes, made in a real browser against a real Hub.
 *
 * Each scenario drives a task all the way from nothing, because a governance decision is only
 * meaningful on state the application actually produced. Nothing here posts to the API, runs a
 * CLI command, or writes to the database.
 */

/** Drives a task to AwaitingAcceptance through the UI and returns its key. */
async function awaitingAcceptance(page: import('@playwright/test').Page, objective: string) {
  const key = await createTask(page, objective);

  await markReady(page);
  await run(page);
  await waitForOutcome(page, 'Passed');

  await page.goto(`/tasks/${key}`);
  await expect(page.locator('.header .badge')).toHaveText('Awaiting acceptance');

  return key;
}

test.describe('Override eligibility', () => {
  test('offers the ordinary Accept and no override on a run that passed', async ({ page }) => {
    await awaitingAcceptance(page, 'A run that passes needs no override');

    const decision = governance(page);

    await expect(decision.getByRole('button', { name: 'Accept', exact: true })).toBeVisible();
    await expect(decision.getByRole('button', { name: 'Request changes' })).toBeVisible();
    await expect(decision.getByRole('button', { name: 'Reject' })).toBeVisible();

    // The override is for one thing, and this is not it. Not present, and not present-and-grey.
    await expect(decision.getByRole('button', { name: /override/i })).toHaveCount(0);

    // The acceptance projection agrees, from the server's own computation.
    await expect(acceptance(page)).toContainText('Ordinary');
  });

  test('offers nothing to decide on a task that has never run', async ({ page }) => {
    await createTask(page, 'A task nobody has run yet');
    await markReady(page);

    // Ready, no attempt, no outcome: there is nothing for a person to decide about.
    await expect(page.locator('.header .badge')).toHaveText('Ready');
    await expect(governance(page)).toBeHidden();

    for (const name of [/accept/i, /override/i, /request changes/i, /reject/i]) {
      await expect(actions(page).getByRole('button', { name })).toHaveCount(0);
    }
  });
});

test.describe('Request changes', () => {
  test('sends the attempt back and keeps it as the record', async ({ page }) => {
    const key = await awaitingAcceptance(page, 'A change that needs another pass');

    const attempts = page.getByRole('region', { name: 'Attempts' });

    await expect(attempts.getByRole('row')).toHaveCount(2); // header plus one attempt

    await governance(page).getByRole('button', { name: 'Request changes' }).click();

    const dialog = page.getByRole('dialog');

    await expect(dialog).toContainText('stays exactly where it is');
    await expect(dialog).toContainText('new attempt beside it');

    await confirm(page, 'Request changes');

    await expect(page.locator('.header .badge')).toHaveText('Ready', { timeout: 30_000 });

    // The attempt is still there, still Passed, and nothing started on its own.
    await hardReload(page, `/tasks/${key}`);

    await expect(page.getByRole('region', { name: 'Attempts' }).getByRole('row')).toHaveCount(2);
    await expect(page.getByRole('region', { name: 'Attempts' })).toContainText('Passed');
    await expect(page.locator('.header .badge')).toHaveText('Ready');

    // Ready means runnable again - by a person, when they choose.
    await expect(actions(page).getByRole('button', { name: 'Run' })).toBeVisible();
  });
});

test.describe('Reject', () => {
  test('ends the task through its own route and keeps the history', async ({ page }) => {
    const key = await awaitingAcceptance(page, 'Work that is not going to be taken');

    const posted: string[] = [];

    page.on('request', (request) => {
      if (request.method() === 'POST') {
        posted.push(new URL(request.url()).pathname);
      }
    });

    await governance(page).getByRole('button', { name: 'Reject' }).click();

    await expect(page.getByRole('dialog')).toContainText('Nothing is deleted');

    await confirm(page, 'Reject');

    await expect(page.locator('.header .badge')).toHaveText('Rejected', { timeout: 30_000 });

    // Its own endpoint. Rejecting is not cancelling, and the browser proves which one it asked.
    expect(posted.some((path) => path.endsWith('/reject'))).toBe(true);
    expect(posted.some((path) => path.includes('/cancel'))).toBe(false);

    await hardReload(page, `/tasks/${key}`);

    await expect(page.locator('.header .badge')).toHaveText('Rejected');

    const decided = governance(page);

    await expect(decided).toContainText('Reject');
    await expect(decided).toContainText('hub:operator:ui');
    await expect(decided.getByRole('button')).toHaveCount(0);

    // The attempt, its outcome and its evidence are all still readable.
    await expect(page.getByRole('region', { name: 'Attempts' })).toContainText('Passed');
    await expect(actions(page)).toBeHidden();
  });
});

test.describe('Live updates', () => {
  test('follows a run without a reload, and leaving the page does not stop it', async ({ page }) => {
    const key = await createTask(page, 'A run watched from the browser');

    await markReady(page);

    const opened: string[] = [];

    page.on('request', (request) => {
      if (request.resourceType() === 'eventsource') {
        opened.push(new URL(request.url()).pathname);
      }
    });

    const executionId = await run(page);

    // A real EventSource against the real endpoint, opened by the page and not by this test.
    await expect
      .poll(() => opened, { message: 'the page never opened a live stream', timeout: 30_000 })
      .toContain(`/api/v1/executions/${executionId}/events`);

    // The page moves while nobody reloads it. `page.reload()` is never called in this block.
    await expect(summary(page).getByText('Passed', { exact: true })).toBeVisible({
      timeout: 90_000,
    });

    // Navigating away closes the client's stream and has nothing to do with the run: the
    // execution is terminal and stays terminal, and the task moved on without this page.
    await page.goto(`/tasks/${key}`);
    await expect(page.locator('.header .badge')).toHaveText('Awaiting acceptance');

    await page.goto(`/executions/${executionId}`);
    await expect(summary(page).getByText('Passed', { exact: true })).toBeVisible();
  });
});

test.describe('Accepting', () => {
  test('records the decision and shows it again after a reload', async ({ page }) => {
    const key = await awaitingAcceptance(page, 'Work worth accepting');

    await answerHumanCriteria(page, 'Not applicable');

    await governance(page).getByRole('button', { name: 'Accept', exact: true }).click();
    await confirm(page, 'Accept');

    await expect(page.locator('.header .badge')).toHaveText('Accepted', { timeout: 30_000 });

    await hardReload(page, `/tasks/${key}`);

    const decided = governance(page);

    await expect(decided).toContainText('Accept');
    await expect(decided).toContainText('Not applicable');
    await expect(decided).toContainText('hub:operator:ui');
    await expect(decided.getByRole('time')).toBeVisible();

    // A normal acceptance, said plainly: no override was used.
    await expect(decided).toContainText('No');
    await expect(decided.getByRole('button')).toHaveCount(0);
  });
});
