import { useMemo, useState } from 'react';
import { ApiError, type Hold, type HoldStatus } from './api';
import {
  formatCountdown,
  useCreateHold,
  useHolds,
  useInventory,
  useNow,
  useReleaseHold,
} from './hooks';

function ErrorBanner({ error }: { error: unknown }) {
  if (!error) return null;

  const message = error instanceof ApiError ? error.message : 'Something went wrong.';
  const extra =
    error instanceof ApiError && error.problem.sku
      ? ` (${error.problem.sku}: requested ${error.problem.requested}, available ${error.problem.available})`
      : '';

  return (
    <p className="banner banner--error" role="alert">
      {message}
      {extra}
    </p>
  );
}

function StatusBadge({ status }: { status: HoldStatus }) {
  return <span className={`badge badge--${status.toLowerCase()}`}>{status}</span>;
}

export function InventoryDashboard() {
  const { data, isPending, isError, error } = useInventory();

  return (
    <section className="panel">
      <h2>Inventory</h2>
      <ErrorBanner error={isError ? error : null} />

      {isPending ? (
        <p className="muted">Loading inventory…</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Product</th>
              <th className="num">Available</th>
              <th className="num">Held</th>
              <th className="num">Total</th>
            </tr>
          </thead>
          <tbody>
            {data?.map((item) => (
              <tr key={item.sku}>
                <td>
                  {item.name}
                  <span className="muted sku">{item.sku}</span>
                </td>
                <td className={`num ${item.availableQuantity === 0 ? 'zero' : ''}`}>
                  {item.availableQuantity}
                </td>
                <td className="num">{item.heldQuantity}</td>
                <td className="num muted">{item.totalQuantity}</td>
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

export function CreateHoldForm() {
  const { data: inventory } = useInventory();
  const createHold = useCreateHold();

  const [customerId, setCustomerId] = useState('cust-demo');
  const [lines, setLines] = useState<Line[]>([{ sku: '', quantity: 1 }]);

  const available = useMemo(() => inventory ?? [], [inventory]);

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
    <section className="panel">
      <h2>Place a hold</h2>
      <ErrorBanner error={createHold.error} />

      <form onSubmit={submit}>
        <label>
          Customer
          <input
            value={customerId}
            onChange={(e) => setCustomerId(e.target.value)}
            required
            maxLength={64}
          />
        </label>

        {lines.map((line, index) => (
          <div className="line" key={index}>
            <select
              value={line.sku}
              onChange={(e) => updateLine(index, { sku: e.target.value })}
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
              required
            />

            {lines.length > 1 && (
              <button
                type="button"
                className="ghost"
                onClick={() => setLines((c) => c.filter((_, i) => i !== index))}
                aria-label="Remove line"
              >
                ×
              </button>
            )}
          </div>
        ))}

        <div className="actions">
          <button
            type="button"
            className="ghost"
            onClick={() => setLines((c) => [...c, { sku: '', quantity: 1 }])}
          >
            + Add product
          </button>

          <button type="submit" disabled={createHold.isPending}>
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
      <td>
        {hold.items.map((item) => (
          <div key={item.sku}>
            {item.quantity} × {item.name}
          </div>
        ))}
        <span className="muted sku">{hold.customerId}</span>
      </td>
      <td className="num mono">
        {hold.status === 'Active' ? formatCountdown(hold.expiresAt, now) : '—'}
      </td>
      <td className="num">
        <button
          className="danger"
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

  return (
    <section className="panel">
      <h2>Active holds</h2>
      <ErrorBanner error={isError ? error : null} />

      {isPending ? (
        <p className="muted">Loading holds…</p>
      ) : data && data.length > 0 ? (
        <table>
          <thead>
            <tr>
              <th>Status</th>
              <th>Items</th>
              <th className="num">Expires in</th>
              <th className="num">Action</th>
            </tr>
          </thead>
          <tbody>
            {data.map((hold) => (
              <HoldRow key={hold.holdId} hold={hold} now={now} />
            ))}
          </tbody>
        </table>
      ) : (
        <p className="muted">
          No active holds. Place one above — it will expire on its own and return the stock.
        </p>
      )}
    </section>
  );
}
