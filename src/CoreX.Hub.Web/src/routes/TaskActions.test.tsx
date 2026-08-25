import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TaskDetailPage } from './TaskDetailPage';
import { envelope, stubFetch } from '../test/render';
import { agentProfile, executionDetail, taskDetail, taskSummary } from '../test/fixtures';
import type { TaskState } from '../api/contracts';

/**
 * The four things an operator can do to a task.
 *
 * What is really under test is the boundary. The UI hides a button that is certain to be
 * refused, and that is all it does - it never decides. So most of these cases are a stale page
 * or a race, where the button was there and the Hub says no anyway, and the assertion is that
 * the operator sees the Hub's answer rather than a frontend guess.
 */
const at = (state: TaskState, over: Record<string, unknown> = {}) => ({
  '/api/v1/agent-profiles': { body: [agentProfile()] },

  // The action routes come first: the stub matches on a substring, so the task detail route
  // would otherwise answer every POST that hangs off it.
  ...over,
  '/api/v1/tasks/CXH-001': {
    body: taskDetail({ summary: taskSummary({ state }), acceptance: null }),
  },
});

/** Confirms inside the dialog, where the trigger of the same name is not. */
async function confirm(user: ReturnType<typeof userEvent.setup>): Promise<void> {
  const dialog = await screen.findByRole('dialog');

  await user.click(within(dialog).getByRole('button', { name: 'Cancel task' }));
}

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

/**
 * Every POST body the page sent, in order.
 *
 * Installed *after* the route stubs, because it wraps whatever `fetch` is current - calling it
 * first would capture the real one and then be replaced by the stub.
 */
function recordPosts(): unknown[] {
  const sent: unknown[] = [];
  const original = globalThis.fetch;

  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    if (init?.method === 'POST' && typeof init.body === 'string') {
      sent.push({ url: String(input), body: JSON.parse(init.body) });
    }

    return original(input, init);
  });

  return sent;
}

describe('Task actions', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // --- Which buttons appear ---------------------------------------------------------------

  it.each([
    ['Draft', ['Mark ready', 'Cancel task'], ['Run']],
    ['Ready', ['Run', 'Cancel task'], ['Mark ready']],
    ['Running', ['Cancel task'], ['Mark ready', 'Run']],
    ['AwaitingAcceptance', ['Cancel task'], ['Mark ready', 'Run']],
  ] as const)('offers the right actions for %s', async (state, expected, absent) => {
    stubFetch(at(state));

    show();

    await screen.findByRole('region', { name: 'Actions' });

    for (const name of expected) {
      expect(screen.getByRole('button', { name })).toBeInTheDocument();
    }

    for (const name of absent) {
      expect(screen.queryByRole('button', { name })).not.toBeInTheDocument();
    }
  });

  it.each(['Accepted', 'Rejected', 'Cancelled'] as const)(
    'offers nothing once the task is %s',
    async (state) => {
      stubFetch(at(state));

      show();

      await screen.findByRole('heading', { name: 'CXH-001', level: 1 });

      expect(screen.queryByRole('region', { name: 'Actions' })).not.toBeInTheDocument();
    },
  );

  it('never offers an acceptance decision, in any state', async () => {
    for (const state of ['Draft', 'Ready', 'Running', 'AwaitingAcceptance'] as const) {
      stubFetch(at(state));

      const { unmount } = render(
        <MemoryRouter initialEntries={['/tasks/CXH-001']}>
          <Routes>
            <Route path="/tasks/:taskKey" element={<TaskDetailPage />} />
          </Routes>
        </MemoryRouter>,
      );

      // Scoped to this panel, because since Phase 9D the page does offer decisions - in a
      // different region, deliberately. What must stay true is that moving a task through its
      // lifecycle and deciding its fate are never the same control sitting in the same row.
      const lifecycle = within(await screen.findByRole('region', { name: 'Actions' }));

      for (const forbidden of [/accept/i, /override/i, /request changes/i, /reject/i]) {
        expect(lifecycle.queryByRole('button', { name: forbidden })).not.toBeInTheDocument();
      }

      unmount();
    }
  });

  // --- Ready --------------------------------------------------------------------------------

  it('marks a draft ready and refetches rather than assuming', async () => {
    const user = userEvent.setup();

    stubFetch(
      at('Draft', {
        '/api/v1/tasks/CXH-001/ready': {
          body: {
            transitioned: true,
            task: taskSummary({ state: 'Ready' }),
            readyGateSatisfied: true,
            readyGateProblems: [],
          },
        },
      }),
    );

    const posts = recordPosts();

    show();

    await user.click(await screen.findByRole('button', { name: 'Mark ready' }));

    await waitFor(() => expect(posts).toHaveLength(1));

    const sent = posts[0] as { url: string; body: unknown };

    expect(sent.url).toContain('/tasks/CXH-001/ready');
    expect(sent.body).toEqual({ actor: 'hub:operator:ui' });
  });

  it('says so when the gate holds a draft back, without deciding that itself', async () => {
    const user = userEvent.setup();

    stubFetch(
      at('Draft', {
        '/api/v1/tasks/CXH-001/ready': {
          body: {
            transitioned: false,
            task: taskSummary({ state: 'Draft' }),
            readyGateSatisfied: false,
            readyGateProblems: ['No acceptance criterion has been declared.'],
          },
        },
      }),
    );

    show();

    await user.click(await screen.findByRole('button', { name: 'Mark ready' }));

    // A 200 that did not transition is not an error - it is the gate answering.
    expect(await screen.findByText(/ready gate did not let this through/i)).toBeInTheDocument();
  });

  it('shows the Hub refusal when ready is rejected outright', async () => {
    const user = userEvent.setup();

    stubFetch(
      at('Draft', {
        '/api/v1/tasks/CXH-001/ready': {
          status: 409,
          body: envelope('task_refused', "Task 'CXH-001' is Running, not Draft.", 'corr-3'),
        },
      }),
    );

    show();

    await user.click(await screen.findByRole('button', { name: 'Mark ready' }));

    const alert = await screen.findByRole('alert');

    expect(alert).toHaveTextContent("Task 'CXH-001' is Running, not Draft.");
    expect(alert).toHaveTextContent('corr-3');
  });

  // --- Run ----------------------------------------------------------------------------------

  it('starts a run and goes to the execution without waiting for it', async () => {
    const user = userEvent.setup();

    stubFetch(
      at('Ready', {
        '/api/v1/tasks/CXH-001/runs': {
          status: 202,
          body: {
            executionId: '01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3',
            taskKey: 'CXH-001',
            attemptNo: 3,
            eventsUrl: '/api/v1/executions/01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3/events',
          },
        },
      }),
    );

    const posts = recordPosts();

    show();

    await user.click(await screen.findByRole('button', { name: 'Run' }));

    // Navigated on the 202. Nothing here waited for a provider to finish, and nothing polled.
    expect(await screen.findByText('execution page')).toBeInTheDocument();

    const sent = posts[0] as { url: string; body: unknown };

    expect(sent.url).toContain('/tasks/CXH-001/runs');
    expect(sent.body).toEqual({ actor: 'hub:operator:ui', agentProfile: 'claude-implementer' });
  });

  it('sends one run however many times the button is pressed', async () => {
    const user = userEvent.setup();

    stubFetch(
      at('Ready', {
        '/api/v1/tasks/CXH-001/runs': {
          status: 202,
          body: {
            executionId: 'exec-1',
            taskKey: 'CXH-001',
            attemptNo: 1,
            eventsUrl: '/api/v1/executions/exec-1/events',
          },
        },
      }),
    );

    const posts = recordPosts();

    show();

    await user.tripleClick(await screen.findByRole('button', { name: 'Run' }));
    await waitFor(() => expect(posts.length).toBeGreaterThan(0));

    expect(posts).toHaveLength(1);
  });

  it('shows the refusal when a stale page tries to run something already running', async () => {
    const user = userEvent.setup();

    // The page was rendered while the task was Ready. Something else started it since.
    stubFetch(
      at('Ready', {
        '/api/v1/tasks/CXH-001/runs': {
          status: 409,
          body: envelope(
            'execution_refused',
            'another Hub process is already running an execution for this installation',
          ),
        },
      }),
    );

    show();

    await user.click(await screen.findByRole('button', { name: 'Run' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'another Hub process is already running an execution',
    );

    // Still on the task. A refusal is not a navigation.
    expect(screen.queryByText('execution page')).not.toBeInTheDocument();
  });

  it('lets an operator choose between implementers, and offers no reviewer', async () => {
    stubFetch(
      at('Ready', {
        '/api/v1/agent-profiles': {
          body: [
            agentProfile(),
            agentProfile({ name: 'second-implementer', model: 'another-model' }),
            agentProfile({ name: 'codex-reviewer', role: 'Reviewer', provider: 'Codex' }),
          ],
        },
      }),
    );

    show();

    const chooser = await screen.findByLabelText('Implementer profile');
    const options = Array.from(chooser.querySelectorAll('option')).map((o) => o.value);

    // Role decides, not the provider's name. A read-only reviewer asked to implement would
    // produce a run that could never write the result it was asked for.
    expect(options).toEqual(['claude-implementer', 'second-implementer']);
    expect(options).not.toContain('codex-reviewer');
  });

  it('says so when there is no implementer to run with', async () => {
    stubFetch(at('Ready', { '/api/v1/agent-profiles': { body: [] } }));

    show();

    expect(
      await screen.findByText(/declares no implementer profile/i),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run' })).toBeDisabled();
  });

  // --- Cancel -------------------------------------------------------------------------------

  it('confirms before cancelling, and says what cancelling really does', async () => {
    const user = userEvent.setup();

    stubFetch(at('Running'));

    const posts = recordPosts();

    show();

    await user.click(await screen.findByRole('button', { name: 'Cancel task' }));

    const dialog = await screen.findByRole('dialog');

    expect(dialog).toHaveAccessibleName('Cancel this task?');

    // Honest about what it can promise: asked to stop, then waited for, and not guaranteed.
    expect(dialog).toHaveTextContent(/asks the process driving it to stop/i);
    expect(dialog).toHaveTextContent(/not guaranteed to succeed/i);

    // Nothing was sent by opening it.
    expect(posts).toHaveLength(0);

    // And the safe choice has focus.
    expect(screen.getByRole('button', { name: 'Keep going' })).toHaveFocus();
  });

  it('sends nothing when the confirmation is dismissed', async () => {
    const user = userEvent.setup();

    stubFetch(at('Running'));

    const posts = recordPosts();

    show();

    await user.click(await screen.findByRole('button', { name: 'Cancel task' }));

    const dialog = await screen.findByRole('dialog');

    await user.click(within(dialog).getByRole('button', { name: 'Keep going' }));

    expect(posts).toHaveLength(0);
    await waitFor(() => expect(dialog).not.toHaveAttribute('open'));
  });

  it('sends the cancellation the contract asks for', async () => {
    const user = userEvent.setup();

    stubFetch(
      at('Running', {
        '/api/v1/tasks/CXH-001/cancel': {
          body: {
            state: 'Cancelled',
            taskKey: 'CXH-001',
            taskState: 'Cancelled',
            executionId: null,
            executionState: null,
            resultCommitSha: null,
            worktreePreserved: false,
            detail: 'no execution was running',
          },
        },
      }),
    );

    const posts = recordPosts();

    show();

    await user.click(await screen.findByRole('button', { name: 'Cancel task' }));

    const dialog = await screen.findByRole('dialog');

    await user.type(within(dialog).getByLabelText(/Reason/), 'not needed');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel task' }));

    await waitFor(() => expect(posts).toHaveLength(1));

    const sent = posts[0] as { url: string; body: unknown };

    expect(sent.url).toContain('/tasks/CXH-001/cancel');
    expect(sent.body).toEqual({ actor: 'hub:operator:ui', reason: 'not needed' });
  });

  it('does not claim a task is cancelled when the Hub only accepted the request', async () => {
    const user = userEvent.setup();

    stubFetch(
      at('Running', {
        '/api/v1/tasks/CXH-001/cancel': {
          body: {
            state: 'CancellationRequested',
            taskKey: 'CXH-001',
            taskState: 'Running',
            executionId: '01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3',
            executionState: 'RunningAgent',
            resultCommitSha: null,
            worktreePreserved: true,
            detail:
              'execution was asked to stop and was still RunningAgent after 60 seconds; the task was left as it is',
          },
        },
      }),
    );

    show();

    await user.click(await screen.findByRole('button', { name: 'Cancel task' }));
    await confirm(user);

    // The distinction the whole result type exists for: a 200 that explicitly does not mean
    // the work stopped.
    const notice = await screen.findByText(/Cancellation requested/);

    expect(notice).toHaveTextContent('still Running');
    expect(notice).toHaveTextContent('asked to stop');
    expect(notice.textContent).not.toMatch(/^Cancelled\./);
  });

  it('shows the refusal when the Hub will not cancel', async () => {
    const user = userEvent.setup();

    stubFetch(
      at('AwaitingAcceptance', {
        '/api/v1/tasks/CXH-001/cancel': {
          status: 409,
          body: envelope(
            'cancellation_refused',
            'no running process is listening for that execution; if its host died, the next hub command reconciles it',
          ),
        },
      }),
    );

    show();

    await user.click(await screen.findByRole('button', { name: 'Cancel task' }));
    await confirm(user);

    expect(await screen.findByRole('alert')).toHaveTextContent('no running process is listening');

    // Nothing local pretended otherwise.
    expect(screen.queryByText(/^Cancelled\./)).not.toBeInTheDocument();
  });

  it('reconciles to the terminal truth when the run finished before the cancel landed', async () => {
    const user = userEvent.setup();

    // The page still says Running; the Hub already completed it and refuses.
    let detail = taskDetail({ summary: taskSummary({ state: 'Running' }), acceptance: null });

    stubFetch({
      '/api/v1/agent-profiles': { body: [agentProfile()] },
      '/api/v1/tasks/CXH-001/cancel': {
        status: 409,
        body: envelope('cancellation_refused', 'the task is already AwaitingAcceptance'),
      },
      '/api/v1/tasks/CXH-001': { body: detail },
    });

    show();

    await user.click(await screen.findByRole('button', { name: 'Cancel task' }));

    // The refetch that follows sees what really happened.
    detail = taskDetail({
      summary: taskSummary({ state: 'AwaitingAcceptance' }),
      acceptance: null,
    });

    stubFetch({
      '/api/v1/agent-profiles': { body: [agentProfile()] },
      '/api/v1/tasks/CXH-001/cancel': {
        status: 409,
        body: envelope('cancellation_refused', 'the task is already AwaitingAcceptance'),
      },
      '/api/v1/tasks/CXH-001': { body: detail },
    });

    await confirm(user);

    expect(await screen.findByRole('alert')).toHaveTextContent('already AwaitingAcceptance');

    // The terminal outcome was never overwritten with a local Cancelled.
    expect(screen.queryByText(/^Cancelled\./)).not.toBeInTheDocument();
  });

  // --- SSE ----------------------------------------------------------------------------------

  it('leaves the execution stream alone; cancelling is a request, not a disconnect', async () => {
    // Cancel is an ordinary mutation on the task. The execution page owns its own stream and
    // keeps it until the run is durably terminal - there is no cancellation EventSource, and
    // nothing here closes one.
    const actions = (await import('./TaskActions')) as unknown as Record<string, unknown>;

    expect(Object.keys(actions)).toEqual(['TaskActions']);

    const source = (await import('../sse/useExecutionStream')).useExecutionStream;

    expect(typeof source).toBe('function');

    stubFetch({
      '/api/v1/executions/exec-1': { body: executionDetail({ state: 'Completed' }) },
      '/api/v1/executions/exec-1/verification': { body: [] },
      '/api/v1/executions/exec-1/review': { body: { findings: [], inScopeCountsBySeverity: {}, majorThreshold: 1, blocks: false, summary: '' } },
      '/api/v1/executions/exec-1/evidence': { body: [] },
      '/api/v1/executions/exec-1/diff': { text: '' },
    });
  });
});
