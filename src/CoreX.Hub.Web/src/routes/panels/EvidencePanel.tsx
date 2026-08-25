import type { ReactNode } from 'react';
import { api } from '../../api/client';
import { useQuery } from '../../api/useQuery';
import { Empty, Failed, Identifier, Loading, Panel, Timestamp } from '../../components/primitives';
import { formatBytes } from '../../lib/format';

/**
 * What was recorded, listed but not opened.
 *
 * Metadata only, on purpose. Some of these artifacts are megabytes of provider output, and a
 * panel that fetched each one to show a row would make opening an execution expensive in
 * exactly the situation where an operator is already waiting.
 *
 * There is no "View" action in Phase 9B. Raw evidence viewing is a deliberate later step: the
 * endpoint exists and is safe, but a text viewer needs its own decisions about size, encoding
 * and what to do with content that is not text, and this slice is about proving the read
 * experience rather than accumulating half of one.
 */
export function EvidencePanel({
  executionId,
  revision,
}: {
  executionId: string;
  revision: number;
}): ReactNode {
  const { state, refetch } = useQuery(
    (signal) => api.evidence(executionId, signal),
    [executionId, revision],
  );

  return (
    <Panel
      title="Evidence"
      hint={state.status === 'ready' ? `${state.data.length} artifacts` : undefined}
      flush
    >
      {state.status === 'loading' ? <Loading label="Loading evidence" /> : null}
      {state.status === 'error' ? <Failed error={state.error} onRetry={refetch} /> : null}
      {state.status === 'ready' && state.data.length === 0 ? (
        <Empty
          title="Nothing recorded"
          detail="This attempt captured no evidence, which usually means it stopped before doing any work."
        />
      ) : null}
      {state.status === 'ready' && state.data.length > 0 ? (
        <div className="table-scroll">
          <table className="table">
            <caption className="visually-hidden">Evidence artifacts recorded for this attempt</caption>
            <thead>
              <tr>
                <th scope="col">Kind</th>
                <th scope="col">Step</th>
                <th scope="col" className="table__num">Size</th>
                <th scope="col">Type</th>
                <th scope="col">Hash</th>
                <th scope="col">Redactions</th>
                <th scope="col">Recorded</th>
              </tr>
            </thead>
            <tbody>
              {state.data.map((artifact) => (
                <tr key={artifact.id}>
                  <th scope="row" style={{ fontWeight: 400 }}>{artifact.kind}</th>
                  <td>
                    {artifact.executionStepId ? (
                      <Identifier value={artifact.executionStepId} label="step id" length={8} />
                    ) : (
                      <span className="muted">execution</span>
                    )}
                  </td>
                  <td className="table__num">{formatBytes(artifact.sizeBytes)}</td>
                  <td className="mono">{artifact.mediaType}</td>
                  <td>
                    <Identifier value={artifact.sha256} label="content hash" />
                  </td>
                  <td className="table__num">
                    {artifact.redactionCount > 0 ? (
                      artifact.redactionCount
                    ) : (
                      <span className="muted">none</span>
                    )}
                  </td>
                  <td>
                    <Timestamp value={artifact.createdAtUtc} />
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
