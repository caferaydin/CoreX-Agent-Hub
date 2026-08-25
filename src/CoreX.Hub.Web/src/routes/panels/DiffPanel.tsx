import { useMemo, type ReactNode } from 'react';
import { api } from '../../api/client';
import { useQuery } from '../../api/useQuery';
import { Empty, Failed, Loading, Panel } from '../../components/primitives';

/**
 * The change, as text.
 *
 * Deliberately simple. This is a unified diff the Hub already stored as evidence, and the job
 * here is to make it readable - monospace, whitespace preserved, added and removed lines
 * distinguishable - not to become a code review editor. Monaco and a real review surface are
 * later work; putting them here would mean shipping an editor before the read experience has
 * been shown to be right.
 *
 * The important property is that every line is rendered as text. React escapes it, nothing
 * assembles markup from it, and there is no innerHTML anywhere in this file - a diff is
 * content the Hub captured from an agent, and content from an agent is never markup.
 */

/** Past this, the panel shows a head and says what it kept back. */
const MAX_LINES = 3000;

export function DiffPanel({
  executionId,
  hasResult,
}: {
  executionId: string;
  hasResult: boolean;
}): ReactNode {
  const { state, refetch } = useQuery((signal) => api.diff(executionId, signal), [executionId], {
    // An attempt with no result commit has no diff to fetch; asking would be a guaranteed 404.
    enabled: hasResult,
  });

  const lines = useMemo(
    () => (state.status === 'ready' ? state.data.split('\n') : []),
    [state],
  );

  const shown = lines.length > MAX_LINES ? lines.slice(0, MAX_LINES) : lines;

  if (!hasResult) {
    return (
      <Panel title="Diff">
        <Empty
          title="No result commit"
          detail="This attempt produced no commit, so there is no diff to show."
        />
      </Panel>
    );
  }

  return (
    <Panel
      title="Diff"
      hint={state.status === 'ready' ? `${lines.length} lines` : undefined}
      flush
    >
      {state.status === 'loading' ? <Loading label="Loading diff" /> : null}
      {state.status === 'error' ? <Failed error={state.error} onRetry={refetch} /> : null}

      {state.status === 'ready' && state.data.trim().length === 0 ? (
        <Empty title="The diff is empty" detail="The stored artifact contains no changes." />
      ) : null}

      {state.status === 'ready' && state.data.trim().length > 0 ? (
        <>
          <pre className="diff" tabIndex={0} aria-label="Unified diff of the result commit">
            {shown.map((line, index) => (
              <span key={index} className={`diff__line diff__line--${classify(line)}`}>
                {line}
                {'\n'}
              </span>
            ))}
          </pre>
          {lines.length > MAX_LINES ? (
            <p className="diff__truncated">
              Showing the first {MAX_LINES.toLocaleString()} of {lines.length.toLocaleString()}{' '}
              lines.
            </p>
          ) : null}
        </>
      ) : null}
    </Panel>
  );
}

/**
 * Which kind of diff line this is.
 *
 * Order matters: `+++` and `---` are file headers, not an added and a removed line, and
 * checking the single-character cases first would colour every header wrongly.
 */
function classify(line: string): 'add' | 'del' | 'hunk' | 'meta' | 'ctx' {
  if (line.startsWith('+++') || line.startsWith('---')) {
    return 'meta';
  }

  if (line.startsWith('@@')) {
    return 'hunk';
  }

  if (line.startsWith('+')) {
    return 'add';
  }

  if (line.startsWith('-')) {
    return 'del';
  }

  if (
    line.startsWith('diff --git') ||
    line.startsWith('index ') ||
    line.startsWith('new file') ||
    line.startsWith('deleted file') ||
    line.startsWith('similarity index') ||
    line.startsWith('rename ')
  ) {
    return 'meta';
  }

  return 'ctx';
}
