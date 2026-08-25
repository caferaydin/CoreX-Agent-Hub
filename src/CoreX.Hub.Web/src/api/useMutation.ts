import { useCallback, useRef, useState } from 'react';

/**
 * One state-changing call, and what the operator can see of it.
 *
 * Deliberately small. Four actions do not need a command framework, and the thing that would
 * be genuinely dangerous here - a queue, a retry policy, an optimistic update - is exactly
 * what a framework would encourage. The Hub's state machine is the authority; this only
 * carries a request to it and reports what came back.
 *
 * Two behaviours are load-bearing rather than incidental.
 *
 * A request in flight is not sent again. `submitting` gates the call itself, not just the
 * button, so a keyboard repeat, a double click and a re-render that fires an effect all reach
 * the same guard. That is a courtesy, not the guarantee - the server refuses a duplicate on
 * its own, and there is a test for that.
 *
 * A failure is never retried automatically. A mutation that failed for an unknown reason may
 * or may not have happened, and sending it again is how one click becomes two attempts.
 *
 * `once` covers the third case: a call that has already succeeded. For most actions repeating
 * one is harmless - asking a stopped task to stop again is refused by the Hub and costs
 * nothing - but a create that ran twice is two tasks. Navigation usually carries the operator
 * away before a second click lands, and "usually" is not a guarantee worth relying on.
 */
export type MutationState<T> =
  | { status: 'idle'; data: null; error: null }
  | { status: 'submitting'; data: null; error: null }
  | { status: 'done'; data: T; error: null }
  | { status: 'failed'; data: null; error: unknown };

export interface Mutation<TArgs extends readonly unknown[], TResult> {
  readonly state: MutationState<TResult>;
  readonly submitting: boolean;

  /** Resolves to the result, or null when the call was refused or never sent. */
  readonly submit: (...args: TArgs) => Promise<TResult | null>;

  /** Clears a refusal so the operator can try again after changing something. */
  readonly reset: () => void;
}

export function useMutation<TArgs extends readonly unknown[], TResult>(
  action: (...args: TArgs) => Promise<TResult>,
  options: { once?: boolean } = {},
): Mutation<TArgs, TResult> {
  const [state, setState] = useState<MutationState<TResult>>({
    status: 'idle',
    data: null,
    error: null,
  });

  // The gates live in refs as well as in state: state updates are asynchronous, and two clicks
  // in the same tick would both see `submitting: false`.
  const inFlight = useRef(false);
  const succeeded = useRef(false);
  const once = options.once === true;

  const run = useRef(action);
  run.current = action;

  const submit = useCallback(async (...args: TArgs): Promise<TResult | null> => {
    if (inFlight.current || (once && succeeded.current)) {
      return null;
    }

    inFlight.current = true;
    setState({ status: 'submitting', data: null, error: null });

    try {
      const data = await run.current(...args);

      succeeded.current = true;
      setState({ status: 'done', data, error: null });

      return data;
    } catch (error) {
      // Never rethrown: a refusal is an answer, and a caller that had to catch would end up
      // deciding what a 409 means - which is the server's job.
      setState({ status: 'failed', data: null, error });

      return null;
    } finally {
      inFlight.current = false;
    }
  }, [once]);

  const reset = useCallback(() => {
    succeeded.current = false;
    setState({ status: 'idle', data: null, error: null });
  }, []);

  return { state, submitting: state.status === 'submitting', submit, reset };
}
