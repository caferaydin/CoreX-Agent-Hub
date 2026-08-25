import type { ReactNode } from 'react';
import { api } from '../../api/client';
import { useQuery } from '../../api/useQuery';
import { Badge, Empty, Failed, Loading, Panel } from '../../components/primitives';
import { formatDuration } from '../../lib/format';
import { lookOfVerificationStatus } from '../../lib/status';

/**
 * What the repository's own checks said.
 *
 * Counts and exit codes only. The full output of a build is evidence, sometimes megabytes of
 * it, and putting it on the default view would make the page unusable for the case it exists
 * to serve - scanning which command failed.
 */
export function VerificationPanel({
  executionId,
  revision,
}: {
  executionId: string;
  revision: number;
}): ReactNode {
  const { state, refetch } = useQuery(
    (signal) => api.verification(executionId, signal),
    [executionId, revision],
  );

  return (
    <Panel title="Verification" flush>
      {state.status === 'loading' ? <Loading label="Loading verification" /> : null}
      {state.status === 'error' ? <Failed error={state.error} onRetry={refetch} /> : null}
      {state.status === 'ready' && state.data.length === 0 ? (
        <Empty
          title="No verification recorded"
          detail="This attempt did not reach verification, or the repository declares no profile."
        />
      ) : null}
      {state.status === 'ready' && state.data.length > 0 ? (
        <div className="table-scroll">
          <table className="table">
            <caption className="visually-hidden">Verification commands, in the order they ran</caption>
            <thead>
              <tr>
                <th scope="col">Command</th>
                <th scope="col">Result</th>
                <th scope="col">Required</th>
                <th scope="col" className="table__num">Exit</th>
                <th scope="col" className="table__num">Took</th>
                <th scope="col" className="table__num">Tests</th>
              </tr>
            </thead>
            <tbody>
              {state.data.map((command) => (
                <tr key={`${command.order}-${command.name}`}>
                  <th scope="row" className="mono" style={{ fontWeight: 400 }}>
                    {command.name}
                  </th>
                  <td>
                    <Badge look={lookOfVerificationStatus(command.status)} />
                  </td>
                  <td>{command.required ? 'Required' : <span className="muted">Optional</span>}</td>
                  <td className="table__num">
                    {command.exitCode === null ? <span className="muted">—</span> : command.exitCode}
                  </td>
                  <td className="table__num">{formatDuration(command.durationMilliseconds)}</td>
                  <td className="table__num">
                    {command.testsPassed === null && command.testsFailed === null ? (
                      <span className="muted">—</span>
                    ) : (
                      <span>
                        {command.testsPassed ?? 0} passed
                        {command.testsFailed ? ` · ${command.testsFailed} failed` : ''}
                        {command.testsSkipped ? ` · ${command.testsSkipped} skipped` : ''}
                      </span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </Panel>
  );
}
