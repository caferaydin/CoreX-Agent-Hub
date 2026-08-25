import type { ExecutionOutcome, TaskState } from '../api/contracts';

/**
 * Which actions are worth showing, given a state.
 *
 * These are affordance rules and nothing more. They exist so an operator is not offered a
 * button that is certain to be refused, and they are kept coarse on purpose: they look at one
 * value, the task's state, and they never look at the ready gate, the attempt history, the
 * verification evidence or anything else the server actually decides with.
 *
 * The distinction matters because the tempting version of this file is the dangerous one. A
 * frontend that reproduced the real conditions would be a second copy of the state machine,
 * and the day the two disagreed the UI would be confidently wrong. So: hiding a button is a
 * convenience, and the server refusing is the rule. Every one of these actions is tested
 * against a stale page whose button should not have been there, and the refusal is what the
 * operator sees.
 */

/** Ready is the Draft to Ready transition, and the gate still decides whether it happens. */
export const canMarkReady = (state: TaskState): boolean => state === 'Draft';

/** A task is run when it is Ready. Whether an attempt may actually start is the Hub's answer. */
export const canRun = (state: TaskState): boolean => state === 'Ready';

/**
 * Cancelling applies to anything not already finished.
 *
 * Broader than the other two on purpose: a task waiting for a person is still cancellable, and
 * so is one whose run has stopped being watched. What cannot be cancelled is something already
 * terminal, and that is the one thing this predicate is confident about.
 */
export const canCancel = (state: TaskState): boolean =>
  state === 'Draft' || state === 'Ready' || state === 'Running' || state === 'AwaitingAcceptance';

/** Whether the Hub considers this task finished. Used to stop offering anything at all. */
export const isTerminalTask = (state: TaskState): boolean =>
  state === 'Accepted' || state === 'Rejected' || state === 'Cancelled';

// --- Human governance ------------------------------------------------------------------------

/**
 * Accepting by the ordinary route is offered while a task is waiting for a person.
 *
 * Coarse, like the rest. Whether this attempt actually may be accepted - the latest execution
 * passed, machine truth could be established, no decision exists yet - is the acceptance
 * service's answer, and asking it here would be keeping a second copy of it.
 */
export const canAccept = (state: TaskState): boolean => state === 'AwaitingAcceptance';

export const canRequestChanges = (state: TaskState): boolean => state === 'AwaitingAcceptance';

export const canReject = (state: TaskState): boolean => state === 'AwaitingAcceptance';

/**
 * Offering to override a blocking review.
 *
 * Two values, because two is what it takes to tell this situation apart from every other one:
 * a blocked review leaves the task Ready rather than awaiting acceptance, so the state alone
 * would offer this on a task that had simply never run.
 *
 * This is the one affordance rule worth being precise about, because getting it wrong is
 * visible: an override offered on a passing execution would suggest the Hub thinks something
 * blocked it. What it is not is the rule - the service checks six prerequisites this file
 * knows nothing about, and any of them can still refuse.
 */
export const canOfferOverride = (
  state: TaskState,
  latestOutcome: ExecutionOutcome | null,
): boolean => state === 'Ready' && latestOutcome === 'ReviewBlocked';
