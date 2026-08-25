import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CreateTaskPage } from './CreateTaskPage';
import { TaskDetailPage } from './TaskDetailPage';
import { envelope, stubFetch } from '../test/render';
import { agentProfile, organization, project, repository, taskDetail, taskSummary } from '../test/fixtures';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { render } from '@testing-library/react';

/**
 * Creating a task.
 *
 * The tests that matter here are about restraint: the payload is exactly what the contract
 * asks for, the form does not decide anything the Hub decides, and a refusal leaves what was
 * typed on the screen.
 */
const catalogue = (over: Record<string, unknown> = {}) => ({
  '/api/v1/organizations': { body: [organization()] },
  '/api/v1/projects': { body: [project()] },
  '/api/v1/repositories': { body: [repository()] },
  '/api/v1/agent-profiles': { body: [agentProfile()] },
  ...over,
});

/** Rendered with the task detail route present, so a successful create can navigate to it. */
const show = (): void => {
  render(
    <MemoryRouter initialEntries={['/tasks/new']}>
      <Routes>
        <Route path="/tasks/new" element={<CreateTaskPage />} />
        <Route path="/tasks/:taskKey" element={<TaskDetailPage />} />
        <Route path="/tasks" element={<p>tasks list</p>} />
      </Routes>
    </MemoryRouter>,
  );
};

/** Fills the three things the form insists on before it will send anything. */
async function fillMinimum(user: ReturnType<typeof userEvent.setup>): Promise<void> {
  await user.selectOptions(await screen.findByLabelText('Repository'), [
    '01a03a11-0000-7001-b175-8c2679d883a8',
  ]);
  await user.type(screen.getByLabelText('Objective'), 'Write the acceptance document');
  await user.type(screen.getByLabelText('Criterion 1 text'), 'The solution builds');
  await user.type(screen.getByLabelText('Criterion 1 verification step'), 'build');
}

describe('CreateTaskPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('asks only for what the create contract takes', async () => {
    stubFetch(catalogue());

    show();

    await screen.findByLabelText('Repository');

    expect(screen.getByLabelText('Objective')).toBeInTheDocument();
    expect(screen.getByLabelText(/Task key/)).toBeInTheDocument();

    // Not asked for, because the create contract does not take them - and because an operator
    // declares work rather than a provider invocation.
    for (const forbidden of [/runtime/i, /permission/i, /sandbox/i, /model/i, /reviewer/i, /provider/i]) {
      expect(screen.queryByLabelText(forbidden)).not.toBeInTheDocument();
    }
  });

  it('refuses to send until the things it can check are there', async () => {
    const user = userEvent.setup();

    stubFetch(catalogue());

    show();

    await screen.findByLabelText('Repository');
    await user.click(screen.getByRole('button', { name: 'Create task' }));

    // Ergonomic only: nothing chosen, nothing written. Linked to the field for a screen reader
    // rather than announced as loose red text.
    expect(screen.getByText('Choose the repository this task is about.')).toBeInTheDocument();
    expect(screen.getByLabelText('Repository')).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByText('Describe what the work is.')).toBeInTheDocument();
  });

  it('checks the shape of a task key without guessing whether it is free', async () => {
    const user = userEvent.setup();

    stubFetch(catalogue());

    show();

    await user.type(await screen.findByLabelText(/Task key/), 'nonsense');
    await user.click(screen.getByRole('button', { name: 'Create task' }));

    expect(screen.getByText(/A key looks like PROJECT-001/)).toBeInTheDocument();
  });

  it('adds and removes scope paths', async () => {
    const user = userEvent.setup();

    stubFetch(catalogue());

    show();

    await screen.findByLabelText('Repository');

    await user.type(screen.getByLabelText('Allowed path 1'), 'docs/**');
    await user.click(screen.getByRole('button', { name: 'Add path' }));
    await user.type(screen.getByLabelText('Allowed path 2'), 'src/**');

    expect(screen.getByLabelText('Allowed path 2')).toHaveValue('src/**');

    await user.click(screen.getAllByRole('button', { name: 'Remove' })[0]!);

    // The second became the first; nothing was lost from the row that stayed.
    expect(screen.getByLabelText('Allowed path 1')).toHaveValue('src/**');
    expect(screen.queryByLabelText('Allowed path 2')).not.toBeInTheDocument();
  });

  it('offers the two criterion kinds the Hub has and no third', async () => {
    const user = userEvent.setup();

    stubFetch(catalogue());

    show();

    const kind = await screen.findByLabelText('Criterion 1 kind');

    expect(Array.from(kind.querySelectorAll('option')).map((o) => o.textContent)).toEqual([
      'Machine',
      'Human',
    ]);

    // A Machine criterion names the verification step that answers it; a Human one does not.
    expect(screen.getByLabelText('Criterion 1 verification step')).toBeInTheDocument();

    await user.selectOptions(kind, ['Human']);

    expect(screen.queryByLabelText('Criterion 1 verification step')).not.toBeInTheDocument();
  });

  it('sends exactly the contract payload', async () => {
    const user = userEvent.setup();
    const sent: unknown[] = [];

    stubFetch(
      catalogue({
        // The detail route first: a create that succeeds navigates to it, and the substring
        // stub would otherwise answer that GET with the summary and render a task page with
        // no criteria on it.
        '/api/v1/tasks/API-007': { body: taskDetail({ summary: taskSummary({ taskKey: 'API-007' }) }) },
        '/api/v1/tasks': { status: 201, body: taskSummary({ taskKey: 'API-007' }) },
      }),
    );

    const original = globalThis.fetch;
    vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST' && typeof init.body === 'string') {
        sent.push(JSON.parse(init.body));
      }

      return original(input, init);
    });

    show();

    await fillMinimum(user);
    await user.type(screen.getByLabelText('Allowed path 1'), '  docs/**  ');
    await user.click(screen.getByRole('button', { name: 'Create task' }));

    await waitFor(() => expect(sent).toHaveLength(1));

    expect(sent[0]).toEqual({
      organizationSlug: 'core-x-logic',
      projectSlug: 'agent-hub',
      repositorySlug: 'corex-agent-hub',
      objective: 'Write the acceptance document',
      // Trimmed, and empty rows dropped.
      allowedScope: ['docs/**'],
      forbiddenScope: [],
      criteria: [
        {
          kind: 'Machine',
          text: 'The solution builds',
          verificationRef: 'build',
          humanCheckHint: null,
        },
      ],
      actor: 'hub:operator:ui',
    });

    // No task key was typed, so none is sent - the Hub takes the next free number.
    expect(Object.keys(sent[0] as object)).not.toContain('taskKey');
  });

  it('navigates to the key the Hub assigned, not the one that was typed', async () => {
    const user = userEvent.setup();

    stubFetch(
      catalogue({
        '/api/v1/tasks/API-007': { body: taskDetail({ summary: taskSummary({ taskKey: 'API-007' }) }) },
        '/api/v1/tasks': { status: 201, body: taskSummary({ taskKey: 'API-007' }) },
      }),
    );

    show();

    await fillMinimum(user);
    await user.type(screen.getByLabelText(/Task key/), 'API-999');
    await user.click(screen.getByRole('button', { name: 'Create task' }));

    // The detail page for the server's key, fetched fresh - nothing about the new task was
    // built from the create response.
    expect(await screen.findByRole('heading', { name: 'API-007', level: 1 })).toBeInTheDocument();
  });

  it('sends once however many times the button is pressed', async () => {
    const user = userEvent.setup();
    let posts = 0;

    stubFetch(
      catalogue({
        // The detail route first: a create that succeeds navigates to it, and the substring
        // stub would otherwise answer that GET with the summary and render a task page with
        // no criteria on it.
        '/api/v1/tasks/CXH-001': { body: taskDetail() },
        '/api/v1/tasks': { status: 201, body: taskSummary() },
      }),
    );

    const original = globalThis.fetch;
    vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') {
        posts += 1;
      }

      return original(input, init);
    });

    show();

    await fillMinimum(user);

    const submit = screen.getByRole('button', { name: 'Create task' });

    await user.tripleClick(submit);
    await waitFor(() => expect(posts).toBeGreaterThan(0));

    expect(posts).toBe(1);
  });

  it('keeps everything typed when the Hub refuses the key', async () => {
    const user = userEvent.setup();

    stubFetch(
      catalogue({
        '/api/v1/tasks': {
          status: 409,
          body: envelope('task_refused', "Task key 'API-001' is already in use.", 'corr-7'),
        },
      }),
    );

    show();

    await fillMinimum(user);
    await user.type(screen.getByLabelText(/Task key/), 'API-001');
    await user.click(screen.getByRole('button', { name: 'Create task' }));

    const alert = await screen.findByRole('alert');

    expect(alert).toHaveTextContent("Task key 'API-001' is already in use.");
    expect(alert).toHaveTextContent('corr-7');

    // Nothing was cleared. Retyping an objective because the key was taken would be its own
    // small betrayal.
    expect(screen.getByLabelText('Objective')).toHaveValue('Write the acceptance document');
    expect(screen.getByLabelText(/Task key/)).toHaveValue('API-001');
    expect(screen.getByLabelText('Criterion 1 text')).toHaveValue('The solution builds');
  });

  it('keeps the form when the Hub cannot be reached at all', async () => {
    const user = userEvent.setup();

    stubFetch(catalogue());

    const original = globalThis.fetch;
    vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'POST') {
        return Promise.reject(new TypeError('Failed to fetch'));
      }

      return original(input, init);
    });

    show();

    await fillMinimum(user);
    await user.click(screen.getByRole('button', { name: 'Create task' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The Hub did not answer');
    expect(screen.getByLabelText('Objective')).toHaveValue('Write the acceptance document');
  });
});
