# Finding: SyncTypeAndFilename renames to the first-declared type, not the target type

**Status:** confirmed tool defect, not yet fixed.

## What's broken

`SyncTypeAndFilename` renamed `DocumentationTools.cs`/`GitTools.cs` to match the **first** type
declared in the file (`DocReadResult`, `GitStatusEntry`) instead of the target type actually named
by the caller's `docCommentId` (`SentinelDocumentationTools`, `SentinelGitTools` — both declared
last in their respective files). Confirmed via `^public class` grep order in both files: the tool
picks whichever type appears first in source order, not the one the caller identified.

## How to reproduce

Call `SyncTypeAndFilename` against a `.cs` file that declares more than one top-level type, where
the type matching the given `docCommentId` is not the first one declared in the file. The tool
renames the file to match the first-declared type's name instead of the target type's name.

## Impact

No MCP-tool path exists to fix the mistake once it happens — the tool creates the bad state via
direct filesystem I/O (not a git-aware move), so recovering requires a manual `mv` outside the tool
surface (`git mv` won't work either, since git isn't yet tracking the wrong name at that point).

Don't trust `SyncTypeAndFilename`'s output filename on any multi-type file without independently
verifying it targeted the right type.

## Related, separately resolved: UndoLastApply couldn't reverse either bad rename

At the time this was found, `UndoLastApply` also failed to revert either bad rename (both change
IDs returned `NoReversibleItems`), which looked like it might be the same bug surfacing twice. It
wasn't — root-caused separately (2026-09-10, commit `e3d32b8`) to `OperationBlobWriter` receiving
slashed operation names (e.g. `"WrapRange/region"`) from several `SentinelAdvancedRefactoringTools`
call sites, which broke the blob write for those specific tools and silently withheld a resolvable
change ID. That fix is unrelated to `SyncTypeAndFilename`'s type-selection bug and does not resolve
it — a rename that succeeds but targets the wrong type still produces a blob with no pre-image to
undo (a `NoReversibleItems` case, not `NoOperationBlobFound`); `GetOperationDetail` is the
recommended way to tell the two conditions apart.

## How to apply

Don't rely on `UndoLastApply` as a safety net for `SyncTypeAndFilename` specifically — verify the
target type manually before and after any call against a multi-type file.
