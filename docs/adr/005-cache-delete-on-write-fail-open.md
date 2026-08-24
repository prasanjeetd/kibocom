# ADR-005: Cache invalidates by deletion and fails open

## Context
`GET /api/inventory` is the high-frequency read path. Cached data must not contradict MongoDB after
a hold is created, released, or expired, and a Redis outage must not take the API down with it.

## Decision
Cache-aside through a `CachedInventoryRepository` decorator, so the domain service never learns
Redis exists. On every mutation the key is **deleted**, never rewritten. A short TTL (30s) backs it
up as a safety net for a missed invalidation rather than as the primary mechanism. Every cache
interaction is guarded at both the adapter and the decorator, so any failure degrades to a read
from MongoDB with a logged warning.

## Consequences
- Correct under concurrency: deletion has no lost-update race, and the next reader re-derives truth.
- A cache outage costs latency, never availability. Health reports Redis as Degraded, not Unhealthy.
- `GetBySku` stays uncached because it feeds the deduction decision.

## Rejected: write-through cache updates
Two concurrent holds can interleave read, compute, and SET, persisting a value that never existed
in MongoDB. Deletion cannot exhibit that failure.
