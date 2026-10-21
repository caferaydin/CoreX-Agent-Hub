import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { PageHeader } from '../App';
import { api } from '../api/client';
import { useQuery } from '../api/useQuery';
import type { TaskSummaryDto } from '../api/contracts';
import { AutopilotPanel } from '../components/AutopilotPanel';
import { Badge, Empty, Failed, Loading, Panel } from '../components/primitives';
import { exactTime, formatRelative } from '../lib/format';
import { lookOfOutcome, lookOfTaskState } from '../lib/status';

/**
 * The working screen.
 *
 * A table, because that is what this is: many rows, scanned by state, compared by column. The
 * columns are chosen so that the question an operator actually arrives with - "is anything
 * waiting for me, and did anything fail" - is answerable without opening a single row.
 *
 * The order is the server's. Sorting differently here by default would mean the page and the
 * API disagreed about what "first" means the moment paging entered the picture.
 */
export function TasksPage(): ReactNode {
  const { state, refetch } = useQuery((signal) => api.tasks({ limit: 100 }, signal), []);

  return (
    <>
      <PageHeader
        title="Tasks"
        context={
          state.status === 'ready'
            ? `${state.data.total} task${state.data.total === 1 ? '' : 's'}`
            : undefined
        }
        aside={
          <Link to="/tasks/new" className="button button--primary">
            New task
          </Link>
        }
      />
      <div className="content">
        <div className="page">
          <AutopilotPanel onActivity={refetch} />
          <Panel
            title="All tasks"
            hint={state.status === 'ready' && state.refreshing ? 'Refreshing…' : undefined}
            flush
          >
            {state.status === 'loading' ? <Loading label="Loading tasks" /> : null}
            {state.status === 'error' ? <Failed error={state.error} onRetry={refetch} /> : null}
            {state.status === 'ready' && state.data.items.length === 0 ? (
              <Empty
                title="No tasks yet"
                detail="Create one and it appears here with its latest attempt."
              />
            ) : null}
            {state.status === 'ready' && state.data.items.length > 0 ? (
              <TaskTable tasks={state.data.items} />
            ) : null}
          </Panel>
        </div>
      </div>
    </>
  );
}

function TaskTable({ tasks }: { tasks: readonly TaskSummaryDto[] }): ReactNode {
  return (
    <div className="table-scroll">
      <table className="table">
        <caption className="visually-hidden">
          Tasks, newest first, with the state of the most recent attempt
        </caption>
        <thead>
          <tr>
            <th scope="col">Task</th>
            <th scope="col">State</th>
            <th scope="col">Repository</th>
            <th scope="col" className="table__num">
              Attempts
            </th>
            <th scope="col">Latest attempt</th>
            <th scope="col">Last activity</th>
          </tr>
        </thead>
        <tbody>
          {tasks.map((task) => (
            <TaskRow key={task.id} task={task} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

function TaskRow({ task }: { task: TaskSummaryDto }): ReactNode {
  const outcome = lookOfOutcome(task.latestExecutionOutcome);

  return (
    <tr>
      <th scope="row" style={{ fontWeight: 400 }}>
        <div className="stack">
          <Link to={`/tasks/${encodeURIComponent(task.taskKey)}`} className="mono">
            {task.taskKey}
          </Link>
          <span className="muted" style={{ maxWidth: '52ch' }}>
            {task.objective}
          </span>
        </div>
      </th>
      <td>
        <Badge look={lookOfTaskState(task.state)} />
      </td>
      <td className="mono">{task.repositorySlug}</td>
      <td className="table__num">{task.attemptCount}</td>
      <td>
        {task.latestExecutionId ? (
          <div className="row">
            {/* The outcome is the useful one; the state says whether it is final yet. */}
            {outcome ? (
              <Badge look={outcome} />
            ) : task.latestExecutionState ? (
              <Badge look={lookOfTaskState(task.latestExecutionState)} />
            ) : null}
            <Link to={`/executions/${task.latestExecutionId}`} className="muted">
              open
            </Link>
          </div>
        ) : (
          <span className="muted">Never run</span>
        )}
      </td>
      <td>
        <time title={exactTime(task.updatedAtUtc)}>{formatRelative(task.updatedAtUtc)}</time>
      </td>
    </tr>
  );
}
