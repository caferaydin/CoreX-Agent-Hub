import type { ReactNode } from 'react';
import { PageHeader } from '../App';
import { api } from '../api/client';
import { useQuery } from '../api/useQuery';
import { Badge, Empty, Failed, Loading, Panel } from '../components/primitives';

/**
 * Which agents this installation is configured to run.
 *
 * Operational status, not configuration editing. Everything here is a fact the server already
 * decided to publish: a name, a role, a model, a permission profile, and whether a runtime is
 * declared under the profile's key.
 *
 * What is deliberately absent is anything about how a provider is reached - no executable
 * path, no environment, no credential, no auth file. Those are the workstation's business and
 * the Hub does not put them on the wire, so there is nothing here to render.
 *
 * The provider column is a label. Nothing on this page - or anywhere else in this UI -
 * branches on its value.
 */
export function AgentProfilesPage(): ReactNode {
  const { state, refetch } = useQuery((signal) => api.agentProfiles(signal), []);

  return (
    <>
      <PageHeader
        title="Agent profiles"
        context={state.status === 'ready' ? `${state.data.length} declared` : undefined}
      />
      <div className="content">
        <div className="page">
          <Panel title="Declared profiles" flush>
            {state.status === 'loading' ? <Loading label="Loading agent profiles" /> : null}
            {state.status === 'error' ? <Failed error={state.error} onRetry={refetch} /> : null}
            {state.status === 'ready' && state.data.length === 0 ? (
              <Empty
                title="No agent profiles"
                detail="Profiles are declared per organization in hub.config.yaml."
              />
            ) : null}
            {state.status === 'ready' && state.data.length > 0 ? (
              <div className="table-scroll">
                <table className="table">
                  <caption className="visually-hidden">Declared agent profiles</caption>
                  <thead>
                    <tr>
                      <th scope="col">Profile</th>
                      <th scope="col">Role</th>
                      <th scope="col">Provider</th>
                      <th scope="col">Model</th>
                      <th scope="col">Permission</th>
                      <th scope="col">Runtime</th>
                    </tr>
                  </thead>
                  <tbody>
                    {state.data.map((profile) => (
                      <tr key={`${profile.name}-${profile.runtimeKey}`}>
                        <th scope="row" className="mono" style={{ fontWeight: 400 }}>
                          {profile.name}
                        </th>
                        <td>{profile.role}</td>
                        <td className="muted">{profile.provider}</td>
                        <td>
                          <div className="stack">
                            <span className="mono">{profile.model ?? '—'}</span>
                            {profile.effort ? (
                              <span className="muted">effort {profile.effort}</span>
                            ) : null}
                          </div>
                        </td>
                        <td>
                          <Badge
                            look={
                              profile.permissionProfile === 'ReadOnly'
                                ? {
                                    label: 'Read only',
                                    tone: 'info',
                                    shape: 'outline',
                                    meaning: 'This profile cannot change the tree it is given.',
                                  }
                                : {
                                    label: profile.permissionProfile,
                                    tone: 'neutral',
                                    shape: 'outline',
                                    meaning: 'The permission profile this agent runs under.',
                                  }
                            }
                          />
                        </td>
                        <td>
                          <div className="row">
                            <span className="mono muted">{profile.runtimeKey}</span>
                            {profile.runtimeDeclared ? (
                              <Badge
                                look={{
                                  label: 'Declared',
                                  tone: 'good',
                                  shape: 'outline',
                                  meaning:
                                    'This installation declares a runtime under that key. Not a health check.',
                                }}
                              />
                            ) : (
                              <Badge
                                look={{
                                  label: 'Not declared',
                                  tone: 'warn',
                                  shape: 'outline',
                                  meaning:
                                    'No runtime is configured under that key here, so this profile cannot run.',
                                }}
                              />
                            )}
                          </div>
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
