import { expect, test } from './fixtures';
import {
  actions,
  createTask,
  markReady,
  run,
  slowImplementer,
  summary,
} from './hub';

/**
 * Stopping a run that is genuinely running.
 *
 * The scripted agent used here stays alive, so there is a real process to interrupt rather
 * than a race against something that was going to finish anyway. What the UI must never do is
 * claim more than the Hub did: asking a separate process to stop and it having stopped are two
 * different facts, and the answer distinguishes them.
 */
test.describe('Cancel', () => {
  test('asks a running attempt to stop and reports only what the Hub answered', async ({
    page,
  }) => {
    const key = await createTask(page, 'A run that has to be stopped');

    await markReady(page);

    const executionId = await run(page, slowImplementer);

    // Genuinely running: the agent step has started and nothing is terminal.
    await expect(
      page.getByRole('region', { name: 'Timeline' }).getByText('Agent'),
    ).toBeVisible({ timeout: 60_000 });

    await expect(summary(page).getByText('Not finished')).toBeVisible();

    await page.goto(`/tasks/${key}`);
    await expect(page.locator('.header .badge')).toHaveText('Running');

    const posted: string[] = [];

    page.on('request', (request) => {
      if (request.method() === 'POST') {
        posted.push(new URL(request.url()).pathname);
      }
    });

    await actions(page).getByRole('button', { name: 'Cancel task' }).click();

    const dialog = page.getByRole('dialog');

    // Honest about what it can promise before it is clicked.
    await expect(dialog).toContainText(/asks the process driving it to stop/i);
    await expect(dialog).toContainText(/not guaranteed to succeed/i);

    // The Hub's own answer, read off the wire the browser used, so what the page says can be
    // compared with what it was told rather than with what this test hoped for.
    const [answer] = await Promise.all([
      page.waitForResponse((response) => response.url().includes('/cancel')),
      dialog.getByRole('button', { name: 'Cancel task' }).click(),
    ]);

    const cancellation = (await answer.json()) as { state: string; taskState: string };

    expect(answer.status()).toBe(200);
    expect(['Cancelled', 'CancellationRequested']).toContain(cancellation.state);

    if (cancellation.state === 'CancellationRequested') {
      // The one thing that must not happen: showing a request as a finished cancellation.
      // The task is still whatever the Hub said it was, and the page has to say so too.
      await expect(actions(page).getByRole('status')).toContainText('Cancellation requested');
      await expect(actions(page).getByRole('status')).toContainText(cancellation.taskState);
      await expect(page.locator('.header .badge')).not.toHaveText('Cancelled');
    }

    // Through the cancel route, and never by rejecting the task instead.
    expect(posted.some((path) => path.endsWith('/cancel'))).toBe(true);
    expect(posted.some((path) => path.includes('/reject'))).toBe(false);

    // The durable end state, whenever the Hub gets there. Read from the server, not assumed.
    await expect
      .poll(
        async () => {
          const response = await fetch(
            `${new URL(page.url()).origin}/api/v1/tasks/${key}`,
          );

          return ((await response.json()) as { summary: { state: string } }).summary.state;
        },
        { message: 'the task never reached a terminal state', timeout: 120_000 },
      )
      .toBe('Cancelled');

    await page.goto(`/tasks/${key}`);
    await expect(page.locator('.header .badge')).toHaveText('Cancelled');

    // Cancelled is terminal: nothing is offered, and the attempt is still readable.
    await expect(actions(page)).toBeHidden();
    await expect(page.getByRole('region', { name: 'Attempts' })).toContainText('1');

    await page.goto(`/executions/${executionId}`);
    await expect(summary(page).getByText('Cancelled', { exact: true })).toBeVisible();
  });
});
