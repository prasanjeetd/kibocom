import { useMemo, useState } from 'react';
import { ApiError, type Hold, type HoldStatus, type InventoryItem } from './api';
import {
  formatCountdown,
  useCreateHold,
  useHolds,
  useInventory,
  useNow,
  useReleaseHold,
} from './hooks';

/* Icons are drawn inline rather than pulled from a font or a sprite: three glyphs do not
   justify a dependency, and inheriting currentColor is what lets the message bar tint its
   own icon from the status token. */

function ErrorCircleIcon() {
  return (
    <svg viewBox="0 0 20 20" width="20" height="20" aria-hidden="true" focusable="false">
      <circle cx="10" cy="10" r="7.25" fill="none" stroke="currentColor" strokeWidth="1.5" />
      <path
        d="M10 5.75v4.75"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
      />
      <circle cx="10" cy="13.7" r="0.9" fill="currentColor" />
    </svg>
  );
}

function AddIcon() {
  return (
    <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false">
      <path
        d="M8 3.25v9.5M3.25 8h9.5"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
      />
    </svg>
  );
}

function DismissIcon() {
  return (
    <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true" focusable="false">
      <path
        d="M4.25 4.25l7.5 7.5M11.75 4.25l-7.5 7.5"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        strokeLinecap="round"
      />
    </svg>
  );
}

function MessageBar({ error }: { error: unknown }) {
  if (!error) return null;

  const message = error instanceof ApiError ? error.message : 'Something went wrong.';
  // A 409 carries the numbers that explain the refusal. Showing them turns "rejected" into
  // something the operator can act on.
  const detail =
    error instanceof ApiError && error.problem.sku
      ? `${error.problem.sku} — ${error.problem.requested} requested, ${error.problem.available} available`
      : null;

  return (
    <div className="message-bar" role="alert">
      <ErrorCircleIcon />
      <span>
        {message}
        {detail && <span className="detail">{detail}</span>}
      </span>
    </div>
  );
}

function StatusBadge({ status }: { status: HoldStatus }) {
  return <span className={`badge badge--${status.toLowerCase()}`}>{status}</span>;
}

/** Placeholder rows that hold a card height steady while the first fetch lands. */
function Skeleton({ rows, label }: { rows: number; label: string }) {
  return (
    <div className="skeleton" role="status" aria-label={label}>
      {Array.from({ length: rows }, (_, index) => (
        <div className="shimmer" key={index} style={{ width: `${100 - index * 14}%` }} />
      ))}
    </div>
  );
}

export function InventoryDashboard() {
  const { data, isPending, isError, error } = useInventory();

  return (
    <section className="card" aria-labelledby="inventory-heading">
      <div className="card-header">
        <h2 id="inventory-heading">Inventory</h2>
        {data && <span className="count">{data.length} products</span>}
      </div>

      <MessageBar error={isError ? error : null} />

      {isPending ? (
        <Skeleton rows={4} label="Loading inventory" />
      ) : (
        <table className="grid">
          <thead>
            <tr>
              <th scope="col">Product</th>
              <th scope="col" className="num">
                Available
              </th>
              <th scope="col" className="num">
                Held
              </th>
              <th scope="col" className="num">
                Total
              </th>
            </tr>
          </thead>
          <tbody>
            {data?.map((item) => (
              <tr key={item.sku}>
                <td className="primary-cell">
                  {item.name}
                  <span className="secondary">{item.sku}</span>
                </td>
                <td className={`num ${item.availableQuantity === 0 ? 'depleted' : ''}`}>
                  {item.availableQuantity}
                </td>
                <td className="num">{item.heldQuantity}</td>
                <td className="num num--subtle">{item.totalQuantity}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

interface Line {
  sku: string;
  quantity: number;
}

/**
 * Shows what is actually in stock for the chosen product, and warns before submission when the
 * requested quantity exceeds it. The server is still the authority — it answers 409 under
 * contention — but there is no reason to make someone submit to learn something already known.
 */
function StockHint({
  line,
  inventory,
}: {
  line: Line;
  inventory: InventoryItem[];
}) {
  if (!line.sku) return null;

  const item = inventory.find((i) => i.sku === line.sku);
  if (!item) return null;

  const over = line.quantity > item.availableQuantity;

  return (
    <span className={over ? 'stock-hint stock-hint--over' : 'stock-hint'}>
      {over
        ? `Only ${item.availableQuantity} available`
        : `${item.availableQuantity} of ${item.totalQuantity} available`}
    </span>
  );
}

export function CreateHoldForm() {
  const { data: inventory } = useInventory();
  const createHold = useCreateHold();

  const [customerId, setCustomerId] = useState('cust-demo');
  const [lines, setLines] = useState<Line[]>([{ sku: '', quantity: 1 }]);

  const available = useMemo(() => inventory ?? [], [inventory]);
  const removable = lines.length > 1;

  const updateLine = (index: number, patch: Partial<Line>) =>
    setLines((current) => current.map((line, i) => (i === index ? { ...line, ...patch } : line)));

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    const items = lines.filter((line) => line.sku !== '' && line.quantity > 0);
    if (items.length === 0) return;

    createHold.mutate(
      { customerId, items },
      { onSuccess: () => setLines([{ sku: '', quantity: 1 }]) },
    );
  };

  return (
    <section className="card" aria-labelledby="place-hold-heading">
      <div className="card-header">
        <h2 id="place-hold-heading">Place a hold</h2>
      </div>

      <MessageBar error={createHold.error} />

      <form onSubmit={submit}>
        <div className="field">
          <label className="label" htmlFor="customer-id">
            Customer
          </label>
          <input
            id="customer-id"
            value={customerId}
            onChange={(e) => setCustomerId(e.target.value)}
            required
            maxLength={64}
          />
        </div>

        <div className="field">
          <span className="label">Products</span>
          {lines.map((line, index) => (
            <div className={`line${removable ? ' removable' : ''}`} key={index}>
              <select
                value={line.sku}
                onChange={(e) => updateLine(index, { sku: e.target.value })}
                aria-label={`Product, line ${index + 1}`}
                required
              >
                <option value="">Select a product…</option>
                {available.map((item) => (
                  <option key={item.sku} value={item.sku} disabled={item.availableQuantity === 0}>
                    {item.name} ({item.availableQuantity} available)
                  </option>
                ))}
              </select>

              <input
                type="number"
                min={1}
                value={line.quantity}
                onChange={(e) => updateLine(index, { quantity: Number(e.target.value) })}
                aria-label={`Quantity, line ${index + 1}`}
                required
              />

              {removable && (
                <button
                  type="button"
                  className="icon"
                  onClick={() => setLines((c) => c.filter((_, i) => i !== index))}
                  aria-label={`Remove line ${index + 1}`}
                >
                  <DismissIcon />
                </button>
              )}
              <StockHint line={line} inventory={available} />
            </div>
          ))}
        </div>

        <div className="actions">
          <button
            type="button"
            className="subtle"
            onClick={() => setLines((c) => [...c, { sku: '', quantity: 1 }])}
          >
            <AddIcon />
            Add product
          </button>

          <button type="submit" className="primary" disabled={createHold.isPending}>
            {createHold.isPending ? 'Placing…' : 'Place hold'}
          </button>
        </div>
      </form>
    </section>
  );
}

function HoldRow({ hold, now }: { hold: Hold; now: number }) {
  const releaseHold = useReleaseHold();

  const confirmRelease = () => {
    if (window.confirm(`Release this hold and return ${hold.items.length} line(s) to stock?`)) {
      releaseHold.mutate(hold.holdId);
    }
  };

  return (
    <tr>
      <td>
        <StatusBadge status={hold.status} />
      </td>
      <td className="primary-cell">
        {hold.items.map((item) => (
          <div key={item.sku}>
            {item.quantity} × {item.name}
          </div>
        ))}
      </td>
      <td>{hold.customerId}</td>
      <td className="num countdown">
        {hold.status === 'Active' ? formatCountdown(hold.expiresAt, now) : '—'}
      </td>
      <td className="num">
        <button
          onClick={confirmRelease}
          disabled={hold.status !== 'Active' || releaseHold.isPending}
        >
          {releaseHold.isPending ? 'Releasing…' : 'Release'}
        </button>
      </td>
    </tr>
  );
}

export function ActiveHoldsList() {
  const { data, isPending, isError, error } = useHolds();
  const now = useNow();

  const activeCount = data?.filter((hold) => hold.status === 'Active').length ?? 0;

  return (
    <section className="card" aria-labelledby="holds-heading">
      <div className="card-header">
        <h2 id="holds-heading">Holds</h2>
        {data && <span className="count">{activeCount} active</span>}
      </div>

      <MessageBar error={isError ? error : null} />

      {isPending ? (
        <Skeleton rows={3} label="Loading holds" />
      ) : data && data.length > 0 ? (
        <table className="grid">
          <thead>
            <tr>
              <th scope="col">Status</th>
              <th scope="col">Items</th>
              <th scope="col">Customer</th>
              <th scope="col" className="num">
                Expires in
              </th>
              <th scope="col" className="num">
                Action
              </th>
            </tr>
          </thead>
          <tbody>
            {data.map((hold) => (
              <HoldRow key={hold.holdId} hold={hold} now={now} />
            ))}
          </tbody>
        </table>
      ) : (
        <p className="empty">
          No holds yet. Place one and it will expire on its own, returning the stock.
        </p>
      )}
    </section>
  );
}
