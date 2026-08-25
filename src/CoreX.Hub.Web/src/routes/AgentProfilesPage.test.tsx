import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AgentProfilesPage } from './AgentProfilesPage';
import { RepositoriesPage } from './RepositoriesPage';
import { renderAt, stubFetch } from '../test/render';
import { agentProfile, repository } from '../test/fixtures';

/**
 * The runtime screens, and the property that matters most about them: the UI knows which
 * provider is configured only as a label it prints.
 */
describe('AgentProfilesPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders either provider identically, branching on neither', async () => {
    stubFetch({
      '/api/v1/agent-profiles': {
        body: [
          agentProfile(),
          agentProfile({
            name: 'codex-reviewer',
            role: 'Reviewer',
            provider: 'Codex',
            runtimeKey: 'codex-reviewer-runtime',
            permissionProfile: 'ReadOnly',
            model: 'gpt-5.5',
          }),
        ],
      },
    });

    renderAt('/agents', '/agents', <AgentProfilesPage />);

    await screen.findByText('claude-implementer');

    const rows = screen.getAllByRole('row').slice(1);

    expect(rows).toHaveLength(2);

    // Same columns, same treatment. The provider is a value in a cell, and nothing in the
    // component reads it to decide anything.
    for (const row of rows) {
      expect(row.querySelectorAll('td, th')).toHaveLength(6);
    }

    expect(screen.getByText('ClaudeCode')).toBeInTheDocument();
    expect(screen.getByText('Codex')).toBeInTheDocument();

    // The reviewer's read-only permission is stated, because that is the whole reason a
    // reviewer cannot change what it reviews.
    expect(screen.getByText('Read only')).toBeInTheDocument();
  });

  it('marks a profile whose runtime this installation does not declare', async () => {
    stubFetch({
      '/api/v1/agent-profiles': {
        body: [agentProfile({ runtimeDeclared: false })],
      },
    });

    renderAt('/agents', '/agents', <AgentProfilesPage />);

    expect(await screen.findByText('Not declared')).toBeInTheDocument();
  });

  it('shows nothing about how a provider is reached', async () => {
    stubFetch({ '/api/v1/agent-profiles': { body: [agentProfile()] } });

    const { container } = renderAt('/agents', '/agents', <AgentProfilesPage />);

    await screen.findByText('claude-implementer');

    // No path, no environment, no credential - the server does not send them and there is
    // nothing here that would render one if it did.
    const text = container.textContent ?? '';

    for (const forbidden of ['.exe', 'C:\\', 'token', 'ANTHROPIC', 'OPENAI', 'auth.json']) {
      expect(text).not.toContain(forbidden);
    }
  });

  it('has no control that would edit provider configuration', async () => {
    stubFetch({ '/api/v1/agent-profiles': { body: [agentProfile()] } });

    renderAt('/agents', '/agents', <AgentProfilesPage />);

    await screen.findByText('claude-implementer');

    expect(screen.queryAllByRole('button')).toHaveLength(0);
    expect(screen.queryAllByRole('textbox')).toHaveLength(0);
  });
});

describe('RepositoriesPage', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows which profile reviews a repository, as data', async () => {
    stubFetch({ '/api/v1/repositories': { body: [repository()] } });

    renderAt('/repositories', '/repositories', <RepositoriesPage />);

    await screen.findByText('corex-agent-hub');

    // After Phase 8 the provider switch is a line of configuration, and this is where an
    // operator sees which way it is set.
    expect(screen.getByText('codex-reviewer')).toBeInTheDocument();
    expect(screen.getByText('Present')).toBeInTheDocument();
  });

  it('never publishes the local path', async () => {
    stubFetch({
      '/api/v1/repositories': { body: [repository({ localPathExists: false })] },
    });

    const { container } = renderAt('/repositories', '/repositories', <RepositoriesPage />);

    await screen.findByText('Missing');

    const text = container.textContent ?? '';

    expect(text).not.toMatch(/[A-Z]:\\/);
    expect(text).not.toContain('/home/');
  });

  it('says so when a repository is not reviewed at all', async () => {
    stubFetch({
      '/api/v1/repositories': { body: [repository({ reviewerProfile: null })] },
    });

    renderAt('/repositories', '/repositories', <RepositoriesPage />);

    expect(await screen.findByText('Not reviewed')).toBeInTheDocument();
  });
});
