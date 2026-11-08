# `Git(operation: stage, hunkIds)` puts a pure insertion at the wrong line when an earlier hunk is not selected

**Status:** FIXED 2026-11-05. `BuildPatch` recomputes each selected hunk's new start; see Resolution.

## What was being attempted

Committing sequence pager work while another session held uncommitted `SearchSolutionText` edits in the same file:

```
Git(operation: "stage", paths: "RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs",
    hunkIds: "1,2,19,20,21,22,23", hunkFingerprint: "b687efd7e688")
```

Hunks 3-18 belonged to the other session and were left unselected. They add 23 lines net before hunk 19.

## The exact symptom

The call succeeded, with only this warning:

```
"remainingUnstagedHunks":21,
"warning":"Staged 7 hunk(s) but the remaining count is 21 (expected 16); verify with Git(reason: \"<why you call this>\", operation: diff, ref: \"staged\")."
```

`Git(operation: diff, ref: staged)` showed hunk 21 (`-1578,0 +1602,3`) inserted after `response = Wrap(page);` near old line 1601, not at old line 1578:

```
             page = SequencePager.BuildPage(full, sequence, offset, unitLimit, innerBudget, wire);
             response = Wrap(page);
+                // Server-side marker (never serialized): this page is already cut to the budget, so the marshaller must neither
+                // re-page it nor store it under a new resultId when one oversize unit leaves it over budget.
+                Paging = new PagingHints([], PagingOrder.Interleaved, AlreadyPaged: true),
         }
```

Hunks 22 and 23, also pure insertions, moved the same way. The staged file would not compile. Replacement hunks 2, 19 and 20 landed correctly, and so did insertion hunk 1, which has no unselected hunk before it.

## Source trace

- `GitHunkParser.BuildPatch` (`RoslynSentinel.Tools.Basic/GitHunkParser.cs:80-91`) concatenates the selected hunks verbatim, so each keeps the `+newStart` computed against the whole working-tree diff.
- `GitImpl.StageHunksAsync` (`RoslynSentinel.Tools.Basic/GitImpl.cs:2071-2074`) applies that patch with `git apply --cached --unidiff-zero`.
- A zero-context hunk with no old lines has nothing to match, so its position comes from the header. The misplaced hunks landed at positions consistent with their `+newStart`, which counts the unselected hunks' lines.

The tests in `RoslynSentinel.Tests.Tools.Basic/GitToolsSmokeTests.cs` that were listed (for example `Git_Stage_HunkIds_StagesOnlyThoseHunksAsync`) did not catch this; their fixtures have not been read to establish why. Hypothesis: they use replacement hunks or have no net line change before the selected hunk.

## What is and is not confirmed

Confirmed:

- The misplacement, from the staged diff above.
- Patch construction and the apply command, from source.

Not confirmed:

- git's internal positioning rule. It is inferred from where the hunks landed.
- Whether `lineRange` staging is affected. It selects hunks and appears to reach the same `BuildPatch`, so it probably is.

## Why this matters

Partial staging is the documented way to commit beside another session's edits in one file. This path silently produces index content that exists in no working tree and may not compile, and the only signal is a count warning. A commit straight after the stage would record a broken file while `Build` still passes, because `Build` reads the working tree.

## Suggested direction (not implemented)

1. `BuildPatch` recomputes each selected hunk's `+newStart` from its `-oldStart` plus the net line change of the selected hunks before it, keeping the rest of the hunk byte-exact.
2. Add real-git tests where a pure insertion follows an unselected hunk that changes the line count.
3. Consider turning the count mismatch from a warning into a refusal that restores the index, since it signals that the index no longer matches the request.

## Resolution

Root cause: `BuildPatch` kept each hunk's `+newStart` from the whole-file diff, which counts unselected hunks. Fixed in the commit that moved this file here (see `git log` on this path): `GitHunkParser.BuildPatch` now writes each selected hunk's new start from its old start plus the net line change of the selected hunks before it, through the new `WithNewStart` helper; hunk bodies stay byte-exact.

Tests: `GitHunkParserTests.BuildPatch_SelectedSubset_RewritesNewStartOnlyForPureHunks` and real-git `GitToolsSmokeTests.Git_Stage_HunkIds_LowerInsertionAfterUnselectedInsertion_LandsAfterItsNeighbourAsync`. The filtered run passed (89 of 89). The new real-git test was not run against the pre-fix code.

Item 3 (the count mismatch as a refusal) was not done.
