# ADR-004: Time is injected

## Context
Every expiry decision depends on the current time. Code that calls `DateTime.UtcNow` directly can
only be tested by actually waiting.

## Decision
All time comes from an injected `TimeProvider`. Tests use `FakeTimeProvider` and advance the clock
instantly.

## Consequences
- Expiry behaviour is deterministic and fast to test: sixteen minutes pass in microseconds.
- No `Thread.Sleep` in the test suite, so no flaky tests to be deleted the first time CI runs slow.
- The rule has to hold everywhere, including audit timestamps in the repositories, or the seam
  leaks.
