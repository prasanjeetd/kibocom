import { ActiveHoldsList, CreateHoldForm, InventoryDashboard } from './components';

export default function App() {
  return (
    <div className="app">
      <header>
        <h1>Inventory Hold Service</h1>
        <p className="muted">
          Stock is reserved atomically at checkout and returns automatically when a hold expires.
        </p>
      </header>

      <main>
        <InventoryDashboard />
        <CreateHoldForm />
        <ActiveHoldsList />
      </main>
    </div>
  );
}
