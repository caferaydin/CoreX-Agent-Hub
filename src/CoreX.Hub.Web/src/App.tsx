import { Component, type ErrorInfo, type ReactNode } from 'react';
import { NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { Icon } from './components/Icon';
import { AgentProfilesPage } from './routes/AgentProfilesPage';
import { CreateTaskPage } from './routes/CreateTaskPage';
import { ExecutionDetailPage } from './routes/ExecutionDetailPage';
import { RepositoriesPage } from './routes/RepositoriesPage';
import { TaskDetailPage } from './routes/TaskDetailPage';
import { TasksPage } from './routes/TasksPage';

/**
 * The shell.
 *
 * One frame, three destinations, and the content underneath. The navigation deliberately does
 * not mirror the backend: organizations and projects are context an operator reads, not places
 * they go, so they appear as breadcrumbs on the screens that have them rather than as items
 * here.
 *
 * Phase 9C adds four operator actions - create, ready, run, cancel - and no more. Deciding
 * whether work is accepted is Phase 9D, and there is deliberately not even a disabled control
 * for it anywhere in this tree.
 */
export function App(): ReactNode {
  return (
    <div className="shell">
      <Sidebar />
      <div className="main">
        <Boundary>
          <Routes>
            <Route path="/" element={<Navigate to="/tasks" replace />} />
            <Route path="/tasks" element={<TasksPage />} />
            <Route path="/tasks/new" element={<CreateTaskPage />} />
            <Route path="/tasks/:taskKey" element={<TaskDetailPage />} />
            <Route path="/executions/:executionId" element={<ExecutionDetailPage />} />
            <Route path="/repositories" element={<RepositoriesPage />} />
            <Route path="/agents" element={<AgentProfilesPage />} />
            <Route path="*" element={<NotFound />} />
          </Routes>
        </Boundary>
      </div>
    </div>
  );
}

function Sidebar(): ReactNode {
  return (
    <nav className="sidebar" aria-label="Main">
      <div className="sidebar__brand">
        <Icon name="hub" label="CoreX Agent Hub" size={18} />
        <span className="sidebar__brand-text">CoreX Agent Hub</span>
      </div>

      <div className="sidebar__group">
        <div className="sidebar__group-title" id="nav-workspace">
          Workspace
        </div>
        <ul style={{ listStyle: 'none', margin: 0, padding: 0 }} aria-labelledby="nav-workspace">
          <li>
            <NavLink to="/tasks" className="sidebar__link">
              <Icon name="tasks" />
              <span className="sidebar__label">Tasks</span>
            </NavLink>
          </li>
          <li>
            <NavLink to="/repositories" className="sidebar__link">
              <Icon name="repositories" />
              <span className="sidebar__label">Repositories</span>
            </NavLink>
          </li>
        </ul>
      </div>

      <div className="sidebar__group">
        <div className="sidebar__group-title" id="nav-runtime">
          Runtime
        </div>
        <ul style={{ listStyle: 'none', margin: 0, padding: 0 }} aria-labelledby="nav-runtime">
          <li>
            <NavLink to="/agents" className="sidebar__link">
              <Icon name="agents" />
              <span className="sidebar__label">Agent profiles</span>
            </NavLink>
          </li>
        </ul>
      </div>
    </nav>
  );
}

/**
 * The page frame every screen renders into.
 *
 * Exported because each route supplies its own title and context, and a shell that guessed
 * them from the URL would be wrong on the two screens that matter most.
 */
export function PageHeader({
  title,
  context,
  aside,
}: {
  title: string;
  context?: ReactNode;
  aside?: ReactNode;
}): ReactNode {
  return (
    <header className="header">
      <div className="header__titles">
        <h1 className="header__title">{title}</h1>
        {context ? <div className="header__context">{context}</div> : null}
      </div>
      {aside}
    </header>
  );
}

function NotFound(): ReactNode {
  const location = useLocation();

  return (
    <>
      <PageHeader title="Not found" />
      <div className="content">
        <div className="state">
          <p className="state__title">There is no page at {location.pathname}</p>
          <p className="state__detail">
            <NavLink to="/tasks">Back to tasks</NavLink>
          </p>
        </div>
      </div>
    </>
  );
}

/**
 * One boundary, around the routed area.
 *
 * Deliberately not one per component: a boundary around everything catches the render bug that
 * would otherwise blank the window, while panels that merely failed to load handle their own
 * failure inline and leave the rest of the page working.
 */
class Boundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  override state = { failed: false };

  static getDerivedStateFromError(): { failed: boolean } {
    return { failed: true };
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    // The local console is where a developer looks; nothing is sent anywhere.
    console.error('The operator UI failed to render.', error, info.componentStack);
  }

  override render(): ReactNode {
    if (!this.state.failed) {
      return this.props.children;
    }

    return (
      <>
        <PageHeader title="Something went wrong" />
        <div className="content">
          <div className="state state--error" role="alert">
            <p className="state__title">This screen could not be drawn</p>
            <p className="state__detail">
              The Hub itself is unaffected. Reload the page; the details are in the browser
              console.
            </p>
          </div>
        </div>
      </>
    );
  }
}
