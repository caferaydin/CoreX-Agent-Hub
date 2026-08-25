import { screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ExecutionDetailPage } from './ExecutionDetailPage';
import { envelope, renderAt, stubFetch } from '../test/render';
import {
  diffText,
  evidence,
  executionDetail,
  review,
  verification,
} from '../test/fixtures';

const id = '01a039bf-4fa9-7f71-9f09-f1c4cf41c0c3';

const everything = (over: Record<string, unknown> = {}) => ({
  [`/executions/${id}/verification`]: { body: verification() },
  [`/executions/${id}/review`]: { body: review() },
  [`/executions/${id}/evidence`]: { body: evidence() },
  [`/executions/${id}/diff`]: { text: diffText },
  [`/executions/${id}`]: { body: executionDetail() },
  ...over,
});

const show = (): void => {
  renderAt(`/executions/${id}`, '/executions/:executionId', <ExecutionDetailPage />);
};

/**
 * The diagnostic screen.
 *
 * Most of what is asserted here is about not losing information: every step state stays
 * distinguishable, a blocked review does not read as a failure, an out-of-scope finding is
 * shown and marked rather than hidden, and one panel failing does not take the others with it.
 */
describe('ExecutionDetailPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders the timeline with each step state kept distinct', async () => {
    stubFetch(everything());

    show();

    await screen.findByText('Prepare workspace');

    // Four different states, four different words. None of them collapsed into the others.
    expect(screen.getAllByText('Succeeded').length).toBeGreaterThanOrEqual(2);
    expect(screen.getByText('Skipped')).toBeInTheDocument();
    expect(screen.getAllByText('Failed').length).toBeGreaterThanOrEqual(1);

    // And the step says why, in the Hub's words.
    expect(screen.getByText(/VerificationDidNotPass/)).toBeInTheDocument();
  });

  it('shows the commits and identifiers a person needs to copy', async () => {
    stubFetch(everything());

    show();

    await screen.findByText('Summary');

    expect(
      screen.getByRole('button', { name: /Copy result commit 9b2ea76dacd8/ }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Copy base commit 67dbafab56c6/ })).toBeInTheDocument();
  });

  it('renders verification results with counts rather than raw output', async () => {
    stubFetch(everything());

    show();

    // The panel title appears while it is still loading, so wait for a row.
    await screen.findByText('restore');

    expect(screen.getByText(/1434 passed/)).toBeInTheDocument();

    // Skipped is its own answer, not a failure.
    expect(screen.getByText('ef-pending-model')).toBeInTheDocument();
  });

  it('shows findings ordered by what blocks, and marks out-of-scope rather than hiding it', async () => {
    stubFetch(
      everything({
        [`/executions/${id}/review`]: {
          body: review({
            findings: [
              {
                id: 'f-info',
                severity: 'Info',
                category: 'Style',
                title: 'An observation',
                rationale: null,
                file: 'a.cs',
                line: 1,
                suggestedFix: null,
                confidence: 'Low',
                outOfScope: false,
              },
              {
                id: 'f-out',
                severity: 'Critical',
                category: 'Security',
                title: 'Something outside the scope',
                rationale: null,
                file: 'elsewhere.cs',
                line: 2,
                suggestedFix: null,
                confidence: 'High',
                outOfScope: true,
              },
              {
                id: 'f-crit',
                severity: 'Critical',
                category: 'Security',
                title: 'Authorization always succeeds',
                rationale: 'It returns true for every input.',
                file: 'probe.cs',
                line: 18,
                suggestedFix: 'Return false for a missing token.',
                confidence: 'High',
                outOfScope: false,
              },
            ],
            inScopeCountsBySeverity: { Critical: 1, Info: 1 },
          }),
        },
      }),
    );

    show();

    await screen.findByText('Authorization always succeeds');

    const titles = screen
      .getAllByText(/An observation|Something outside the scope|Authorization always succeeds/)
      .map((node) => node.textContent);

    // Blocking first, out-of-scope last.
    expect(titles[0]).toBe('Authorization always succeeds');
    expect(titles[titles.length - 1]).toBe('Something outside the scope');

    // Shown, and labelled as not counting.
    expect(screen.getByText('Out of scope')).toBeInTheDocument();
    expect(screen.getByText('Return false for a missing token.')).toBeInTheDocument();
  });

  it('renders the diff as text, with headers not mistaken for added lines', async () => {
    stubFetch(everything());

    show();

    const diff = await screen.findByLabelText('Unified diff of the result commit');

    expect(diff).toHaveTextContent('# Phase 8 Codex Reviewer Acceptance');

    const lines = Array.from(diff.querySelectorAll('.diff__line'));
    const classOf = (starts: string): string | undefined =>
      lines.find((l) => l.textContent?.startsWith(starts))?.className;

    // '+++' and '---' are file headers, not an addition and a deletion.
    expect(classOf('+++')).toContain('diff__line--meta');
    expect(classOf('---')).toContain('diff__line--meta');
    expect(classOf('@@')).toContain('diff__line--hunk');
    expect(classOf('+# Phase 8')).toContain('diff__line--add');
    expect(classOf('-removed')).toContain('diff__line--del');
  });

  it('never builds markup out of diff content', async () => {
    stubFetch(
      everything({
        [`/executions/${id}/diff`]: { text: '+<img src=x onerror="alert(1)">\n' },
      }),
    );

    show();

    const diff = await screen.findByLabelText('Unified diff of the result commit');

    // The angle brackets are characters on the screen, not an element in the tree.
    expect(diff).toHaveTextContent('<img src=x onerror="alert(1)">');
    expect(diff.querySelector('img')).toBeNull();
  });

  it('keeps working panels when one of them fails', async () => {
    stubFetch(
      everything({
        [`/executions/${id}/diff`]: {
          status: 500,
          body: envelope('internal_error', 'The Hub could not complete this request', 'corr-9'),
        },
      }),
    );

    show();

    // The findings are there…
    await screen.findByText('Authorization check always succeeds');

    // …and the diff says what went wrong, on its own.
    await waitFor(() =>
      expect(screen.getByText('The Hub could not complete this request')).toBeInTheDocument(),
    );

    expect(screen.getByText('Verification')).toBeInTheDocument();
  });

  it('does not ask for a diff when the attempt produced no commit', async () => {
    const fetches: string[] = [];

    stubFetch(everything({ [`/executions/${id}`]: { body: executionDetail({ resultCommitSha: null }) } }));

    const original = globalThis.fetch;
    vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
      fetches.push(typeof input === 'string' ? input : input.toString());
      return original(input, init);
    });

    show();

    await screen.findByText('No result commit');

    expect(fetches.some((url) => url.endsWith('/diff'))).toBe(false);
  });

  it('offers no control that would change Hub state', async () => {
    stubFetch(everything());

    show();

    await screen.findByText('Summary');

    const actions = screen
      .queryAllByRole('button')
      .map((button) => button.getAttribute('aria-label') ?? button.textContent ?? '');

    // The only buttons on this page copy an identifier.
    expect(actions.every((label) => /^Copy /.test(label))).toBe(true);
  });
});
