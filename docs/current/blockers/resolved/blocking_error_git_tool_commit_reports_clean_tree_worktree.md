# Blocking error: `Git` MCP tool's `commit`/`status`/`diff` operations silently misreport a loaded worktree's true state

## Resolution (2026-09-14, Phase 0 of manual-selfrun-20260914-remediation-v1)

Root cause confirmed at `RoslynSentinel.Server.Basic/SentinelGitTools.cs`, method `TryGetGitRoot`
(lines 172-209 pre-fix): the priority order tried `AppContext.BaseDirectory` and
`Directory.GetCurrentDirectory()` — both fixed at server process launch, inside whichever repo the
server binary was built/started from — **before** `_workspaceManager.GetSolutionRoot()` (the
loaded solution's directory, the only candidate that reflects a `LoadSolution` call into a
worktree). Since the server's base directory is almost always inside a git repo (it's built inside
this repo), step 1 succeeded first every time, so the worktree-aware fallback never ran.

Fix: reordered `TryGetGitRoot` to try the loaded solution root first, falling back to base
directory then working directory only when no solution is loaded. Single chokepoint — confirmed via
`FindReferences` that only `Git()`'s dispatch method calls `TryGetGitRoot`, so this fixes
status/diff/commit/stage/etc. uniformly. Verified live: `Git(status)` output now matches shell
`git status --short` exactly in the loaded-solution's repo. A live worktree wasn't available this
run to re-run the original 6x repro directly; re-verify against a real worktree next time one is
created via PlanStepRunner.

## Symptom

Inside the step-08 PlanStepRunner worktree
(`C:/RoslynSentinel-TestRuns/manual-selfrun-20260914-004605/08-phase3-finding-type/Worktree`),
with 5 real, `git add`-staged changes present (confirmed independently via shell
`git status --short` and the verbose output of `git add` itself), the `Git` MCP tool's three
read/write operations all report the wrong state instead of erroring:

- `Git(operation: "status")` → `{"branch":"master","isClean":true,"staged":[],"unstaged":[],"untracked":[]}`
- `Git(operation: "diff", target: "working")` → empty diff, 0 files changed (per earlier
  reproduction this run, see findings-log.md step 07 section)
- `Git(operation: "commit", message: "...")` → fails with
  `"git commit failed: On branch master\r\n...\r\nnothing to commit, working tree clean"`

Ground truth via shell in the same directory:

```
$ git status --short
 M RoslynSentinel.Common/DataTag.cs
 M RoslynSentinel.Common/EngineResultWrapper.cs
 M RoslynSentinel.Common/ToolResult.cs
 M RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs
?? RoslynSentinel.Common/Finding.cs
?? bin-runner/

$ git add RoslynSentinel.Common/DataTag.cs RoslynSentinel.Common/EngineResultWrapper.cs \
    RoslynSentinel.Common/ToolResult.cs RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs \
    RoslynSentinel.Common/Finding.cs
$ git status --short
M  RoslynSentinel.Common/DataTag.cs
M  RoslynSentinel.Common/EngineResultWrapper.cs
A  RoslynSentinel.Common/Finding.cs
M  RoslynSentinel.Common/ToolResult.cs
M  RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs
?? bin-runner/
```

5 files genuinely staged. The `Git` MCP tool's `commit` call, issued immediately after, still
reports `nothing to commit, working tree clean`.

## Why this is the worse failure mode (per CLAUDE.md's failure doctrine)

This is not a crash or a loud error — it is a **plausible, well-formed, wrong** answer. A model
acting on `Git(operation: "status")`'s `isClean: true` would conclude there is nothing to commit
and either stop (silently losing the work) or become confused when a later `commit` call also
"succeeds" at doing nothing. There is no error message pointing at a wrong path, wrong repo root,
or stale cache — the tool actively asserts a false negative with full confidence.

## Hypothesis (not fully traced to source this run)

Suspected root cause, consistent with all three operations failing the same way: `Git`'s
status/diff/commit operations likely resolve the git repository root from a fixed or
default-workspace location (e.g. the primary loaded solution's root at server startup) rather
than from the currently active worktree's actual path — unlike the rest of the MCP tool surface,
which correctly operates against whatever solution/worktree was most recently `LoadSolution`'d.
This would explain why the tool consistently believes it's looking at a clean `master` when the
real target is a distinct worktree directory with real uncommitted changes.

Not yet traced: the actual git-root-resolution code path inside the `Git` tool's implementation.

## Impact this run

Blocked committing step 08's changes (`RoslynSentinel.Common/Finding.cs` [new],
`RoslynSentinel.Common/EngineResultWrapper.cs`, `RoslynSentinel.Common/ToolResult.cs`,
`RoslynSentinel.Common/DataTag.cs`, `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs`) via
the intended MCP path. The session's standing authorization ("git bypass is authorized for this
session when mcp git is defective or does not have the required functionality") covers *choosing*
to fall back to shell git, but the repo's `.claude/hooks/enforce-dogfood.ps1` `PreToolUse` hook
mechanically blocks `git commit` via shell regardless of in-conversation authorization — it has no
way to see or honor that context, which is correct hook design (a hook enforced only by
remembering decays), but means the block cannot be lifted from inside this conversation.

## Requested next step

Stopping here per CLAUDE.md's blocking-error policy: "write the blocker doc, do not resume until
told the issue is fixed." Step 08's 5 files are correctly staged in the worktree (verified via
shell `git status --short`) but not yet committed. Needs either:
1. A fix to `Git`'s git-root resolution so `status`/`diff`/`commit` operate against the actually
   loaded/active worktree, not a fixed default, or
2. Explicit permission to use the shell for this specific `git commit` (acknowledging the hook
   will need a one-off override, or the hook's block reworded/permitted for this case) so this
   self-run can proceed.

## Related findings (same run, same tool, earlier reproductions)

See `C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md`, step 07 section
("Confirmed (again, more precisely): `Git` MCP tool's `status`/`diff` silently report the wrong
repo for a worktree") for the first two reproductions of the `status`/`diff` half of this defect.
This document adds the third reproduction plus the new `commit` failure mode, which is more severe
because it blocks forward progress entirely rather than just misinforming.
