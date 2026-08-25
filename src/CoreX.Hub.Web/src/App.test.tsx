import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { App } from './App';
import { stubFetch } from './test/render';
import { taskPage } from './test/fixtures';

/**
 * The shell, and the promise this whole slice makes: it can be looked at and not touched.
 */
describe('App shell', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const show = (at = '/'): void => {
    render(
      <MemoryRouter initialEntries={[at]}>
        <App />
      </MemoryRouter>,
    );
  };

  it('offers the three places an operator goes', () => {
    stubFetch({ '/api/v1/tasks': { body: taskPage([]) } });

    show();

    const nav = screen.getByRole('navigation', { name: 'Main' });

    expect(nav).toBeInTheDocument();

    for (const [name, href] of [
      ['Tasks', '/tasks'],
      ['Repositories', '/repositories'],
      ['Agent profiles', '/agents'],
    ] as const) {
      expect(screen.getByRole('link', { name })).toHaveAttribute('href', href);
    }
  });

  it('lands on tasks', async () => {
    stubFetch({ '/api/v1/tasks': { body: taskPage([]) } });

    show('/');

    expect(await screen.findByRole('heading', { name: 'Tasks', level: 1 })).toBeInTheDocument();
  });

  it('says so for a route that does not exist, instead of a blank page', () => {
    stubFetch({ '/api/v1': { body: {} } });

    show('/nowhere');

    expect(screen.getByRole('heading', { name: 'Not found', level: 1 })).toBeInTheDocument();
    expect(screen.getByText(/There is no page at \/nowhere/)).toBeInTheDocument();
  });

  it('navigates with real links, so the keyboard and the browser both work', () => {
    stubFetch({ '/api/v1/tasks': { body: taskPage([]) } });

    show();

    // Not divs with click handlers: every destination is an anchor with an href, which is what
    // makes middle-click, focus order and screen-reader navigation work without extra code.
    for (const link of screen.getAllByRole('link')) {
      expect(link.tagName).toBe('A');
      expect(link).toHaveAttribute('href');
    }
  });

  it('has no mutation control anywhere in the shell', () => {
    stubFetch({ '/api/v1/tasks': { body: taskPage([]) } });

    show();

    for (const forbidden of [
      /create/i,
      /new task/i,
      /ready/i,
      /^run$/i,
      /cancel/i,
      /accept/i,
      /override/i,
      /request changes/i,
      /reject/i,
    ]) {
      expect(screen.queryByRole('button', { name: forbidden })).not.toBeInTheDocument();
      expect(screen.queryByRole('link', { name: forbidden })).not.toBeInTheDocument();
    }
  });
});
