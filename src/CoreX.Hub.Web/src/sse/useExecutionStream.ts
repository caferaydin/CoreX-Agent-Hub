import { useEffect, useRef, useState } from 'react';
import { api } from '../api/client';
import type { ExecutionEventDto, ExecutionSnapshotDto } from '../api/contracts';

/**
 * The live view of one execution.
 *
 * Two rules shape this, both from the Phase 9A contract.
 *
 * The first: the stream is a convenience and the database is the truth. So this hook never
 * becomes the application's state - it hands back the latest snapshot and a change counter,
 * and the page uses that counter to refetch the durable queries. A dropped event costs a
 * moment of staleness, not a wrong screen.
 *
 * The second: connect only while there is something to watch. A terminal execution has no
 * updates left, so opening a stream for one would be a socket held for nothing.
 */

export type LiveStatus = 'idle' | 'connecting' | 'live' | 'offline';

export interface ExecutionStream {
  readonly status: LiveStatus;
  readonly snapshot: ExecutionSnapshotDto | null;

  /** The most recent events, newest last. Bounded - this is a view, not a log. */
  readonly events: readonly ExecutionEventDto[];

  /** Increments whenever durable state may have moved. The page refetches on it. */
  readonly revision: number;
}

/** How many events are kept for display. The durable timeline is the full record. */
const WINDOW = 200;

/**
 * How far each execution has been read, remembered across mounts.
 *
 * The browser sends Last-Event-ID by itself when *it* reconnects, which covers a dropped
 * socket. It does not cover navigating away and back: that is a new EventSource, and without
 * this the stream would replay a long-running execution from its first event every time. The
 * snapshot makes that harmless but not free, and an operator flicking between two attempts
 * should not pay for it.
 *
 * An entry is dropped when the run completes, so this cannot grow with the session.
 */
const resumeFrom = new Map<string, number>();

export function useExecutionStream(
  executionId: string | null,
  options: { enabled: boolean },
): ExecutionStream {
  const [status, setStatus] = useState<LiveStatus>('idle');
  const [snapshot, setSnapshot] = useState<ExecutionSnapshotDto | null>(null);
  const [events, setEvents] = useState<readonly ExecutionEventDto[]>([]);
  const [revision, setRevision] = useState(0);

  // Kept across reconnects so the browser resumes rather than replaying from the start.
  const lastSequence = useRef(resumeFrom.get(executionId ?? '') ?? 0);

  useEffect(() => {
    if (!executionId || !options.enabled) {
      setStatus('idle');
      return;
    }

    // EventSource is not available in every environment this code is exercised in; when it is
    // absent the page simply falls back to what it already fetched.
    if (typeof EventSource === 'undefined') {
      setStatus('offline');
      return;
    }

    setStatus('connecting');

    // The browser sends Last-Event-ID by itself on its own reconnects. A cursor in the query
    // covers the case where the page itself remounts and wants to resume where it was.
    const resume = lastSequence.current > 0 ? `?after=${lastSequence.current}` : '';
    const source = new EventSource(`${api.eventsUrl(executionId)}${resume}`);

    let closed = false;

    source.addEventListener('open', () => {
      if (!closed) {
        setStatus('live');
      }
    });

    source.addEventListener('snapshot', (message) => {
      const parsed = read<ExecutionSnapshotDto>(message);

      if (parsed && !closed) {
        setStatus('live');
        setSnapshot(parsed);
        setRevision((n) => n + 1);
      }
    });

    source.addEventListener('execution-event', (message) => {
      const parsed = read<ExecutionEventDto>(message);

      if (!parsed || closed) {
        return;
      }

      // Sequence-keyed, so a reconnect that overlaps cannot render the same event twice.
      setEvents((previous) => {
        if (previous.some((e) => e.sequence === parsed.sequence)) {
          return previous;
        }

        const next = [...previous, parsed].sort((a, b) => a.sequence - b.sequence);

        return next.length > WINDOW ? next.slice(next.length - WINDOW) : next;
      });

      lastSequence.current = Math.max(lastSequence.current, parsed.sequence);
      resumeFrom.set(executionId, lastSequence.current);
      setRevision((n) => n + 1);
    });

    source.addEventListener('complete', (message) => {
      const parsed = read<ExecutionSnapshotDto>(message);

      if (closed) {
        return;
      }

      if (parsed) {
        setSnapshot(parsed);
      }

      // The run is over: one last reconcile, then stop rather than reconnecting for ever.
      // Nothing more will arrive for this execution, so its cursor is not worth keeping.
      resumeFrom.delete(executionId);
      setRevision((n) => n + 1);
      setStatus('offline');
      closed = true;
      source.close();
    });

    source.addEventListener('error', () => {
      if (closed) {
        return;
      }

      // EventSource reconnects on its own; this only reports that it is trying. The page
      // keeps showing what it last fetched, which is still durable truth.
      setStatus(source.readyState === EventSource.CLOSED ? 'offline' : 'connecting');
    });

    return () => {
      // Closing the client stream, and nothing else. The execution is not owned by whoever
      // happens to be watching it - the Hub is explicit about this and so is this line.
      closed = true;
      source.close();
      setStatus('idle');
    };
  }, [executionId, options.enabled]);

  return { status, snapshot, events, revision };
}

/** Server-sent data is text off the network: parsed defensively, never trusted to be shaped. */
function read<T>(message: Event): T | null {
  const data = (message as MessageEvent<string>).data;

  if (typeof data !== 'string' || data.length === 0) {
    return null;
  }

  try {
    return JSON.parse(data) as T;
  } catch {
    return null;
  }
}
