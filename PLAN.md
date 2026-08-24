# Inventory Hold Microservice — Architecture & Phase Plan

> Phase 0 builds a *walking skeleton* — every component connected, one trivial vertical slice live.
> Every later phase layers one business capability onto plumbing that is already proven.

**Verified on this machine:** .NET SDK 10.0.111 · Node 24.14 · Docker 29.1.3 / Compose v2.40.3 ·
git 2.43 · 16 GB RAM (6.3 GB free). ⚠️ Docker Desktop's Linux engine is currently returning 500s —
restart Docker Desktop before Phase 0.

---

## 0. Decisions locked

| Question | Decision |
|---|---|
| Database | MongoDB (mandated by the brief; Postgres would suit a purely transactional workload, noted in README) |
| Concurrency | Guarded atomic `$inc` + transaction for multi-item — ADR‑1/2 |
| Expiry | Lazy-on-read + atomic-claim sweeper. **Never** a Mongo TTL index — ADR‑3 |
| Time | Injected `TimeProvider` everywhere — ADR‑4 |
| Cache | Redis, delete-on-write, fail-open — ADR‑5 |
| Transactional outbox | **Cut from code.** Documented as a known limitation with the fix |
| Frontend state | TanStack Query alone. No Zustand/Redux — there is no client-only state |
| Event visibility | **Activity page inside the React app.** No shared credentials, ever |
| Deployment | Optional Phase 7. Cloudflare Pages + Cloud Run + Atlas M0 + Upstash + CloudAMQP |
| Secrets | Never in the repo, never in chat. `.env` is gitignored; cloud values set in provider consoles |

---

## 1. The core domain — one invariant

Everything in this service exists to protect one equation:

```
For every product:   availableQty  +  Σ(qty of all ACTIVE holds)  ==  totalQty
```

Every decision below answers: *does this survive concurrency, crashes, and clock drift?* If a change
can break that equation, it is wrong however clean it looks.

**What a hold is.** One chair in stock. Alice starts checkout at 3:00pm and spends four minutes
typing her card details. Bob starts checkout at 3:02pm. Without a hold, one of them gets
"out of stock" *after* entering payment details. Deduct at add-to-cart instead and abandoned carts
strangle inventory for days. A hold is the middle path: **a temporary reservation with a deadline.**
Expiry is what makes reserving safe — it is a promise that cancels itself.

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

`RELEASED` and `EXPIRED` are terminal; both restore stock. **The sharpest race is a user releasing a
hold at the same instant the sweeper expires it** — restore twice and the invariant breaks silently.
That is the concurrency test the brief asks for.

---

## 2. Layering & dependency direction

The brief prescribes this layout. Follow it **exactly** — no "improvements". Adhering to a given
standard is itself part of what is being evaluated.

```
src/
├── InventoryHold.Contracts/       DTOs, enums, request/response.  ZERO dependencies.
├── InventoryHold.Domain/          Entities, invariants, domain events, exceptions
│   ├── Services/                  HoldService, InventoryService
│   └── Repositories/              PORTS: IHoldRepository, IInventoryRepository,
│                                        ICacheService, IEventPublisher
├── InventoryHold.Infrastructure/  ADAPTERS: Mongo, Redis, RabbitMQ. Refs Domain + Contracts.
├── InventoryHold.WebApi/          Controllers, DI, Program.cs, middleware, BackgroundServices
└── InventoryHold.UnitTests/       NUnit + NSubstitute + FakeTimeProvider
web/                               React 19 + Vite + TS + TanStack Query
docs/adr/                          ADR-001..005
```

**Dependency rule:** arrows point inward only. `Domain` must never reference MongoDB, Redis, or
RabbitMQ packages. Phase 6 adds a **NetArchTest** case that fails the build otherwise — that test is
the proof the DDD claim is real rather than cosmetic.

---

## 3. ADRs

Each gets a short file in `docs/adr/` (Context · Decision · Consequences) and a README link. They
also serve as AI context — an ADR pasted into `CLAUDE.md` constrains a model far better than prose.

### ADR-001 — Guarded atomic `$inc`, never read-then-write

```
filter: { _id: sku, availableQty: { $gte: n } }     ← the condition
update: { $inc: { availableQty: -n } }              ← the change
```

One round trip; **the filter is the check**, so there is no window between checking and writing.
`FindOneAndUpdate` returning `null` means someone else won the race → **409**, not a crash.

SQL equivalent, for the README:

```sql
UPDATE inventory SET available_qty = available_qty - @n
WHERE  sku = @sku AND available_qty >= @n
RETURNING sku, available_qty;          -- 0 rows returned  ⇒  lost the race ⇒ 409
```

**Rejected:** `SELECT` the quantity, check it in C#, then `UPDATE`. Every AI assistant writes this
first. Two callers both read `1`, both pass the check, both write `0`. Oversold.

### ADR-002 — Multi-item holds use a MongoDB transaction (single-node replica set)

A cart holds a chair *and* a desk. If the chair deducts and the desk is out of stock, the chair is
stranded — deducted, with no hold to release it. A transaction makes it all-or-nothing.

Compose runs `mongo:8` with `--replSet rs0`, self-initiating inside its own healthcheck (no init
container). Deductions + hold insert commit together via `WithTransactionAsync`, which also
auto-retries `TransientTransactionError` — the expected result of two guarded `$inc`s colliding.

> In SQL, `BEGIN`/`ROLLBACK` is simply always there. MongoDB builds transactions on its replication
> machinery, so it only enables them when configured as a replica set. `--replSet rs0` is how you
> switch the undo button on.

**Rejected:** manual compensating rollback. It works, but leaves a crash window between the failure
and the compensation in which stock is permanently lost.

### ADR-003 — Lazy expiry **plus** an atomic sweeper. Never a Mongo TTL index.

Two mechanisms, two different jobs:

| Concern | Mechanism |
|---|---|
| Never report an expired hold as Active | `Hold.StatusAt(now)` derives status from `expiresAt`; stored status is never trusted alone |
| Stock must actually come back | `ExpirySweeper : BackgroundService`, every ~30s |
| `HoldExpired` fires exactly once | The sweeper **claims** each hold atomically before acting |

```
filter: { _id: holdId, status: "Active", expiresAt: { $lt: now } }
update: { $set: { status: "Expired", resolvedAt: now } }
```

Lazy alone is broken — a hold nobody reads never returns its stock. Sweeper alone is broken — a read
in the gap between expiry and the next sweep would lie. Both are needed.

**The same guard shape appears three times.** Once you see it, the design is one idea repeated:

| Purpose | Guard |
|---|---|
| Take stock | `sku = @s AND available_qty >= @n` |
| Release | `id = @id AND status = 'Active'` |
| Expire | `id = @id AND status = 'Active' AND expires_at < now()` |

All three: **0 rows means someone else got there first.** That is exactly how release-vs-expire can
never both return the stock.

**Rejected:** a MongoDB TTL index on `expiresAt` — the first thing AI suggests, and silently
catastrophic. It *deletes the document*, so stock is never restored, `HoldExpired` never publishes,
and `GET /api/holds/{id}` starts 404ing on holds that genuinely existed.

### ADR-004 — Time is injected: `TimeProvider`, never `DateTime.UtcNow`

Tests use `FakeTimeProvider` and advance the clock 16 minutes instantly. Without it, expiry is only
testable via `Thread.Sleep`, and that test gets deleted the first time CI runs slow.

### ADR-005 — Cache: delete-on-write, fail-open

`GET /api/inventory` is the hot read. Cache-aside via a `CachedInventoryRepository` **decorator** —
the domain service never learns Redis exists, which keeps cache concerns out of hold-logic tests.

- Key `inventory:all`, TTL 30s — a safety net for a missed invalidation, not the primary mechanism.
- On every mutation: **`DEL` the key.** Never write the new value.
- Redis unreachable ⇒ log a warning, serve from Mongo. **A cache outage must never become an API outage.**

**Rejected:** write-through updates. Two concurrent holds interleave read → compute → SET and
persist a value that never existed in Mongo.

---

## 4. Data model

```jsonc
// inventory
{ "_id": "SKU-1001", "name": "Aeron Chair", "totalQty": 100,
  "availableQty": 87, "updatedAt": ISODate() }

// holds
{ "_id": UUID, "status": "Active",            // Active | Released | Expired
  "items": [ { "sku": "SKU-1001", "quantity": 2, "nameSnapshot": "Aeron Chair" } ],
  "customerId": "cust-42",
  "createdAt": ISODate(), "expiresAt": ISODate(), "resolvedAt": null }

// event_log  — CAPPED collection (~1 MB), feeds the Activity page (Phase 7b)
{ "_id": UUID, "eventType": "HoldCreated", "occurredAt": ISODate(), "payload": { } }
```

Indexes: `holds { status: 1, expiresAt: 1 }` — the sweeper's only query, must not table-scan.
A `$jsonSchema` validator asserting `availableQty >= 0` turns any future logic bug into a loud write
failure instead of silent corruption.

`nameSnapshot` is deliberate: the holds list renders with no N+1 back to inventory.
`event_log` is **capped** — fixed size, oldest auto-evicted, no cleanup job, no unbounded growth.

---

## 5. API contract & status codes

RFC 9457 `ProblemDetails` via `IExceptionHandler`, so **no controller contains a `try/catch`**.

| Endpoint | Outcome | Code |
|---|---|---|
| `POST /api/holds` | created | **201** + `Location` |
| | insufficient stock | **409** + SKU, requested vs available |
| | unknown SKU | **422** |
| | qty ≤ 0, empty items, duplicate SKU | **400** |
| `GET /api/holds/{id}` | found, any status | **200** — expired returns `status: "Expired"` |
| | never existed | **404** |
| `DELETE /api/holds/{id}` | released | **200** + final state (the UI needs it) |
| | not found | **404** |
| | already Released or Expired | **409** |
| `GET /api/inventory` | | **200** |

`200 + status:"Expired"` rather than `410 Gone` is deliberate — the client must distinguish
"your hold timed out" from "that ID never existed."

---

## 6. Event topology

Topic exchange `inventory.holds` (durable) · routing keys `hold.created` / `hold.released` /
`hold.expired` · queue `inventory.holds.audit` bound to `hold.#`, with a dead-letter exchange.

```jsonc
{ "eventId": UUID, "eventType": "HoldCreated", "occurredAt": "...", "holdId": UUID,
  "customerId": "cust-42", "expiresAt": "...",
  "items": [ { "sku": "SKU-1001", "quantity": 2 } ] }
```

Payloads carry enough for a consumer to act **without calling back** into this service — the whole
point of an event. `eventId` exists so consumers can dedupe.

---

## 7. Scope — in and out

**In (non-negotiable):** the four endpoints with the real status-code matrix · guarded `$inc` +
transaction · atomic state transitions · lazy expiry + sweeper · three events · Redis cache with
invalidation · 5+ mocked unit tests including a concurrency case · React SPA with the four screens ·
one-command startup · README + AI-USAGE.md.

**Cut, and stated in the README:**

- **Transactional outbox.** Publishing happens post-commit; a crash in that window loses the event.
  README entry: *"Production fix: persist the event in the same transaction, relay asynchronously."*
  Naming the gap earns most of the credit at zero cost.
- **Value objects** (`Sku`, `Quantity`) — primitives validated in the factory method instead.
- Any RabbitMQ consumer beyond the audit logger.

**Kept because it is cheap and reads as production-ready:** `/health` covering all three
dependencies · structured logging with correlation IDs · ProblemDetails everywhere · env-var config
with no secrets in code · non-root container user · graceful shutdown and connection retry/backoff ·
the two indexes · the NetArchTest dependency rule.

---

## 8. Development environment

**Nothing is installed on Windows.** Mongo, Redis and RabbitMQ run as containers — isolated,
~1.5 GB RAM total, and `docker compose down -v` erases every trace.

Two compose profiles, because the fast dev loop and the graded deliverable are different things:

```bash
# Day-to-day: infra in containers, code on the host — hot reload + full debugger
docker compose up -d mongo redis rabbitmq
dotnet watch --project src/InventoryHold.WebApi
cd web && npm run dev

# The deliverable, verified before every phase is called done
docker compose up --build
```

Local development never touches a cloud account. `.env` is gitignored from the first commit;
`.env.example` is committed with placeholder values only.

---

## 9. Phases

Each phase ends **green and demoable** with its own commits, so the git history itself evidences the
AI-augmented workflow.

### Phase 0 — Walking skeleton *(~3h) — de-risks everything else*

Goal: one command → the browser shows **real seeded inventory read from Mongo through all five
layers**, with Redis and RabbitMQ connected and health-checked. Zero hold logic.

- [ ] `git init`; `.gitignore`, `.gitattributes`, `.dockerignore`; commit **`CLAUDE.md` first**
- [ ] Solution + 5 projects, references wired to enforce the dependency rule
- [ ] `docker-compose.yml`: api, mongo (replSet + self-init healthcheck), redis, rabbitmq, web —
      `depends_on: { condition: service_healthy }` throughout
- [ ] Multi-stage `Dockerfile` (SDK 10 build → aspnet 10 runtime, non-root)
- [ ] Options: `MongoOptions`, `RedisOptions`, `RabbitMqOptions`, `HoldOptions`
      (`ExpirationMinutes: 15`) — env-var overridable, zero hardcoded credentials
- [ ] `/health` covering Mongo + Redis + RabbitMQ — *this is what proves "connected"*
- [ ] Idempotent seeder: 5 products
- [ ] `GET /api/inventory` end to end + Vite React dashboard rendering it
- [ ] ProblemDetails, CORS, structured logging, OpenAPI + Scalar

**Done when:** clean clone → `docker-compose up --build` → inventory in the browser, `/health` all
green, and `docker compose down -v && up --build` reproduces it from scratch.

### Phase 1 — Core hold lifecycle *(~4h) — where the assignment is won*
Rich `Hold` / `InventoryItem` entities, private setters, static factory methods. ADR‑001 guarded
`$inc`. ADR‑002 transaction. POST / GET / DELETE. Lazy expiry. Guarded state transitions. Full
status-code matrix. **Unit tests land here, not afterwards.**

### Phase 2 — Messaging *(~2h)*
`IEventPublisher` port + RabbitMQ adapter, topology declaration, DLX, publish on create and release,
connection resilience.

### Phase 3 — Expiry sweeper *(~1.5h)* — needs Phase 2's publisher
`ExpirySweeper : BackgroundService` with the atomic claim, stock restoration, `HoldExpired`. Tested
by advancing `FakeTimeProvider`.

### Phase 4 — Redis caching *(~1.5h)*
`CachedInventoryRepository` decorator, TTL, `DEL`-on-mutation, fail-open. Tests: cache hit,
miss-then-populate, invalidation-after-mutation, and **Redis-down still serves**.

### Phase 5 — Frontend completion *(~2.5h)*
Multi-product create-hold form, active holds with live countdown, release with confirmation,
TanStack Query `invalidateQueries` so inventory and holds re-sync with no page refresh, loading
states, API errors surfaced. Typed client generated from OpenAPI.

**No optimistic updates on hold creation** — this domain is *about* contention and the server may
legitimately answer 409. Optimistically decrementing shows a number that is about to be retracted.
Invalidate on settle instead. The domain informs the UI pattern.

### Phase 6 — Hardening & docs *(~1.5h)*
NetArchTest dependency rule · concurrency test (N parallel holds on 1 unit ⇒ exactly one 201 and
N−1 409s) · `docs/adr/` written up · README with setup, design decisions and known limitations ·
final `AI-USAGE.md` pass · fresh-clone rehearsal.

---

## 10. Phase 7 — Deployment *(optional, ~1h each, drop without regret)*

Only after Phase 6 is green. Because config is already env-var driven, **none of this needs a code
change** — same image, five environment variables.

| Piece | Service | Free tier |
|---|---|---|
| Frontend | Cloudflare Pages | Unlimited bandwidth, no card |
| API | Google Cloud Run | 2M requests/month always-free, scales to zero |
| MongoDB | Atlas **M0** | 512 MB, 3-node replica set ✅ transactions work |
| Redis | Upstash | 256 MB, 500K commands/month |
| RabbitMQ | CloudAMQP Little Lemur | Free shared broker |

Atlas M0 caveats: 100 ops/sec, no backups, auto-pauses after 30 days idle. Fine for a demo, and a
one-line transaction smoke test should confirm `WithTransactionAsync` works there before relying on it.

**7a — deploy.** Secrets go in the provider consoles, never the repo.

**7b — Activity page.** Events are invisible, so a reviewer has no way to confirm the messaging layer
exists. Rather than share broker credentials — which CloudAMQP's free plan cannot scope to read-only
anyway — build it into the product:

```
RabbitMQ  →  queue inventory.holds.audit  →  AuditConsumer (BackgroundService)
          →  capped collection event_log  →  GET /api/events  →  React "Activity" page
```

Proves the **full round trip**, publish *and* consume. Works identically for a reviewer running
`docker-compose up` locally. No credentials to share or rotate.

**The trick that makes the demo land:** on the deployed instance only, set
`Hold__ExpirationMinutes` to **60 seconds**. The reviewer then creates a hold, watches inventory
drop, sees `HoldCreated` on the Activity page, waits a minute, and sees **`HoldExpired` appear on its
own with inventory restored** — atomic deduction, sweeper, events, and cache invalidation all
demonstrated in one interaction. README note: *"Demo runs a 60-second expiry so expiration is
observable; default is 15 minutes."* It also proves the configurability requirement is real.

**7c — Grafana Cloud Loki** for application logs, if anything is left. Verify shared/public
dashboards are on the free plan first, or reviewers hit a login wall.

---

## 11. AI orchestration (graded, not incidental)

The JD's line — *"Prompt Engineering is actually Context Engineering"* — is the instruction. The
evidence is a committed context file, not a claim in prose.

**`CLAUDE.md`, committed before any code:** the §1 invariant, the §2 layout and dependency rule,
ADR‑001..005 as hard constraints, the status-code matrix, and standing rules — *never
`DateTime.UtcNow`, never read-then-write on stock, no infra types in Domain, no `try/catch` in
controllers, tests mock all four ports*. The AI then conforms by default instead of being corrected
file by file. **That file is the deliverable** for "how you managed context".

**Per-phase loop:** plan → AI drafts against `CLAUDE.md` → human-audits the diff against the ADRs →
run tests → commit naming what was accepted and rejected → append to `AI-USAGE.md` **while fresh**.
Reconstructed on the last day, it reads exactly as fabricated as it would be.

**Rejection log to capture as it happens** — each is a predictable AI failure mode this design
already anticipates, so the "Human Audit" section writes itself:

1. read-then-write stock check → guarded `$inc` (ADR‑001)
2. Mongo TTL index for expiry → sweeper, or stock is silently lost (ADR‑003)
3. `DateTime.UtcNow` → injected `TimeProvider` (ADR‑004)
4. anemic entities with public setters → rich model enforcing its own invariants
5. publish-then-commit dual write → post-commit publish, gap documented
6. write-through cache update → `DEL`-on-write (ADR‑005)
7. silently swallowed Redis exceptions → fail open, *with* a logged warning
8. `IMongoCollection` leaking into `Domain` → caught by the NetArchTest
9. tests that spin up real Mongo → mocked ports only, as the brief requires
10. connection strings baked into `appsettings.json` → env-var-overridable options

**Verification:** AI drafts the test *matrix* from the ADRs; correctness comes from running the
suite, mutation-checking the concurrency test (break the guard filter — the test must fail), and the
fresh-clone startup rehearsal. AI-generated code that is never executed is not verified, only plausible.

---

## 12. Risks

| Risk | Mitigation |
|---|---|
| Docker Desktop engine currently erroring | restart it before Phase 0 — first thing, not on day 2 |
| Replica-set init flakiness in Compose | self-initiating healthcheck + `service_healthy` gating, rehearsed in Phase 0 |
| RabbitMQ slower to boot than the API | healthcheck gate plus publisher retry/backoff; the API starts regardless |
| Time sink on frontend polish | out of scope per the brief — no auth, no routing, no pixel-perfect CSS |
| Phase 7 eating core time | strictly optional, after Phase 6, droppable in any order |
| Windows path / line-ending issues | `.gitattributes` and `.dockerignore` from the first commit |
