---
name: project_git_status_rev_parse_hang_confirmed
description: "MCP Git(status) reproducibly timed out at 30s on 'git rev-parse --abbrev-ref HEAD' across 4 occurrences; fix applied 2026-09-01 (absolute exe path + stdin close in GitTools.RunGitAsync) and confirmed working in a fresh session — status/commit both instant, blocker doc moved to obsolete"
metadata: 
  node_type: memory
  type: project
  originSessionId: 1eb62413-88c5-4b73-98a5-413d813d544b
  modified: 2026-09-01T08:42:18.749Z
---

`Git(operation: "status")` in the RoslynSentinel MCP server timed out at exactly 30s
(`GitProcessTimeout` fast-fail) four times across at least 3 separate sessions (2026-08-27 original,
2026-08-31 x2, 2026-09-01 CS0122 session) — always the same signature, always stalling on `git
rev-parse --abbrev-ref HEAD` specifically (the first subprocess `StatusAsync` runs, used to get the
branch name before the heavier `status` porcelain command). Full details and update log live in
`docs/current/blockers/blocking_error_git_status_timeout.md`.

**Why:** investigated 2026-09-01 in `RoslynSentinel.Server.Basic/GitTools.cs`. Ruled out the two
leading theories from prior sessions: (1) concurrent `RoslynSentinel.Server.Advanced.exe` processes
(6 were running at investigation time, one more than the original 5) — ruled out because
`rev-parse --abbrev-ref HEAD` takes no lock, it's a plain read of `.git/HEAD`, and no `.git/*.lock`
files existed at the time; (2) PATHEXT/shim wrapping — ruled out because `where.exe git` resolves
directly to a real exe (`C:\Program Files\Git\cmd\git.exe`), not a `.bat`. Found two real gaps in
`RunGitAsync` instead, both consistent with a process-*spawn*-time stall (matches: always the first
git call, never mid-command): `FileName = "git"` forced PATH re-resolution via `CreateProcess` on
every spawn (no caching), and `RedirectStandardInput` was never set/closed — an open, undrained
stdin handle on a spawned console child is a known stall cause even for commands that never read
stdin.

**Fix applied:** `GitTools` now resolves git's absolute exe path once (static field, PATH+PATHEXT
scan) instead of re-resolving `"git"` per call, and `RunGitAsync` explicitly redirects and
immediately closes `StandardInput` after `process.Start()`. Build verified clean (0
errors/warnings) on `RoslynSentinel.Server.Basic`, which `RoslynSentinel.Server.Advanced`
project-references (see [[project_advanced_extends_basic]]) — no separate fix needed for Advanced.

**Confirmed 2026-09-01 (later session):** after the fix commit (945ce12), `Git(status)` and
`Git(commit)` both returned instantly against this same repo — no timeout, first try. Used to
commit the CS0122 lookup-helper work (commit d4397b3). Blocker doc moved to
`docs/obsolete/blockers/blocking_error_git_status_timeout.md`.

**How to apply:** treat this as resolved. If `Git(status/diff/commit)` ever times out again in this
repo, that would be a genuinely new occurrence post-fix (not a continuation of this one) — check
`docs/obsolete/blockers/blocking_error_git_status_timeout.md` for prior history/theories already
ruled out (concurrent server processes, PATHEXT/shim wrapping) before re-investigating, and open a
fresh blocker doc rather than reviving the obsolete one.
