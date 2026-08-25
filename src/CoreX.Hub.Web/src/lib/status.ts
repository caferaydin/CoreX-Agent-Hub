import type {
  ExecutionOutcome,
  ExecutionState,
  ExecutionStepState,
  FindingSeverity,
  TaskState,
} from '../api/contracts';

/**
 * What a state looks like, decided once.
 *
 * The reason this is a table and not a switch inside each component is that these meanings are
 * operational, not decorative. "ReviewBlocked" is a reviewer that found something;
 * "ReviewUnusable" is a review nobody could obtain; "VerificationFailed" is the machine checks
 * failing. Three different things to do next. A component that rendered them from its own
 * switch would eventually collapse two of them into red-and-failed, and the operator would
 * lose the distinction at the moment it mattered.
 *
 * `tone` drives colour. `shape` and the label carry the same information without it, because
 * an operator who cannot distinguish red from amber still has to be able to read this screen.
 */
export type Tone = 'neutral' | 'info' | 'running' | 'good' | 'warn' | 'bad' | 'muted';

/** A second channel beside colour: outline, solid and striped are distinguishable without it. */
export type Shape = 'outline' | 'solid' | 'striped';

export interface StatusLook {
  readonly label: string;
  readonly tone: Tone;
  readonly shape: Shape;

  /** Said out loud where the badge alone is not enough - tooltips and screen readers. */
  readonly meaning: string;
}

const taskStates: Record<TaskState, StatusLook> = {
  Draft: {
    label: 'Draft',
    tone: 'muted',
    shape: 'outline',
    meaning: 'Not yet offered for work.',
  },
  Ready: {
    label: 'Ready',
    tone: 'info',
    shape: 'outline',
    meaning: 'The ready gate is satisfied and an attempt may be started.',
  },
  Running: {
    label: 'Running',
    tone: 'running',
    shape: 'striped',
    meaning: 'An attempt is in flight.',
  },
  AwaitingAcceptance: {
    label: 'Awaiting acceptance',
    tone: 'warn',
    shape: 'solid',
    meaning: 'The machine has finished; a person still has to decide.',
  },
  Accepted: {
    label: 'Accepted',
    tone: 'good',
    shape: 'solid',
    meaning: 'A person accepted this work.',
  },
  Rejected: {
    label: 'Rejected',
    tone: 'bad',
    shape: 'solid',
    meaning: 'A person ended this task without accepting it.',
  },
  Cancelled: {
    label: 'Cancelled',
    tone: 'neutral',
    shape: 'solid',
    meaning: 'An operator stopped this task.',
  },
};

const executionStates: Record<ExecutionState, StatusLook> = {
  Created: { label: 'Created', tone: 'muted', shape: 'outline', meaning: 'The attempt exists.' },
  PreparingWorkspace: {
    label: 'Preparing workspace',
    tone: 'running',
    shape: 'striped',
    meaning: 'Isolating a worktree for the attempt.',
  },
  BuildingContext: {
    label: 'Building context',
    tone: 'running',
    shape: 'striped',
    meaning: 'Assembling what the agent is allowed to see.',
  },
  RunningAgent: {
    label: 'Running agent',
    tone: 'running',
    shape: 'striped',
    meaning: 'The implementer is working.',
  },
  CapturingChanges: {
    label: 'Capturing changes',
    tone: 'running',
    shape: 'striped',
    meaning: 'Recording what the agent changed.',
  },
  Verifying: {
    label: 'Verifying',
    tone: 'running',
    shape: 'striped',
    meaning: 'Running the repository’s own checks.',
  },
  Reviewing: {
    label: 'Reviewing',
    tone: 'running',
    shape: 'striped',
    meaning: 'An independent reviewer is inspecting the result.',
  },
  Completed: {
    label: 'Completed',
    tone: 'neutral',
    shape: 'solid',
    meaning: 'The attempt finished. Its outcome says how.',
  },
  Failed: { label: 'Failed', tone: 'bad', shape: 'solid', meaning: 'The attempt did not finish.' },
  Cancelled: {
    label: 'Cancelled',
    tone: 'neutral',
    shape: 'solid',
    meaning: 'The attempt was stopped.',
  },
};

const outcomes: Record<ExecutionOutcome, StatusLook> = {
  Passed: {
    label: 'Passed',
    tone: 'good',
    shape: 'solid',
    meaning: 'Verification passed and the review found nothing that blocks.',
  },
  VerificationFailed: {
    label: 'Verification failed',
    tone: 'bad',
    shape: 'solid',
    meaning: 'The repository’s own checks failed. No review was attempted.',
  },
  ReviewBlocked: {
    label: 'Review blocked',
    tone: 'warn',
    shape: 'solid',
    meaning: 'The review succeeded and found something that blocks acceptance.',
  },
  ReviewUnusable: {
    label: 'Review unusable',
    tone: 'warn',
    shape: 'striped',
    meaning: 'No usable review could be obtained. This is not a review that found nothing.',
  },
  ScopeViolation: {
    label: 'Scope violation',
    tone: 'bad',
    shape: 'solid',
    meaning: 'The agent changed something outside the declared scope.',
  },
  SecretGuardBlocked: {
    label: 'Secret guard blocked',
    tone: 'bad',
    shape: 'solid',
    meaning: 'Something that looked like a credential was about to be committed.',
  },
  RuntimePolicyViolation: {
    label: 'Runtime policy violation',
    tone: 'bad',
    shape: 'solid',
    meaning: 'The run broke a rule the Hub enforces about how agents may run.',
  },
  Failed: { label: 'Failed', tone: 'bad', shape: 'solid', meaning: 'The attempt failed.' },
  TimedOut: {
    label: 'Timed out',
    tone: 'bad',
    shape: 'solid',
    meaning: 'The attempt ran past its deadline and was stopped.',
  },
  Cancelled: {
    label: 'Cancelled',
    tone: 'neutral',
    shape: 'solid',
    meaning: 'The attempt was stopped by an operator.',
  },
  Interrupted: {
    label: 'Interrupted',
    tone: 'warn',
    shape: 'striped',
    meaning: 'The process driving this attempt disappeared. A later run reconciled it.',
  },
};

const stepStates: Record<ExecutionStepState, StatusLook> = {
  Pending: { label: 'Pending', tone: 'muted', shape: 'outline', meaning: 'Not started.' },
  Running: { label: 'Running', tone: 'running', shape: 'striped', meaning: 'In progress.' },
  Succeeded: { label: 'Succeeded', tone: 'good', shape: 'solid', meaning: 'Finished as asked.' },
  Failed: { label: 'Failed', tone: 'bad', shape: 'solid', meaning: 'Did not finish as asked.' },
  TimedOut: { label: 'Timed out', tone: 'bad', shape: 'solid', meaning: 'Ran past its deadline.' },
  Cancelled: { label: 'Cancelled', tone: 'neutral', shape: 'solid', meaning: 'Stopped.' },
  Skipped: {
    label: 'Skipped',
    tone: 'muted',
    shape: 'outline',
    meaning: 'Not run, for a reason the step records.',
  },
  Unsupported: {
    label: 'Unsupported',
    tone: 'warn',
    shape: 'outline',
    meaning: 'The runtime declared it cannot do this. Not a failure - a capability gap.',
  },
};

const severities: Record<FindingSeverity, StatusLook> = {
  Critical: {
    label: 'Critical',
    tone: 'bad',
    shape: 'solid',
    meaning: 'Blocks acceptance on its own.',
  },
  Major: {
    label: 'Major',
    tone: 'warn',
    shape: 'solid',
    meaning: 'Blocks acceptance once the threshold is reached.',
  },
  Minor: { label: 'Minor', tone: 'info', shape: 'outline', meaning: 'Worth knowing; not blocking.' },
  Info: { label: 'Info', tone: 'muted', shape: 'outline', meaning: 'An observation.' },
};

/**
 * What to show for a value the server sent that this build has never heard of.
 *
 * Shown as itself rather than as "Unknown": a state nobody has taught the UI about is still
 * information, and hiding it behind a placeholder would be worse than showing the raw word.
 */
const unknown = (value: string): StatusLook => ({
  label: value,
  tone: 'neutral',
  shape: 'outline',
  meaning: 'This build of the UI does not have a description for this value.',
});

export const lookOfTaskState = (state: string): StatusLook =>
  taskStates[state as TaskState] ?? unknown(state);

export const lookOfExecutionState = (state: string): StatusLook =>
  executionStates[state as ExecutionState] ?? unknown(state);

export const lookOfOutcome = (outcome: string | null): StatusLook | null =>
  outcome === null ? null : (outcomes[outcome as ExecutionOutcome] ?? unknown(outcome));

export const lookOfStepState = (state: string): StatusLook =>
  stepStates[state as ExecutionStepState] ?? unknown(state);

export const lookOfSeverity = (severity: string): StatusLook =>
  severities[severity as FindingSeverity] ?? unknown(severity);

/** Verification command status comes from a smaller vocabulary and is mapped the same way. */
export const lookOfVerificationStatus = (status: string): StatusLook => {
  switch (status) {
    case 'Passed':
      return { label: 'Passed', tone: 'good', shape: 'solid', meaning: 'The command succeeded.' };
    case 'Failed':
      return { label: 'Failed', tone: 'bad', shape: 'solid', meaning: 'The command failed.' };
    case 'TimedOut':
      return {
        label: 'Timed out',
        tone: 'bad',
        shape: 'solid',
        meaning: 'The command ran past its deadline.',
      };
    case 'Skipped':
      return {
        label: 'Skipped',
        tone: 'muted',
        shape: 'outline',
        meaning: 'Not run, because an earlier required command had already failed.',
      };
    default:
      return unknown(status);
  }
};

/** Severity order for display: the ones that block come first. */
export const severityRank = (severity: string): number =>
  ({ Critical: 0, Major: 1, Minor: 2, Info: 3 })[severity] ?? 4;
