# ADR-001: Guarded atomic deduction, never read-then-write

## Context
Several checkouts can target the same SKU at the same instant. Reading the quantity, checking it in
application code, and writing it back leaves a window between the check and the write. Two callers
both read 1, both pass the check, and both write 0. The product is oversold.

## Decision
Deduct with a single `FindOneAndUpdate` whose filter carries the precondition:

    filter: { _id: sku, availableQty: { $gte: n } }
    update: { $inc: { availableQty: -n } }

The check and the write are one indivisible operation. A `null` result means another caller won the
race, which the API surfaces as `409 Conflict`.

SQL equivalent: `UPDATE inventory SET available_qty = available_qty - @n WHERE sku = @s AND
available_qty >= @n RETURNING *`, where zero rows affected means the race was lost.

## Consequences
- Oversell is impossible by construction. No locks, no retries, one round trip.
- Callers must treat "no rows matched" as an expected business outcome rather than an error.
- Distinguishing "unknown SKU" from "insufficient stock" needs a follow-up read, done only on the
  failure path so the happy path stays a single operation.
- Multi-SKU holds still need a transaction. See ADR-002.
