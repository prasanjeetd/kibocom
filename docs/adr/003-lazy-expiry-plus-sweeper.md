# ADR-003: Lazy expiry and an atomic sweeper, never a TTL index

## Context
A hold expires at a fixed instant, but nothing is watching the clock at that instant. Three things
must happen: reads must never report a timed-out hold as Active, the stock must actually return,
and `HoldExpired` must be published exactly once even with several API replicas running.

## Decision
Two mechanisms, because they solve two different problems.

**Lazy expiry.** `Hold.StatusAt(now)` derives status from `expiresAt`. The stored value is never
trusted on its own, so a read is honest the moment the deadline passes.

**Sweeper.** A background service claims each due hold with a compare-and-swap before touching
anything:

    filter: { _id: holdId, status: "Active", expiresAt: { $lt: now } }
    update: { $set: { status: "Expired", resolvedAt: now } }

Only the caller whose filter matches restores stock and publishes.

The same guard shape protects release (`status: "Active"` to `"Released"`), which is how a customer
releasing a hold at the same instant the sweeper expires it cannot restore stock twice.

## Consequences
- Correct across replicas with no coordination, leader election, or distributed lock.
- Expiry is observable and testable by moving an injected clock. See ADR-004.
- Requires an index on `{ status, expiresAt }`, or the sweep is a collection scan.

## Rejected: a TTL index on expiresAt
It is the obvious one-line answer and it is silently catastrophic. A TTL index **deletes the
document**: stock is never restored, `HoldExpired` is never published, and `GET /api/holds/{id}`
begins returning 404 for holds that genuinely existed. Data loss dressed up as elegance.
