import { useEffect, useState } from 'react';
import type { LogEntry } from './api';
import { useLogs } from './hooks';

/**
 * Fields worth surfacing next to the message; the rest stay in the raw properties bag.
 * Ordered deliberately - correlation keys first, because those are what you scan for.
 */
const HIGHLIGHTED = ['HoldId', 'CustomerId', 'Sku', 'EventType', 'Method', 'Path', 'StatusCode'];

/**
 * A chip is only worth drawing if the message does not already say it. The message is a rendered
 * format string, so `GET /api/holds responded 200` already contains Method, Path and StatusCode -
 * repeating them as chips is pure noise. What survives this filter is the useful case: a value
 * inherited from the request scope on a line that never mentions it.
 */
function extraFields(entry: LogEntry): Array<[string, string]> {
  const properties = entry.properties ?? {};

  return HIGHLIGHTED.filter((key) => {
    const value = properties[key];
    return value !== undefined && value !== '' && !entry.message.includes(value);
  }).map((key) => [key, properties[key]] as [string, string]);
}

const PAGE_SIZE = 20;

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour12: false });
}

function LevelBadge({ level }: { level: string }) {
  return <span className={`badge badge--${level.toLowerCase()}`}>{level}</span>;
}

function Row({ entry, onTrace }: { entry: LogEntry; onTrace: (id: string) => void }) {
  const fields = extraFields(entry);

  return (
    <tr>
      <td className="countdown">{formatTime(entry.timestamp)}</td>
      <td>
        <LevelBadge level={entry.level} />
      </td>
      <td>
        {entry.message}
        {fields.length > 0 && (
          <span className="fields">
            {fields.map(([key, value]) => (
              <span className="chip" key={key}>
                {key} <b>{value}</b>
              </span>
            ))}
          </span>
        )}
        {entry.exception && <pre className="exception">{entry.exception}</pre>}
        <span className="secondary" title={entry.category}>
          {entry.category.split('.').pop()}
          {entry.eventName && <b className="event-name"> · {entry.eventName}</b>}
        </span>
      </td>
      <td>
        {entry.traceId ? (
          <button
            type="button"
            className="trace"
            onClick={() => onTrace(entry.traceId!)}
            title="Show every line from this request"
          >
            {entry.traceId.slice(0, 8)}
          </button>
        ) : (
          <span className="secondary">—</span>
        )}
      </td>
    </tr>
  );
}

export function LogsPage() {
  const [level, setLevel] = useState('');
  const [search, setSearch] = useState('');
  const [traceId, setTraceId] = useState('');
  const [page, setPage] = useState(1);
  // Off by default: a feed that reorders itself while you are reading it is hostile.
  const [autoRefresh, setAutoRefresh] = useState(false);

  // Changing a filter makes the current page number meaningless - page 5 of the old result set
  // is rarely page 5 of the new one, and is frequently past the end of it.
  useEffect(() => {
    setPage(1);
  }, [level, search, traceId]);

  const { data, isPending, isFetching, refetch } = useLogs(
    {
      level: level || undefined,
      search: search || undefined,
      traceId: traceId || undefined,
      page,
      pageSize: PAGE_SIZE,
    },
    autoRefresh,
  );

  const from = data && data.total > 0 ? (data.page - 1) * data.pageSize + 1 : 0;
  const to = data ? Math.min(data.page * data.pageSize, data.total) : 0;

  return (
    <section className="card" aria-labelledby="logs-heading">
      <div className="card-header">
        <h2 id="logs-heading">API activity</h2>
        <span className="count">{data ? `${from}–${to} of ${data.total}` : ''}</span>
      </div>

      <div className="toolbar">
        <select value={level} onChange={(e) => setLevel(e.target.value)} aria-label="Level">
          <option value="">All levels</option>
          <option value="Trace">Trace (includes polling)</option>
          <option value="Debug">Debug</option>
          <option value="Information">Information</option>
          <option value="Warning">Warning</option>
          <option value="Error">Error</option>
        </select>

        <input
          type="search"
          placeholder="Search messages…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          aria-label="Search messages"
        />

        <button type="button" onClick={() => void refetch()} disabled={isFetching}>
          {isFetching ? 'Refreshing…' : 'Refresh'}
        </button>

        <label className="toggle">
          <input
            type="checkbox"
            checked={autoRefresh}
            onChange={(e) => setAutoRefresh(e.target.checked)}
          />
          Auto-refresh
        </label>
      </div>

      {traceId && (
        <div className="filter-note">
          Showing one request · trace <code>{traceId.slice(0, 16)}…</code>
          <button type="button" className="subtle" onClick={() => setTraceId('')}>
            Clear
          </button>
        </div>
      )}

      {isPending ? (
        <p className="empty">Loading…</p>
      ) : data && data.items.length > 0 ? (
        <>
          <table className="grid logs">
            <thead>
              <tr>
                <th scope="col">Time</th>
                <th scope="col">Level</th>
                <th scope="col">Message</th>
                <th scope="col">Trace</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((entry, index) => (
                <Row key={`${entry.timestamp}-${index}`} entry={entry} onTrace={setTraceId} />
              ))}
            </tbody>
          </table>

          <nav className="pager" aria-label="Log pages">
            <button
              type="button"
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={!data.hasPrevious || isFetching}
            >
              Previous
            </button>

            <span className="pager-position">
              Page <b>{data.page}</b> of {Math.max(1, data.totalPages)}
            </span>

            <button
              type="button"
              onClick={() => setPage((p) => p + 1)}
              disabled={!data.hasNext || isFetching}
            >
              Next
            </button>
          </nav>
        </>
      ) : (
        <p className="empty">
          {level || search || traceId
            ? 'Nothing matches those filters.'
            : 'Nothing logged yet. Place or release a hold and it will appear here within a few seconds.'}
        </p>
      )}
    </section>
  );
}
