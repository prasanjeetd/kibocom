import { ActiveHoldsList, CreateHoldForm, InventoryDashboard } from './components';

export default function App() {
  return (
    <div className="app">
      <header className="app-header">
        <h1>Inventory Hold Service</h1>
        <p>
          Stock is reserved atomically at checkout and returns automatically when a hold expires.
        </p>
      </header>

      <main>
        {/* The form sits beside the stock it draws from, so the effect of placing a hold is
            visible without scrolling. Holds carry wide rows and take the full width below. */}
        <div className="columns">
          <CreateHoldForm />
          <InventoryDashboard />
        </div>

        <ActiveHoldsList />
      </main>
    </div>
  );
}
