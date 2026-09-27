# Finding: SyncTypeAndFilename renames to the first-declared type, not the target type

**Status:** FIXED 2026-09-19 (CLOSED.md, "Phase 5 of plan-open-blockers-remediation-v1"). Doc kept
for repro/history; do not treat as an open defect.

## What was broken

`SyncTypeAndFilename` renamed `DocumentationTools.cs`/`GitTools.cs` to match the **first** type
declared in the file (`DocReadResult`, `GitStatusEntry`) instead of the target type actually named
by the caller's `docCommentId` (`SentinelDocumentationTools`, `SentinelGitTools` — both declared
last in their respective files). Confirmed via `^public class` grep order in both files: the tool
picked whichever type appeared first in source order, not the one the caller identified.

## How to reproduce (pre-fix)

Call `SyncTypeAndFilename` against a `.cs` file that declares more than one top-level type, where
the type matching the given `docCommentId` is not the first one declared in the file. Pre-fix, the
tool renamed the file to match the first-declared type's name instead of the target type's name.

## Fix (2026-09-19)

`StructuralRefinementEngine.SyncTypeAndFilenameAsync` (`RoslynSentinel.Basic`) gained an optional
`targetTypeName` parameter — when supplied, matches it against the file's top-level type
declarations explicitly instead of `.FirstOrDefault()` on declaration order; an unmatched name
returns an actionable error naming every top-level type actually present in the file. When omitted,
first-declared stays the default (documented as a real default for the common single-type-per-file
case, not an accident). Wired through all 4 call-site layers (`RefactoringStructuralImpl`,
`RefactoringStructuralTools`, the legacy `SentinelRefactoringTools` facade).

## Related, separately resolved: UndoLastApply couldn't reverse either bad rename

At the time this was found, `UndoLastApply` also failed to revert either bad rename (both change
IDs returned `NoReversibleItems`). Two distinct causes, both now fixed:

1. Root-caused separately (2026-09-10, commit `e3d32b8`) to `OperationBlobWriter` receiving slashed
   operation names from several `SentinelAdvancedRefactoringTools` call sites, unrelated to
   `SyncTypeAndFilename`.
2. Fixed as part of this same 2026-09-19 pass: `SyncTypeAndFilename` deleted the old file via a bare
   `FileIoHelper.DeleteAsync` call outside `ApplyProposedChangesAsync`'s tracked delete path, so the
   old file's content was structurally unrecordable. Fix routes the delete through the existing
   `deletePaths` mechanism instead, making the rename changeId genuinely revertible via
   `UndoLastApply`.

## How to apply

No longer a live caution — `targetTypeName` is available whenever addressing a specific type in a
multi-type file matters. `GetOperationDetail` remains the recommended way to distinguish
`NoReversibleItems` from `NoOperationBlobFound` for any tool.
