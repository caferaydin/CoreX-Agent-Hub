import { useEffect, useId, useRef, type ReactNode } from 'react';
import { ApiError, TransportError } from '../api/client';

/**
 * The pieces an action needs: a button that says what it is doing, a dialog that can be used
 * from a keyboard, and one way of showing a refusal.
 */

/**
 * A button that changes something.
 *
 * `busy` is shown in the label as well as in the disabled attribute. A control that only went
 * grey would leave an operator guessing whether their click landed, and "Starting…" answers
 * that without a spinner needing to mean anything.
 */
export function ActionButton({
  onClick,
  busy,
  busyLabel,
  variant = 'default',
  disabled,
  children,
  ...rest
}: {
  onClick?: () => void;
  busy?: boolean;
  busyLabel?: string;
  variant?: 'default' | 'primary' | 'danger';
  disabled?: boolean;
  children: ReactNode;
  type?: 'button' | 'submit';
  form?: string;
}): ReactNode {
  return (
    <button
      type={rest.type ?? 'button'}
      form={rest.form}
      className={`button button--${variant}`}
      onClick={onClick}
      disabled={disabled === true || busy === true}
      aria-busy={busy === true}
    >
      {busy === true ? (busyLabel ?? 'Working…') : children}
    </button>
  );
}

/**
 * A refusal, in the Hub's own words.
 *
 * Announced, because a message that appeared silently below a button is a message a
 * keyboard-only operator never learns about.
 */
export function ActionError({ error }: { error: unknown }): ReactNode {
  const described = describe(error);

  return (
    <div className="notice notice--error" role="alert">
      <p className="notice__message">{described.message}</p>
      {described.details.length > 0 ? (
        <ul className="notice__details">
          {described.details.map((detail) => (
            <li key={detail}>{detail}</li>
          ))}
        </ul>
      ) : null}
      {described.correlationId ? (
        <p className="notice__correlation">Correlation {described.correlationId}</p>
      ) : null}
    </div>
  );
}

/** A neutral statement about something that happened. Not an error, not a success claim. */
export function ActionNotice({ children }: { children: ReactNode }): ReactNode {
  return (
    <div className="notice" role="status">
      <p className="notice__message">{children}</p>
    </div>
  );
}

function describe(error: unknown): {
  message: string;
  details: readonly string[];
  correlationId: string;
} {
  if (error instanceof ApiError) {
    return {
      message: error.envelope.message,
      details: error.envelope.details ?? [],
      correlationId: error.correlationId,
    };
  }

  if (error instanceof TransportError) {
    return { message: error.message, details: [], correlationId: '' };
  }

  // Never the raw exception: it may carry a path, and it will not mean anything to an operator.
  return {
    message: 'The Hub could not complete this. Nothing was changed as far as this page knows.',
    details: [],
    correlationId: '',
  };
}

/**
 * A confirmation, for the one action that can stop work in progress.
 *
 * A real `<dialog>` rather than a div with a high z-index: the browser gives modal semantics,
 * Escape, focus containment and the backdrop for free, and every hand-rolled version of those
 * is a keyboard trap waiting to happen.
 *
 * Used once, for Cancel. Confirming everything would train an operator to dismiss dialogs
 * without reading them, which costs exactly the moment this one exists for.
 */
export function ConfirmDialog({
  open,
  title,
  confirmLabel,
  busy,
  busyLabel,
  onConfirm,
  onDismiss,
  children,
}: {
  open: boolean;
  title: string;
  confirmLabel: string;
  busy?: boolean;
  busyLabel?: string;
  onConfirm: () => void;
  onDismiss: () => void;
  children: ReactNode;
}): ReactNode {
  const dialog = useRef<HTMLDialogElement>(null);
  const dismiss = useRef<HTMLButtonElement>(null);
  const headingId = useId();

  useEffect(() => {
    const element = dialog.current;

    if (!element) {
      return;
    }

    if (open && !element.open) {
      // showModal gives the focus trap and the inert background. jsdom implements the open
      // attribute but not always the method, so the fallback keeps tests meaningful.
      if (typeof element.showModal === 'function') {
        element.showModal();
      } else {
        element.setAttribute('open', '');
      }

      // Focus starts on the safe choice, not on the one that stops an agent.
      dismiss.current?.focus();
    }

    if (!open && element.open) {
      if (typeof element.close === 'function') {
        element.close();
      } else {
        element.removeAttribute('open');
      }
    }
  }, [open]);

  return (
    <dialog
      ref={dialog}
      className="dialog"
      aria-labelledby={headingId}
      onCancel={(event) => {
        // Escape. Dismissing is always allowed, even mid-request - it closes the dialog and
        // never un-sends anything.
        event.preventDefault();
        onDismiss();
      }}
    >
      <h2 className="dialog__title" id={headingId}>
        {title}
      </h2>
      <div className="dialog__body">{children}</div>
      <div className="dialog__actions">
        <button type="button" className="button" ref={dismiss} onClick={onDismiss}>
          Keep going
        </button>
        <ActionButton variant="danger" busy={busy} busyLabel={busyLabel} onClick={onConfirm}>
          {confirmLabel}
        </ActionButton>
      </div>
    </dialog>
  );
}
