# Inventory Hold Microservice

A checkout-time inventory reservation service. When a customer begins checkout their items are
held so nobody else can buy them; holds expire automatically and return the stock.

.NET 10 · MongoDB · Redis · RabbitMQ · React 19 + TypeScript · Docker Compose

---

## The invariant

Everything here exists to protect one equation:

```
For every product:   availableQty  +  Σ(qty of all ACTIVE holds)  ==  totalQty
```

Every design decision below is justified by whether it survives concurrency, crashes, and clock
drift. If a change can break that equation, it is wrong however clean it looks.

```
                 ┌──────────────────────────────┐
   POST /holds   │                              │  DELETE /holds/{id}
   ─────────────▶│           ACTIVE             │───────────────────▶ RELEASED
   deduct stock  │  (expiresAt = now + TTL)     │   restore stock
                 └──────────────┬───────────────┘   HoldReleased
                    HoldCreated │
                                │ sweeper: expiresAt < now
                                ▼
                             EXPIRED
                          restore stock
                          HoldExpired
```

`RELEASED` and `EXPIRED` are terminal, and both restore stock. The sharpest race in the system is a
customer releasing a hold at the same instant the sweeper expires it — restore twice and the
invariant breaks silently. See ADR‑003.

---

## Quick start

```bash
docker compose up --build
```

| | URL |
|---|---|
| Web app | http://localhost:8081 |
| API | http://localhost:8080 |
| API reference (Scalar) | http://localhost:8080/scalar/v1 |
| Health | http://localhost:8080/health |
| RabbitMQ management | http://localhost:15672 (guest / guest) |

Five products are seeded on first start. Seeding is idempotent, so restarting never resets stock.

### Local development

The fast loop runs infrastructure in containers and the code on the host:

```bash
docker compose up -d mongo redis rabbitmq

dotnet watch --project src/InventoryHold.WebApi     # http://localhost:5xxx
cd web && npm run dev                               # http://localhost:5173
```

To develop against managed free tiers instead of local containers, copy `.env.example` to `.env`
and fill in your MongoDB Atlas, Upstash and CloudAMQP connection strings. `.env` is gitignored.
No code changes are needed — every connection is configuration.

---

## Architecture

```
src/
├── InventoryHold.Contracts/       DTOs and enums. Zero dependencies.
├── InventoryHold.Domain/          Entities, invariants, events, exceptions.
│   ├── Services/                  HoldService, InventoryService
│   └── Repositories/              Ports: IHoldRepository, IInventoryRepository,
│                                         ICacheService, IEventPublisher
├── InventoryHold.Infrastructure/  Adapters: MongoDB, Redis, RabbitMQ
├── InventoryHold.WebApi/          Controllers, DI, middleware, ExpirySweeper
└── InventoryHold.UnitTests/       NUnit + NSubstitute + FakeTimeProvider
web/                               React 19 + Vite + TanStack Query
```

**Dependencies point inward only.** `Domain` has *no NuGet package references at all* — no MongoDB,
no Redis, no RabbitMQ, not even the framework extensions. This is enforced by a test
(`ArchitectureTests`) that fails the build if a driver type ever leaks in, so the layering is
verifiable rather than aspirational.

That constraint is also why persistence has its own document types (`HoldDocument`,
`InventoryDocument`) that map to and from the domain entities: BSON attributes would have meant a
MongoDB dependency in `Domain`.

---

## Design decisions

Full records in [`docs/adr/`](docs/adr/). Summary:

### ADR‑001 — Guarded atomic `$inc`, never read-then-write

```
filter: { _id: sku, availableQty: { $gte: n } }     ← the condition
update: { $inc: { availableQty: -n } }              ← the change
```

One round trip; **the filter is the check**, so no window exists between checking and writing.
`FindOneAndUpdate` returning `null` means another caller won the race → `409`, not a crash.

In SQL this is `UPDATE inventory SET available_qty = available_qty - @n WHERE sku = @s AND
available_qty >= @n RETURNING *` — zero rows affected means you lost.

*Rejected:* read the quantity, check it in C#, then write it back. Two callers both read `1`, both
pass the check, both write `0`. Oversold.

### ADR‑002 — Multi-item holds use a MongoDB transaction

A cart holds a chair *and* a desk. If the chair deducts and the desk is out of stock, the chair is
stranded: deducted, with no hold that could release it. Deductions and the hold insert therefore
commit together via `WithTransactionAsync`, which also auto-retries `TransientTransactionError` —
the expected outcome when two guarded deductions collide.

Transactions require a replica set, which is why Compose starts MongoDB with `--replSet rs0`,
self-initiating inside its own healthcheck. MongoDB Atlas M0 is also a 3-node replica set, so the
same code works against the free tier unchanged.

*Rejected as primary:* compensating rollback. It is implemented as a fallback for standalone
servers (`Mongo:UseTransactions=false`), but it leaves a crash window in which stock is lost.

### ADR‑003 — Lazy expiry **and** an atomic sweeper. Never a TTL index.

| Concern | Mechanism |
|---|---|
| Never report an expired hold as Active | `Hold.StatusAt(now)` derives status from `expiresAt`; stored status is never trusted alone |
| Stock must actually come back | `ExpirySweeper` background service |
| `HoldExpired` fires exactly once | Each hold is *claimed* before anything is restored |

Lazy expiry alone is broken — a hold nobody reads keeps its stock forever. A sweeper alone is
broken — a read landing between expiry and the next sweep would lie. Both are required.

**The same guard shape appears three times.** Once you see it, the concurrency design is one idea
repeated:

| Purpose | Guard |
|---|---|
| Take stock | `sku = @s AND available_qty >= @n` |
| Release | `id = @id AND status = 'Active'` |
| Expire | `id = @id AND status = 'Active' AND expires_at < now()` |

In all three, **zero rows matched means someone else got there first.** That is precisely how
release-versus-expire can never both return the stock.

*Rejected:* a MongoDB TTL index on `expiresAt`. It is the obvious answer and it is silently
catastrophic — it *deletes the document*, so stock is never restored, `HoldExpired` never
publishes, and `GET /api/holds/{id}` begins returning 404 for holds that genuinely existed.

### ADR‑004 — Time is injected

Every expiry decision reads an injected `TimeProvider`; tests use `FakeTimeProvider` and advance
the clock 16 minutes instantly. Without it, expiry would only be testable with `Thread.Sleep`, and
that test gets deleted the first time CI runs slow.

### ADR‑005 — Cache: delete-on-write, fail open

`GET /api/inventory` is the hot read, served through a `CachedInventoryRepository` **decorator**, so
the domain service never learns Redis exists. On every mutation the key is **deleted**, not
rewritten. Redis being unreachable produces a logged warning and a read from MongoDB — a cache
outage must never become an API outage.

*Rejected:* write-through updates. Two concurrent holds can interleave read → compute → SET and
persist a value that never existed in MongoDB.

### Why MongoDB and not PostgreSQL

MongoDB is specified by the brief. For a purely transactional, integrity-critical workload
PostgreSQL would be a defensible and in some ways stronger fit — `UPDATE … WHERE qty >= n` needs no
replica set, `CHECK (available_qty >= 0)` is enforced by the engine, and `SELECT … FOR UPDATE SKIP
LOCKED` is ideal for the sweeper. MongoDB earns its place through document-shaped aggregates (a
hold *is* one document with its items embedded, so the list renders with no join), single-document
atomicity on the single-SKU hot path, and horizontal scale. The replica-set requirement is the
price, paid deliberately in ADR‑002.

---

## API

| Endpoint | Outcome | Code |
|---|---|---|
| `POST /api/holds` | created | **201** + `Location` |
| | insufficient stock | **409** with `sku`, `requested`, `available` |
| | unknown SKU | **422** |
| | quantity ≤ 0, no items, duplicate SKU | **400** |
| `GET /api/holds` | active holds | **200** |
| `GET /api/holds/{id}` | found, any status | **200** — expired returns `status: "Expired"` |
| | never existed | **404** |
| `DELETE /api/holds/{id}` | released | **200** + final state |
| | not found | **404** |
| | already Released or Expired | **409** |
| `GET /api/inventory` | stock levels | **200** |

Errors are RFC 9457 `ProblemDetails`, mapped centrally by `DomainExceptionHandler` — which is why
**no controller in this solution contains a `try/catch`**.

Returning `200` with `status: "Expired"` rather than `410 Gone` is deliberate: the client must be
able to distinguish "your hold timed out" from "that id never existed".

### Events

Durable topic exchange `inventory.holds`, routing keys `hold.created` / `hold.released` /
`hold.expired`, audit queue bound to `hold.#` with a dead-letter exchange. Payloads are
self-contained so a consumer can act without calling back, and carry an `eventId` for deduplication
because delivery is at-least-once.

---

## Testing

```bash
dotnet test
```

26 tests, all mocked — **no running infrastructure required**, which is what the ports exist for.
Coverage spans validation and error handling, the full lifecycle, cache behaviour including
Redis-down, the architecture rule, and the release-versus-expire race.

---

## Configuration

Nothing is hardcoded. Every value below is overridable by environment variable using `__` for
nesting (e.g. `Mongo__ConnectionString`).

| Key | Default | Notes |
|---|---|---|
| `Mongo__ConnectionString` | `mongodb://localhost:27017/?replicaSet=rs0` | |
| `Mongo__UseTransactions` | `true` | `false` falls back to compensating rollback |
| `Redis__ConnectionString` | `localhost:6379` | |
| `Redis__InventoryTtlSeconds` | `30` | Safety net for a missed invalidation |
| `RabbitMq__Uri` | `amqp://guest:guest@localhost:5672/` | |
| `Hold__ExpirationMinutes` | `15` | Fractional values allowed, so a demo can use seconds |
| `Hold__SweeperIntervalSeconds` | `15` | |

---

## Known limitations

- **Events are published after the database commit, not atomically with it.** A crash in that
  window loses the event; the database stays correct. The production fix is a transactional outbox:
  persist the event inside the same transaction and relay it asynchronously. Scoped out
  deliberately for the time budget rather than overlooked.
- **No hold-to-order conversion.** Out of scope per the brief; holds only end by release or expiry.
- **Sweeper batch is capped** at `SweeperBatchSize` per tick, so a very large backlog drains over
  several ticks.
- **No authentication.** Explicitly out of scope.

## AI usage

See [AI-USAGE.md](AI-USAGE.md).
