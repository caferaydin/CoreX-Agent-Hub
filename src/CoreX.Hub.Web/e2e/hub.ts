import { expect, type Locator, type Page } from '@playwright/test';

/**
 * The things every browser scenario does, written once.
 *
 * Every function here drives the UI. There is deliberately no helper that posts to the API, no
 * helper that opens the database, and no helper that sets a state - if a scenario needs a task
 * to be Running, a browser clicks Run, because a lifecycle proven by a fixture that arranged
 * the state proves the fixture.
 */

/** The implementer profile a clean run uses. Named, never chosen by provider. */
export const implementer = 'scripted-implementer';

/** The one that stays alive, so a cancellation has something to interrupt. */
export const slowImplementer = 'scripted-slow-implementer';

export interface Criterion {
  readonly kind: 'Machine' | 'Human';
  readonly text: string;
  readonly ref?: string;
}

const DEFAULT_CRITERIA: readonly Criterion[] = [
  { kind: 'Machine', text: 'The fixture verification passes.', ref: 'test' },
  { kind: 'Human', text: 'The change is the one that was asked for.' },
];

/**
 * Creates a task through the form and lands on its detail page.
 *
 * Returns the key the Hub assigned, which is deliberately not one this function chose - it
 * never chooses one, and reading it from the URL is how the browser learns it too.
 */
export async function createTask(
  page: Page,
  objective: string,
  criteria: readonly Criterion[] = DEFAULT_CRITERIA,
): Promise<string> {
  await page.goto('/tasks');
  await page.getByRole('link', { name: 'New task' }).click();

  await expect(page).toHaveURL(/\/tasks\/new$/);

  await page.getByLabel('Repository').selectOption({ label: 'hub-e2e / hub-sample / hub-sample-repo' });
  await page.getByLabel('Objective').fill(objective);
  await page.getByLabel('Allowed path 1').fill('src/**');

  for (const [index, criterion] of criteria.entries()) {
    if (index > 0) {
      await page.getByRole('button', { name: 'Add criterion' }).click();
    }

    const n = index + 1;

    await page.getByLabel(`Criterion ${n} kind`).selectOption(criterion.kind);
    await page.getByLabel(`Criterion ${n} text`).fill(criterion.text);

    if (criterion.kind === 'Machine') {
      await page.getByLabel(`Criterion ${n} verification step`).fill(criterion.ref ?? 'test');
    }
  }

  await page.getByRole('button', { name: 'Create task' }).click();

  // The Hub assigns the key. The URL is where it is learned.
  await expect(page).toHaveURL(/\/tasks\/E2E-\d+$/, { timeout: 30_000 });

  return new URL(page.url()).pathname.split('/').pop()!;
}

/** Moves a Draft task to Ready, through the button. */
export async function markReady(page: Page): Promise<void> {
  await actions(page).getByRole('button', { name: 'Mark ready' }).click();
  await expect(actions(page).getByRole('button', { name: 'Run' })).toBeVisible();
}

/**
 * Starts an attempt, which takes the browser to the execution that was started.
 *
 * Returns the execution id from the URL - the only place this harness learns it.
 */
export async function run(page: Page, profile: string = implementer): Promise<string> {
  await actions(page).getByLabel('Implementer profile').selectOption(profile);
  await actions(page).getByRole('button', { name: 'Run' }).click();

  await expect(page).toHaveURL(/\/executions\/[0-9a-f-]{36}$/, { timeout: 30_000 });

  return new URL(page.url()).pathname.split('/').pop()!;
}

/** What the execution page currently says the run ended as. */
export const outcome = (page: Page): Locator =>
  summary(page).locator('div').filter({ hasText: /^Outcome$/ }).locator('..').getByRole('generic');

/** Waits for the execution page to show a terminal outcome, reading only what is on screen. */
export async function waitForOutcome(page: Page, expected: string): Promise<void> {
  await expect(summary(page).getByText(expected, { exact: true })).toBeVisible({ timeout: 90_000 });
}

/** Answers every human criterion in the governance panel with one verdict. */
export async function answerHumanCriteria(page: Page, verdict = 'Met'): Promise<void> {
  const human = governance(page).getByRole('region', { name: 'Human criteria' });

  for (const group of await human.getByRole('group').all()) {
    await group.getByRole('radio', { name: verdict, exact: true }).check();
  }
}

/** Confirms whatever dialog is open, by the label on its confirming button. */
export async function confirm(page: Page, label: string | RegExp): Promise<void> {
  await page.getByRole('dialog').getByRole('button', { name: label }).click();
  await expect(page.getByRole('dialog')).toBeHidden({ timeout: 30_000 });
}

export const governance = (page: Page): Locator =>
  page.getByRole('region', { name: 'Human decision' });

export const actions = (page: Page): Locator => page.getByRole('region', { name: 'Actions' });

/** The read-only acceptance projection. Exact, because 'Acceptance criteria' is a panel too. */
export const acceptance = (page: Page): Locator =>
  page.getByRole('region', { name: 'Acceptance', exact: true });

export const summary = (page: Page): Locator => page.getByRole('region', { name: 'Summary' });

export const timeline = (page: Page): Locator => page.getByRole('region', { name: 'Timeline' });

/** The task's own state, in the page header where it never competes with a panel's copy. */
export const headerState = (page: Page): Locator => page.locator('.header .badge');

/** A page reloaded the way a person reloads it: a fresh navigation, no client state kept. */
export async function hardReload(page: Page, url: string): Promise<void> {
  await page.goto('about:blank');
  await page.goto(url, { waitUntil: 'networkidle' });
}
