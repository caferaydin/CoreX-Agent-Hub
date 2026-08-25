import { screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TasksPage } from './TasksPage';
import { envelope, renderAt, stubFetch } from '../test/render';
import { taskPage, taskSummary } from '../test/fixtures';

/**
 * The working screen, in the four states it actually has.
 *
 * The populated case is where the value is: an operator arrives asking "is anything waiting
 * for me, and did anything fail", and the row has to answer both without being opened.
 */
describe('TasksPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows what is waiting and what failed, without opening a row', async () => {
    stubFetch({
      '/api/v1/tasks': {
        body: taskPage([
          taskSummary({ taskKey: 'CXH-001', state: 'AwaitingAcceptance' }),
          taskSummary({
            id: 'two',
            taskKey: 'CXH-002',
            state: 'Ready',
            latestExecutionOutcome: 'ReviewBlocked',
            attemptCount: 3,
          }),
        ]),
      },
    });

    renderAt('/tasks', '/tasks', <TasksPage />);

    await screen.findByText('CXH-001');

    expect(screen.getByText('Awaiting acceptance')).toBeInTheDocument();
    expect(screen.getByText('Passed')).toBeInTheDocument();

    // The distinction that matters most: a blocked review is not "failed".
    expect(screen.getByText('Review blocked')).toBeInTheDocument();
    expect(screen.queryByText('Failed')).not.toBeInTheDocument();

    expect(screen.getByRole('link', { name: 'CXH-002' })).toHaveAttribute(
      'href',
      '/tasks/CXH-002',
    );
  });

  it('says so when there is nothing yet, rather than showing a blank page', async () => {
    stubFetch({ '/api/v1/tasks': { body: taskPage([]) } });

    renderAt('/tasks', '/tasks', <TasksPage />);

    expect(await screen.findByText('No tasks yet')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('announces that it is loading', async () => {
    stubFetch({ '/api/v1/tasks': { body: taskPage([]) } });

    renderAt('/tasks', '/tasks', <TasksPage />);

    // Announced to a screen reader rather than only drawn as a shimmer.
    expect(screen.getByRole('status')).toHaveTextContent('Loading tasks');

    // Then let it settle, so the request completing is not an unwrapped update.
    await screen.findByText('No tasks yet');
  });

  it('shows the Hub own words when it refuses, and the correlation id', async () => {
    stubFetch({
      '/api/v1/tasks': {
        status: 409,
        body: envelope('task_refused', 'The catalogue would not answer.', 'corr-42'),
      },
    });

    renderAt('/tasks', '/tasks', <TasksPage />);

    const alert = await screen.findByRole('alert');

    expect(alert).toHaveTextContent('The catalogue would not answer.');
    expect(alert).toHaveTextContent('corr-42');

    // Never a stack trace, never a path.
    expect(alert.textContent).not.toMatch(/at CoreX|Exception|[A-Z]:\\/);
  });

  it('reports a Hub that is not running as unreachable rather than crashing', async () => {
    stubFetch({
      '/api/v1/tasks': () => {
        throw new TypeError('Failed to fetch');
      },
    });

    renderAt('/tasks', '/tasks', <TasksPage />);

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('Could not reach the Hub'),
    );
  });

  it('offers nothing that would change Hub state', async () => {
    stubFetch({ '/api/v1/tasks': { body: taskPage([taskSummary()]) } });

    renderAt('/tasks', '/tasks', <TasksPage />);

    await screen.findByText('CXH-001');

    // Phase 9B is a read experience. Not even a disabled control, so nothing suggests an
    // action the slice has not proved.
    expect(screen.queryAllByRole('button')).toHaveLength(0);

    for (const forbidden of [/run/i, /cancel/i, /accept/i, /override/i, /reject/i, /ready/i]) {
      expect(screen.queryByRole('button', { name: forbidden })).not.toBeInTheDocument();
    }
  });
});
