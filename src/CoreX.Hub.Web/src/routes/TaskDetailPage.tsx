import type { ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import { PageHeader } from '../App';
import { api } from '../api/client';
import { useQuery } from '../api/useQuery';
import type { AcceptanceStateDto, CriterionDto, ExecutionSummaryDto } from '../api/contracts';
import {
  Badge,
  Empty,
  Failed,
  Field,
  Identifier,
  Loading,
  Panel,
  Path,
  Timestamp,
} from '../components/primitives';
import { durationBetween, formatDuration } from '../lib/format';
import { lookOfOutcome, lookOfTaskState } from '../lib/status';

/**
 * One task, in the order a person asks about it.
 *
 * What was asked for, what it was allowed to touch, what it has to satisfy, what has been
 * tried, and where the last attempt got to. Nothing here is rendered as raw JSON: a scope glob
 * is a path, a criterion is a sentence with a kind, an attempt is a row that leads somewhere.
 */
export function TaskDetailPage(): ReactNode {
  const { taskKey = '' } = useParams();
  const { state, refetch } = useQuery((signal) => api.task(taskKey, signal), [taskKey]);

  if (state.status === 'loading') {
    return (
      <>
        <PageHeader title={taskKey} />
        <div className="content">
          <Loading label="Loading task" />
        </div>
      </>
    );
  }

  if (state.status === 'error') {
    return (
      <>
        <PageHeader title={taskKey} />
        <div className="content">
          <Failed error={state.error} onRetry={refetch} />
        </div>
      </>
    );
  }

  const task = state.data;
  const machine = task.criteria.filter((c) => c.kind === 'Machine');
  const human = task.criteria.filter((c) => c.kind === 'Human');

  return (
    <>
      <PageHeader
        title={task.summary.taskKey}
        context={task.summary.repositorySlug}
        aside={<Badge look={lookOfTaskState(task.summary.state)} />}
      />
      <div className="content">
        <div className="page">
          <Panel title="Overview">
            <p style={{ marginTop: 0, marginBottom: 'var(--space-4)' }}>{task.summary.objective}</p>
            <div className="fields">
              <Field label="State">
                <Badge look={lookOfTaskState(task.summary.state)} />
              </Field>
              <Field label="Repository">
                <span className="mono">{task.summary.repositorySlug}</span>
              </Field>
              <Field label="Attempts">{task.summary.attemptCount}</Field>
              <Field label="Ready gate">
                {task.readyGateSatisfied ? (
                  <Badge
                    look={{
                      label: 'Satisfied',
                      tone: 'good',
                      shape: 'solid',
                      meaning: 'Everything the gate requires is present.',
                    }}
                  />
                ) : (
                  <Badge
                    look={{
                      label: 'Not satisfied',
                      tone: 'warn',
                      shape: 'outline',
                      meaning: 'The gate is holding this task back.',
                    }}
                  />
                )}
              </Field>
              <Field label="Created">
                <Timestamp value={task.summary.createdAtUtc} />
              </Field>
              <Field label="Updated">
                <Timestamp value={task.summary.updatedAtUtc} />
              </Field>
            </div>

            {task.readyGateProblems.length > 0 ? (
              <ul className="stack" style={{ marginTop: 'var(--space-4)', paddingLeft: '1.2em' }}>
                {task.readyGateProblems.map((problem) => (
                  <li key={problem} className="muted">
                    {problem}
                  </li>
                ))}
              </ul>
            ) : null}
          </Panel>

          <div className="grid-2">
            <Panel title="Scope" hint="What an attempt may change">
              <div className="stack" style={{ gap: 'var(--space-4)' }}>
                <div>
                  <div className="field__label">Allowed</div>
                  {task.allowedScope.length > 0 ? (
                    <ul className="path-list">
                      {task.allowedScope.map((glob) => (
                        <li key={glob}>
                          <Path value={glob} />
                        </li>
                      ))}
                    </ul>
                  ) : (
                    <span className="muted">Nothing declared</span>
                  )}
                </div>
                {task.forbiddenScope.length > 0 ? (
                  <div>
                    <div className="field__label">Forbidden</div>
                    <ul className="path-list">
                      {task.forbiddenScope.map((glob) => (
                        <li key={glob}>
                          <Path value={glob} />
                        </li>
                      ))}
                    </ul>
                  </div>
                ) : null}
              </div>
            </Panel>

            <Panel title="Acceptance criteria" hint={`${machine.length} machine · ${human.length} human`}>
              <Criteria machine={machine} human={human} />
            </Panel>
          </div>

          <Panel title="Attempts" flush>
            {task.attempts.length === 0 ? (
              <Empty
                title="No attempts yet"
                detail="Nothing has been run for this task. Attempts appear here once an execution exists."
              />
            ) : (
              <Attempts attempts={task.attempts} />
            )}
          </Panel>

          {task.acceptance ? <Acceptance acceptance={task.acceptance} /> : null}
        </div>
      </div>
    </>
  );
}

/**
 * Machine and Human criteria, kept visually apart.
 *
 * They are answered by different things - evidence and a person - and a list that mixed them
 * would invite the reader to assume the Hub decides both.
 */
function Criteria({
  machine,
  human,
}: {
  machine: readonly CriterionDto[];
  human: readonly CriterionDto[];
}): ReactNode {
  if (machine.length === 0 && human.length === 0) {
    return <Empty title="No criteria declared" />;
  }

  return (
    <div className="stack" style={{ gap: 'var(--space-5)' }}>
      {machine.length > 0 ? (
        <section aria-label="Machine criteria">
          <div className="field__label">Machine · answered from evidence</div>
          <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
            {machine.map((criterion) => (
              <li key={criterion.id} className="row" style={{ alignItems: 'baseline' }}>
                <Badge
                  look={{
                    label: 'Machine',
                    tone: 'info',
                    shape: 'outline',
                    meaning: 'Resolved from recorded verification evidence.',
                  }}
                />
                <span>{criterion.text}</span>
                {criterion.verificationRef ? (
                  <span className="path">{criterion.verificationRef}</span>
                ) : null}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {human.length > 0 ? (
        <section aria-label="Human criteria">
          <div className="field__label">Human · answered by a person</div>
          <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
            {human.map((criterion) => (
              <li key={criterion.id} className="row" style={{ alignItems: 'baseline' }}>
                <Badge
                  look={{
                    label: 'Human',
                    tone: 'warn',
                    shape: 'outline',
                    meaning: 'Answered by a person at acceptance time.',
                  }}
                />
                <div className="stack">
                  <span>{criterion.text}</span>
                  {criterion.humanCheckHint ? (
                    <span className="muted">{criterion.humanCheckHint}</span>
                  ) : null}
                </div>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}

function Attempts({ attempts }: { attempts: readonly ExecutionSummaryDto[] }): ReactNode {
  return (
    <div className="table-scroll">
      <table className="table">
        <caption className="visually-hidden">Attempt history, newest first</caption>
        <thead>
          <tr>
            <th scope="col" className="table__num">
              #
            </th>
            <th scope="col">Outcome</th>
            <th scope="col">State</th>
            <th scope="col">Execution</th>
            <th scope="col">Started</th>
            <th scope="col">Took</th>
          </tr>
        </thead>
        <tbody>
          {attempts.map((attempt) => {
            const outcome = lookOfOutcome(attempt.outcome);

            return (
              <tr key={attempt.id}>
                <th scope="row" className="table__num" style={{ fontWeight: 400 }}>
                  <Link to={`/executions/${attempt.id}`}>{attempt.attemptNo}</Link>
                </th>
                <td>{outcome ? <Badge look={outcome} /> : <span className="muted">—</span>}</td>
                <td>
                  <Badge look={lookOfTaskState(attempt.state)} />
                </td>
                <td>
                  <Identifier value={attempt.id} label="execution id" />
                </td>
                <td>
                  <Timestamp value={attempt.createdAtUtc} />
                </td>
                <td>
                  {formatDuration(durationBetween(attempt.createdAtUtc, attempt.updatedAtUtc))}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Where the latest attempt stands with a person.
 *
 * Read-only: this says what the Hub is waiting for, and offers nothing to click. Deciding is
 * Phase 9D.
 */
function Acceptance({ acceptance }: { acceptance: AcceptanceStateDto }): ReactNode {
  return (
    <Panel
      title="Acceptance"
      hint={acceptance.existingDecision ? `Decided: ${acceptance.existingDecision}` : 'No decision yet'}
    >
      <div className="fields">
        <Field label="Attempt">#{acceptance.attemptNo}</Field>
        <Field label="Execution outcome">
          {lookOfOutcome(acceptance.executionOutcome) ? (
            <Badge look={lookOfOutcome(acceptance.executionOutcome)!} />
          ) : (
            <span className="muted">—</span>
          )}
        </Field>
        <Field label="Route">
          {acceptance.overrideApplicable ? (
            <Badge
              look={{
                label: 'Override applies',
                tone: 'warn',
                shape: 'striped',
                meaning:
                  'A review blocked this attempt, so accepting it would be an explicit override with a stated reason.',
              }}
            />
          ) : (
            <Badge
              look={{
                label: 'Ordinary',
                tone: 'neutral',
                shape: 'outline',
                meaning: 'The ordinary acceptance route applies to this attempt.',
              }}
            />
          )}
        </Field>
        <Field label="Human criteria pending">{acceptance.humanCriteria.length}</Field>
      </div>

      {acceptance.machineTruthProblems.length > 0 ? (
        <div className="timeline__reason" style={{ marginTop: 'var(--space-4)' }}>
          <strong>Machine truth could not be established.</strong>
          <ul style={{ margin: 'var(--space-2) 0 0', paddingLeft: '1.2em' }}>
            {acceptance.machineTruthProblems.map((problem) => (
              <li key={problem}>{problem}</li>
            ))}
          </ul>
        </div>
      ) : null}
    </Panel>
  );
}
