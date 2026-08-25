import { screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TaskDetailPage } from './TaskDetailPage';
import { renderAt, stubFetch } from '../test/render';
import { executionSummary, taskDetail, taskSummary } from '../test/fixtures';

const show = (): void => {
  renderAt('/tasks/CXH-001', '/tasks/:taskKey', <TaskDetailPage />);
};

describe('TaskDetailPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('keeps Machine and Human criteria visually apart', async () => {
    stubFetch({ '/api/v1/tasks/CXH-001': { body: taskDetail() } });

    show();

    await screen.findByText('Acceptance criteria');

    // Two different things, answered by two different authorities - evidence and a person -
    // so they are labelled rather than mixed into one list.
    const machine = screen.getByRole('region', { name: 'Machine criteria' });
    const human = screen.getByRole('region', { name: 'Human criteria' });

    expect(machine).toHaveTextContent('The solution builds in Release');
    expect(machine).toHaveTextContent('build');
    expect(human).toHaveTextContent('The document contains the requested semantic content.');

    // And the hint an author left for the person is shown with the criterion it belongs to.
    expect(human).toHaveTextContent('Read it and decide.');
  });

  it('renders scope as paths rather than as prose or json', async () => {
    stubFetch({ '/api/v1/tasks/CXH-001': { body: taskDetail() } });

    show();

    const scope = await screen.findByRole('region', { name: 'Scope' });

    expect(scope).toHaveTextContent('docs/phase8-codex-reviewer-acceptance.md');
    expect(scope.querySelector('.path')).not.toBeNull();
    expect(scope.textContent).not.toContain('{');
  });

  it('lists attempts from the API and links each to its execution', async () => {
    stubFetch({
      '/api/v1/tasks/CXH-001': {
        body: taskDetail({
          attempts: [
            executionSummary({ id: 'exec-2', attemptNo: 2, outcome: 'ReviewBlocked' }),
            executionSummary({ id: 'exec-1', attemptNo: 1, outcome: 'VerificationFailed' }),
          ],
        }),
      },
    });

    show();

    // 'Attempts' is both a panel title and an overview field label, so the panel is found by
    // its role rather than by a word that legitimately appears twice.
    await screen.findByRole('region', { name: 'Attempts' });

    // The two outcomes stay distinct - neither becomes a generic failure.
    expect(screen.getByText('Review blocked')).toBeInTheDocument();
    expect(screen.getByText('Verification failed')).toBeInTheDocument();

    expect(screen.getByRole('link', { name: '2' })).toHaveAttribute('href', '/executions/exec-2');
  });

  it('says so when a task has never been run', async () => {
    stubFetch({
      '/api/v1/tasks/CXH-001': {
        body: taskDetail({
          summary: taskSummary({ attemptCount: 0, latestExecutionId: null }),
          attempts: [],
        }),
      },
    });

    show();

    expect(await screen.findByText('No attempts yet')).toBeInTheDocument();
  });

  it('shows what acceptance is waiting for without offering to decide it', async () => {
    stubFetch({
      '/api/v1/tasks/CXH-001': {
        body: taskDetail({
          acceptance: {
            taskState: 'Ready',
            executionId: 'exec-1',
            attemptNo: 1,
            isLatestExecution: true,
            executionState: 'Completed',
            executionOutcome: 'ReviewBlocked',
            overrideApplicable: true,
            existingDecision: null,
            machineCriteria: [],
            humanCriteria: [],
            machineTruthProblems: [],
          },
        }),
      },
    });

    show();

    const projection = within(await screen.findByRole('region', { name: 'Acceptance' }));

    // The server computed that overriding is the applicable route, and this panel says so.
    expect(projection.getByText('Override applies')).toBeInTheDocument();

    // Scoped to this panel: since Phase 9D the page does offer decisions, in the Human
    // decision panel. What this asserts is that the projection stayed a projection - it
    // reports what the Hub is waiting for and offers nothing to click.
    expect(projection.queryAllByRole('button')).toHaveLength(0);
    expect(projection.queryAllByRole('radio')).toHaveLength(0);
    expect(projection.queryAllByRole('textbox')).toHaveLength(0);
  });

  it('reports what the ready gate is holding back', async () => {
    stubFetch({
      '/api/v1/tasks/CXH-001': {
        body: taskDetail({
          readyGateSatisfied: false,
          readyGateProblems: ['No acceptance criterion has been declared.'],
        }),
      },
    });

    show();

    expect(await screen.findByText('Not satisfied')).toBeInTheDocument();
    expect(
      screen.getByText('No acceptance criterion has been declared.'),
    ).toBeInTheDocument();
  });
});
