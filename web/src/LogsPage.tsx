import { useState } from 'react';
import type { LogEntry } from './api';
import { useLogs } from './hooks';

/** Fields worth surfacing next to the message; the rest stay in the raw properties bag. */
const HIGHLIGHTED = ['Method', 'Path', 'StatusCode', 'ElapsedMs', 'HoldId', 'EventType', 'Sku'];

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour12: false });
}

function LevelBadge({ level }: { level: string }) {
  return <span className={`badge badge--${level.toLowerCase()}`}>{level}</span>;
}

function Row({ entry, onTrace }: { entry: LogEntry; onTrace: (id: string) => void }) {
  const fields = Object.entries(entry.properties ?? {}).filter(([key]) =>
    HIGHLIGHTED.includes(key),
  );

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
        <span className="secondary">{entry.category}</span>
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
  // Off by default: a feed that reorders itself while you are reading it is hostile.
  const [autoRefresh, setAutoRefresh] = useState(false);

  const { data, isPending, isFetching, refetch } = useLogs(
    { level: level || undefined, search: search || undefined, traceId: traceId || undefined, limit: 20 },
    autoRefresh,
  );

  return (
    <section className="card" aria-labelledby="logs-heading">
      <div className="card-header">
        <h2 id="logs-heading">API activity</h2>
        <span className="count">
          {data ? `${data.length} most recent` : ''}
        </span>
      </div>

      <div className="toolbar">
        <select value={level} onChange={(e) => setLevel(e.target.value)} aria-label="Level">
          <option value="">All levels</option>
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
      ) : data && data.length > 0 ? (
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
            {data.map((entry, index) => (
              <Row key={`${entry.timestamp}-${index}`} entry={entry} onTrace={setTraceId} />
            ))}
          </tbody>
        </table>
      ) : (
        <p className="empty">
          Nothing logged yet. Place or release a hold and it will appear here within a few seconds.
        </p>
      )}
    </section>
  );
}
