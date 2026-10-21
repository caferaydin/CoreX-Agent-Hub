import { useEffect, useRef, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import type { AutopilotRepositoryDto, AutopilotState } from '../api/contracts';
import { useQuery } from '../api/useQuery';
import type { StatusLook } from '../lib/status';
import { Badge, Panel, Timestamp } from './primitives';

/** How often the report is read again. The autopilot itself passes every few seconds. */
const RefreshMilliseconds = 5000;

const looks: Record<AutopilotState, StatusLook> = {
  Off: {
    label: 'Off',
    tone: 'muted',
    shape: 'outline',
    meaning: 'The autopilot block switches nothing on.',
  },
  Unavailable: {
    label: 'Unavailable',
    tone: 'bad',
    shape: 'outline',
    meaning: 'The configuration names a repository the autopilot cannot use.',
  },
  Idle: {
    label: 'Idle',
    tone: 'neutral',
    shape: 'outline',
    meaning: 'Nothing is queued and nothing needs anyone.',
  },
  Working: {
    label: 'Working',
    tone: 'running',
    shape: 'solid',
    meaning: 'One of this repository’s tasks is running.',
  },
  Waiting: {
    label: 'Waiting',
    tone: 'info',
    shape: 'outline',
    meaning: 'Work is queued; the machine’s single execution slot is busy elsewhere.',
  },
  Paused: {
    label: 'Needs you',
    tone: 'warn',
    shape: 'striped',
    meaning: 'Something needs a person before the next task may start.',
  },
};

/**
 * The autopilot's report (ADR-0028), read-only.
 *
 * Renders nothing where no repository switches an autopilot on, so an installation that does
 * not use it sees no trace of it. There is no button here: the configuration file is the only
 * switch, and a person who wants a task run presses Run on the task.
 *
 * `onActivity` fires when a pass changed something - accepted a task, started one - so the
 * page around this can read its own data again instead of showing a queue that already moved.
 */
export function AutopilotPanel({ onActivity }: { onActivity?: () => void }): ReactNode {
  const { state, refetch } = useQuery((signal) => api.autopilot(signal), []);

  useEffect(() => {
    const timer = window.setInterval(refetch, RefreshMilliseconds);
    return () => window.clearInterval(timer);
  }, [refetch]);

  const seen = useRef<string | null>(null);
  const report = state.status === 'ready' ? state.data : null;

  useEffect(() => {
    if (report?.lastPassUtc && report.actions.length > 0 && seen.current !== report.lastPassUtc) {
      seen.current = report.lastPassUtc;
      onActivity?.();
    }
  }, [report, onActivity]);

  if (report === null || report.repositories.length === 0) {
    return null;
  }

  return (
    <Panel
      title="Autopilot"
      hint={
        report.running ? (
          report.lastPassUtc ? (
            <>
              Last pass <Timestamp value={report.lastPassUtc} />
            </>
          ) : (
            'Starting…'
          )
        ) : (
          'This host does not run the autopilot'
        )
      }
    >
      {report.problem ? <p className="notice notice--error">{report.problem}</p> : null}
      <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
        {report.repositories.map((repository) => (
          <RepositoryRow
            key={`${repository.organizationSlug}/${repository.projectSlug}/${repository.repositorySlug}`}
            repository={repository}
          />
        ))}
      </ul>
    </Panel>
  );
}

function RepositoryRow({ repository }: { repository: AutopilotRepositoryDto }): ReactNode {
  const powers = [
    repository.runQueue ? 'runs the queue' : null,
    repository.acceptWhenClean ? 'accepts clean work' : null,
  ].filter((p): p is string => p !== null);

  return (
    <li className="stack">
      <div className="row" style={{ alignItems: 'baseline' }}>
        <Badge look={looks[repository.state]} />
        <strong>{repository.repositorySlug}</strong>
        <span className="muted">{powers.join(' · ')}</span>
      </div>
      <span>{repository.detail}</span>
      {repository.queue.length > 0 ? (
        <span className="muted">Queue: {repository.queue.join(', ')}</span>
      ) : null}
      {repository.attention.length > 0 ? (
        <ul className="stack" aria-label="Needs attention" style={{ margin: 0, paddingLeft: 'var(--space-5)' }}>
          {repository.attention.map((item) => (
            <li key={`${item.taskKey}-${item.kind}`}>
              <Link to={`/tasks/${encodeURIComponent(item.taskKey)}`}>{item.taskKey}</Link> — {item.detail}
              {item.hint ? (
                <div>
                  <code>{item.hint}</code>
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      ) : null}
    </li>
  );
}
