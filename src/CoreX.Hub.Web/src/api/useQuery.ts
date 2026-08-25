import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * Loading, data, error and refetch - and nothing else.
 *
 * A cache library was considered and left out. What this UI actually needs is: fetch on route
 * entry, refetch when the live stream says something changed, and retry when a panel failed.
 * That is roughly forty lines, and a dependency to obtain it would also bring a cache whose
 * invalidation rules would then be the thing to reason about. If Phase 9C needs cross-screen
 * cache coherence after mutations, that is the moment to reconsider - not before.
 *
 * The one non-obvious behaviour is deliberate: a refetch keeps the previous data on screen.
 * An execution page that blanked every time the stream ticked would be unusable, so `data`
 * survives while `refreshing` is true and only an error clears it.
 */
export type QueryState<T> =
  | { status: 'loading'; data: null; error: null; refreshing: false }
  | { status: 'ready'; data: T; error: null; refreshing: boolean }
  | { status: 'error'; data: null; error: unknown; refreshing: false };

export interface Query<T> {
  readonly state: QueryState<T>;
  readonly refetch: () => void;
}

export function useQuery<T>(
  fetcher: (signal: AbortSignal) => Promise<T>,
  deps: readonly unknown[],
  options: { enabled?: boolean } = {},
): Query<T> {
  const enabled = options.enabled ?? true;

  const [state, setState] = useState<QueryState<T>>({
    status: 'loading',
    data: null,
    error: null,
    refreshing: false,
  });

  // Held in a ref so a refetch does not need the fetcher in its dependency list, which would
  // make every inline arrow a new identity and every render a new request.
  const run = useRef(fetcher);
  run.current = fetcher;

  const [nonce, setNonce] = useState(0);
  const refetch = useCallback(() => setNonce((n) => n + 1), []);

  useEffect(() => {
    if (!enabled) {
      return;
    }

    const controller = new AbortController();
    let live = true;

    setState((previous) =>
      previous.status === 'ready'
        ? { ...previous, refreshing: true }
        : { status: 'loading', data: null, error: null, refreshing: false },
    );

    run
      .current(controller.signal)
      .then((data) => {
        if (live) {
          setState({ status: 'ready', data, error: null, refreshing: false });
        }
      })
      .catch((error: unknown) => {
        // An abort is this component going away, not a failure to report.
        if (!live || (error instanceof DOMException && error.name === 'AbortError')) {
          return;
        }

        setState({ status: 'error', data: null, error, refreshing: false });
      });

    return () => {
      live = false;
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, nonce, ...deps]);

  return { state, refetch };
}
