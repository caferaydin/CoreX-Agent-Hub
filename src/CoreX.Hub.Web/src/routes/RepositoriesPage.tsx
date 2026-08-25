import type { ReactNode } from 'react';
import { PageHeader } from '../App';
import { api } from '../api/client';
import { useQuery } from '../api/useQuery';
import { Badge, Empty, Failed, Loading, Panel } from '../components/primitives';

/**
 * The repositories this installation knows about.
 *
 * Safe metadata only. The local path is deliberately absent - the server sends whether the
 * checkout is present, not where it is, and a browser response is not the place to publish a
 * filesystem layout even on a machine only its owner can reach.
 *
 * The reviewer column is the interesting one operationally: it is how an operator sees which
 * profile reviews a repository, which after Phase 8 is the entire provider switch made visible
 * as data rather than as code.
 */
export function RepositoriesPage(): ReactNode {
  const { state, refetch } = useQuery((signal) => api.repositories(signal), []);

  return (
    <>
      <PageHeader
        title="Repositories"
        context={state.status === 'ready' ? `${state.data.length} configured` : undefined}
      />
      <div className="content">
        <div className="page">
          <Panel title="Configured repositories" flush>
            {state.status === 'loading' ? <Loading label="Loading repositories" /> : null}
            {state.status === 'error' ? <Failed error={state.error} onRetry={refetch} /> : null}
            {state.status === 'ready' && state.data.length === 0 ? (
              <Empty
                title="No repositories configured"
                detail="Repositories are declared in hub.config.yaml and seeded into the catalogue."
              />
            ) : null}
            {state.status === 'ready' && state.data.length > 0 ? (
              <div className="table-scroll">
                <table className="table">
                  <caption className="visually-hidden">Configured repositories</caption>
                  <thead>
                    <tr>
                      <th scope="col">Repository</th>
                      <th scope="col">Checkout</th>
                      <th scope="col">Default branch</th>
                      <th scope="col">Protected</th>
                      <th scope="col">Reviewer profile</th>
                      <th scope="col">Verification</th>
                    </tr>
                  </thead>
                  <tbody>
                    {state.data.map((repository) => (
                      <tr key={repository.id}>
                        <th scope="row" style={{ fontWeight: 400 }}>
                          <div className="stack">
                            <span className="mono">{repository.slug}</span>
                            <span className="muted">{repository.name}</span>
                          </div>
                        </th>
                        <td>
                          {repository.localPathExists ? (
                            <Badge
                              look={{
                                label: 'Present',
                                tone: 'good',
                                shape: 'outline',
                                meaning: 'The configured checkout exists on this machine.',
                              }}
                            />
                          ) : (
                            <Badge
                              look={{
                                label: 'Missing',
                                tone: 'warn',
                                shape: 'outline',
                                meaning:
                                  'The configured checkout is not present here, so nothing can be run against it.',
                              }}
                            />
                          )}
                        </td>
                        <td className="mono">{repository.defaultBranch}</td>
                        <td>
                          {repository.protectedBranches.length === 0 ? (
                            <span className="muted">none</span>
                          ) : (
                            <ul className="path-list">
                              {repository.protectedBranches.map((branch) => (
                                <li key={branch}>
                                  <span className="path">{branch}</span>
                                </li>
                              ))}
                            </ul>
                          )}
                        </td>
                        <td>
                          {repository.reviewerProfile ? (
                            <span className="mono">{repository.reviewerProfile}</span>
                          ) : (
                            <span className="muted">Not reviewed</span>
                          )}
                        </td>
                        <td>
                          {repository.defaultVerificationProfile ? (
                            <span className="mono">{repository.defaultVerificationProfile}</span>
                          ) : (
                            <span className="muted">none</span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : null}
          </Panel>
        </div>
      </div>
    </>
  );
}
