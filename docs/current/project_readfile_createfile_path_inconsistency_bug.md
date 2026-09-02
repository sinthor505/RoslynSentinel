# ReadFile/CreateFile path inconsistency — fixed 2026-09-02

**Note:** this file didn't exist anywhere in the repo (working tree or git history, any branch)
before this session, despite the session's branch name (`readfile-createfile-path-bug-*`) implying
it should. Written now to document the bug that name pointed at, found by reading `ReadFile` and
`CreateFile` in `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` side by side.

## The bug

`CreateFile` (`SentinelWorkspaceTools.cs`, `CreateFile` method) writes a new file to disk through
`PersistentWorkspaceManager.ApplyProposedChangesAsync` for **any** path under the solution
root — any extension, whether or not the path falls inside a loaded project's directory.

`ReadFile` (same file, `ReadFile` method) only ever looked the path up as a Roslyn `Document` in
`solution.GetDocumentIdsWithFilePath(...)` / a scan over `solution.Projects...Documents`. If no
`Document` matched, it returned `FileNotFound` — even when the file plainly existed on disk
(the error message's own `existsOnDisk=...` field could read `true` while still failing).

The two tools disagree about what "exists" means because
`PersistentWorkspaceManager`'s post-write in-memory sync
(`ApplyInMemoryDocumentUpdatesAsync`/`ApplyProposedChangesAsync`) only ever turns a written path
into a tracked `Document` when **both**:
1. its extension is `.cs` (everything else is skipped outright — `if (ext != ".cs") continue;`), and
2. `SolutionProjectLocator.FindContainingProject` can resolve an owning project for its directory
   (otherwise it's skipped with a logged warning and never becomes a `Document`).

So `CreateFile` could successfully write, e.g., a `.txt`/`.json`/`.md` file, or a `.cs` file
outside every project's directory tree, and `ReadFile` would then report `FileNotFound` for that
exact path forever after — a write/read round trip that silently fails for anything that isn't a
project-owned `.cs` file. `CreateFile_NewPath_WritesContentAndReturnsSuccessAsync` in
`RoslynSentinel.Tests.Battery/CreateFileDeleteFileTests.cs` only asserted the file landed on disk
via raw `File.ReadAllTextAsync`, never that the MCP `ReadFile` tool could read it back — so this
had no test coverage before now.

This same asymmetry is a known, already-established pattern elsewhere in the codebase in the
*other* direction: `AugmentToolsTests.cs` has a regression guard
(`GenerateToStringSafe_WorkspaceFirstRead_NoDiskFileRequired`) specifically because an engine once
read only from disk and needed a workspace-first fallback added. `ReadFile` had the mirror-image
gap — workspace-only, no disk fallback.

## Fix

`ReadFile` (`SentinelWorkspaceTools.cs`) now falls back to a raw disk read via
`FileIoHelper.ReadAllTextIfExistsAsync` when no tracked `Document` matches the path, building a
`SourceText` from the disk content instead of a `Document`'s. Everything downstream (line-range
slicing, large-result offload, `totalLines`) is unchanged since it only depends on the resulting
`SourceText`. `FileNotFound` is now returned only when the path has no `Document` **and** doesn't
exist on disk either — matching what `CreateFile` is actually able to produce.

## Tests added

- `RoslynSentinel.Tests.Battery/CreateFileDeleteFileTests.cs`:
  `CreateFile_NonCsFile_ThenReadFile_ReturnsContentAsync` — end-to-end round trip against a real
  `PersistentWorkspaceManager`/`TestSolutionFixture`: `CreateFile` a `.txt` file, then `ReadFile`
  it back and assert the content matches.
- `RoslynSentinel.Tests.Battery/ReadFileTests.cs`:
  `ReadFile_FileOnDiskButNotTrackedAsDocument_FallsBackToDiskReadAsync` — unit-level check against
  the fake workspace manager: a file written straight to disk (never added as a `Document` to the
  test solution) is still readable through `ReadFile`.
- Existing `ReadFile_FileNotInSolution_ReturnsFileNotFoundAsync` still passes unchanged — it covers
  a path that doesn't exist on disk at all, which must still fail.

## How to apply

No dotnet SDK was available in the session that made this fix (headless container, `dotnet` not on
`PATH`) — the change was verified by manual code review only, not by running `dotnet build`/`dotnet
test`. Before trusting this as done, run
`dotnet test RoslynSentinel.Tests.Battery --filter "FullyQualifiedName~ReadFileTests|FullyQualifiedName~CreateFileDeleteFileTests"`
(or the full battery) on a machine with the SDK installed, and fix forward if anything doesn't
compile or pass.

If a similar "one tool's write path is more permissive than another tool's matching read path"
gap turns up elsewhere, check first whether it's rooted in the same place as this one — the
`.cs`-only, project-owned-only filter in `PersistentWorkspaceManager`'s in-memory Document sync
(see `docs/current/reference-code-file-write-paths-v1.md` for the write chokepoint this interacts
with) — rather than assuming it's a new, unrelated defect.
