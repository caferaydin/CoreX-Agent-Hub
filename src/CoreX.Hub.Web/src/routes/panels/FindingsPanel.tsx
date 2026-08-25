import type { ReactNode } from 'react';
import { api } from '../../api/client';
import { useQuery } from '../../api/useQuery';
import type { FindingDto } from '../../api/contracts';
import { Badge, Empty, Failed, Loading, Panel } from '../../components/primitives';
import { lookOfSeverity, severityRank } from '../../lib/status';

/**
 * What the independent reviewer found.
 *
 * These are the Hub's canonical findings, not a provider's document: the severity, the scope
 * flag and the verdict all come from the Hub's own gate, which is why nothing on this screen
 * needs to know which reviewer ran.
 *
 * Out-of-scope findings are shown and marked, never hidden. A reviewer noticing something real
 * outside the declared scope is worth reading; what it is not is a reason to block, and the
 * gate has already made that distinction.
 */
export function FindingsPanel({
  executionId,
  revision,
}: {
  executionId: string;
  revision: number;
}): ReactNode {
  const { state, refetch } = useQuery(
    (signal) => api.review(executionId, signal),
    [executionId, revision],
  );

  const hint =
    state.status === 'ready'
      ? state.data.blocks
        ? `Blocks acceptance · threshold ${state.data.majorThreshold} major`
        : 'Nothing blocking'
      : undefined;

  return (
    <Panel title="Review findings" hint={hint} flush>
      {state.status === 'loading' ? <Loading label="Loading findings" /> : null}
      {state.status === 'error' ? <Failed error={state.error} onRetry={refetch} /> : null}

      {state.status === 'ready' && state.data.findings.length === 0 ? (
        <Empty
          title="No findings"
          detail={
            state.data.summary ||
            'The reviewer returned an empty findings list, or this attempt was never reviewed.'
          }
        />
      ) : null}

      {state.status === 'ready' && state.data.findings.length > 0 ? (
        <>
          <div className="panel__body" style={{ paddingBottom: 0 }}>
            <p className="muted" style={{ marginTop: 0 }}>
              {state.data.summary}
            </p>
            <div className="row" style={{ marginBottom: 'var(--space-2)' }}>
              {Object.entries(state.data.inScopeCountsBySeverity)
                .sort(([a], [b]) => severityRank(a) - severityRank(b))
                .map(([severity, count]) => (
                  <span key={severity} className="row" style={{ gap: 'var(--space-1)' }}>
                    <Badge look={lookOfSeverity(severity)} />
                    <span className="muted">{count} in scope</span>
                  </span>
                ))}
            </div>
          </div>

          <div>
            {[...state.data.findings]
              .sort(order)
              .map((finding) => (
                <Finding key={finding.id} finding={finding} />
              ))}
          </div>
        </>
      ) : null}
    </Panel>
  );
}

/** Blocking first, then by severity, then by file - what a reader needs, in that order. */
function order(a: FindingDto, b: FindingDto): number {
  if (a.outOfScope !== b.outOfScope) {
    return a.outOfScope ? 1 : -1;
  }

  const bySeverity = severityRank(a.severity) - severityRank(b.severity);

  if (bySeverity !== 0) {
    return bySeverity;
  }

  return (a.file ?? '').localeCompare(b.file ?? '');
}

function Finding({ finding }: { finding: FindingDto }): ReactNode {
  return (
    <article className={finding.outOfScope ? 'finding finding--out-of-scope' : 'finding'}>
      <div className="finding__head">
        <Badge look={lookOfSeverity(finding.severity)} />
        <span className="finding__title">{finding.title}</span>
        {finding.outOfScope ? (
          <Badge
            look={{
              label: 'Out of scope',
              tone: 'muted',
              shape: 'outline',
              meaning:
                'The Hub computed that this is about a file the attempt did not change, so it does not count towards blocking.',
            }}
          />
        ) : null}
      </div>

      {finding.file ? (
        <div className="finding__where">
          {finding.file}
          {finding.line !== null ? `:${finding.line}` : ''}
        </div>
      ) : null}

      {finding.rationale ? <p className="finding__body">{finding.rationale}</p> : null}

      <div className="row muted" style={{ fontSize: 'var(--text-sm)', marginTop: 'var(--space-2)' }}>
        <span>{finding.category}</span>
        <span aria-hidden="true">·</span>
        <span>{finding.confidence} confidence</span>
      </div>

      {finding.suggestedFix ? (
        <p className="finding__fix">
          <span className="field__label">Suggested fix</span>
          {finding.suggestedFix}
        </p>
      ) : null}
    </article>
  );
}
