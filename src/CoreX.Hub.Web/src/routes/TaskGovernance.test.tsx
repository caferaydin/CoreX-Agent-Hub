import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TaskDetailPage } from './TaskDetailPage';
import { envelope, stubFetch } from '../test/render';
import {
  acceptanceResult,
  acceptanceState,
  agentProfile,
  executionSummary,
  finding,
  recordedDecision,
  review,
  taskDetail,
  taskSummary,
} from '../test/fixtures';
import type { AcceptanceStateDto, TaskDetailDto } from '../api/contracts';

/**
 * The four decisions a person makes, and the two things that must stay true through all of
 * them.
 *
 * A decision is the server's, and this UI carries one to it. So the interesting cases are the
 * ones where the page and the Hub disagree: a stale button, a refusal, a race between two
 * decisions. In every one the assertion is that the operator sees the Hub's answer rather than
 * a frontend guess, and that nothing they typed was thrown away on the way.
 *
 * The second is the boundary between what a person supplies and what the machine found. There
 * are tests here for what the panel does *not* offer, and they matter more than the ones for
 * what it does.
 */

/** The ordinary case: an attempt that passed and is waiting for a person. */
const awaiting = (over: Partial<AcceptanceStateDto> = {}): TaskDetailDto =>
  taskDetail({
    summary: taskSummary({ state: 'AwaitingAcceptance', attemptCount: 2 }),
    attempts: [executionSummary({ attemptNo: 2 })],
    acceptance: acceptanceState(over),
  });

/** The override case: a review blocked the attempt, so the task went back to Ready. */
const blocked = (over: Partial<AcceptanceStateDto> = {}): TaskDetailDto =>
  taskDetail({
    summary: taskSummary({
      state: 'Ready',
      attemptCount: 2,
      latestExecutionState: 'Completed',
      latestExecutionOutcome: 'ReviewBlocked',
    }),
    attempts: [executionSummary({ attemptNo: 2, outcome: 'ReviewBlocked' })],
    acceptance: acceptanceState({
      taskState: 'Ready',
      executionOutcome: 'ReviewBlocked',
      overrideApplicable: true,
      ...over,
    }),
  });

const routes = (task: TaskDetailDto, over: Record<string, unknown> = {}) => ({
  '/api/v1/agent-profiles': { body: [agentProfile()] },
  '/api/v1/executions/01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3/review': { body: review() },

  // The decision routes come first: the stub matches on a substring, so the task detail route
  // would otherwise answer every POST that hangs off it.
  ...over,
  '/api/v1/tasks/CXH-001': { body: task },
});

const show = (): void => {
  render(
    <MemoryRouter initialEntries={['/tasks/CXH-001']}>
      <Routes>
        <Route path="/tasks/:taskKey" element={<TaskDetailPage />} />
        <Route path="/executions/:executionId" element={<p>execution page</p>} />
      </Routes>
    </MemoryRouter>,
  );
};

const panel = async () => within(await screen.findByRole('region', { name: 'Human decision' }));

/** Answers every Human criterion, which is what the form requires before it will ask. */
async function answerAll(user: ReturnType<typeof userEvent.setup>, verdict = 'Met'): Promise<void> {
  const decision = await panel();

  for (const group of decision.getAllByRole('group')) {
    await user.click(within(group).getByRole('radio', { name: verdict }));
  }
}

/** Confirms in the dialog, where the trigger of the same name is not. */
async function confirm(
  user: ReturnType<typeof userEvent.setup>,
  name: string | RegExp,
): Promise<void> {
  const dialog = await screen.findByRole('dialog');

  await user.click(within(dialog).getByRole('button', { name }));
}

/**
 * Every POST body the page sent, in order.
 *
 * Installed *after* the route stubs, because it wraps whatever `fetch` is current - calling it
 * first would capture the real one and then be replaced by the stub.
 */
function recordPosts(): { url: string; body: unknown }[] {
  const sent: { url: string; body: unknown }[] = [];
  const original = globalThis.fetch;

  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === 'POST' && typeof init.body === 'string') {
      sent.push({ url: String(input), body: JSON.parse(init.body) as unknown });
    }

    return original(input, init);
  });

  return sent;
}

describe('Human governance', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // --- What is offered, and where ------------------------------------------------------------

  it('puts every decision in one place, and offers none of them on the task list', async () => {
    stubFetch(routes(awaiting()));

    show();

    const decision = await panel();

    for (const name of ['Accept', 'Request changes', 'Reject']) {
      expect(decision.getByRole('button', { name })).toBeInTheDocument();
    }

    // The override is not the ordinary route and never wears its label.
    expect(
      decision.queryByRole('button', { name: /override/i }),
    ).not.toBeInTheDocument();
  });

  it('offers nothing to decide while a task is still being worked on', async () => {
    for (const state of ['Draft', 'Running'] as const) {
      stubFetch(routes(taskDetail({ summary: taskSummary({ state }), acceptance: null })));

      const { unmount } = render(
        <MemoryRouter initialEntries={['/tasks/CXH-001']}>
          <Routes>
            <Route path="/tasks/:taskKey" element={<TaskDetailPage />} />
          </Routes>
        </MemoryRouter>,
      );

      await screen.findByRole('heading', { name: 'CXH-001', level: 1 });

      expect(screen.queryByRole('region', { name: 'Human decision' })).not.toBeInTheDocument();

      unmount();
    }
  });

  it('shows the state, the attempt and the outcome before anything can be clicked', async () => {
    stubFetch(routes(awaiting()));

    show();

    const decision = await panel();

    expect(decision.getByText('#2')).toBeInTheDocument();
    expect(decision.getByText('Awaiting acceptance')).toBeInTheDocument();
    expect(decision.getByText('Passed')).toBeInTheDocument();
    expect(decision.getByText('0 of 1')).toBeInTheDocument();
  });

  // --- Human verdicts ------------------------------------------------------------------------

  it('starts every human criterion unanswered', async () => {
    stubFetch(routes(awaiting()));

    show();

    const decision = await panel();

    for (const radio of decision.getAllByRole('radio')) {
      expect(radio).not.toBeChecked();
    }
  });

  it('will not ask to accept until every criterion is answered', async () => {
    const user = userEvent.setup();

    stubFetch(routes(awaiting()));

    const posts = recordPosts();

    show();

    await user.click((await panel()).getByRole('button', { name: 'Accept' }));

    expect(await screen.findByText(/answer every criterion/i)).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(posts).toHaveLength(0);
  });

  it('sends each verdict as the operator gave it, notes and all', async () => {
    const user = userEvent.setup();

    stubFetch(
      routes(awaiting(), {
        '/attempts/2/accept': { body: acceptanceResult() },
      }),
    );

    const posts = recordPosts();

    show();

    const decision = await panel();

    await user.click(decision.getByRole('radio', { name: 'Not applicable' }));
    await user.type(decision.getByRole('textbox', { name: /note for/i }), '  out of scope  ');
    await user.click(decision.getByRole('button', { name: 'Accept' }));
    await confirm(user, 'Accept');

    await waitFor(() => expect(posts).toHaveLength(1));

    expect(posts[0]!.url).toContain('/tasks/CXH-001/attempts/2/accept');
    expect(posts[0]!.body).toEqual({
      actor: 'hub:operator:ui',
      humanVerdicts: [{ criterionId: 'c2', verdict: 'NotApplicable', note: 'out of scope' }],
    });
  });

  it('never lets a machine criterion be answered by a person', async () => {
    stubFetch(routes(awaiting()));

    show();

    const decision = await panel();
    const machine = within(decision.getByRole('region', { name: 'Machine criteria' }));

    expect(machine.queryAllByRole('radio')).toHaveLength(0);
    expect(machine.queryAllByRole('textbox')).toHaveLength(0);

    // It is shown, with the verdict the evidence produced.
    expect(machine.getByText('Met')).toBeInTheDocument();
  });

  // --- Accept ---------------------------------------------------------------------------------

  it('says what accepting does not do before it does it', async () => {
    const user = userEvent.setup();

    stubFetch(routes(awaiting()));

    show();

    await answerAll(user);
    await user.click((await panel()).getByRole('button', { name: 'Accept' }));

    const dialog = within(await screen.findByRole('dialog'));

    expect(dialog.getByText(/does not merge, push or deploy/i)).toBeInTheDocument();
  });

  it('asks the Hub again after accepting rather than trusting its own idea of the state', async () => {
    const user = userEvent.setup();

    let detail = awaiting();

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);

        if (init?.method === 'POST') {
          detail = taskDetail({
            summary: taskSummary({ state: 'Accepted' }),
            acceptance: acceptanceState({
              taskState: 'Accepted',
              existingDecision: recordedDecision(),
            }),
          });

          return new Response(JSON.stringify(acceptanceResult()), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }

        if (url.includes('/agent-profiles')) {
          return new Response(JSON.stringify([agentProfile()]), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          });
        }

        return new Response(JSON.stringify(detail), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    show();

    await answerAll(user);
    await user.click((await panel()).getByRole('button', { name: 'Accept' }));
    await confirm(user, 'Accept');

    // The refetched task is Accepted, so the panel becomes a record rather than a form.
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Accept' })).not.toBeInTheDocument();
    });

    expect((await panel()).getByText('Recorded')).toBeInTheDocument();
  });

  it('shows the refusal when a stale page accepts something the Hub will not', async () => {
    const user = userEvent.setup();

    stubFetch(
      routes(awaiting(), {
        '/attempts/2/accept': {
          status: 409,
          body: envelope(
            'acceptance_refused',
            'Execution 2 is ReviewBlocked; accepting it requires an explicit override.',
            'corr-9d',
          ),
        },
      }),
    );

    show();

    await answerAll(user);
    await user.click((await panel()).getByRole('button', { name: 'Accept' }));
    await confirm(user, 'Accept');

    expect(await screen.findByText(/requires an explicit override/i)).toBeInTheDocument();
    expect(screen.getByText(/corr-9d/)).toBeInTheDocument();
  });

  it('refuses rather than sending an override through the ordinary accept route', async () => {
    stubFetch(routes(blocked()));

    show();

    const decision = await panel();

    // A blocked attempt offers the override and only the override: there is no plain Accept
    // that would quietly become one.
    expect(
      decision.getByRole('button', { name: 'Accept with review override' }),
    ).toBeInTheDocument();
    expect(decision.queryByRole('button', { name: 'Accept' })).not.toBeInTheDocument();
  });

  // --- Override --------------------------------------------------------------------------------

  it('shows what is being overridden, expanded, before asking', async () => {
    stubFetch(routes(blocked()));

    show();

    const findings = within(
      (await panel()).getByRole('region', { name: 'Review findings' }),
    );

    expect(await findings.findByText('Authorization check always succeeds')).toBeInTheDocument();
    expect(findings.getByText('Major')).toBeInTheDocument();
    expect(findings.getByText(/1 in-scope Major/)).toBeInTheDocument();
  });

  it('leaves the override reason empty and offers no wording for it', async () => {
    stubFetch(routes(blocked()));

    show();

    const reason = (await panel()).getByRole('textbox', { name: /why this is being accepted/i });

    expect(reason).toHaveValue('');

    for (const template of [/approved/i, /false positive/i, /known issue/i]) {
      expect(screen.queryByText(template)).not.toBeInTheDocument();
    }
  });

  it('will not ask to override without a reason', async () => {
    const user = userEvent.setup();

    stubFetch(routes(blocked()));

    const posts = recordPosts();

    show();

    await answerAll(user);

    const decision = await panel();

    // Whitespace is not a reason.
    await user.type(decision.getByRole('textbox', { name: /why this is being accepted/i }), '   ');
    await user.click(decision.getByRole('button', { name: 'Accept with review override' }));

    expect(await screen.findByText(/may never be silent/i)).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(posts).toHaveLength(0);
  });

  it('sends the override to its own route, with the reason trimmed', async () => {
    const user = userEvent.setup();

    stubFetch(
      routes(blocked(), {
        '/attempts/2/accept-override': {
          body: acceptanceResult({
            decision: 'AcceptWithOverride',
            overrideUsed: true,
            executionOutcome: 'ReviewBlocked',
            reason: 'The finding is about generated code we do not own.',
          }),
        },
      }),
    );

    const posts = recordPosts();

    show();

    await answerAll(user);

    const decision = await panel();

    await user.type(
      decision.getByRole('textbox', { name: /why this is being accepted/i }),
      '  The finding is about generated code we do not own.  ',
    );
    await user.click(decision.getByRole('button', { name: 'Accept with review override' }));
    await confirm(user, 'Accept with review override');

    await waitFor(() => expect(posts).toHaveLength(1));

    expect(posts[0]!.url).toContain('/attempts/2/accept-override');
    expect(posts[0]!.body).toEqual({
      actor: 'hub:operator:ui',
      reason: 'The finding is about generated code we do not own.',
      humanVerdicts: [{ criterionId: 'c2', verdict: 'Met', note: null }],
    });
  });

  it('says the execution stays blocked, and does not pretend the task passed', async () => {
    const user = userEvent.setup();

    stubFetch(
      routes(blocked(), {
        '/attempts/2/accept-override': {
          body: acceptanceResult({
            decision: 'AcceptWithOverride',
            overrideUsed: true,
            taskState: 'Accepted',
            executionOutcome: 'ReviewBlocked',
            reason: 'Accepted deliberately.',
          }),
        },
      }),
    );

    show();

    await answerAll(user);
    await user.type(
      (await panel()).getByRole('textbox', { name: /why this is being accepted/i }),
      'Accepted deliberately.',
    );
    await user.click(
      (await panel()).getByRole('button', { name: 'Accept with review override' }),
    );

    const dialog = within(await screen.findByRole('dialog'));

    expect(dialog.getByText(/kept exactly as they are/i)).toBeInTheDocument();
    expect(dialog.getByText('Review blocked')).toBeInTheDocument();

    await user.click(dialog.getByRole('button', { name: 'Accept with review override' }));

    // Recorded, and honest about both axes: the task is Accepted, the execution is not Passed.
    const notice = await screen.findByRole('status');

    expect(notice).toHaveTextContent(/AcceptWithOverride with a review override/);
    expect(notice).toHaveTextContent(/task is Accepted/);
    expect(notice).toHaveTextContent(/execution is still ReviewBlocked/);
    expect(notice).not.toHaveTextContent(/Passed/);
  });

  it('keeps the reason and the verdicts when an override is refused', async () => {
    const user = userEvent.setup();

    stubFetch(
      routes(blocked(), {
        '/attempts/2/accept-override': {
          status: 409,
          body: envelope('acceptance_refused', 'Attempt 2 already has a decision.', 'corr-x'),
        },
      }),
    );

    show();

    await answerAll(user);
    await user.type(
      (await panel()).getByRole('textbox', { name: /why this is being accepted/i }),
      'A carefully written justification.',
    );
    await user.click(
      (await panel()).getByRole('button', { name: 'Accept with review override' }),
    );
    await confirm(user, 'Accept with review override');

    expect(await screen.findByText(/already has a decision/i)).toBeInTheDocument();

    const decision = await panel();

    expect(decision.getByRole('textbox', { name: /why this is being accepted/i })).toHaveValue(
      'A carefully written justification.',
    );
    expect(decision.getByRole('radio', { name: 'Met' })).toBeChecked();
  });

  // --- Request changes and reject ---------------------------------------------------------------

  it('sends work back without needing a verdict, and stays on the task', async () => {
    const user = userEvent.setup();

    stubFetch(
      routes(awaiting(), {
        '/attempts/2/request-changes': {
          body: acceptanceResult({ decision: 'RequestChanges', taskState: 'Ready' }),
        },
      }),
    );

    const posts = recordPosts();

    show();

    await user.click((await panel()).getByRole('button', { name: 'Request changes' }));
    await confirm(user, 'Request changes');

    await waitFor(() => expect(posts).toHaveLength(1));

    expect(posts[0]!.url).toContain('/attempts/2/request-changes');
    expect(posts[0]!.body).toEqual({ actor: 'hub:operator:ui', reason: null });

    expect(screen.getByRole('heading', { name: 'CXH-001', level: 1 })).toBeInTheDocument();
  });

  it('says a new attempt starts beside the old one, not on top of it', async () => {
    const user = userEvent.setup();

    stubFetch(routes(awaiting()));

    show();

    await user.click((await panel()).getByRole('button', { name: 'Request changes' }));

    const dialog = within(await screen.findByRole('dialog'));

    expect(dialog.getByText(/stays exactly where it is/i)).toBeInTheDocument();
    expect(dialog.getByText(/new attempt beside it/i)).toBeInTheDocument();
  });

  it('rejects through the reject route, never through cancel', async () => {
    const user = userEvent.setup();

    stubFetch(
      routes(awaiting(), {
        '/attempts/2/reject': {
          body: acceptanceResult({ decision: 'Reject', taskState: 'Rejected' }),
        },
      }),
    );

    const posts = recordPosts();

    show();

    await user.click((await panel()).getByRole('button', { name: 'Reject' }));
    await confirm(user, 'Reject');

    await waitFor(() => expect(posts).toHaveLength(1));

    expect(posts[0]!.url).toContain('/attempts/2/reject');
    expect(posts[0]!.url).not.toContain('/cancel');
  });

  it('says nothing is deleted when a task is rejected', async () => {
    const user = userEvent.setup();

    stubFetch(routes(awaiting()));

    show();

    await user.click((await panel()).getByRole('button', { name: 'Reject' }));

    expect(
      within(await screen.findByRole('dialog')).getByText(/nothing is deleted/i),
    ).toBeInTheDocument();
  });

  it('sends nothing when the operator backs out of a decision', async () => {
    const user = userEvent.setup();

    stubFetch(routes(awaiting()));

    const posts = recordPosts();

    show();

    await user.click((await panel()).getByRole('button', { name: 'Reject' }));
    await user.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'Not yet' }),
    );

    expect(posts).toHaveLength(0);
    expect((await panel()).getByRole('button', { name: 'Reject' })).toBeInTheDocument();
  });

  // --- Races and repeats -------------------------------------------------------------------

  it('sends one decision even when the button is clicked twice', async () => {
    const user = userEvent.setup();

    let release: (() => void) | null = null;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });

    stubFetch(routes(awaiting()));

    const original = globalThis.fetch;
    const posts: string[] = [];

    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') {
        posts.push(String(input));
        await held;

        return new Response(JSON.stringify(acceptanceResult({ decision: 'Reject' })), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return original(input, init);
    });

    show();

    await user.click((await panel()).getByRole('button', { name: 'Reject' }));

    const dialog = within(await screen.findByRole('dialog'));
    const confirmButton = dialog.getByRole('button', { name: 'Reject' });

    await user.click(confirmButton);
    await user.click(confirmButton);

    expect(posts).toHaveLength(1);

    release!();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('does not let two different decisions be sent at once', async () => {
    const user = userEvent.setup();

    let release: (() => void) | null = null;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });

    stubFetch(routes(awaiting()));

    const original = globalThis.fetch;
    const posts: string[] = [];

    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') {
        posts.push(String(input));
        await held;

        return new Response(JSON.stringify(acceptanceResult()), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return original(input, init);
    });

    show();

    await user.click((await panel()).getByRole('button', { name: 'Request changes' }));
    await confirm(user, 'Request changes');

    // While that is in flight, everything else is unavailable - and the server would refuse a
    // second decision regardless, which is the guarantee this is only being polite about.
    const decision = await panel();

    await waitFor(() => expect(decision.getByRole('button', { name: 'Reject' })).toBeDisabled());
    expect(decision.getByRole('button', { name: 'Accept' })).toBeDisabled();

    release!();
    await waitFor(() => expect(decision.getByRole('button', { name: 'Reject' })).toBeEnabled());
    expect(posts).toHaveLength(1);
  });

  it('never retries a decision the Hub did not answer', async () => {
    const user = userEvent.setup();

    stubFetch(
      routes(awaiting(), {
        '/attempts/2/reject': () => {
          throw new TypeError('Failed to fetch');
        },
      }),
    );

    const posts = recordPosts();

    show();

    await user.click((await panel()).getByRole('button', { name: 'Reject' }));
    await confirm(user, 'Reject');

    expect(await screen.findByText(/did not answer/i)).toBeInTheDocument();

    // A mutation that failed for an unknown reason may or may not have happened.
    await new Promise((resolve) => setTimeout(resolve, 40));

    expect(posts).toHaveLength(1);
  });

  // --- Once decided ---------------------------------------------------------------------------

  it.each([
    ['Accepted', 'Accept'],
    ['Rejected', 'Reject'],
  ] as const)('offers nothing once a task is %s', async (state, kind) => {
    stubFetch(
      routes(
        taskDetail({
          summary: taskSummary({ state }),
          acceptance: acceptanceState({
            taskState: state,
            existingDecision: recordedDecision({ decision: kind }),
          }),
        }),
      ),
    );

    show();

    const decision = await panel();

    expect(decision.getByText(kind)).toBeInTheDocument();
    expect(decision.queryAllByRole('button')).toHaveLength(0);
    expect(decision.queryAllByRole('radio')).toHaveLength(0);
  });

  // --- Reading a decision back ------------------------------------------------------------------

  it('shows a normal decision from a fresh load, with no mutation behind it', async () => {
    // Nothing was clicked in this test. Everything on screen came from the REST body, which is
    // what a person opening the task tomorrow sees.
    stubFetch(
      routes(
        taskDetail({
          summary: taskSummary({ state: 'Accepted' }),
          acceptance: acceptanceState({
            taskState: 'Accepted',
            existingDecision: recordedDecision(),
          }),
        }),
      ),
    );

    show();

    const decision = await panel();

    expect(decision.getByText('Accept')).toBeInTheDocument();
    expect(decision.getByText('hub:operator:ui')).toBeInTheDocument();
    expect(decision.getByText('No')).toBeInTheDocument();
    expect(decision.getByRole('time')).toHaveAttribute(
      'title',
      expect.stringContaining('2026'),
    );

    // The verdict, named by the criterion it answered rather than by a bare identifier.
    expect(decision.getByText('Met')).toBeInTheDocument();
    expect(
      decision.getByText('The document contains the requested semantic content.'),
    ).toBeInTheDocument();
    expect(decision.getByText('read it and it is fine')).toBeInTheDocument();
  });

  it('shows an override reason from the persisted decision, not from component state', async () => {
    stubFetch(
      routes(
        taskDetail({
          summary: taskSummary({
            state: 'Accepted',
            latestExecutionOutcome: 'ReviewBlocked',
          }),
          acceptance: acceptanceState({
            taskState: 'Accepted',
            executionOutcome: 'ReviewBlocked',
            existingDecision: recordedDecision({
              overrideUsed: true,
              reason: 'the finding is about generated code we do not own',
              humanVerdicts: [{ criterionId: 'c2', verdict: 'NotApplicable', note: null }],
            }),
          }),
        }),
      ),
    );

    show();

    const decision = await panel();

    expect(decision.getByText('Used')).toBeInTheDocument();
    expect(
      decision.getByText('the finding is about generated code we do not own'),
    ).toBeInTheDocument();
    expect(decision.getByText('Not applicable')).toBeInTheDocument();

    // The two axes stay apart on a reload as well.
    expect(decision.getByText('Accepted')).toBeInTheDocument();
    expect(decision.getByText('Review blocked')).toBeInTheDocument();
    expect(decision.queryByText('Passed')).not.toBeInTheDocument();

    // And there is nothing left to decide.
    expect(decision.queryAllByRole('button')).toHaveLength(0);
  });

  it('names a verdict by its identifier when the task no longer declares the criterion', async () => {
    // A decision outlives an edit to the task it was about. The identifier is the guarantee;
    // the sentence is the convenience, and showing a sentence that was never answered would be
    // worse than showing neither.
    stubFetch(
      routes(
        taskDetail({
          summary: taskSummary({ state: 'Accepted' }),
          criteria: [],
          acceptance: acceptanceState({
            taskState: 'Accepted',
            existingDecision: recordedDecision({
              humanVerdicts: [{ criterionId: 'c-removed', verdict: 'Met', note: null }],
            }),
          }),
        }),
      ),
    );

    show();

    expect((await panel()).getByText('c-removed')).toBeInTheDocument();
  });

  it('renders a persisted reason as text, never as markup', async () => {
    stubFetch(
      routes(
        taskDetail({
          summary: taskSummary({ state: 'Accepted' }),
          acceptance: acceptanceState({
            taskState: 'Accepted',
            existingDecision: recordedDecision({
              overrideUsed: true,
              reason: '<img src=x onerror=alert(1)> accepted anyway',
            }),
          }),
        }),
      ),
    );

    show();

    expect(
      (await panel()).getByText('<img src=x onerror=alert(1)> accepted anyway'),
    ).toBeInTheDocument();
    expect(document.querySelector('img')).toBeNull();
  });

  // --- What is never on offer ------------------------------------------------------------------

  it('offers no way to change what the review found', async () => {
    stubFetch(routes(blocked()));

    show();

    const findings = within((await panel()).getByRole('region', { name: 'Review findings' }));

    await findings.findByText('Authorization check always succeeds');

    expect(findings.queryAllByRole('button')).toHaveLength(0);
    expect(findings.queryAllByRole('checkbox')).toHaveLength(0);
    expect(findings.queryAllByRole('combobox')).toHaveLength(0);

    for (const forbidden of [/dismiss/i, /false positive/i, /change severity/i, /out of scope/i]) {
      expect(screen.queryByRole('button', { name: forbidden })).not.toBeInTheDocument();
    }
  });

  it('renders a finding as text, never as markup', async () => {
    // A finding is provider output. If a reviewer emitted a tag, it stays a tag on the screen.
    stubFetch({
      ...routes(blocked()),
      '/api/v1/executions/01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3/review': {
        body: review({
          findings: [finding({ title: '<img src=x onerror=alert(1)>', rationale: '<b>bold</b>' })],
        }),
      },
    });

    show();

    const findings = within((await panel()).getByRole('region', { name: 'Review findings' }));

    expect(await findings.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument();
    expect(findings.getByText('<b>bold</b>')).toBeInTheDocument();
    expect(document.querySelector('img')).toBeNull();
  });

  it('does not let the operator say who they are', async () => {
    stubFetch(routes(awaiting()));

    show();

    const decision = await panel();

    for (const name of [/actor/i, /who/i, /identity/i, /user/i]) {
      expect(decision.queryByRole('textbox', { name })).not.toBeInTheDocument();
    }
  });

  it('names no provider, runtime or permission setting anywhere in the decision', async () => {
    stubFetch(routes(blocked()));

    show();

    const decision = await panel();

    await within(decision.getByRole('region', { name: 'Review findings' })).findByText(
      'Authorization check always succeeds',
    );

    for (const forbidden of [/claude/i, /codex/i, /sandbox/i, /permission profile/i]) {
      expect(decision.queryByText(forbidden)).not.toBeInTheDocument();
    }
  });
});
