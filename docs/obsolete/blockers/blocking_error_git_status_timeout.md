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

**Update 2026-09-01 (separate session, CS0122 lookup-helper work):** fourth occurrence, same
signature — `Git(operation: "status")` timed out at exactly 30s after applying/testing the CS0122
`CompilerErrorLookupHelper` changes. Server log
(`bin-vscode/Advanced/logs/server-20260901-012449.log`) shows the actual underlying git subprocess
call for the first time, which the earlier occurrences in this file didn't capture:

```
2026-09-01 01:32:01.552 [INF] tools/call request handler called.
2026-09-01 01:32:31.592 [ERR] git rev-parse --abbrev-ref HEAD did not exit within 30s and was killed.
  Not a normal git failure — see docs/TODO.md's 'Git(operation: status) hung indefinitely' entry.
2026-09-01 01:32:31.594 [ERR] Git status failed
System.TimeoutException: Git operation timed out after 30s and was cancelled.
   at RoslynSentinel.Server.Basic.GitTools.RunGitAsync(...) GitTools.cs:line 232
   at RoslynSentinel.Server.Basic.GitTools.StatusAsync(...) GitTools.cs:line 333
```

So `StatusAsync`'s first subprocess call (`git rev-parse --abbrev-ref HEAD` — used to get the
branch name) is the one stalling, not `git status` itself. This is a trivial, lock-free read that
normally completes in tens of milliseconds; a 30s+ stall on it specifically (rather than on the
heavier `status` porcelain command) is a useful new data point for whoever root-causes this.

**New lead — concurrent server processes:** at the time of this occurrence, `tasklist` showed
**five** separate `RoslynSentinel.Server.Advanced.exe` processes running simultaneously (PIDs
32056, 26280, 26592, 35528, 21724), consistent with [[project_concurrent_sessions]]. Untested
whether multiple server processes each spawning `git` subprocesses against the same working
directory produces contention that manifests as a `rev-parse` stall specifically — worth checking
`tasklist | findstr RoslynSentinel.Server` for strays and retrying with only one server instance
alive before deeper root-causing (e.g. Process Monitor / strace on the spawned `git.exe` to see
what it's actually blocked on: stdin read, credential helper, antivirus, etc).

Given this is now the 4th consecutive occurrence across 3+ sessions with 100% reproduction (not a
flake), this should be treated as a confirmed, reproducible bug rather than deferred again.

**Update 2026-09-01 (investigation + fix applied):** re-checked the concurrent-server-processes lead
first — at investigation time there were **6** `RoslynSentinel.Server.Advanced.exe` processes (one
more than the original 5), plus GitHubDesktop running. But `rev-parse --abbrev-ref HEAD` doesn't
take any lock (it's a plain read of `.git/HEAD`), and no `.git/*.lock` files were present at the
time, so process contention doesn't explain a stall on this specific command. Treating the
concurrent-server-count correlation as coincidental/unconfirmed rather than causal — it was never
actually tested, just observed alongside the hang each time.

Checked `where.exe git` → resolves cleanly to `C:\Program Files\Git\cmd\git.exe`, a real exe (not a
`.bat`/shim), so PATHEXT-triggered shell wrapping isn't the mechanism either. However
`GitTools.RunGitAsync` (`RoslynSentinel.Server.Basic/GitTools.cs`) had two real gaps consistent with
a process-spawn-time stall rather than anything git itself does once running:
1. `FileName = "git"` (bare name) forced .NET to re-resolve PATH via `CreateProcess` on every single
   subprocess spawn, rather than once.
2. `RedirectStandardInput` was never set, and stdin was never explicitly closed — an inherited/open
   but undrained stdin handle is a known cause of a spawned console child stalling indefinitely
   waiting for input, even for a command that never reads from it.

Applied a fix: `GitTools` now resolves git's absolute exe path once via a PATH+PATHEXT scan (cached
in a static field), and `RunGitAsync` now explicitly redirects and immediately closes
`StandardInput`. Build verified clean (0 errors/warnings) on `RoslynSentinel.Server.Basic`, which
`RoslynSentinel.Server.Advanced` project-references (see [[project_advanced_extends_basic]]).

**Not yet confirmed:** this fix has not been proven to eliminate the hang — the failure was
intermittent (not on-demand reproducible), so absence of a recurrence is only weak evidence. Next
occurrence (if any) should note whether it happened post-fix; if it recurs after this fix ships and
a server rebuild, the stdin/PATH theory is wrong and this needs a live repro with Process Monitor on
the spawned `git.exe` to see what handle/syscall it's actually blocked on.
