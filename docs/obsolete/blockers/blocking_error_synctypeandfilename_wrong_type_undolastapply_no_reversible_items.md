# Blocker — `SyncTypeAndFilename` picks the wrong type; `UndoLastApply` can't recover it

**Status:** OPEN — worked around with manual filesystem `mv`, waiting for fix/confirmation.

## Symptom 1 — `SyncTypeAndFilename` renames to the first-declared type, not the primary one

Called on a file containing multiple type declarations, expecting the file to be renamed to match
the file's *primary* tool class (the one the file is conceptually about). Instead it renamed to
match whichever type is declared **first** in the file — in both repro cases, a small helper
record/result type that happens to be declared before the main class.

## Repro

`RoslynSentinel.Server.Basic/DocumentationTools.cs` contains, in declaration order:
`DocReadResult`, `DocWriteResult`, `DocListResult`, `DocumentationTools` (renamed to
`SentinelDocumentationTools` earlier in the same session). Call:

```
SyncTypeAndFilename(docCommentId: "T:RoslynSentinel.Server.Basic.SentinelDocumentationTools", ...)
```

Result: file renamed to `DocReadResult.cs` — the first type in the file — not
`SentinelDocumentationTools.cs`.

Same shape on `RoslynSentinel.Server.Basic/GitTools.cs`: declaration order is `GitStatusEntry`,
`GitStatusResult`, `GitCommitEntry`, `GitLogResult`, `GitDiffResult`, `GitCommitResult`,
`GitRevertResult`, `SentinelGitTools` (last). Result: renamed to `GitStatusEntry.cs`, not
`SentinelGitTools.cs`.

Confirmed via grep of `^public class` order in both files — the intended target type is declared
**last**, not first, in both cases, and the tool consistently picked the first.

## Symptom 2 — `UndoLastApply` reports no reversible items for the rename changeId

Attempted to undo both bad renames via `UndoLastApply(changeId: ...)` using the changeIds
`SyncTypeAndFilename` itself returned (`382873af`, `5837afe0`). Both calls returned:

```
NoReversibleItems — "Ensure the apply completed successfully and a solution is loaded"
```

The apply *did* complete successfully (the file really was renamed on disk, confirmed by listing
the directory) and a solution *was* loaded (verified via `GetWorkspaceStatus` — same session, no
intervening `LoadSolution` call). The error message's suggested causes do not match the actual
state, which points to the changeId either not being honored/looked-up correctly, or file-rename
operations not producing a reversible content blob in this system at all (as opposed to text-edit
operations, which do revert cleanly via the same tool elsewhere in this session).

**This is not an isolated incident** — per prior observation across other agent sessions,
`UndoLastApply` reporting "no changes to undo" / not honoring the supplied `changeId` has recurred
with other tools/operations beyond just `SyncTypeAndFilename`, suggesting the gap is in
`UndoLastApply`'s changeId lookup or reversible-item recording in general, not specific to renames.

## Impact

Any call to `SyncTypeAndFilename` on a multi-type file is unsafe to trust without manually
verifying the resulting filename against the intended primary type — and when it does pick wrong,
there is no working undo path via MCP tooling; the only recovery is a manual filesystem `mv`
(and `git mv` doesn't apply either, since git hasn't caught up to the on-disk rename at that point —
see "What I did NOT do").

## What I did NOT do

Did not use `git mv` to fix the wrong filenames — the source path under the wrong name was not yet
tracked by git under that name (the rename happened via direct filesystem I/O from the MCP tool,
not a git-aware move), so `git mv` failed with "not under version control." Used plain filesystem
`mv` instead, which is the file-level equivalent of the narrow bypass exception (no MCP tool path
existed to fix an MCP tool's own mistake).

## Confirmed NOT the cause (of Symptom 2)

- Not a stale changeId typo — used the exact changeId string returned by the prior
  `SyncTypeAndFilename` call in each case.
- Not a missing/wrong solution load — `GetWorkspaceStatus` confirmed a solution was loaded
  throughout, no reload occurred between the bad rename and the undo attempt.
- Not a general `UndoLastApply` outage — the same tool successfully reverted content-edit changeIds
  elsewhere in this session; only the two rename changeIds failed.

## Next step

Waiting for confirmation/fix on both:
1. `SyncTypeAndFilename` should target the type the caller specified via `docCommentId`, not the
   first type declared in the file.
2. `UndoLastApply` should either honor rename changeIds (fully reversing the file move) or return a
   more specific error if rename operations are structurally not undoable, rather than the generic
   "ensure apply completed / solution loaded" message when both preconditions are actually true.

Once resolved, move this file to `docs/obsolete/blockers/`.
