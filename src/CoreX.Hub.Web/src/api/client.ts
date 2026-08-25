import type {
  AcceptanceStateDto,
  AgentProfileDto,
  AcceptanceResultDto,
  AcceptRequest,
  ApiErrorEnvelope,
  CancellationResultDto,
  DecisionRequest,
  CreateTaskRequest,
  MarkReadyResultDto,
  OverrideRequest,
  RunStartedDto,
  TaskSummaryDto,
  EvidenceDto,
  ExecutionDetailDto,
  HealthDto,
  OrganizationDto,
  ProjectDto,
  RepositoryDto,
  ReviewDto,
  TaskDetailDto,
  TaskPageDto,
  VerificationCommandDto,
} from './contracts';

/**
 * Everything this UI knows about talking to the Hub.
 *
 * One place, because the alternative is `fetch('/api/...')` scattered through components and
 * then four slightly different ideas about what an error is. It is deliberately not an SDK:
 * there is no retry policy, no interceptor stack and no cache here - just the base URL, the
 * envelope, and one function per thing a screen needs.
 *
 * Every request the UI makes goes through here, which is what makes what the UI can and
 * cannot do checkable rather than merely intended. After Phase 9D the state-changing surface
 * is exactly eight calls - four lifecycle, four governance - each one named after the
 * operation it performs.
 *
 * There is no generic `transition(path, body)` and there never should be. Accept, override,
 * request-changes and reject are four different governance decisions with four different
 * prerequisites, and a function that took the verb as a parameter would make them look
 * interchangeable to every caller that used it.
 */

/**
 * Where the API is.
 *
 * Same-origin by default, which is what production is and - because the dev server proxies
 * `/api` - what development is too. `VITE_HUB_API` exists for the case where someone runs the
 * two separately on purpose; it is never a network address discovered at runtime.
 */
const base = (import.meta.env['VITE_HUB_API'] as string | undefined)?.replace(/\/$/, '') ?? '';

export const apiBase = `${base}/api/v1`;

/**
 * A refusal the Hub explained.
 *
 * Carries the envelope so a screen can show the message and, for support, the correlation id.
 * The status is kept because 404 and 409 mean different things to a screen even when the
 * message is the same.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly envelope: ApiErrorEnvelope,
  ) {
    super(envelope.message);
    this.name = 'ApiError';
  }

  get code(): string {
    return this.envelope.code;
  }

  get correlationId(): string {
    return this.envelope.correlationId;
  }
}

/** The network did not reach the Hub, or the Hub said something this client cannot read. */
export class TransportError extends Error {
  constructor(message: string, cause?: unknown) {
    super(message, cause === undefined ? undefined : { cause });
    this.name = 'TransportError';
  }
}

/**
 * Turns a response into data, or into an error that says something useful.
 *
 * The unhappy paths are the point. A body that is not JSON, an error shape the server never
 * promised, a connection that dropped - each becomes a typed failure rather than an exception
 * from deep inside a parser, because a panel that cannot load should say so and leave the rest
 * of the page alone.
 */
async function request<T>(path: string, signal?: AbortSignal): Promise<T> {
  let response: Response;

  try {
    response = await fetch(`${apiBase}${path}`, {
      signal,
      headers: { Accept: 'application/json' },
    });
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause;
    }

    throw new TransportError('The Hub did not answer. Is it running?', cause);
  }

  const text = await response.text();

  if (!response.ok) {
    throw new ApiError(response.status, readEnvelope(text, response.status));
  }

  if (text.length === 0) {
    throw new TransportError('The Hub returned an empty response.');
  }

  try {
    return JSON.parse(text) as T;
  } catch (cause) {
    throw new TransportError('The Hub returned a response this client could not read.', cause);
  }
}

/**
 * Sends something, and reads the answer the same way a read does.
 *
 * Never retried. A mutation that failed for an unknown reason may or may not have happened,
 * and quietly sending it again is how one click becomes two attempts. The operator retries,
 * because only the operator knows whether they still want to.
 */
async function send<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
  let response: Response;

  try {
    response = await fetch(`${apiBase}${path}`, {
      method: 'POST',
      signal,
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(body),
    });
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause;
    }

    throw new TransportError('The Hub did not answer. Is it running?', cause);
  }

  const text = await response.text();

  if (!response.ok) {
    throw new ApiError(response.status, readEnvelope(text, response.status));
  }

  if (text.length === 0) {
    throw new TransportError('The Hub accepted this but said nothing about it.');
  }

  try {
    return JSON.parse(text) as T;
  } catch (cause) {
    throw new TransportError('The Hub returned a response this client could not read.', cause);
  }
}

/** Text bodies - the diff - come back as they are, never parsed and never interpreted. */
async function requestText(path: string, signal?: AbortSignal): Promise<string> {
  let response: Response;

  try {
    response = await fetch(`${apiBase}${path}`, { signal, headers: { Accept: 'text/plain' } });
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause;
    }

    throw new TransportError('The Hub did not answer. Is it running?', cause);
  }

  const text = await response.text();

  if (!response.ok) {
    throw new ApiError(response.status, readEnvelope(text, response.status));
  }

  return text;
}

/**
 * Reads the server's error envelope, and invents a usable one when the body is not that.
 *
 * A proxy timing out, a crash before the handler, a 502 from something in between - none of
 * those produce the Hub's shape, and a client that assumed they would would throw while
 * handling an error.
 */
function readEnvelope(body: string, status: number): ApiErrorEnvelope {
  try {
    const parsed: unknown = JSON.parse(body);

    if (
      typeof parsed === 'object' &&
      parsed !== null &&
      typeof (parsed as { code?: unknown }).code === 'string' &&
      typeof (parsed as { message?: unknown }).message === 'string'
    ) {
      return parsed as ApiErrorEnvelope;
    }
  } catch {
    // Not JSON. Fall through to the generic envelope below.
  }

  return {
    code: `http_${status}`,
    message:
      status === 404
        ? 'The Hub has no record of that.'
        : `The Hub returned ${status} without an explanation.`,
    correlationId: '',
  };
}

const query = (params: Record<string, string | number | undefined>): string => {
  const search = new URLSearchParams();

  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') {
      search.set(key, String(value));
    }
  }

  const text = search.toString();

  return text.length > 0 ? `?${text}` : '';
};

export const api = {
  health: (signal?: AbortSignal) => request<HealthDto>('/health', signal),

  organizations: (signal?: AbortSignal) => request<OrganizationDto[]>('/organizations', signal),

  projects: (organizationId?: string, signal?: AbortSignal) =>
    request<ProjectDto[]>(`/projects${query({ organizationId })}`, signal),

  repositories: (signal?: AbortSignal) => request<RepositoryDto[]>('/repositories', signal),

  repository: (id: string, signal?: AbortSignal) =>
    request<RepositoryDto>(`/repositories/${id}`, signal),

  agentProfiles: (signal?: AbortSignal) => request<AgentProfileDto[]>('/agent-profiles', signal),

  tasks: (options: { state?: string; offset?: number; limit?: number }, signal?: AbortSignal) =>
    request<TaskPageDto>(`/tasks${query({ ...options })}`, signal),

  task: (taskKey: string, signal?: AbortSignal) =>
    request<TaskDetailDto>(`/tasks/${encodeURIComponent(taskKey)}`, signal),

  execution: (id: string, signal?: AbortSignal) =>
    request<ExecutionDetailDto>(`/executions/${id}`, signal),

  verification: (id: string, signal?: AbortSignal) =>
    request<VerificationCommandDto[]>(`/executions/${id}/verification`, signal),

  review: (id: string, signal?: AbortSignal) => request<ReviewDto>(`/executions/${id}/review`, signal),

  evidence: (id: string, signal?: AbortSignal) =>
    request<EvidenceDto[]>(`/executions/${id}/evidence`, signal),

  /** The persisted result diff, as text. Never parsed, never rendered as markup. */
  diff: (id: string, signal?: AbortSignal) => requestText(`/executions/${id}/diff`, signal),

  acceptance: (taskKey: string, attemptNo: number, signal?: AbortSignal) =>
    request<AcceptanceStateDto>(
      `/tasks/${encodeURIComponent(taskKey)}/attempts/${attemptNo}/acceptance`,
      signal,
    ),

  /** Where the live stream for an execution lives. Opened by the SSE hook, not by fetch. */
  eventsUrl: (id: string) => `${apiBase}/executions/${id}/events`,

  // --- The four lifecycle steps --------------------------------------------------------------

  createTask: (request: CreateTaskRequest, signal?: AbortSignal) =>
    send<TaskSummaryDto>('/tasks', request, signal),

  markTaskReady: (taskKey: string, actor: string, signal?: AbortSignal) =>
    send<MarkReadyResultDto>(`/tasks/${encodeURIComponent(taskKey)}/ready`, { actor }, signal),

  /**
   * Starts an attempt and returns as soon as the Hub has an execution to name.
   *
   * A 202 with an identity, not a finished run - the work continues without this request, and
   * the execution page follows it from durable state.
   */
  runTask: (taskKey: string, actor: string, agentProfile: string, signal?: AbortSignal) =>
    send<RunStartedDto>(`/tasks/${encodeURIComponent(taskKey)}/runs`, { actor, agentProfile }, signal),

  cancelTask: (taskKey: string, actor: string, reason: string | null, signal?: AbortSignal) =>
    send<CancellationResultDto>(
      `/tasks/${encodeURIComponent(taskKey)}/cancel`,
      { actor, reason },
      signal,
    ),

  // --- The four human decisions ------------------------------------------------------------

  /**
   * Accepting by the ordinary route.
   *
   * The request cannot carry an override, because {@link AcceptRequest} has nowhere to put
   * one. An execution a review blocked is refused here, by the server, and that refusal is
   * what an operator sees.
   */
  acceptTask: (
    taskKey: string,
    attemptNo: number,
    request: AcceptRequest,
    signal?: AbortSignal,
  ) =>
    send<AcceptanceResultDto>(
      `/tasks/${encodeURIComponent(taskKey)}/attempts/${attemptNo}/accept`,
      request,
      signal,
    ),

  /** Accepting despite a blocking review. A different route, on purpose. */
  overrideReviewBlocked: (
    taskKey: string,
    attemptNo: number,
    request: OverrideRequest,
    signal?: AbortSignal,
  ) =>
    send<AcceptanceResultDto>(
      `/tasks/${encodeURIComponent(taskKey)}/attempts/${attemptNo}/accept-override`,
      request,
      signal,
    ),

  requestChanges: (
    taskKey: string,
    attemptNo: number,
    request: DecisionRequest,
    signal?: AbortSignal,
  ) =>
    send<AcceptanceResultDto>(
      `/tasks/${encodeURIComponent(taskKey)}/attempts/${attemptNo}/request-changes`,
      request,
      signal,
    ),

  rejectTask: (
    taskKey: string,
    attemptNo: number,
    request: DecisionRequest,
    signal?: AbortSignal,
  ) =>
    send<AcceptanceResultDto>(
      `/tasks/${encodeURIComponent(taskKey)}/attempts/${attemptNo}/reject`,
      request,
      signal,
    ),
} as const;
