# ADR-002: A MongoDB transaction spans multi-item holds

## Context
A cart can contain several products. If the chair deducts successfully and the desk is out of
stock, the chair is stranded: its stock is gone and no hold exists that could release it. MongoDB
guarantees atomicity across documents only inside a transaction, and transactions require a
replica set.

## Decision
All deductions plus the hold insert commit together through `WithTransactionAsync`, which also
retries `TransientTransactionError` automatically. That error is the expected outcome when two
guarded deductions collide on the same document.

Compose starts MongoDB with `--replSet rs0`, self-initiating inside its own healthcheck so no
separate init container is needed. MongoDB Atlas M0 is also a three-node replica set, so the same
code runs unchanged against the free tier.

## Consequences
- Partial deduction is impossible. Either the whole hold exists or nothing changed.
- A replica set becomes a hard requirement of the deployment.
- `Mongo:UseTransactions=false` selects a compensating-rollback fallback for standalone servers.
  It is correct in the ordinary case but leaves a crash window in which stock is permanently lost,
  which is why it is not the default.
