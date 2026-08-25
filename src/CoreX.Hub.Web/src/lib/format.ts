/**
 * How values are written down, decided once.
 *
 * Small functions, but each of them is somewhere a bespoke version would have drifted: three
 * screens formatting a duration three ways, or one of them quietly showing a server timestamp
 * as if it were local.
 */

/**
 * A duration a person can read at a glance.
 *
 * No fake precision: a run that took four minutes is "4m 12s", not "4m 12.384s". Sub-second
 * work keeps its milliseconds because that is the range where the difference matters.
 */
export function formatDuration(milliseconds: number | null | undefined): string {
  if (milliseconds === null || milliseconds === undefined || Number.isNaN(milliseconds)) {
    return '—';
  }

  if (milliseconds < 0) {
    return '—';
  }

  if (milliseconds < 1000) {
    return `${Math.round(milliseconds)} ms`;
  }

  const seconds = milliseconds / 1000;

  if (seconds < 60) {
    return `${seconds.toFixed(1)} s`;
  }

  const wholeMinutes = Math.floor(seconds / 60);
  const remainder = Math.round(seconds - wholeMinutes * 60);

  if (wholeMinutes < 60) {
    return `${wholeMinutes}m ${remainder}s`;
  }

  const hours = Math.floor(wholeMinutes / 60);

  return `${hours}h ${wholeMinutes - hours * 60}m`;
}

/** The gap between two server timestamps, or null when either is missing. */
export function durationBetween(
  from: string | null | undefined,
  to: string | null | undefined,
): number | null {
  if (!from || !to) {
    return null;
  }

  const start = Date.parse(from);
  const end = Date.parse(to);

  return Number.isNaN(start) || Number.isNaN(end) ? null : end - start;
}

/**
 * Server time is UTC; operators live somewhere. The browser does the conversion, because it
 * is the only thing that knows where that is.
 */
export function formatTime(utc: string | null | undefined): string {
  if (!utc) {
    return '—';
  }

  const parsed = new Date(utc);

  return Number.isNaN(parsed.getTime())
    ? '—'
    : parsed.toLocaleString(undefined, {
        year: 'numeric',
        month: 'short',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
      });
}

/** The exact instant, for a tooltip beside the friendly one. */
export function exactTime(utc: string | null | undefined): string {
  if (!utc) {
    return '';
  }

  const parsed = new Date(utc);

  return Number.isNaN(parsed.getTime()) ? '' : `${parsed.toISOString()} (UTC)`;
}

/** "3 minutes ago", for the column an operator scans rather than reads. */
export function formatRelative(utc: string | null | undefined, now: number = Date.now()): string {
  if (!utc) {
    return '—';
  }

  const parsed = Date.parse(utc);

  if (Number.isNaN(parsed)) {
    return '—';
  }

  const seconds = Math.round((now - parsed) / 1000);

  if (seconds < 45) {
    return 'just now';
  }

  const units: readonly (readonly [Intl.RelativeTimeFormatUnit, number])[] = [
    ['year', 31536000],
    ['month', 2592000],
    ['day', 86400],
    ['hour', 3600],
    ['minute', 60],
  ];

  const relative = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });

  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size) {
      return relative.format(-Math.round(seconds / size), unit);
    }
  }

  return relative.format(-seconds, 'second');
}

/**
 * A git object id or a guid, short enough to scan.
 *
 * Only ever for display. Every call that goes back to the API uses the full value, because a
 * shortened id is a different string and the Hub would rightly not recognise it.
 */
export function shorten(value: string | null | undefined, length = 12): string {
  if (!value) {
    return '—';
  }

  return value.length <= length ? value : value.slice(0, length);
}

/** Bytes, for evidence sizes. */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || Number.isNaN(bytes)) {
    return '—';
  }

  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;

  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }

  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

/**
 * Puts a value on the clipboard, and says whether it worked.
 *
 * Never throws. The clipboard is unavailable in plenty of ordinary situations - an insecure
 * origin, a denied permission - and a copy button that took the page down with it would be a
 * poor trade for a convenience.
 */
export async function copyToClipboard(value: string): Promise<boolean> {
  try {
    if (!navigator.clipboard?.writeText) {
      return false;
    }

    await navigator.clipboard.writeText(value);

    return true;
  } catch {
    return false;
  }
}
