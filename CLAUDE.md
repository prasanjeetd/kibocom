# Project instructions

## Git — hard rules

**Never run `git add` or `git commit` without being asked.**

- Staging is the maximum permitted action, and it still requires explicit permission first.
- Committing without being asked is never acceptable. Pushing is always the user's.
- Finish the work, leave it in the working tree, then report what changed and say it is ready to
  stage. Wait to be asked.
- A task that seems to imply committing ("save this", "wrap up", "finish") is **not** permission.
  Ask anyway.
- Prefer handing over a ready-to-paste command over running it.

**Never revert or rewrite anything already pushed.** Once a commit is on the remote it is off
limits: no `git revert`, no `git reset` that discards it, no history rewrite (`filter-branch`,
`rebase`, `commit --amend`), no force-push. Fix problems by proposing a new commit on top, and only
when asked. Rewriting unpushed local history is a separate case — confirm before doing even that.

## Architecture constraints

The invariant this service exists to protect:

```
For every product:   availableQty + Σ(qty of all ACTIVE holds) == totalQty
```

Standing rules — see `docs/adr/` for the reasoning:

- **Never read-then-write on stock.** The quantity precondition belongs inside the update filter,
  never in a preceding `if`. `FindOneAndUpdate` returning null is the "lost the race" signal.
- **Never `DateTime.UtcNow`.** Inject `TimeProvider` everywhere, including audit timestamps.
- **Never a MongoDB TTL index for hold expiry.** It deletes the document, so stock is never
  restored and `HoldExpired` never publishes.
- **No infrastructure types in `Domain`.** It has zero NuGet package references and
  `ArchitectureTests` enforces that. Persistence uses separate document types.
- **No `try/catch` in controllers.** Domain exceptions map centrally in `DomainExceptionHandler`.
- **Tests mock all four ports** and must never require running infrastructure.
- **Health probes must not consume what they measure**, and must not report `Unhealthy` for a
  dependency the app is designed to survive without. `/health/live` runs no dependency checks.
