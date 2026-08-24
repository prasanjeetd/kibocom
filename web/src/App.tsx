import { useState } from 'react';
import { ActiveHoldsList, CreateHoldForm, InventoryDashboard } from './components';
import { LogsPage } from './LogsPage';

type Tab = 'operations' | 'logs';

export default function App() {
  const [tab, setTab] = useState<Tab>('operations');

  return (
    <div className="app">
      <header className="app-header">
        <h1>Inventory Hold Service</h1>
        <p>
          Stock is reserved atomically at checkout and returns automatically when a hold expires.
        </p>
      </header>

      {/* Two views rather than a router: the brief puts routing out of scope, and one piece of
          state is a smaller thing to reason about than a routing library. */}
      <nav className="tabs" aria-label="Views">
        <button
          type="button"
          className={tab === 'operations' ? 'tab tab--selected' : 'tab'}
          aria-current={tab === 'operations'}
          onClick={() => setTab('operations')}
        >
          Operations
        </button>
        <button
          type="button"
          className={tab === 'logs' ? 'tab tab--selected' : 'tab'}
          aria-current={tab === 'logs'}
          onClick={() => setTab('logs')}
        >
          Logs
        </button>
      </nav>

      <main>
        {tab === 'operations' ? (
          /* Left column is the input side — what you are about to do, and the stock you are
             deciding against, adjacent. Right column is the output side: the holds that exist and
             can be released. Cause and effect sit side by side rather than a scroll apart. */
          <div className="ops">
            <div className="ops-side">
              <CreateHoldForm />
              <InventoryDashboard />
            </div>
            <ActiveHoldsList />
          </div>
        ) : (
          <LogsPage />
        )}
      </main>
    </div>
  );
}
