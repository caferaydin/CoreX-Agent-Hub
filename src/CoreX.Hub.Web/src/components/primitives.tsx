import { useCallback, useState, type ReactNode } from 'react';
import { ApiError, TransportError } from '../api/client';
import { copyToClipboard, exactTime, formatTime, shorten } from '../lib/format';
import type { StatusLook } from '../lib/status';

/**
 * The handful of pieces every screen is made of.
 *
 * Deliberately small and local rather than a component library. What this UI needs is a badge,
 * a table, a panel and three states - importing an enterprise framework to obtain those would
 * bring a theming system, a bundle, and an opinion about layout that this product does not
 * need. If Phase 9C wants dialogs and menus, that is the moment to reconsider.
 */

// --- Status --------------------------------------------------------------------------------

/**
 * A state, shown three ways at once: the word, the fill and the outline shape.
 *
 * Colour is never the only channel. An operator with any form of colour blindness reads the
 * label; a screen reader reads the label and the meaning.
 */
export function Badge({ look }: { look: StatusLook }): ReactNode {
  return (
    <span className={`badge badge--${look.shape} badge--${look.tone}`} title={look.meaning}>
      {look.label}
    </span>
  );
}

// --- Panels --------------------------------------------------------------------------------

export function Panel({
  title,
  hint,
  actions,
  flush,
  children,
}: {
  title: string;
  hint?: ReactNode;
  actions?: ReactNode;
  flush?: boolean;
  children: ReactNode;
}): ReactNode {
  return (
    <section className="panel" aria-label={title}>
      <div className="panel__head">
        <h2 className="panel__title">{title}</h2>
        {actions ?? (hint ? <span className="panel__hint">{hint}</span> : null)}
      </div>
      <div className={flush ? 'panel__body panel__body--flush' : 'panel__body'}>{children}</div>
    </section>
  );
}

export function Field({ label, children }: { label: string; children: ReactNode }): ReactNode {
  return (
    <div>
      <div className="field__label">{label}</div>
      <div className="field__value">{children}</div>
    </div>
  );
}

// --- Loading, empty, error -----------------------------------------------------------------

export function Loading({ label = 'Loading' }: { label?: string }): ReactNode {
  return (
    <div className="state" role="status" aria-live="polite">
      <div className="stack" aria-hidden="true">
        <div className="skeleton" style={{ width: '38%' }} />
        <div className="skeleton" style={{ width: '72%' }} />
        <div className="skeleton" style={{ width: '55%' }} />
      </div>
      <span className="visually-hidden">{label}</span>
    </div>
  );
}

export function Empty({ title, detail }: { title: string; detail?: string }): ReactNode {
  return (
    <div className="state">
      <p className="state__title">{title}</p>
      {detail ? <p className="state__detail">{detail}</p> : null}
    </div>
  );
}

/**
 * A failure, said in the Hub's own words where it gave any.
 *
 * The correlation id is shown because that is how a local log entry is found. A stack trace is
 * not, and the server does not send one - this is only the last place that could leak it.
 */
export function Failed({ error, onRetry }: { error: unknown; onRetry?: () => void }): ReactNode {
  const described = describe(error);

  return (
    <div className="state state--error" role="alert">
      <p className="state__title">{described.title}</p>
      <p className="state__detail">{described.message}</p>
      {described.correlationId ? (
        <p className="state__correlation">Correlation {described.correlationId}</p>
      ) : null}
      {onRetry ? (
        <p style={{ marginTop: 'var(--space-4)' }}>
          <button type="button" className="copy" onClick={onRetry}>
            Try again
          </button>
        </p>
      ) : null}
    </div>
  );
}

function describe(error: unknown): {
  title: string;
  message: string;
  correlationId: string;
} {
  if (error instanceof ApiError) {
    return {
      title: error.status === 404 ? 'Not found' : 'The Hub refused this',
      message: error.envelope.message,
      correlationId: error.correlationId,
    };
  }

  if (error instanceof TransportError) {
    return { title: 'Could not reach the Hub', message: error.message, correlationId: '' };
  }

  return {
    title: 'Something went wrong',
    message: 'This panel could not be loaded.',
    correlationId: '',
  };
}

// --- Values --------------------------------------------------------------------------------

/**
 * A long identifier, shortened for reading and copyable in full.
 *
 * The shortened form is only ever shown. Anything that goes back to the API uses the whole
 * value, because a truncated id is a different string.
 */
export function Identifier({
  value,
  label,
  length = 12,
}: {
  value: string | null | undefined;
  label: string;
  length?: number;
}): ReactNode {
  const [copied, setCopied] = useState(false);

  const copy = useCallback(() => {
    if (!value) {
      return;
    }

    void copyToClipboard(value).then((ok) => {
      setCopied(ok);
      window.setTimeout(() => setCopied(false), 1400);
    });
  }, [value]);

  if (!value) {
    return <span className="muted">—</span>;
  }

  return (
    <button
      type="button"
      className="copy mono"
      onClick={copy}
      title={`${value}\nClick to copy`}
      aria-label={`Copy ${label} ${value}`}
    >
      {shorten(value, length)}
      <span className="muted" aria-hidden="true">
        {copied ? '✓' : '⧉'}
      </span>
    </button>
  );
}

/** A server timestamp, shown in the operator's zone with the exact instant behind it. */
export function Timestamp({ value }: { value: string | null | undefined }): ReactNode {
  return <time title={exactTime(value)}>{formatTime(value)}</time>;
}

/** A repository-relative path. Rendered as a value, never as markup. */
export function Path({ value }: { value: string }): ReactNode {
  return <span className="path">{value}</span>;
}
