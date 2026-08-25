import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useExecutionStream } from './useExecutionStream';

/**
 * The live stream, and the three properties that make having one safe.
 *
 * It reconciles rather than replaces - the durable query stays the authority. It does not
 * render the same event twice when a reconnect overlaps. And unmounting closes the client
 * stream and nothing else, because an execution is not owned by whoever is watching it.
 */

/** A stand-in for the browser's EventSource, so a test can drive the wire directly. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];

  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 2;

  readyState = FakeEventSource.CONNECTING;
  closed = false;

  private readonly listeners = new Map<string, Array<(event: Event) => void>>();

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(type: string, handler: (event: Event) => void): void {
    const existing = this.listeners.get(type) ?? [];
    existing.push(handler);
    this.listeners.set(type, existing);
  }

  close(): void {
    this.closed = true;
    this.readyState = FakeEventSource.CLOSED;
  }

  emit(type: string, data?: unknown): void {
    if (type === 'open') {
      this.readyState = FakeEventSource.OPEN;
    }

    const event =
      data === undefined
        ? new Event(type)
        : new MessageEvent(type, { data: JSON.stringify(data) });

    for (const handler of this.listeners.get(type) ?? []) {
      handler(event);
    }
  }

  /** Something the reader cannot parse - a truncated frame, a proxy injecting noise. */
  emitRaw(type: string, data: string): void {
    for (const handler of this.listeners.get(type) ?? []) {
      handler(new MessageEvent(type, { data }));
    }
  }
}

const snapshot = (over: Record<string, unknown> = {}) => ({
  executionId: 'exec-1',
  state: 'RunningAgent',
  outcome: null,
  taskState: 'Running',
  terminal: false,
  sequence: 0,
  steps: [],
  ...over,
});

const event = (sequence: number) => ({
  sequence,
  executionId: 'exec-1',
  occurredAtUtc: '2026-08-25T16:30:00.000Z',
  type: 'Verifying',
  message: 'entered Verifying',
});

describe('useExecutionStream', () => {
  afterEach(() => {
    FakeEventSource.instances = [];
    vi.unstubAllGlobals();
  });

  const install = (): void => {
    vi.stubGlobal('EventSource', FakeEventSource);
  };

  const latest = (): FakeEventSource => {
    const source = FakeEventSource.instances.at(-1);

    if (!source) {
      throw new Error('no stream was opened');
    }

    return source;
  };

  it('connects, reports live, and raises a revision the page reconciles from', async () => {
    install();

    const { result } = renderHook(() => useExecutionStream('exec-1', { enabled: true }));

    act(() => latest().emit('open'));

    await waitFor(() => expect(result.current.status).toBe('live'));

    const before = result.current.revision;

    act(() => latest().emit('snapshot', snapshot()));
    act(() => latest().emit('execution-event', event(1)));

    // Two signals that durable state may have moved. The page refetches on this, rather than
    // rendering from the stream.
    expect(result.current.revision).toBeGreaterThan(before + 1);
    expect(result.current.snapshot?.state).toBe('RunningAgent');
    expect(result.current.events).toHaveLength(1);
  });

  it('does not render the same event twice when a reconnect overlaps', () => {
    install();

    const { result } = renderHook(() => useExecutionStream('exec-1', { enabled: true }));

    act(() => {
      latest().emit('execution-event', event(1));
      latest().emit('execution-event', event(2));

      // What a resumed stream replays when the cursor and the server disagree by one.
      latest().emit('execution-event', event(2));
      latest().emit('execution-event', event(3));
    });

    expect(result.current.events.map((e) => e.sequence)).toEqual([1, 2, 3]);
  });

  it('resumes from where it left off rather than replaying the run', () => {
    install();

    const { result, unmount } = renderHook(() =>
      useExecutionStream('exec-resume', { enabled: true }),
    );

    act(() =>
      latest().emit('execution-event', { ...event(7), executionId: 'exec-resume' }),
    );

    expect(result.current.events).toHaveLength(1);

    unmount();

    // A remount asks for what comes after the last sequence it saw.
    renderHook(() => useExecutionStream('exec-resume', { enabled: true }));

    expect(latest().url).toContain('after=7');
  });

  it('stops when the run is over instead of reconnecting for ever', async () => {
    install();

    const { result } = renderHook(() => useExecutionStream('exec-1', { enabled: true }));

    act(() => latest().emit('complete', snapshot({ state: 'Completed', terminal: true })));

    await waitFor(() => expect(result.current.status).toBe('offline'));

    expect(latest().closed).toBe(true);
    expect(result.current.snapshot?.terminal).toBe(true);
  });

  it('reports a dropped connection without losing what the page already has', async () => {
    install();

    const { result } = renderHook(() => useExecutionStream('exec-1', { enabled: true }));

    act(() => latest().emit('snapshot', snapshot()));
    act(() => latest().emit('error'));

    // Reconnecting, not broken - and the last snapshot is still on screen.
    await waitFor(() => expect(result.current.status).toBe('connecting'));
    expect(result.current.snapshot).not.toBeNull();
  });

  it('steps over a frame it cannot read', () => {
    install();

    const { result } = renderHook(() => useExecutionStream('exec-1', { enabled: true }));

    act(() => latest().emitRaw('execution-event', '{ this is not json'));

    expect(result.current.events).toHaveLength(0);
  });

  it('closes only the client stream when the page goes away', () => {
    install();

    const { unmount } = renderHook(() => useExecutionStream('exec-1', { enabled: true }));

    const source = latest();

    expect(source.closed).toBe(false);

    unmount();

    // The socket is closed. Nothing else is: the Hub is explicit that watching is not owning,
    // and there is no request here that could cancel anything.
    expect(source.closed).toBe(true);
    expect(source.url).not.toContain('cancel');
  });

  it('opens nothing for an execution that has already finished', () => {
    install();

    renderHook(() => useExecutionStream('exec-1', { enabled: false }));

    expect(FakeEventSource.instances).toHaveLength(0);
  });

  it('reports offline rather than failing where EventSource does not exist', () => {
    vi.stubGlobal('EventSource', undefined);

    const { result } = renderHook(() => useExecutionStream('exec-1', { enabled: true }));

    expect(result.current.status).toBe('offline');
    expect(result.current.events).toHaveLength(0);
  });
});
