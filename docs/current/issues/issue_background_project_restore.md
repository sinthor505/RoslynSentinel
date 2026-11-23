# Can project restore run in the background after LoadSolution returns? - OPEN

**Status:** OPEN 2026-11-23. Stub from todo review 2026-11-23; design in a fresh chat after the synchronous auto-restore lands.

## Summary

Todo review 2026-11-23 decided that `LoadSolution` restores unrestored projects in-server, synchronously, before loading (TODO.md
entry "LoadSolution restores unrestored projects automatically"). The owner asked whether that restore could instead run in the
background so `LoadSolution` returns quickly.

## Why it matters

A synchronous restore lengthens the first load of a fresh clone or worktree (eval and PlanStepRunner checkouts) and of any load after a
`.csproj` change; `SolutionRestore.RestoreAsync` runs with a 5-minute caller timeout (`RestoreBeforeLoadTimeout`,
`WorkspaceProjectManagementImpl.cs:~709`).

## What is known

- Today only `LoadSolution(forceReload: true)` restores first (71c0c512), in the tool path before `LoadSolutionAsync`
  (`WorkspaceProjectManagementImpl.cs:800-801`), outside the load timeout.
- Until a background restore finishes, the unrestored projects load without package references: Search, diagnostics and the compile
  gate return wrong results for them (CS0246 and missing symbols).
- After the restore those projects must be reloaded, which bumps `workspaceVersion` and can collide with edits made in between
  (stale-read ledger, FoldPending).
- A precedent exists: tool calls wait up to 30 s while the solution is `Loading` (`McpServerStatus` description).
- Recent test LoadSolution timeouts were MSBuild load contention, not restore (24b54b47).

## Open questions

1. Per-project wait: calls that touch a restoring project wait (like the Loading wait) while others run - how is "touches" decided?
2. Reload after restore versus in-flight or staged edits: reload only the restored projects, and what happens to a pending write?
3. Status: an `McpServerStatus` field for restore in progress and its result.
4. Is the gain worth it - measure the synchronous restore cost on a fresh worktree of this repo first.

## What would resolve it

A measured synchronous restore cost, answers to 1-3, and either a plan doc or a decision to keep the synchronous restore.
