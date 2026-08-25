/**
 * The wire shapes, hand-typed against the Phase 9A contract.
 *
 * These are API-facing names, not the domain model: the server already decided what a screen
 * needs to render a row without four more requests, and copying its aggregates here would
 * mean maintaining a second version of a model this side has no business knowing.
 *
 * Every enum crosses as a string. They are typed as unions where the set is closed and the UI
 * makes a decision from it, and as plain `string` where the UI only displays it - a union that
 * has to be edited every time the server grows a value is a union that will be wrong.
 */

export interface ApiErrorEnvelope {
  readonly code: string;
  readonly message: string;
  readonly correlationId: string;
  readonly details?: readonly string[] | null;
}

// --- Catalogue -----------------------------------------------------------------------------

export interface OrganizationDto {
  readonly id: string;
  readonly slug: string;
  readonly name: string;
}

export interface ProjectDto {
  readonly id: string;
  readonly organizationId: string;
  readonly slug: string;
  readonly name: string;
  readonly taskKeyPrefix: string;
}

export interface RepositoryDto {
  readonly id: string;
  readonly projectId: string;
  readonly slug: string;
  readonly name: string;
  readonly defaultBranch: string;
  readonly protectedBranches: readonly string[];
  readonly reviewerProfile: string | null;
  readonly verificationProfiles: readonly string[];
  readonly defaultVerificationProfile: string | null;

  /** Whether the checkout is present. The path itself is deliberately not on the wire. */
  readonly localPathExists: boolean;
}

export interface AgentProfileDto {
  readonly name: string;
  readonly role: string;
  readonly provider: string;
  readonly runtimeKey: string;
  readonly permissionProfile: string;
  readonly model: string | null;
  readonly effort: string | null;

  /** Configuration says this runtime exists here. Not a health check. */
  readonly runtimeDeclared: boolean;
}

// --- Tasks ---------------------------------------------------------------------------------

/** Closed: the UI renders a distinct badge per state, so a new one must be a compile error. */
export type TaskState =
  | 'Draft'
  | 'Ready'
  | 'Running'
  | 'AwaitingAcceptance'
  | 'Accepted'
  | 'Rejected'
  | 'Cancelled';

export interface TaskSummaryDto {
  readonly id: string;
  readonly taskKey: string;
  readonly objective: string;
  readonly state: TaskState;
  readonly repositoryId: string;
  readonly repositorySlug: string;
  readonly attemptCount: number;
  readonly latestExecutionId: string | null;
  readonly latestExecutionState: string | null;
  readonly latestExecutionOutcome: string | null;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string;
}

export interface TaskPageDto {
  readonly items: readonly TaskSummaryDto[];
  readonly total: number;
  readonly offset: number;
  readonly limit: number;
}

export type CriterionKind = 'Machine' | 'Human';

export interface CriterionDto {
  readonly id: string;
  readonly order: number;
  readonly kind: CriterionKind;
  readonly text: string;
  readonly verificationRef: string | null;
  readonly humanCheckHint: string | null;
}

export interface TaskDetailDto {
  readonly summary: TaskSummaryDto;
  readonly allowedScope: readonly string[];
  readonly forbiddenScope: readonly string[];
  readonly criteria: readonly CriterionDto[];
  readonly readyGateSatisfied: boolean;
  readonly readyGateProblems: readonly string[];
  readonly attempts: readonly ExecutionSummaryDto[];
  readonly acceptance: AcceptanceStateDto | null;
}

// --- Executions ----------------------------------------------------------------------------

export type ExecutionState =
  | 'Created'
  | 'PreparingWorkspace'
  | 'BuildingContext'
  | 'RunningAgent'
  | 'CapturingChanges'
  | 'Verifying'
  | 'Reviewing'
  | 'Completed'
  | 'Failed'
  | 'Cancelled';

/**
 * Outcomes are kept apart on purpose.
 *
 * ReviewBlocked, ReviewUnusable and VerificationFailed mean three operationally different
 * things - a reviewer that found something, a review nobody could obtain, and machine checks
 * that failed - and collapsing them into "failed" would hide which of the three happened at
 * exactly the moment an operator needs to know.
 */
export type ExecutionOutcome =
  | 'Passed'
  | 'VerificationFailed'
  | 'ReviewBlocked'
  | 'ReviewUnusable'
  | 'ScopeViolation'
  | 'SecretGuardBlocked'
  | 'RuntimePolicyViolation'
  | 'Failed'
  | 'TimedOut'
  | 'Cancelled'
  | 'Interrupted';

export interface ExecutionSummaryDto {
  readonly id: string;
  readonly attemptNo: number;
  readonly state: ExecutionState;
  readonly outcome: ExecutionOutcome | null;
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string | null;
}

export type ExecutionStepState =
  | 'Pending'
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'TimedOut'
  | 'Cancelled'
  | 'Skipped'
  | 'Unsupported';

export interface ExecutionStepDto {
  readonly order: number;
  readonly kind: string;
  readonly state: ExecutionStepState;
  readonly stateReason: string | null;
  readonly startedAtUtc: string | null;
  readonly endedAtUtc: string | null;
  readonly evidenceCount: number;
}

export interface ExecutionDetailDto {
  readonly id: string;
  readonly taskId: string;
  readonly taskKey: string;
  readonly attemptNo: number;
  readonly state: ExecutionState;
  readonly outcome: ExecutionOutcome | null;
  readonly baseCommitSha: string | null;
  readonly resultCommitSha: string | null;
  readonly branchName: string | null;
  readonly contextPackHash: string | null;
  readonly workspaceState: string | null;
  readonly unexpectedAgentCommitDetected: boolean;
  readonly unexpectedCommitShas: readonly string[];
  readonly createdAtUtc: string;
  readonly updatedAtUtc: string | null;
  readonly steps: readonly ExecutionStepDto[];
}

// --- Verification --------------------------------------------------------------------------

export interface VerificationCommandDto {
  readonly order: number;
  readonly name: string;
  readonly required: boolean;
  readonly status: string;
  readonly exitCode: number | null;
  readonly durationMilliseconds: number | null;
  readonly testsPassed: number | null;
  readonly testsFailed: number | null;
  readonly testsSkipped: number | null;
}

// --- Review --------------------------------------------------------------------------------

export type FindingSeverity = 'Critical' | 'Major' | 'Minor' | 'Info';

export interface FindingDto {
  readonly id: string;
  readonly severity: FindingSeverity;
  readonly category: string;
  readonly title: string;
  readonly rationale: string | null;
  readonly file: string | null;
  readonly line: number | null;
  readonly suggestedFix: string | null;
  readonly confidence: string;

  /** The Hub's own scope computation, never the provider's claim. */
  readonly outOfScope: boolean;
}

export interface ReviewDto {
  readonly findings: readonly FindingDto[];
  readonly inScopeCountsBySeverity: Readonly<Record<string, number>>;
  readonly majorThreshold: number;
  readonly blocks: boolean;
  readonly summary: string;
}

// --- Evidence ------------------------------------------------------------------------------

export interface EvidenceDto {
  readonly id: string;
  readonly kind: string;
  readonly executionStepId: string | null;
  readonly sizeBytes: number;
  readonly sha256: string;
  readonly mediaType: string;
  readonly redactionCount: number;
  readonly createdAtUtc: string;
}

// --- Acceptance ----------------------------------------------------------------------------

export interface MachineCriterionDto {
  readonly criterionId: string;
  readonly order: number;
  readonly text: string;
  readonly verificationRef: string;
  readonly verdict: string;
  readonly verificationStatus: string;
  readonly required: boolean;
  readonly exitCode: number | null;
  readonly testsPassed: number | null;
  readonly testsFailed: number | null;
  readonly testsSkipped: number | null;
}

export interface HumanCriterionDto {
  readonly criterionId: string;
  readonly order: number;
  readonly text: string;
  readonly humanCheckHint: string | null;
}

export interface AcceptanceStateDto {
  readonly taskState: TaskState;
  readonly executionId: string;
  readonly attemptNo: number;
  readonly isLatestExecution: boolean;
  readonly executionState: ExecutionState;
  readonly executionOutcome: ExecutionOutcome | null;

  /**
   * Whether the override route is the applicable one for this attempt.
   *
   * Read-only here in 9B, and it stays advisory even later: the server refuses an override on
   * anything else whatever this says, so the UI uses it to render honestly rather than to
   * decide anything.
   */
  readonly overrideApplicable: boolean;
  readonly existingDecision: string | null;
  readonly machineCriteria: readonly MachineCriterionDto[];
  readonly humanCriteria: readonly HumanCriterionDto[];
  readonly machineTruthProblems: readonly string[];
}

// --- Diagnostics ---------------------------------------------------------------------------

export interface HealthDto {
  readonly status: string;
  readonly databaseReachable: boolean;
  readonly configurationLoaded: boolean;
  readonly pendingRecoveryCount: number;
  readonly nowUtc: string;
}

// --- Live stream ---------------------------------------------------------------------------

export interface ExecutionSnapshotDto {
  readonly executionId: string;
  readonly state: ExecutionState;
  readonly outcome: ExecutionOutcome | null;
  readonly taskState: string;
  readonly terminal: boolean;
  readonly sequence: number;
  readonly steps: readonly ExecutionStepDto[];
}

export interface ExecutionEventDto {
  readonly sequence: number;
  readonly executionId: string;
  readonly occurredAtUtc: string;
  readonly type: string;
  readonly message: string;
}
