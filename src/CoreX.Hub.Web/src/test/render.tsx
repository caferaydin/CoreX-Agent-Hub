import { render, type RenderResult } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

/**
 * How a screen is put on the page in a test.
 *
 * Routed, because most of these screens read a parameter from the URL and a test that supplied
 * it as a prop would be testing something the application never does.
 */
export function renderAt(path: string, pattern: string, element: ReactNode): RenderResult {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path={pattern} element={element} />
      </Routes>
    </MemoryRouter>,
  );
}

/**
 * A `fetch` that answers from a table of routes.
 *
 * Matching on a substring of the URL keeps the tests readable and keeps them honest: the path
 * they name is the path the client really builds, so a client that started calling the wrong
 * endpoint would stop matching and fail.
 */
export interface StubbedResponse {
  readonly status?: number;
  readonly body?: unknown;
  readonly text?: string;
}

export function stubFetch(routes: Record<string, StubbedResponse | (() => never)>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();

      for (const [fragment, answer] of Object.entries(routes)) {
        if (!url.includes(fragment)) {
          continue;
        }

        if (typeof answer === 'function') {
          // Used for "the network never reached the Hub".
          answer();
        }

        const { status = 200, body, text } = answer as StubbedResponse;

        return new Response(text ?? (body === undefined ? '' : JSON.stringify(body)), {
          status,
          headers: { 'Content-Type': text === undefined ? 'application/json' : 'text/plain' },
        });
      }

      throw new Error(`No stub for ${url}`);
    }),
  );
}

/** The Hub's own error envelope, for the tests that assert how a refusal is shown. */
export const envelope = (code: string, message: string, correlationId = 'abc123') => ({
  code,
  message,
  correlationId,
});
