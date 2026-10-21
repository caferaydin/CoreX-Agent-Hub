import { useId, useState, type ReactNode } from 'react';
import { api } from '../api/client';
import { useMutation } from '../api/useMutation';
import { useQuery } from '../api/useQuery';
import type {
  AcceptanceResultDto,
  AcceptanceStateDto,
  CriterionDto,
  ExecutionOutcome,
  HumanCriterionDto,
  HumanVerdict,
  HumanVerdictRequest,
  ReviewDto,
  TaskDetailDto,
} from '../api/contracts';
import { ActionButton, ActionError, ActionNotice, ConfirmDialog } from '../components/actions';
import { Badge, Empty, Failed, Field, Loading, Panel, Timestamp } from '../components/primitives';
import { canAccept, canOfferOverride, canReject, canRequestChanges } from '../lib/actions';
import { OPERATOR_ACTOR } from '../lib/actor';
import { lookOfOutcome, lookOfSeverity, lookOfTaskState, severityRank } from '../lib/status';

/**
 * The one place a person decides.
 *
 * Four decisions, and they are four rather than one because the Hub treats them as four:
 * accepting, accepting despite a blocking review, sending work back and ending it have
 * different prerequisites and leave different records. A single control with a verb parameter
 * would make them look interchangeable, which is exactly what they are not.
 *
 * Two boundaries run through this component, and both are load-bearing.
 *
 * Machine truth is read; human truth is supplied. Verification verdicts, findings, severities
 * and the Hub's own scope computation are shown and cannot be touched here. The only things an
 * operator adds are a verdict on each Human criterion and, for an override, a reason.
 *
 * Accepting a task and what its execution was are different axes. An overridden task becomes
 * Accepted while its execution stays ReviewBlocked, and this panel renders both rather than
 * letting a decision quietly rewrite what the review found.
 *
 * What it deliberately does not contain: the acceptance service's prerequisites. This decides
 * which buttons are worth showing, never whether the Hub will agree.
 */
export function TaskGovernance({
  task,
  onDecided,
}: {
  task: TaskDetailDto;
  onDecided: () => void;
}): ReactNode {
  const state = task.summary.state;
  const outcome = task.summary.latestExecutionOutcome as ExecutionOutcome | null;

  const offers = {
    accept: canAccept(state),
    override: canOfferOverride(state, outcome),
    changes: canRequestChanges(state),
    reject: canReject(state),
  };

  const anything = offers.accept || offers.override || offers.changes || offers.reject;

  if (!anything) {
    // Decided, cancelled, or simply not at a point where a person is in the loop.
    return task.acceptance?.existingDecision ? (
      <RecordedDecision
        acceptance={task.acceptance}
        outcome={outcome}
        criteria={task.criteria}
      />
    ) : null;
  }

  return <Decide task={task} outcome={outcome} offers={offers} onDecided={onDecided} />;
}

interface Offers {
  readonly accept: boolean;
  readonly override: boolean;
  readonly changes: boolean;
  readonly reject: boolean;
}

type Decision = 'accept' | 'override' | 'changes' | 'reject';

// --- Decided ----------------------------------------------------------------------------------

/**
 * What was decided, once it has been.
 *
 * Read from the server on every load, not from what the mutation happened to return. A person
 * asking "who accepted this, when, and on what grounds" is almost never the person who
 * clicked, and almost never asking in the same minute - so a panel whose only source was
 * component state would be empty exactly when the question gets asked.
 *
 * Read-only, and offering nothing: a task that has been accepted must not present a second
 * Accept, and a rejected one must not present a way to un-reject it.
 */
function RecordedDecision({
  acceptance,
  outcome,
  criteria,
}: {
  acceptance: AcceptanceStateDto;
  outcome: ExecutionOutcome | null;
  criteria: readonly CriterionDto[];
}): ReactNode {
  const decision = acceptance.existingDecision;
  const executionLook = lookOfOutcome(outcome);

  if (decision === null) {
    return null;
  }

  // ADR-0028: a repository's autopilot may accept work its evidence fully answered. The row
  // says so, and so does this panel - an autopilot decision must never read as a person's.
  const byPolicy = decision.source === 'Policy';

  return (
    <Panel
      title={byPolicy ? 'Autopilot decision' : 'Human decision'}
      hint={
        decision.overrideUsed
          ? 'Recorded with a review override'
          : byPolicy
            ? 'Accepted by the repository’s autopilot'
            : 'Recorded'
      }
    >
      <div className="fields">
        <Field label="Decision">
          <Badge
            look={{
              label: decision.decision,
              tone: decision.decision === 'Reject' ? 'bad' : 'good',
              shape: 'solid',
              meaning: byPolicy
                ? 'The decision the repository’s autopilot recorded against this attempt.'
                : 'The decision a person recorded against this attempt.',
            }}
          />
        </Field>
        <Field label="Decided by">
          <Badge
            look={
              byPolicy
                ? {
                    label: 'Autopilot',
                    tone: 'info',
                    shape: 'striped',
                    meaning:
                      'Accepted by policy: no human criteria, every machine criterion met, review clean.',
                  }
                : {
                    label: 'Person',
                    tone: 'neutral',
                    shape: 'outline',
                    meaning: 'A person recorded this decision.',
                  }
            }
          />
        </Field>
        <Field label="Task">
          <Badge look={lookOfTaskState(acceptance.taskState)} />
        </Field>
        <Field label="Attempt">#{acceptance.attemptNo}</Field>
        <Field label="Execution outcome">
          {/* Shown so it is visible rather than assumed: a decision does not change what the
              execution was, and an overridden one is still ReviewBlocked. */}
          {executionLook ? <Badge look={executionLook} /> : <span className="muted">—</span>}
        </Field>
        <Field label="By">{decision.actor}</Field>
        <Field label="When">
          <Timestamp value={decision.decidedAtUtc} />
        </Field>
        <Field label="Review override">
          {decision.overrideUsed ? (
            <Badge
              look={{
                label: 'Used',
                tone: 'warn',
                shape: 'striped',
                meaning:
                  'This attempt was accepted despite a blocking review, with a stated reason.',
              }}
            />
          ) : (
            <span className="muted">No</span>
          )}
        </Field>
      </div>

      {decision.reason ? (
        <div className="form__field" style={{ marginTop: 'var(--space-5)' }}>
          <div className="field__label">
            {decision.overrideUsed ? 'Why this was accepted anyway' : 'Reason'}
          </div>
          {/* Text, always. What a person typed is rendered as what they typed. */}
          <p className="timeline__reason" style={{ margin: 0 }}>
            {decision.reason}
          </p>
        </div>
      ) : null}

      {decision.humanVerdicts.length > 0 ? (
        <section aria-label="Recorded verdicts" style={{ marginTop: 'var(--space-5)' }}>
          <div className="field__label">What the person answered</div>
          <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
            {decision.humanVerdicts.map((verdict) => (
              <li key={verdict.criterionId} className="row" style={{ alignItems: 'baseline' }}>
                <Badge
                  look={{
                    label: labelOf(verdict.verdict),
                    tone: verdict.verdict === 'Met' ? 'good' : verdict.verdict === 'NotMet' ? 'bad' : 'neutral',
                    shape: 'outline',
                    meaning: 'The verdict recorded with the decision.',
                  }}
                />
                <div className="stack">
                  <span>{textOf(criteria, verdict.criterionId)}</span>
                  {verdict.note ? <span className="muted">{verdict.note}</span> : null}
                </div>
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {/* What was accepted anyway. The override form shows the findings while a person
          decides, and the same argument holds for whoever reads the decision later: an
          override whose findings are two clicks away is an override nobody audits. Read-only
          here as everywhere - shown from the execution, never restated by the decision. */}
      {decision.overrideUsed ? (
        <div style={{ marginTop: 'var(--space-5)' }}>
          <WhatWasFound executionId={acceptance.executionId} />
        </div>
      ) : null}
    </Panel>
  );
}

/**
 * The criterion a verdict answered, by its own text where the task still declares it.
 *
 * A decision outlives an edit to the task it was about, so the identifier is what is
 * guaranteed and the sentence is what is useful. Where the two disagree the identifier wins
 * and is shown, rather than a sentence that may no longer be the one that was answered.
 */
const textOf = (criteria: readonly CriterionDto[], criterionId: string): string =>
  criteria.find((criterion) => criterion.id === criterionId)?.text ?? criterionId;

// --- Deciding ---------------------------------------------------------------------------------

function Decide({
  task,
  outcome,
  offers,
  onDecided,
}: {
  task: TaskDetailDto;
  outcome: ExecutionOutcome | null;
  offers: Offers;
  onDecided: () => void;
}): ReactNode {
  const taskKey = task.summary.taskKey;
  const acceptance = task.acceptance;
  const attemptNo = acceptance?.attemptNo ?? task.attempts[0]?.attemptNo ?? 0;
  const humanCriteria = acceptance?.humanCriteria ?? [];

  // No default verdict. An operator answering a Human criterion has to say so; a form that
  // arrived pre-answered would decide for them and then record their name against it.
  const [verdicts, setVerdicts] = useState<ReadonlyMap<string, HumanVerdict>>(new Map());
  const [notes, setNotes] = useState<ReadonlyMap<string, string>>(new Map());
  const [reason, setReason] = useState('');
  const [showProblems, setShowProblems] = useState(false);
  const [confirming, setConfirming] = useState<Decision | null>(null);
  const [recorded, setRecorded] = useState<AcceptanceResultDto | null>(null);

  // Four mutations, never retried, each sent at most once per deliberate confirmation.
  const accept = useMutation(api.acceptTask);
  const override = useMutation(api.overrideReviewBlocked);
  const changes = useMutation(api.requestChanges);
  const reject = useMutation(api.rejectTask);

  const busy = accept.submitting || override.submitting || changes.submitting || reject.submitting;
  const answered = humanCriteria.every((c) => verdicts.has(c.criterionId));
  const reasonGiven = reason.trim().length > 0;

  const built = (): readonly HumanVerdictRequest[] =>
    humanCriteria.map((criterion) => ({
      criterionId: criterion.criterionId,
      verdict: verdicts.get(criterion.criterionId)!,
      note: notes.get(criterion.criterionId)?.trim() || null,
    }));

  /**
   * Checks the form is filled in, then asks.
   *
   * Form completeness only. Whether this attempt may be accepted at all - the several things
   * the acceptance service weighs - is asked by sending the request and reading the answer.
   */
  const ask = (decision: Decision): void => {
    setShowProblems(true);

    if ((decision === 'accept' || decision === 'override') && !answered) {
      return;
    }

    if (decision === 'override' && !reasonGiven) {
      return;
    }

    setConfirming(decision);
  };

  const dispatch = async (decision: Decision): Promise<AcceptanceResultDto | null> => {
    switch (decision) {
      case 'accept':
        return accept.submit(taskKey, attemptNo, { actor: OPERATOR_ACTOR, humanVerdicts: built() });
      case 'override':
        return override.submit(taskKey, attemptNo, {
          actor: OPERATOR_ACTOR,
          reason: reason.trim(),
          humanVerdicts: built(),
        });
      case 'changes':
        return changes.submit(taskKey, attemptNo, {
          actor: OPERATOR_ACTOR,
          reason: reason.trim() || null,
        });
      case 'reject':
        return reject.submit(taskKey, attemptNo, {
          actor: OPERATOR_ACTOR,
          reason: reason.trim() || null,
        });
    }
  };

  const confirm = async (): Promise<void> => {
    const decision = confirming;

    if (decision === null) {
      return;
    }

    const result = await dispatch(decision);

    setConfirming(null);

    if (result) {
      // Held because this is the only moment the override reason and the resulting execution
      // outcome are available together; a later load carries only the decision kind.
      setRecorded(result);
    }

    // Refused or recorded, the durable answer is the server's. Ask it again either way: a
    // refusal usually means this page was looking at a state that has since moved on.
    onDecided();
  };

  const failure = firstFailure([accept.state, override.state, changes.state, reject.state]);

  return (
    <Panel
      title="Human decision"
      hint={offers.override ? 'A review blocked this attempt' : 'The Hub is waiting for a person'}
    >
      {/* Before anything is clicked: where this task stands, and how much of it is a person's
          to answer. */}
      <div className="fields">
        <Field label="Task">
          <Badge look={lookOfTaskState(task.summary.state)} />
        </Field>
        <Field label="Attempt">#{attemptNo}</Field>
        <Field label="Execution outcome">
          {lookOfOutcome(outcome) ? (
            <Badge look={lookOfOutcome(outcome)!} />
          ) : (
            <span className="muted">—</span>
          )}
        </Field>
        <Field label="Human criteria">
          {humanCriteria.length === 0 ? 'None' : `${verdicts.size} of ${humanCriteria.length}`}
        </Field>
      </div>

      <div className="stack" style={{ gap: 'var(--space-5)', marginTop: 'var(--space-5)' }}>
        {acceptance && acceptance.machineTruthProblems.length > 0 ? (
          <div className="notice notice--error" role="alert">
            <p className="notice__message">
              The Hub could not establish what the machine did for this attempt, and will refuse
              a decision until it can.
            </p>
            <ul className="notice__details">
              {acceptance.machineTruthProblems.map((problem) => (
                <li key={problem}>{problem}</li>
              ))}
            </ul>
          </div>
        ) : null}

        {/* Read-only, deliberately. Machine truth is evidence, and nothing here can change it. */}
        {acceptance && acceptance.machineCriteria.length > 0 ? (
          <MachineCriteria acceptance={acceptance} />
        ) : null}

        {offers.override ? <WhatWasFound executionId={task.summary.latestExecutionId} /> : null}

        {humanCriteria.length > 0 ? (
          <HumanCriteria
            criteria={humanCriteria}
            verdicts={verdicts}
            notes={notes}
            disabled={busy}
            problem={showProblems && !answered}
            onVerdict={(id, verdict) => setVerdicts(new Map(verdicts).set(id, verdict))}
            onNote={(id, note) => setNotes(new Map(notes).set(id, note))}
          />
        ) : null}

        {offers.override ? (
          <OverrideReason
            value={reason}
            onChange={setReason}
            disabled={busy}
            problem={showProblems && !reasonGiven}
          />
        ) : null}
      </div>

      <div className="actions" style={{ marginTop: 'var(--space-5)' }}>
        {offers.accept ? (
          <ActionButton
            variant="primary"
            busy={accept.submitting}
            busyLabel="Accepting…"
            disabled={busy}
            onClick={() => ask('accept')}
          >
            Accept
          </ActionButton>
        ) : null}

        {/* Never labelled just "Accept". The word an operator reads has to say what is
            different about this one. */}
        {offers.override ? (
          <ActionButton
            variant="danger"
            busy={override.submitting}
            busyLabel="Accepting with override…"
            disabled={busy}
            onClick={() => ask('override')}
          >
            Accept with review override
          </ActionButton>
        ) : null}

        {offers.changes ? (
          <ActionButton
            busy={changes.submitting}
            busyLabel="Sending back…"
            disabled={busy}
            onClick={() => ask('changes')}
          >
            Request changes
          </ActionButton>
        ) : null}

        {offers.reject ? (
          <ActionButton
            variant="danger"
            busy={reject.submitting}
            busyLabel="Rejecting…"
            disabled={busy}
            onClick={() => ask('reject')}
          >
            Reject
          </ActionButton>
        ) : null}
      </div>

      {/* A refusal, in the Hub's own words. Nothing typed is cleared - an operator who had to
          re-enter their override reason to read why it was refused would write a shorter one. */}
      {failure ? <ActionError error={failure} /> : null}

      {recorded ? <Recorded result={recorded} /> : null}

      {confirming ? (
        <Confirmation
          decision={confirming}
          taskKey={taskKey}
          attemptNo={attemptNo}
          verdictCount={humanCriteria.length}
          busy={busy}
          onConfirm={confirm}
          onDismiss={() => setConfirming(null)}
        />
      ) : null}
    </Panel>
  );
}

const firstFailure = (states: readonly { status: string; error: unknown }[]): unknown =>
  states.find((state) => state.status === 'failed')?.error ?? null;

// --- Criteria ---------------------------------------------------------------------------------

/** What the machine found. Shown, never edited - evidence answered these, not a person. */
function MachineCriteria({ acceptance }: { acceptance: AcceptanceStateDto }): ReactNode {
  return (
    <section aria-label="Machine criteria">
      <div className="field__label">Machine · answered from evidence</div>
      <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
        {acceptance.machineCriteria.map((criterion) => (
          <li key={criterion.criterionId} className="row" style={{ alignItems: 'baseline' }}>
            <Badge
              look={{
                label: criterion.verdict,
                tone: criterion.verdict === 'Met' ? 'good' : 'bad',
                shape: 'solid',
                meaning: `The recorded verification says ${criterion.verificationStatus}.`,
              }}
            />
            <span>{criterion.text}</span>
            <span className="path">{criterion.verificationRef}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

/**
 * What a person has to answer.
 *
 * Radio groups rather than a select: the three answers are peers, and an operator should see
 * all of them without opening anything. Nothing is checked when the form loads, so an
 * unanswered criterion looks unanswered.
 */
function HumanCriteria({
  criteria,
  verdicts,
  notes,
  disabled,
  problem,
  onVerdict,
  onNote,
}: {
  criteria: readonly HumanCriterionDto[];
  verdicts: ReadonlyMap<string, HumanVerdict>;
  notes: ReadonlyMap<string, string>;
  disabled: boolean;
  problem: boolean;
  onVerdict: (criterionId: string, verdict: HumanVerdict) => void;
  onNote: (criterionId: string, note: string) => void;
}): ReactNode {
  const group = useId();

  return (
    <section aria-label="Human criteria">
      <div className="field__label">Human · answered by you</div>

      <div className="stack" style={{ gap: 'var(--space-4)' }}>
        {criteria.map((criterion) => (
          <fieldset key={criterion.criterionId} className="verdict">
            <legend className="verdict__text">{criterion.text}</legend>

            {criterion.humanCheckHint ? (
              <p className="form__hint" style={{ margin: 0 }}>
                {criterion.humanCheckHint}
              </p>
            ) : null}

            <div className="verdict__options">
              {VERDICTS.map((verdict) => (
                <label className="verdict__option" key={verdict}>
                  <input
                    type="radio"
                    name={`${group}-${criterion.criterionId}`}
                    value={verdict}
                    disabled={disabled}
                    checked={verdicts.get(criterion.criterionId) === verdict}
                    onChange={() => onVerdict(criterion.criterionId, verdict)}
                  />
                  {labelOf(verdict)}
                </label>
              ))}
            </div>

            <input
              type="text"
              placeholder="Note (optional)"
              disabled={disabled}
              value={notes.get(criterion.criterionId) ?? ''}
              aria-label={`Note for: ${criterion.text}`}
              onChange={(event) => onNote(criterion.criterionId, event.target.value)}
            />
          </fieldset>
        ))}
      </div>

      {problem ? (
        <p className="form__error" role="alert" style={{ marginTop: 'var(--space-3)' }}>
          Answer every criterion before deciding.
        </p>
      ) : null}
    </section>
  );
}

const VERDICTS = ['Met', 'NotMet', 'NotApplicable'] as const;

const labelOf = (verdict: string): string =>
  verdict === 'NotMet' ? 'Not met' : verdict === 'NotApplicable' ? 'Not applicable' : verdict;

// --- Override ---------------------------------------------------------------------------------

/**
 * What is being overridden - or, once the decision is recorded, what was.
 *
 * Shown expanded, never behind a disclosure. Someone about to accept work despite a review
 * should be reading what the review said while they decide, and a collapsed panel is a
 * collapsed panel nobody opens. The recorded view reuses this for the same reason.
 *
 * The summary here is the server's own sentence. Which findings block is the Hub's single
 * severity authority, and a frontend that recomputed it from a threshold would be a second.
 */
function WhatWasFound({ executionId }: { executionId: string | null }): ReactNode {
  const { state } = useQuery((signal) => api.review(executionId!, signal), [executionId], {
    enabled: executionId !== null,
  });

  if (executionId === null) {
    return null;
  }

  return (
    <section aria-label="Review findings">
      <div className="field__label">What the review found</div>

      {state.status === 'loading' ? <Loading label="Loading findings" /> : null}
      {state.status === 'error' ? <Failed error={state.error} /> : null}
      {state.status === 'ready' ? <Findings review={state.data} /> : null}
    </section>
  );
}

function Findings({ review }: { review: ReviewDto }): ReactNode {
  const inScope = [...review.findings]
    .filter((finding) => !finding.outOfScope)
    .sort((a, b) => severityRank(a.severity) - severityRank(b.severity));

  return (
    <>
      <p className="form__hint" style={{ margin: '0 0 var(--space-3)' }}>
        {review.summary}
      </p>

      {inScope.length === 0 ? (
        <Empty title="No in-scope findings" />
      ) : (
        <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
          {inScope.map((finding) => (
            <li key={finding.id} className="finding">
              <div className="finding__head">
                <Badge look={lookOfSeverity(finding.severity)} />
                <span className="finding__title">{finding.title}</span>
                {finding.file ? (
                  <span className="finding__where">
                    {finding.file}
                    {finding.line === null ? '' : `:${finding.line}`}
                  </span>
                ) : null}
              </div>
              {/* Text, always. A finding is provider output and is never rendered as markup. */}
              {finding.rationale ? <p className="finding__body">{finding.rationale}</p> : null}
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

/** Why. Required, and deliberately empty when the form loads. */
function OverrideReason({
  value,
  onChange,
  disabled,
  problem,
}: {
  value: string;
  onChange: (value: string) => void;
  disabled: boolean;
  problem: boolean;
}): ReactNode {
  const id = useId();

  return (
    <div className="form__field">
      <label className="form__label" htmlFor={id}>
        Why this is being accepted anyway
      </label>
      {/* No template, no suggested phrasing and no menu of stock justifications. A prefilled
          "false positive" is a reason the Hub would record against a person who never wrote
          it. */}
      <textarea
        id={id}
        value={value}
        disabled={disabled}
        style={{ minHeight: '5rem' }}
        aria-invalid={problem}
        aria-describedby={problem ? `${id}-hint ${id}-problem` : `${id}-hint`}
        onChange={(event) => onChange(event.target.value)}
      />
      <p className="form__hint" id={`${id}-hint`}>
        Recorded with the decision. The Hub requires one and refuses a blank.
      </p>
      {problem ? (
        <p className="form__error" id={`${id}-problem`} role="alert">
          An override needs a reason; it may never be silent.
        </p>
      ) : null}
    </div>
  );
}

// --- Confirmation -----------------------------------------------------------------------------

/**
 * The last thing before a decision is recorded.
 *
 * Each one says what the decision does and, more usefully, what it does not: accepting is
 * governance, not integration, and an operator who assumed otherwise would be waiting for a
 * deployment that is never coming.
 */
function Confirmation({
  decision,
  taskKey,
  attemptNo,
  verdictCount,
  busy,
  onConfirm,
  onDismiss,
}: {
  decision: Decision;
  taskKey: string;
  attemptNo: number;
  verdictCount: number;
  busy: boolean;
  onConfirm: () => void;
  onDismiss: () => void;
}): ReactNode {
  const summary = (
    <p className="muted" style={{ marginTop: 'var(--space-4)' }}>
      {taskKey} · attempt {attemptNo}
      {verdictCount > 0 ? ` · ${verdictCount} human verdict${verdictCount === 1 ? '' : 's'}` : ''}
    </p>
  );

  const nothingShips = <p>This records a decision. It does not merge, push or deploy anything.</p>;

  if (decision === 'accept') {
    return (
      <ConfirmDialog
        open
        title="Accept this attempt?"
        confirmLabel="Accept"
        dismissLabel="Not yet"
        tone="primary"
        busy={busy}
        busyLabel="Accepting…"
        onConfirm={onConfirm}
        onDismiss={onDismiss}
      >
        <p style={{ marginTop: 0 }}>
          Your verdicts on the human criteria are recorded with the decision, and the task
          becomes <strong>Accepted</strong>.
        </p>
        {nothingShips}
        {summary}
      </ConfirmDialog>
    );
  }

  if (decision === 'override') {
    return (
      <ConfirmDialog
        open
        title="Accept despite the blocking review?"
        confirmLabel="Accept with review override"
        dismissLabel="Not yet"
        busy={busy}
        busyLabel="Accepting with override…"
        onConfirm={onConfirm}
        onDismiss={onDismiss}
      >
        <p style={{ marginTop: 0 }}>
          An independent review blocked this attempt. You are accepting it anyway, and your
          reason is recorded and auditable.
        </p>
        <p>
          The findings are kept exactly as they are and the execution stays{' '}
          <strong>Review blocked</strong>. Overriding accepts the work; it does not change what
          the review found.
        </p>
        {nothingShips}
        {summary}
      </ConfirmDialog>
    );
  }

  if (decision === 'changes') {
    return (
      <ConfirmDialog
        open
        title="Send this back for changes?"
        confirmLabel="Request changes"
        dismissLabel="Not yet"
        busy={busy}
        busyLabel="Sending back…"
        onConfirm={onConfirm}
        onDismiss={onDismiss}
      >
        <p style={{ marginTop: 0 }}>
          The task returns to <strong>Ready</strong>. This attempt stays exactly where it is -
          its result commit, findings and evidence remain the historical record - and running
          the task again starts a new attempt beside it.
        </p>
        {summary}
      </ConfirmDialog>
    );
  }

  return (
    <ConfirmDialog
      open
      title="Reject this task?"
      confirmLabel="Reject"
      dismissLabel="Not yet"
      busy={busy}
      busyLabel="Rejecting…"
      onConfirm={onConfirm}
      onDismiss={onDismiss}
    >
      <p style={{ marginTop: 0 }}>
        The task becomes <strong>Rejected</strong> and is finished with. Nothing is deleted: the
        attempt, its result commit, its findings and its evidence all remain.
      </p>
      {nothingShips}
      {summary}
    </ConfirmDialog>
  );
}

/**
 * What the Hub just recorded, in its own answer's words.
 *
 * A courtesy and not the record. The durable version arrives with the refetch this decision
 * triggered and is what the panel shows from then on, including after a reload - this exists
 * so the operator gets an answer in the same instant they clicked rather than one render
 * later.
 */
function Recorded({ result }: { result: AcceptanceResultDto }): ReactNode {
  return (
    <ActionNotice>
      Recorded: {result.decision}
      {result.overrideUsed ? ' with a review override' : ''}. The task is {result.taskState}, and
      the execution is still {result.executionOutcome ?? result.executionState}.
      {result.reason ? ` Reason: ${result.reason}` : ''}
    </ActionNotice>
  );
}
