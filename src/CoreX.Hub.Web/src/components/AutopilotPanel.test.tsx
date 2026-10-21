import { screen, within } from '@testing-library/react';
import type { AutopilotDto, AutopilotRepositoryDto } from '../api/contracts';
import { renderAt, stubFetch } from '../test/render';
import { AutopilotPanel } from './AutopilotPanel';

const repository = (over: Partial<AutopilotRepositoryDto> = {}): AutopilotRepositoryDto => ({
  organizationSlug: 'core-x-logic',
  projectSlug: 'talep-takip',
  repositorySlug: 'talep-takip',
  runQueue: true,
  acceptWhenClean: true,
  state: 'Idle',
  detail: 'nothing is queued',
  queue: [],
  attention: [],
  ...over,
});

const report = (over: Partial<AutopilotDto> = {}): AutopilotDto => ({
  running: true,
  lastPassUtc: '2026-10-22T09:00:00.000Z',
  problem: null,
  actions: [],
  repositories: [repository()],
  ...over,
});

const show = (onActivity?: () => void): void => {
  renderAt('/tasks', '/tasks', <AutopilotPanel onActivity={onActivity} />);
};

describe('AutopilotPanel', () => {
  it('shows nothing where no repository switches an autopilot on', async () => {
    const fetch = vi.fn();
    stubFetch({ '/autopilot': { body: report({ repositories: [] }) } });

    show(fetch);

    // Give the query a chance to answer before asserting absence.
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(screen.queryByRole('region', { name: 'Autopilot' })).not.toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('says why the queue waits and what the operator runs about it', async () => {
    stubFetch({
      '/autopilot': {
        body: report({
          repositories: [
            repository({
              state: 'Paused',
              detail: 'the queue waits until these are resolved',
              queue: ['TT-002'],
              attention: [
                {
                  taskKey: 'TT-001',
                  kind: 'NotOnDefaultBranch',
                  detail: "accepted but not on 'main' yet; the next task is built on that branch",
                  hint: 'git merge --ff-only agent/TT-001/1',
                },
              ],
            }),
          ],
        }),
      },
    });

    show();

    const panel = within(await screen.findByRole('region', { name: 'Autopilot' }));

    expect(panel.getByText('Needs you')).toBeInTheDocument();
    expect(panel.getByText('Queue: TT-002')).toBeInTheDocument();
    expect(panel.getByRole('link', { name: 'TT-001' })).toHaveAttribute('href', '/tasks/TT-001');
    expect(panel.getByText('git merge --ff-only agent/TT-001/1')).toBeInTheDocument();

    // A report, never a control.
    expect(panel.queryAllByRole('button')).toHaveLength(0);
  });

  it('tells the page around it when a pass changed something', async () => {
    const activity = vi.fn();
    stubFetch({ '/autopilot': { body: report({ actions: ['accepted TT-001#1'] }) } });

    show(activity);

    await screen.findByRole('region', { name: 'Autopilot' });

    expect(activity).toHaveBeenCalledTimes(1);
  });
});
