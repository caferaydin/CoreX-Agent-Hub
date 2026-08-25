import { useId, useMemo, useState, type FormEvent, type ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { PageHeader } from '../App';
import { api } from '../api/client';
import { useMutation } from '../api/useMutation';
import { useQuery } from '../api/useQuery';
import type { CriterionKind, CriterionRequest } from '../api/contracts';
import { ActionButton, ActionError } from '../components/actions';
import { Failed, Loading, Panel } from '../components/primitives';
import { OPERATOR_ACTOR } from '../lib/actor';

/**
 * Creating a task.
 *
 * A dedicated route rather than a modal: this has a repository, an objective, a scope list and
 * a criteria list, and a dialog that size stops being a dialog and starts being a page that
 * has lost its URL.
 *
 * What the form does not ask for is as deliberate as what it does. There is no runtime, no
 * permission profile, no sandbox and no model - an operator declares a piece of work and what
 * it may touch, and how an agent is reached is the installation's business. Which agent runs
 * it is not asked here either, because the create contract does not take one: that choice
 * belongs to the moment it is run.
 *
 * Validation here is ergonomic only. "You have not written an objective" is worth catching
 * before a round trip; whether a task key is free, whether a repository is usable and whether
 * the criteria satisfy the ready gate are the Hub's answers, and this form does not guess at
 * any of them.
 */
export function CreateTaskPage(): ReactNode {
  const navigate = useNavigate();

  const organizations = useQuery((signal) => api.organizations(signal), []);
  const projects = useQuery((signal) => api.projects(undefined, signal), []);
  const repositories = useQuery((signal) => api.repositories(signal), []);

  const [taskKey, setTaskKey] = useState('');
  const [repositoryId, setRepositoryId] = useState('');
  const [objective, setObjective] = useState('');
  const [scope, setScope] = useState<readonly string[]>(['']);
  const [criteria, setCriteria] = useState<readonly Draft[]>([
    { kind: 'Machine', text: '', verificationRef: '' },
  ]);
  const [showProblems, setShowProblems] = useState(false);

  // Once: a create that succeeded must not be sent again. Everything else is protected by the
  // Hub refusing a duplicate, but a second create would be a second task.
  const create = useMutation(api.createTask, { once: true });

  const catalogue = useMemo(() => {
    if (
      organizations.state.status !== 'ready' ||
      projects.state.status !== 'ready' ||
      repositories.state.status !== 'ready'
    ) {
      return [];
    }

    // The create contract addresses a repository by organization and project slug, so the
    // three lists are joined here rather than asking the server for a shape it does not have.
    const projectById = new Map(projects.state.data.map((p) => [p.id, p]));
    const organizationById = new Map(organizations.state.data.map((o) => [o.id, o]));

    return repositories.state.data.flatMap((repository) => {
      const project = projectById.get(repository.projectId);
      const organization = project ? organizationById.get(project.organizationId) : undefined;

      return project && organization
        ? [{ repository, project, organization }]
        : [];
    });
  }, [organizations.state, projects.state, repositories.state]);

  const chosen = catalogue.find((entry) => entry.repository.id === repositoryId);
  const problems = validate({ objective, repositoryId, taskKey, criteria });

  const submit = async (event: FormEvent): Promise<void> => {
    event.preventDefault();
    setShowProblems(true);

    if (problems.size > 0 || !chosen || create.submitting) {
      return;
    }

    const created = await create.submit({
      organizationSlug: chosen.organization.slug,
      projectSlug: chosen.project.slug,
      repositorySlug: chosen.repository.slug,
      objective,
      ...(taskKey.trim().length > 0 ? { taskKey: taskKey.trim() } : {}),
      allowedScope: scope.map((path) => path.trim()).filter((path) => path.length > 0),
      forbiddenScope: [],
      criteria: criteria.filter((c) => c.text.trim().length > 0).map(toRequest),
      actor: OPERATOR_ACTOR,
    });

    // Only on success, and only to the key the Hub assigned - which may not be the one that
    // was typed. Nothing about the new task is constructed locally; the detail page fetches it.
    if (created) {
      navigate(`/tasks/${encodeURIComponent(created.taskKey)}`);
    }
  };

  const loading =
    organizations.state.status === 'loading' ||
    projects.state.status === 'loading' ||
    repositories.state.status === 'loading';

  const failed =
    organizations.state.status === 'error'
      ? organizations.state.error
      : projects.state.status === 'error'
        ? projects.state.error
        : repositories.state.status === 'error'
          ? repositories.state.error
          : null;

  return (
    <>
      <PageHeader title="New task" context="Describe the work and what it may change" />
      <div className="content">
        <div className="page">
          <Panel title="Task">
            {loading ? <Loading label="Loading repositories" /> : null}
            {failed ? <Failed error={failed} /> : null}

            {!loading && !failed ? (
              <form className="form" onSubmit={submit} noValidate>
                <RepositoryField
                  catalogue={catalogue}
                  value={repositoryId}
                  onChange={setRepositoryId}
                  problem={showProblems ? problems.get('repository') : undefined}
                  disabled={create.submitting}
                />

                <TaskKeyField
                  value={taskKey}
                  onChange={setTaskKey}
                  prefix={chosen?.project.taskKeyPrefix}
                  problem={showProblems ? problems.get('taskKey') : undefined}
                  disabled={create.submitting}
                />

                <ObjectiveField
                  value={objective}
                  onChange={setObjective}
                  problem={showProblems ? problems.get('objective') : undefined}
                  disabled={create.submitting}
                />

                <ScopeField value={scope} onChange={setScope} disabled={create.submitting} />

                <CriteriaField
                  value={criteria}
                  onChange={setCriteria}
                  problem={showProblems ? problems.get('criteria') : undefined}
                  disabled={create.submitting}
                />

                <div className="actions">
                  <ActionButton
                    type="submit"
                    variant="primary"
                    busy={create.submitting}
                    busyLabel="Creating…"
                  >
                    Create task
                  </ActionButton>
                  <ActionButton onClick={() => navigate('/tasks')} disabled={create.submitting}>
                    Discard
                  </ActionButton>
                </div>

                {/* The refusal, in the Hub's words. Nothing typed is cleared. */}
                {create.state.status === 'failed' ? (
                  <ActionError error={create.state.error} />
                ) : null}
              </form>
            ) : null}
          </Panel>
        </div>
      </div>
    </>
  );
}

// --- Fields ---------------------------------------------------------------------------------

interface Entry {
  readonly repository: { readonly id: string; readonly slug: string; readonly name: string };
  readonly project: { readonly slug: string; readonly taskKeyPrefix: string };
  readonly organization: { readonly slug: string };
}

function RepositoryField({
  catalogue,
  value,
  onChange,
  problem,
  disabled,
}: {
  catalogue: readonly Entry[];
  value: string;
  onChange: (value: string) => void;
  problem?: string;
  disabled: boolean;
}): ReactNode {
  const id = useId();

  return (
    <div className="form__field">
      <label className="form__label" htmlFor={id}>
        Repository
      </label>
      <select
        id={id}
        value={value}
        disabled={disabled}
        aria-invalid={problem !== undefined}
        aria-describedby={problem !== undefined ? `${id}-problem` : undefined}
        onChange={(event) => onChange(event.target.value)}
      >
        <option value="">Choose a repository…</option>
        {catalogue.map((entry) => (
          <option key={entry.repository.id} value={entry.repository.id}>
            {entry.organization.slug} / {entry.project.slug} / {entry.repository.slug}
          </option>
        ))}
      </select>
      {problem !== undefined ? (
        <p className="form__error" id={`${id}-problem`}>
          {problem}
        </p>
      ) : null}
    </div>
  );
}

function TaskKeyField({
  value,
  onChange,
  prefix,
  problem,
  disabled,
}: {
  value: string;
  onChange: (value: string) => void;
  prefix: string | undefined;
  problem?: string;
  disabled: boolean;
}): ReactNode {
  const id = useId();

  return (
    <div className="form__field">
      <label className="form__label" htmlFor={id}>
        Task key <span className="muted">(optional)</span>
      </label>
      <input
        id={id}
        type="text"
        className="mono"
        value={value}
        disabled={disabled}
        placeholder={prefix ? `${prefix}-001` : 'PROJECT-001'}
        aria-invalid={problem !== undefined}
        aria-describedby={`${id}-hint${problem !== undefined ? ` ${id}-problem` : ''}`}
        onChange={(event) => onChange(event.target.value)}
      />
      <p className="form__hint" id={`${id}-hint`}>
        Leave this empty and the Hub takes the next free number for the project. Whether a key
        is already in use is the Hub&rsquo;s answer, not this form&rsquo;s.
      </p>
      {problem !== undefined ? (
        <p className="form__error" id={`${id}-problem`}>
          {problem}
        </p>
      ) : null}
    </div>
  );
}

function ObjectiveField({
  value,
  onChange,
  problem,
  disabled,
}: {
  value: string;
  onChange: (value: string) => void;
  problem?: string;
  disabled: boolean;
}): ReactNode {
  const id = useId();

  return (
    <div className="form__field">
      <label className="form__label" htmlFor={id}>
        Objective
      </label>
      {/* Plain text, kept exactly as typed. Nothing rewrites it, nothing adds to it, and it is
          never rendered as markup - it reaches the agent as the operator wrote it. */}
      <textarea
        id={id}
        value={value}
        disabled={disabled}
        aria-invalid={problem !== undefined}
        aria-describedby={`${id}-hint${problem !== undefined ? ` ${id}-problem` : ''}`}
        onChange={(event) => onChange(event.target.value)}
      />
      <p className="form__hint" id={`${id}-hint`}>
        What the work is, in your own words.
      </p>
      {problem !== undefined ? (
        <p className="form__error" id={`${id}-problem`}>
          {problem}
        </p>
      ) : null}
    </div>
  );
}

/**
 * The paths an attempt may change.
 *
 * A repeatable list of text, and nothing more. No filesystem picker and no glob builder: these
 * are declarations about a repository the Hub will check out later, not places on this machine
 * to browse - and a picker would suggest otherwise.
 */
function ScopeField({
  value,
  onChange,
  disabled,
}: {
  value: readonly string[];
  onChange: (value: readonly string[]) => void;
  disabled: boolean;
}): ReactNode {
  const id = useId();

  return (
    <div className="form__field">
      <span className="form__label" id={`${id}-label`}>
        Allowed scope
      </span>
      <div className="repeatable" role="group" aria-labelledby={`${id}-label`}>
        {value.map((path, index) => (
          <div className="repeatable__row" key={index}>
            <input
              type="text"
              className="mono"
              value={path}
              disabled={disabled}
              placeholder="docs/**"
              aria-label={`Allowed path ${index + 1}`}
              onChange={(event) =>
                onChange(value.map((item, i) => (i === index ? event.target.value : item)))
              }
            />
            <ActionButton
              disabled={disabled || value.length === 1}
              onClick={() => onChange(value.filter((_, i) => i !== index))}
            >
              Remove
            </ActionButton>
          </div>
        ))}
        <div>
          <ActionButton disabled={disabled} onClick={() => onChange([...value, ''])}>
            Add path
          </ActionButton>
        </div>
      </div>
      <p className="form__hint">
        Globs relative to the repository. Empty rows are dropped when the task is created.
      </p>
    </div>
  );
}

interface Draft {
  readonly kind: CriterionKind;
  readonly text: string;
  readonly verificationRef: string;
}

/**
 * What the task has to satisfy.
 *
 * Two kinds, because the Hub has two: a Machine criterion is answered from recorded
 * verification evidence and names the step that answers it; a Human one is answered by a
 * person at acceptance time. The operator picks between those two and cannot invent a third.
 */
function CriteriaField({
  value,
  onChange,
  problem,
  disabled,
}: {
  value: readonly Draft[];
  onChange: (value: readonly Draft[]) => void;
  problem?: string;
  disabled: boolean;
}): ReactNode {
  const id = useId();

  const update = (index: number, patch: Partial<Draft>): void =>
    onChange(value.map((item, i) => (i === index ? { ...item, ...patch } : item)));

  return (
    <div className="form__field">
      <span className="form__label" id={`${id}-label`}>
        Acceptance criteria
      </span>
      <div
        className="repeatable"
        role="group"
        aria-labelledby={`${id}-label`}
        aria-describedby={problem !== undefined ? `${id}-problem` : undefined}
      >
        {value.map((criterion, index) => (
          <div className="criterion" key={index}>
            <select
              value={criterion.kind}
              disabled={disabled}
              aria-label={`Criterion ${index + 1} kind`}
              onChange={(event) => update(index, { kind: event.target.value as CriterionKind })}
            >
              <option value="Machine">Machine</option>
              <option value="Human">Human</option>
            </select>

            <div className="stack">
              <input
                type="text"
                value={criterion.text}
                disabled={disabled}
                placeholder="What has to be true"
                aria-label={`Criterion ${index + 1} text`}
                onChange={(event) => update(index, { text: event.target.value })}
              />
              {criterion.kind === 'Machine' ? (
                <input
                  type="text"
                  className="mono"
                  value={criterion.verificationRef}
                  disabled={disabled}
                  placeholder="verification step, e.g. build"
                  aria-label={`Criterion ${index + 1} verification step`}
                  onChange={(event) => update(index, { verificationRef: event.target.value })}
                />
              ) : null}
            </div>

            <ActionButton
              disabled={disabled || value.length === 1}
              onClick={() => onChange(value.filter((_, i) => i !== index))}
            >
              Remove
            </ActionButton>
          </div>
        ))}
        <div>
          <ActionButton
            disabled={disabled}
            onClick={() => onChange([...value, { kind: 'Human', text: '', verificationRef: '' }])}
          >
            Add criterion
          </ActionButton>
        </div>
      </div>
      <p className="form__hint">
        Machine criteria are answered from verification evidence and name the step that answers
        them. Human criteria are answered by a person when the work is accepted.
      </p>
      {problem !== undefined ? (
        <p className="form__error" id={`${id}-problem`}>
          {problem}
        </p>
      ) : null}
    </div>
  );
}

// --- Ergonomics ------------------------------------------------------------------------------

const toRequest = (draft: Draft): CriterionRequest => ({
  kind: draft.kind,
  text: draft.text.trim(),
  verificationRef:
    draft.kind === 'Machine' && draft.verificationRef.trim().length > 0
      ? draft.verificationRef.trim()
      : null,
  humanCheckHint: null,
});

/**
 * What is worth catching before a round trip.
 *
 * Only the things that are certainly wrong regardless of what the Hub thinks: nothing chosen,
 * nothing written, a key that could not be a key. Everything else - is this key taken, is this
 * repository usable, do these criteria satisfy the gate - is the Hub's to answer, and guessing
 * would put a second copy of its rules here.
 */
function validate(form: {
  objective: string;
  repositoryId: string;
  taskKey: string;
  criteria: readonly Draft[];
}): ReadonlyMap<string, string> {
  const problems = new Map<string, string>();

  if (form.repositoryId.length === 0) {
    problems.set('repository', 'Choose the repository this task is about.');
  }

  if (form.objective.trim().length === 0) {
    problems.set('objective', 'Describe what the work is.');
  }

  const key = form.taskKey.trim();

  if (key.length > 0 && !/^[A-Za-z][A-Za-z0-9]*-\d{3}$/.test(key)) {
    problems.set('taskKey', 'A key looks like PROJECT-001. Leave it empty to let the Hub pick.');
  }

  if (form.criteria.every((criterion) => criterion.text.trim().length === 0)) {
    problems.set('criteria', 'State at least one thing this task has to satisfy.');
  }

  return problems;
}
