# AI Usage

## Strategy

**Tool:** Claude Code (Opus 5) running in VS Code, agent mode with filesystem and shell access.

The working assumption was the one in the job description — that prompt engineering is really
context engineering. So the first artefact produced was not code, it was
[`PLAN.md`](PLAN.md): the domain invariant, the layering rules, five ADRs written as *hard
constraints*, and the complete HTTP status-code matrix. Code generation only began once that
existed.

That ordering matters. Given a blank prompt, a model will happily produce a plausible inventory
service with a read-then-write stock check and a TTL index for expiry. Given the invariant

```
availableQty + Σ(active hold quantities) == totalQty
```

plus a standing rule that *the precondition must live inside the write*, the same model produces
the correct thing on the first attempt. The constraints were stated once, up front, rather than
re-litigated per file.

Standing rules carried through the whole build:

- Never `DateTime.UtcNow` — inject `TimeProvider`
- Never read-then-write on stock — the guard belongs in the filter
- No infrastructure types in `Domain`
- No `try/catch` in controllers — map centrally
- Tests mock all four ports and require no running infrastructure

**Working loop:** plan → generate against the constraints → review the diff against the ADRs →
build and run tests → commit. Each phase ended green before the next began, starting with a
walking skeleton so that Docker, replica-set initialisation, and dependency wiring were proven
before any business logic depended on them.

---

## Human audit

### Rejected: read-then-write stock deduction

The default shape for "check stock, then deduct" is a `SELECT`, an `if`, and an `UPDATE`. It
oversells under concurrency and no amount of retry logic repairs it, because the defect is the gap
between the check and the write.

Replaced with a single guarded operation where the precondition is part of the filter, so
`FindOneAndUpdate` returning `null` *is* the "someone else won" signal. This is ADR‑001 and it is
the core of the assignment.

### Rejected: a MongoDB TTL index for expiry

A TTL index on `expiresAt` is the elegant-looking answer and it is silently catastrophic here: it
**deletes the document**. Stock is never restored, `HoldExpired` is never published, and
`GET /api/holds/{id}` starts returning 404 for holds that genuinely existed — data loss dressed up
as a one-line configuration.

Replaced with lazy expiry on the read path (so answers are honest immediately) *plus* a sweeper
that claims each hold atomically before restoring stock (so the restore happens exactly once, even
across replicas). Two mechanisms because they solve two different problems — ADR‑003.

### Rejected: BSON attributes on the domain entities

The path of least resistance is to decorate `Hold` with `[BsonId]` and persist it directly. That
puts a MongoDB dependency inside `Domain` and quietly makes the layering decorative.

Separate `HoldDocument` / `InventoryDocument` types were introduced in `Infrastructure` instead.
The result is that `Domain` has **zero NuGet package references of any kind**, which
`ArchitectureTests` now enforces on every build.

### Rejected: `.slnx` solution format

.NET 10's `dotnet new sln` defaults to the newer XML `.slnx` format. It is genuinely nicer, but it
needs recent tooling to open. For a submission that someone else will clone and build, the classic
`.sln` costs nothing and removes a failure mode. Compatibility beat novelty.

### Accepted after correction: fail-open caching

This is the one worth reading, because a test caught a real defect rather than confirming an
assumption.

The Redis adapter carefully wrapped every operation so an outage could not take down the API. The
caching *decorator* then called `cache.SetAsync(...)` directly. A test was written asserting that
inventory still serves when the cache throws — and it **failed**:

```
Expected: No Exception to be thrown
But was:  <System.InvalidOperationException: redis down>
   at CachedInventoryRepository.GetAllAsync(...)
```

The fail-open guarantee existed at one layer and was silently lost at the next. The decorator was
depending on its collaborator being well behaved, which is exactly the assumption a resilience
guarantee is not allowed to make.

The fix was to the production code, not the test: cache reads and writes in the decorator are now
individually guarded and logged, so *any* `ICacheService` implementation failing still leaves the
read path serving from MongoDB.

### Scoped out on purpose: the transactional outbox

Publishing happens after the database commit, so a crash in that window loses the event. The
correct fix is a transactional outbox — persist the event inside the same transaction, relay it
asynchronously.

It was deliberately left out for the time budget and recorded under Known Limitations in the
README with the fix named. Shipping a documented gap is honest; shipping a half-built outbox would
have been worse than either alternative.

---

## Verification

**Tests were generated from the ADRs, not from the implementation.** Deriving tests from the code
you just wrote mostly proves the code does what it does. Deriving them from the stated invariants
lets them disagree with the implementation — which is what happened above.

Twenty-six tests, all with mocked ports, no infrastructure required:

- validation and error mapping — empty items, duplicate SKU, non-positive quantity, missing customer
- lifecycle — create deducts, invalidates cache, publishes; release restores and publishes
- lazy expiry — a hold past its deadline reports `Expired` even while storage still says `Active`,
  verified by advancing `FakeTimeProvider` rather than sleeping
- negative paths — insufficient stock publishes **nothing** and leaves the cache untouched
- **the concurrency case** — release losing the race to the sweeper returns 409, publishes no
  event, and does not restore stock a second time
- caching — hit, miss-then-populate, uncached `GetBySku`, and Redis-down still serving
- architecture — `Domain` and `Contracts` provably free of driver dependencies

Beyond the suite: `dotnet build` is clean with zero warnings across all five projects, and the
frontend type-checks under `strict` with `erasableSyntaxOnly`.

### Integration evidence

Mocked tests prove the logic; they cannot prove the wiring. The system was therefore exercised
against real infrastructure twice — once on local containers, once against managed free tiers
(MongoDB Atlas M0, Upstash, CloudAMQP) — with no code change, only environment variables.

| Claim | How it was demonstrated |
|---|---|
| Oversell is impossible | 12 concurrent holds, each demanding all 8 remaining units: **exactly one 201, eleven 409**. Stock settled at 0, never negative, `available + held == total` |
| Multi-item holds are atomic | A two-product hold committed inside a real transaction on Atlas M0, and both lines restored together on release |
| Stock returns on its own | A hold with a 6-second TTL: `Active` at t+4s, **`Expired` with stock restored at t+6s**, with no request touching it. Releasing it afterwards returned 409 |
| Status codes are meaningful | 201 / 400 duplicate SKU / 400 zero quantity / 404 unknown hold / 409 insufficient stock / 409 double release / 422 unknown SKU — each verified against a live API |
| Events really flow | Message observed on `inventory.holds.audit` via the `hold.#` binding, `delivery_mode: 2`, carrying a `message_id` for consumer deduplication |
| One-command startup works | `docker compose down -v` then `docker compose up --build` from an empty volume: healthchecks gated API startup until all three dependencies were healthy, the replica set self-initiated, five products seeded, and a create/release cycle completed |

The 409 body is worth showing, because "meaningful status codes" means more than the number:

```json
{
  "title": "Insufficient stock",
  "status": 409,
  "detail": "Insufficient stock for 'SKU-1005': requested 9999, available 6.",
  "sku": "SKU-1005", "requested": 9999, "available": 6
}
```

One defect was found this way and fixed: the event payload was serialising its own `routingKey`,
duplicating information the AMQP envelope already carries.

### What the AI was not trusted with

- **Deciding the concurrency model.** The guard-in-the-filter pattern, the transaction boundary,
  and the claim-before-restore ordering were specified up front and the AI implemented them.
- **Judging its own resilience code.** Fail-open looked correct in review at both layers; only an
  executed test found the gap.
- **Deciding scope.** What to cut (outbox, value objects) was a human trade-off against the
  deadline, not a model suggestion.

Generated code that has not been executed is not verified, only plausible. Every claim in this
document corresponds to a test run or a build that actually happened.
