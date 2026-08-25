import { useEffect, type ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import { PageHeader } from '../App';
import { api } from '../api/client';
import { useQuery } from '../api/useQuery';
import type { ExecutionDetailDto, ExecutionStepDto } from '../api/contracts';
import {
  Badge,
  Empty,
  Failed,
  Field,
  Identifier,
  Loading,
  Panel,
  Timestamp,
} from '../components/primitives';
import { durationBetween, formatDuration } from '../lib/format';
import { lookOfExecutionState, lookOfOutcome, lookOfStepState } from '../lib/status';
import { useExecutionStream, type LiveStatus } from '../sse/useExecutionStream';
import { DiffPanel } from './panels/DiffPanel';
import { EvidencePanel } from './panels/EvidencePanel';
import { FindingsPanel } from './panels/FindingsPanel';
import { VerificationPanel } from './panels/VerificationPanel';

/**
 * One attempt, in full.
 *
 * This is the diagnostic screen, so it is laid out the way a diagnosis proceeds: what
 * happened, then step by step how, then what the machine checks said, then what the reviewer
 * found, then the change itself, then everything that was recorded.
 *
 * Each panel loads on its own. A diff that fails to load must not take the findings with it -
 * at the moment something has gone wrong, losing four working panels because a fifth broke is
 * exactly the wrong behaviour.
 */
export function ExecutionDetailPage(): ReactNode {
  const { executionId = '' } = useParams();

  const execution = useQuery((signal) => api.execution(executionId, signal), [executionId]);
  const detail = execution.state.status === 'ready' ? execution.state.data : null;

  // Watched only while there is something left to watch.
  const live = useExecutionStream(executionId, {
    enabled: detail !== null && !isTerminal(detail),
  });

  // The stream says something moved; the durable query is what says what it moved to.
  useEffect(() => {
    if (live.revision > 0) {
      execution.refetch();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [live.revision]);

  if (execution.state.status === 'loading') {
    return (
      <>
        <PageHeader title="Execution" />
        <div className="content">
          <Loading label="Loading execution" />
        </div>
      </>
    );
  }

  if (execution.state.status === 'error') {
    return (
      <>
        <PageHeader title="Execution" />
        <div className="content">
          <Failed error={execution.state.error} onRetry={execution.refetch} />
        </div>
      </>
    );
  }

  const value = execution.state.data;
  const outcome = lookOfOutcome(value.outcome);

  return (
    <>
      <PageHeader
        title={`${value.taskKey} · attempt ${value.attemptNo}`}
        context={
          <Link to={`/tasks/${encodeURIComponent(value.taskKey)}`}>Back to {value.taskKey}</Link>
        }
        aside={
          isTerminal(value) ? (
            outcome ? (
              <Badge look={outcome} />
            ) : null
          ) : (
            <LiveIndicator status={live.status} />
          )
        }
      />
      <div className="content">
        <div className="page">
          <Panel title="Summary">
            <div className="fields">
              <Field label="State">
                <Badge look={lookOfExecutionState(value.state)} />
              </Field>
              <Field label="Outcome">
                {outcome ? <Badge look={outcome} /> : <span className="muted">Not finished</span>}
              </Field>
              <Field label="Execution">
                <Identifier value={value.id} label="execution id" />
              </Field>
              <Field label="Base commit">
                <Identifier value={value.baseCommitSha} label="base commit" />
              </Field>
              <Field label="Result commit">
                <Identifier value={value.resultCommitSha} label="result commit" />
              </Field>
              <Field label="Branch">
                <span className="mono">{value.branchName ?? '—'}</span>
              </Field>
              <Field label="Workspace">{value.workspaceState ?? '—'}</Field>
              <Field label="Started">
                <Timestamp value={value.createdAtUtc} />
              </Field>
              <Field label="Took">
                {formatDuration(durationBetween(value.createdAtUtc, value.updatedAtUtc))}
              </Field>
            </div>

            {value.unexpectedAgentCommitDetected ? (
              <div className="timeline__reason" style={{ marginTop: 'var(--space-4)' }}>
                <strong>The agent created commits the Hub did not ask for.</strong>
                {value.unexpectedCommitShas.length > 0 ? (
                  <ul style={{ margin: 'var(--space-2) 0 0', paddingLeft: '1.2em' }}>
                    {value.unexpectedCommitShas.map((sha) => (
                      <li key={sha} className="mono">
                        {sha}
                      </li>
                    ))}
                  </ul>
                ) : null}
              </div>
            ) : null}
          </Panel>

          <Panel title="Timeline" hint={`${value.steps.length} steps`} flush>
            <Timeline steps={value.steps} />
          </Panel>

          <VerificationPanel executionId={executionId} revision={live.revision} />
          <FindingsPanel executionId={executionId} revision={live.revision} />
          <DiffPanel executionId={executionId} hasResult={value.resultCommitSha !== null} />
          <EvidencePanel executionId={executionId} revision={live.revision} />
        </div>
      </div>
    </>
  );
}

const isTerminal = (execution: ExecutionDetailDto): boolean =>
  execution.state === 'Completed' || execution.state === 'Failed' || execution.state === 'Cancelled';

/**
 * The steps, exactly as they were persisted.
 *
 * Nothing is inferred. A step the Hub never wrote does not appear here as "pending", because
 * that would be this screen inventing a stage the execution may not even have.
 */
function Timeline({ steps }: { steps: readonly ExecutionStepDto[] }): ReactNode {
  if (steps.length === 0) {
    return (
      <Empty
        title="No steps recorded"
        detail="This attempt has not written any step yet, or it never started one."
      />
    );
  }

  return (
    <ol className="timeline">
      {steps.map((step, index) => {
        const look = lookOfStepState(step.state);
        const took = durationBetween(step.startedAtUtc, step.endedAtUtc);

        return (
          <li key={`${step.order}-${step.kind}`} className="timeline__item">
            <div className="timeline__rail" aria-hidden="true">
              <span
                className="timeline__dot"
                style={{
                  borderColor: `var(--${look.tone}-border)`,
                  background: look.shape === 'outline' ? 'transparent' : `var(--${look.tone}-fg)`,
                }}
              />
              {index < steps.length - 1 ? <span className="timeline__line" /> : null}
            </div>
            <div>
              <div className="timeline__head">
                <span className="timeline__name">{humanise(step.kind)}</span>
                <Badge look={look} />
                {step.evidenceCount > 0 ? (
                  <span className="muted" style={{ fontSize: 'var(--text-sm)' }}>
                    {step.evidenceCount} artifact{step.evidenceCount === 1 ? '' : 's'}
                  </span>
                ) : null}
              </div>
              <div className="timeline__meta">
                {step.startedAtUtc ? <Timestamp value={step.startedAtUtc} /> : 'Not started'}
                {took !== null ? ` · ${formatDuration(took)}` : ''}
              </div>
              {step.stateReason ? <p className="timeline__reason">{step.stateReason}</p> : null}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

/** "PrepareWorkspace" reads as "Prepare workspace" without a second vocabulary to maintain. */
function humanise(kind: string): string {
  const spaced = kind.replace(/([a-z0-9])([A-Z])/g, '$1 $2');

  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

/**
 * Whether the page is seeing updates.
 *
 * Three words, no socket vocabulary. What an operator needs to know is whether what they are
 * looking at is keeping up - not which transport is doing it.
 */
function LiveIndicator({ status }: { status: LiveStatus }): ReactNode {
  const shown: Record<LiveStatus, { className: string; label: string; title: string }> = {
    idle: {
      className: 'live--offline',
      label: 'Not live',
      title: 'This attempt has finished, so there is nothing left to stream.',
    },
    connecting: {
      className: 'live--connecting',
      label: 'Reconnecting',
      title: 'Trying to resume the live stream. What is shown is the last durable state.',
    },
    live: { className: 'live--live', label: 'Live', title: 'Updates are arriving as they happen.' },
    offline: {
      className: 'live--offline',
      label: 'Offline snapshot',
      title: 'Not receiving updates. What is shown is the last durable state; reload to refresh.',
    },
  };

  const look = shown[status];

  return (
    <span className={`live ${look.className}`} title={look.title} role="status">
      <span className="live__dot" aria-hidden="true" />
      {look.label}
    </span>
  );
}
