# Blocking error: `Git(operation: status)` times out after 30s, twice in a row

**Date:** 2026-08-31 (session continuing from `docs/current/ideas/external-drift-hard-blocker.md`
implementation).

**What happened:** while implementing the finalized external-drift-hard-blocker plan (per
[[feedback_dogfood_mcp_blocking_errors]]), after successfully applying 3 naming-correction edits
via `ApplyDiff` (see below), called `Git(operation: "status")` to check for pre-existing
uncommitted changes before starting step 2 (the content-hash baseline). Both the first call and an
immediate retry failed identically:

```json
{"success":false,"branch":"","isClean":false,"staged":[],"unstaged":[],"untracked":[],"error":"Git operation timed out after 30s and was cancelled.","isTruncated":false}
```

**Relation to known issue:** `docs/current/TODO.md` already documents a `Git(status)` hang from
2026-08-27 ("closed" — not root-caused, but a 30s `GitProcessTimeout` fast-fail bound was added as
the accepted mitigation, with a note that a normally-running server handled `Git(status)` fine in a
manual re-test). This looks like the same class of failure recurring, but two consecutive timeouts
(not an isolated one-off) is new data — the earlier note treated a single occasional hang as
acceptable given the fast-fail bound; back-to-back failures suggests something more persistent this
time (possibly `git` itself in a slow/locked state on this host, a large working tree diff, or a
regression in whatever the fast-fail bound wraps).

**State at time of stop:** no in-flight edit — the 3 naming-correction fixes (step 1 of the
finalized plan) were already applied and validated cleanly via `ApplyDiff` before this call:
- `RoslynSentinel.Common/PersistentWorkspaceManager.cs:1091` — refusal message
- `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:1051` — `DeleteFile` description
- `RoslynSentinel.Tests.Battery/BatteryTwentyTests.cs:370` — test renamed to
  `AcknowledgeExternalFileChanges_Always_DoesNotThrow`

All three verified via `SearchSolutionText("ClearExternalDrift")` returning zero matches, and each
`ApplyDiff` call's `validationResult.success: true` with no new diagnostics. These are safe,
complete, uncommitted changes — not committed since git tooling itself is what's blocked.

**What was being checked (not yet answered):** whether the repo has other pre-existing uncommitted
changes (the conversation's initial gitStatus context showed `FilePath.cs`,
`PersistentWorkspaceManager.cs`, `FilePathFromWireTests.cs` modified before this session's own
edits started) that should be accounted for before proceeding into step 2's larger hash-baseline
change.

**Next steps once unblocked:** retry `Git(operation: status)`; if it now succeeds, resume at step 2
of the finalized plan (content-hash baseline gate in `PersistentWorkspaceManager.cs`, layered in
front of `_internalChanges`/`_externalChanges` per the plan doc's "Decisions" section). If it keeps
failing, this may need root-causing for real this time rather than deferring again — two
back-to-back timeouts is a stronger signal than the original single-occurrence note.

**Update 2026-09-01:** retried after completing step 2 (content-hash baseline) and step 3
(SentinelAdminTools extraction + Admin mode wiring) — third consecutive timeout, identical error.
Per explicit user instruction mid-session ("If Git fails again, document it as a blocker and
continue"), this is now documented and treated as non-blocking for the remainder of this session:
proceeding to step 4 (session-wide fatal blocker) without git-status confirmation. Uncommitted work
is accumulating (steps 1-3 of the plan, all individually validated via `ApplyDiff`/`CreateFile`
success + 0 diagnostics) and will need a commit once git tooling is usable again — three consecutive
failures now warrants root-causing this as a real bug rather than a transient flake, separate from
this implementation task.
