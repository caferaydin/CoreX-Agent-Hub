import { useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import { useMutation } from '../api/useMutation';
import { useQuery } from '../api/useQuery';
import type { CancellationResultDto, TaskDetailDto } from '../api/contracts';
import { ActionButton, ActionError, ActionNotice, ConfirmDialog } from '../components/actions';
import { Panel } from '../components/primitives';
import { canCancel, canMarkReady, canRun, isTerminalTask } from '../lib/actions';
import { OPERATOR_ACTOR } from '../lib/actor';

/**
 * What an operator can do to a task, kept apart from what the page shows about it.
 *
 * A separate component rather than buttons sprinkled through the read panels, because those
 * panels are projections of server truth and should stay that way: adding a mutation to the
 * overview would make a component that renders a fact also a component that changes it.
 *
 * Which buttons appear is decided by the coarse predicates in lib/actions - one look at the
 * task's state and nothing else. The Hub decides whether any of them actually happen, and
 * every refusal it gives is shown here verbatim rather than being pre-empted.
 */
export function TaskActions({
  task,
  onChanged,
}: {
  task: TaskDetailDto;
  onChanged: () => void;
}): ReactNode {
  const navigate = useNavigate();
  const state = task.summary.state;
  const taskKey = task.summary.taskKey;

  const ready = useMutation(api.markTaskReady);
  // Once: a run that started is an attempt. The Hub's single-execution slot refuses a second
  // one anyway, but a refusal an operator did not mean to cause is still noise.
  const run = useMutation(api.runTask, { once: true });
  const cancel = useMutation(api.cancelTask);

  const [confirming, setConfirming] = useState(false);
  const [reason, setReason] = useState('');
  const [cancelled, setCancelled] = useState<CancellationResultDto | null>(null);

  // Only the profiles that could implement. Read from the API rather than named here: this
  // file does not know that any particular provider exists.
  const profiles = useQuery((signal) => api.agentProfiles(signal), [], {
    enabled: canRun(state),
  });

  const implementers =
    profiles.state.status === 'ready'
      ? profiles.state.data.filter((profile) => profile.role === 'Implementer')
      : [];

  const [profile, setProfile] = useState('');
  const chosenProfile = profile || implementers[0]?.name || '';

  if (isTerminalTask(state)) {
    return null;
  }

  const markReady = async (): Promise<void> => {
    const result = await ready.submit(taskKey, OPERATOR_ACTOR);

    // Whether it transitioned or the gate held it back, the durable answer is on the server.
    if (result) {
      onChanged();
    }
  };

  const start = async (): Promise<void> => {
    const started = await run.submit(taskKey, OPERATOR_ACTOR, chosenProfile);

    // A 202 with an identity. The run continues without this page; the execution page follows
    // it from durable state and its own stream.
    if (started) {
      navigate(`/executions/${started.executionId}`);
    }
  };

  const requestCancellation = async (): Promise<void> => {
    const result = await cancel.submit(
      taskKey,
      OPERATOR_ACTOR,
      reason.trim().length > 0 ? reason.trim() : null,
    );

    setConfirming(false);

    if (result) {
      // Held so the operator sees which of the two answers they got - the distinction between
      // "asked to stop" and "stopped" is the whole reason this is not a boolean.
      setCancelled(result);
      onChanged();
    }
  };

  return (
    <Panel title="Actions" hint="The Hub decides whether any of these happen">
      <div className="actions">
        {canMarkReady(state) ? (
          <ActionButton
            variant="primary"
            busy={ready.submitting}
            busyLabel="Marking ready…"
            onClick={markReady}
          >
            Mark ready
          </ActionButton>
        ) : null}

        {canRun(state) ? (
          <>
            {implementers.length > 1 ? (
              <label className="row" style={{ gap: 'var(--space-2)' }}>
                <span className="muted">Implementer</span>
                <select
                  value={chosenProfile}
                  disabled={run.submitting}
                  onChange={(event) => setProfile(event.target.value)}
                  aria-label="Implementer profile"
                >
                  {implementers.map((candidate) => (
                    <option key={candidate.name} value={candidate.name}>
                      {candidate.name}
                      {candidate.model ? ` · ${candidate.model}` : ''}
                    </option>
                  ))}
                </select>
              </label>
            ) : null}

            <ActionButton
              variant="primary"
              busy={run.submitting}
              busyLabel="Starting…"
              disabled={chosenProfile.length === 0}
              onClick={start}
            >
              Run
            </ActionButton>
          </>
        ) : null}

        {canCancel(state) ? (
          <ActionButton
            variant="danger"
            busy={cancel.submitting}
            busyLabel="Requesting cancellation…"
            onClick={() => setConfirming(true)}
          >
            Cancel task
          </ActionButton>
        ) : null}
      </div>

      {canRun(state) && implementers.length === 0 && profiles.state.status === 'ready' ? (
        <ActionNotice>
          This organization declares no implementer profile, so there is nothing to run the task
          with.
        </ActionNotice>
      ) : null}

      {/* Every refusal, in the Hub's own words. */}
      {ready.state.status === 'failed' ? <ActionError error={ready.state.error} /> : null}
      {run.state.status === 'failed' ? <ActionError error={run.state.error} /> : null}
      {cancel.state.status === 'failed' ? <ActionError error={cancel.state.error} /> : null}

      {ready.state.status === 'done' && !ready.state.data.transitioned ? (
        <ActionNotice>
          The ready gate did not let this through. What it is waiting for is shown above.
        </ActionNotice>
      ) : null}

      {cancelled ? <CancellationOutcome result={cancelled} /> : null}

      <ConfirmDialog
        open={confirming}
        title="Cancel this task?"
        confirmLabel="Cancel task"
        busy={cancel.submitting}
        busyLabel="Requesting cancellation…"
        onConfirm={requestCancellation}
        onDismiss={() => setConfirming(false)}
      >
        <p style={{ marginTop: 0 }}>
          If an attempt is running, the Hub asks the process driving it to stop and then waits to
          see that it did. That takes as long as it takes, and it is not guaranteed to succeed -
          the task is only recorded as cancelled once the work has really stopped.
        </p>
        <p>Work already committed by an earlier attempt is kept.</p>
        <label className="form__field" style={{ marginTop: 'var(--space-4)' }}>
          <span className="form__label">Reason (optional)</span>
          <input
            type="text"
            value={reason}
            disabled={cancel.submitting}
            placeholder="Why this is being stopped"
            onChange={(event) => setReason(event.target.value)}
          />
        </label>
      </ConfirmDialog>
    </Panel>
  );
}

/**
 * What the cancellation actually achieved.
 *
 * Three answers, kept apart. `CancellationRequested` arrives as a 200 and explicitly does not
 * mean the task is finished - reporting it as "Cancelled" would be a claim about a process
 * that is demonstrably still running.
 */
function CancellationOutcome({ result }: { result: CancellationResultDto }): ReactNode {
  if (result.state === 'Cancelled') {
    return (
      <ActionNotice>
        Cancelled. {result.detail}
      </ActionNotice>
    );
  }

  return (
    <ActionNotice>
      Cancellation requested — the run was asked to stop and had not finished stopping when the
      Hub answered. The task is still {result.taskState}. {result.detail}
    </ActionNotice>
  );
}
