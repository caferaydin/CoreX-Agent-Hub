import type {
  AcceptanceResultDto,
  AcceptanceStateDto,
  AgentProfileDto,
  OrganizationDto,
  ProjectDto,
  EvidenceDto,
  ExecutionDetailDto,
  ExecutionSummaryDto,
  FindingDto,
  RepositoryDto,
  ReviewDto,
  TaskDetailDto,
  TaskPageDto,
  TaskSummaryDto,
  VerificationCommandDto,
} from '../api/contracts';

/**
 * Responses shaped like the ones the Hub actually sends.
 *
 * Copied from the Phase 9A contract rather than invented, which is the whole point: if the UI
 * and the API disagree, that disagreement should show up here as a type error rather than
 * being smoothed over by a fixture that was written to match the component.
 */

export const taskSummary = (over: Partial<TaskSummaryDto> = {}): TaskSummaryDto => ({
  id: '01a03a11-384f-7001-b175-8c2679d883a8',
  taskKey: 'CXH-001',
  objective: 'Add the acceptance document the phase asked for',
  state: 'AwaitingAcceptance',
  repositoryId: '01a03a11-0000-7001-b175-8c2679d883a8',
  repositorySlug: 'corex-agent-hub',
  attemptCount: 2,
  latestExecutionId: '01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3',
  latestExecutionState: 'Completed',
  latestExecutionOutcome: 'Passed',
  createdAtUtc: '2026-08-25T14:00:00.000Z',
  updatedAtUtc: '2026-08-25T16:41:00.000Z',
  ...over,
});

export const taskPage = (items: readonly TaskSummaryDto[]): TaskPageDto => ({
  items,
  total: items.length,
  offset: 0,
  limit: 50,
});

export const executionSummary = (over: Partial<ExecutionSummaryDto> = {}): ExecutionSummaryDto => ({
  id: '01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3',
  attemptNo: 1,
  state: 'Completed',
  outcome: 'Passed',
  createdAtUtc: '2026-08-25T16:30:00.000Z',
  updatedAtUtc: '2026-08-25T16:41:00.000Z',
  ...over,
});

export const taskDetail = (over: Partial<TaskDetailDto> = {}): TaskDetailDto => ({
  summary: taskSummary(),
  allowedScope: ['docs/phase8-codex-reviewer-acceptance.md'],
  forbiddenScope: [],
  criteria: [
    {
      id: 'c1',
      order: 1,
      kind: 'Machine',
      text: 'The solution builds in Release with warnings as errors.',
      verificationRef: 'build',
      humanCheckHint: null,
    },
    {
      id: 'c2',
      order: 2,
      kind: 'Human',
      text: 'The document contains the requested semantic content.',
      verificationRef: null,
      humanCheckHint: 'Read it and decide.',
    },
  ],
  readyGateSatisfied: true,
  readyGateProblems: [],
  attempts: [executionSummary()],
  acceptance: null,
  ...over,
});

export const executionDetail = (over: Partial<ExecutionDetailDto> = {}): ExecutionDetailDto => ({
  id: '01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3',
  taskId: '01a03a11-384f-7001-b175-8c2679d883a8',
  taskKey: 'CXH-001',
  attemptNo: 1,
  state: 'Completed',
  outcome: 'Passed',
  baseCommitSha: '67dbafab56c61125a56b0290d793e4bf965f816d',
  resultCommitSha: '9b2ea76dacd8bb26dd59e435d0fdf982d5b4c951',
  branchName: 'agent/CXH-001/1',
  contextPackHash: '16c5ddf62822479ee9c2679f3799e05bb91ec99c',
  workspaceState: 'Released',
  unexpectedAgentCommitDetected: false,
  unexpectedCommitShas: [],
  createdAtUtc: '2026-08-25T16:30:00.000Z',
  updatedAtUtc: '2026-08-25T16:41:00.000Z',
  steps: [
    {
      order: 1,
      kind: 'PrepareWorkspace',
      state: 'Succeeded',
      stateReason: null,
      startedAtUtc: '2026-08-25T16:30:00.000Z',
      endedAtUtc: '2026-08-25T16:30:04.000Z',
      evidenceCount: 0,
    },
    {
      order: 2,
      kind: 'Agent',
      state: 'Succeeded',
      stateReason: null,
      startedAtUtc: '2026-08-25T16:30:04.000Z',
      endedAtUtc: '2026-08-25T16:33:00.000Z',
      evidenceCount: 1,
    },
    {
      order: 3,
      kind: 'Verify',
      state: 'Failed',
      stateReason: 'hub-release-full [Failed] restore=Passed, build=Passed, test=Failed',
      startedAtUtc: '2026-08-25T16:33:00.000Z',
      endedAtUtc: '2026-08-25T16:40:00.000Z',
      evidenceCount: 0,
    },
    {
      order: 4,
      kind: 'Review',
      state: 'Skipped',
      stateReason: 'VerificationDidNotPass',
      startedAtUtc: null,
      endedAtUtc: null,
      evidenceCount: 0,
    },
  ],
  ...over,
});

export const verification = (): VerificationCommandDto[] => [
  {
    order: 1,
    name: 'restore',
    required: true,
    status: 'Passed',
    exitCode: 0,
    durationMilliseconds: 1800,
    testsPassed: null,
    testsFailed: null,
    testsSkipped: null,
  },
  {
    order: 2,
    name: 'test',
    required: true,
    status: 'Failed',
    exitCode: 1,
    durationMilliseconds: 543_000,
    testsPassed: 1434,
    testsFailed: 1,
    testsSkipped: 0,
  },
  {
    order: 3,
    name: 'ef-pending-model',
    required: true,
    status: 'Skipped',
    exitCode: null,
    durationMilliseconds: null,
    testsPassed: null,
    testsFailed: null,
    testsSkipped: null,
  },
];

export const finding = (over: Partial<FindingDto> = {}): FindingDto => ({
  id: 'f1',
  severity: 'Major',
  category: 'Security',
  title: 'Authorization check always succeeds',
  rationale: 'The helper returns true for every input.',
  file: 'src/CoreX.Hub.Application/Phase8Acceptance/ReviewBlockerProbe.cs',
  line: 18,
  suggestedFix: 'Return false for a missing token.',
  confidence: 'High',
  outOfScope: false,
  ...over,
});

export const review = (over: Partial<ReviewDto> = {}): ReviewDto => ({
  findings: [finding()],
  inScopeCountsBySeverity: { Major: 1 },
  majorThreshold: 1,
  blocks: true,
  summary: '1 finding(s), 1 in scope; 0 in-scope Critical, 1 in-scope Major (threshold 1)',
  ...over,
});

export const evidence = (): EvidenceDto[] => [
  {
    id: 'e1',
    kind: 'GitCommitDiff',
    executionStepId: null,
    sizeBytes: 383,
    sha256: '2d64b7507d60101553f4f8d0c48b68cc5ebc170cd39ef66a8634b62f8417c47e',
    mediaType: 'text/plain',
    redactionCount: 0,
    createdAtUtc: '2026-08-25T16:40:00.000Z',
  },
];

export const repository = (over: Partial<RepositoryDto> = {}): RepositoryDto => ({
  id: '01a03a11-0000-7001-b175-8c2679d883a8',
  projectId: '01a03a11-1111-7001-b175-8c2679d883a8',
  slug: 'corex-agent-hub',
  name: 'CoreX Agent Hub',
  defaultBranch: 'main',
  protectedBranches: ['main'],
  reviewerProfile: 'codex-reviewer',
  verificationProfiles: ['hub-release-full'],
  defaultVerificationProfile: 'hub-release-full',
  localPathExists: true,
  ...over,
});

export const agentProfile = (over: Partial<AgentProfileDto> = {}): AgentProfileDto => ({
  name: 'claude-implementer',
  role: 'Implementer',
  provider: 'ClaudeCode',
  runtimeKey: 'claude-code',
  permissionProfile: 'WorkspaceWrite',
  model: 'claude-opus-5',
  effort: 'high',
  runtimeDeclared: true,
  ...over,
});

/** A small real unified diff, including the header lines that must not read as add/remove. */
export const diffText = `diff --git a/docs/phase8.md b/docs/phase8.md
new file mode 100644
index 0000000..c3903ed
--- /dev/null
+++ b/docs/phase8.md
@@ -0,0 +1,3 @@
+# Phase 8 Codex Reviewer Acceptance
+
-removed line for the sake of the test
 context line
`;

export const organization = (over: Partial<OrganizationDto> = {}): OrganizationDto => ({
  id: '01a03a11-2222-7001-b175-8c2679d883a8',
  slug: 'core-x-logic',
  name: 'Core X Logic',
  ...over,
});

export const project = (over: Partial<ProjectDto> = {}): ProjectDto => ({
  id: '01a03a11-1111-7001-b175-8c2679d883a8',
  organizationId: '01a03a11-2222-7001-b175-8c2679d883a8',
  slug: 'agent-hub',
  name: 'Agent Hub',
  taskKeyPrefix: 'CXH',
  ...over,
});

/**
 * Where an attempt stands with a person.
 *
 * Defaults to the ordinary case - machine truth established, one Human criterion outstanding,
 * nothing decided - so a test that is about the override says only what makes it an override.
 */
export const acceptanceState = (over: Partial<AcceptanceStateDto> = {}): AcceptanceStateDto => ({
  taskState: 'AwaitingAcceptance',
  executionId: '01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3',
  attemptNo: 2,
  isLatestExecution: true,
  executionState: 'Completed',
  executionOutcome: 'Passed',
  overrideApplicable: false,
  existingDecision: null,
  machineCriteria: [
    {
      criterionId: 'c1',
      order: 1,
      text: 'The solution builds in Release with warnings as errors.',
      verificationRef: 'build',
      verdict: 'Met',
      verificationStatus: 'Passed',
      required: true,
      exitCode: 0,
      testsPassed: null,
      testsFailed: null,
      testsSkipped: null,
    },
  ],
  humanCriteria: [
    {
      criterionId: 'c2',
      order: 2,
      text: 'The document contains the requested semantic content.',
      humanCheckHint: 'Read it and decide.',
    },
  ],
  machineTruthProblems: [],
  ...over,
});

/** What the Hub answers when a decision is recorded. */
export const acceptanceResult = (
  over: Partial<AcceptanceResultDto> = {},
): AcceptanceResultDto => ({
  taskKey: 'CXH-001',
  executionId: '01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3',
  attemptNo: 2,
  decision: 'Accept',
  overrideUsed: false,
  taskState: 'Accepted',
  executionState: 'Completed',
  executionOutcome: 'Passed',
  reason: null,
  workspaceCleanup: 'Released',
  ...over,
});
