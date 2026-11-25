# `SplitProjectByFolder` is a stub: it creates an empty project and reports "Moved N files" without moving any

**Status:** OPEN 2026-11-24, false success removed: since bf27d721 the tool resolves the source project, then throws `ToolNotImplementedException` (`NotImplemented`, names CreateProject + MoveFile) before creating anything; the move itself is still unimplemented (code TODO in `SplitProjectByFolderAsync`). Root cause traced to source (`SolutionStructureEngine.SplitProjectByFolderAsync` has no move logic and already carries a TODO saying so); the numbers below are from the Step 12 spike run by a session in a throwaway worktree and were not re-measured while writing this doc.

## What was being attempted
Step 12 (spike) of [plan_isolate_git_area_into_own_project](../plans/plan_isolate_git_area_into_own_project.md):
turn the folder `RoslynSentinel.Common/Git/` into a new project `RoslynSentinel.Git` with ONE call, in a throwaway worktree:

```
SplitProjectByFolder(sourceProjectName: RoslynSentinel.Common, folderName: Git, targetProjectName: RoslynSentinel.Git)
```

Toolset `projectAdmin` (enabled via `McpToolsetControl`). The plan's Step 14 (production side) and Step 18 (test side,
`RoslynSentinel.Tests.Git`) are written around this one call. The tool description promises: "Moves all files under a folder
from one project to a new target project, preserving structure." (`RoslynSentinel.Tools.Basic/WorkspaceTools.cs:297-306`).

## The exact symptom
The call returned success with this message shape (count measured by the spike: 32; the tool's own template is quoted from source):

```
Moved 32 files from RoslynSentinel.Common/Git to RoslynSentinel.Git. References will need manual updating.
```

Source template: `$"Moved {filesToMove.Count} files from {sourceProjectName}/{folderName} to {targetProjectName}. References will need manual updating."`

Measured on disk afterwards (spike worktree):

- Files moved: 0. `Common/Git/` still held 60 files (including the `GitWorktree/` subfolder); the "32" is the count of documents
  in the committed snapshot whose `Folders` contain `Git` (the subfolder `Git/GitWorktree/` is not counted the same way).
- Created `RoslynSentinel.Git/RoslynSentinel.Git.csproj`: `net10.0`, `ImplicitUsings`, `Nullable` only. No `ProjectReference` to
  `RoslynSentinel.Common`, no `LibGit2Sharp` `PackageReference`, no `InternalsVisibleTo` handling.
- Created `RoslynSentinel.Git/Class1.cs` (the `dotnet new classlib` template file).
- Added a `.slnx` entry for the project, including an odd line `<BuildType Solution="DebugVisualStudio|*" Project="Debug" />`.
- Did not touch: `ProjectReference` items, `InternalsVisibleTo` lines, the `LibGit2Sharp` `PackageReference`
  (`RoslynSentinel.Common.csproj:11`), or the `GitWorktree/` subfolder.

## Source trace
- Tool class: `RoslynSentinel.Tools.Basic/WorkspaceTools.cs:297` (`[McpServerTool(Name = "SplitProjectByFolder")]`, `SplitProjectByFolderTool`),
  delegating to `WorkspaceProjectManagementImpl.SplitProjectByFolder` (`RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs:917`),
  which calls `_solutionStructureEngine.SplitProjectByFolderAsync` (`:923`).
- Engine: `RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs:1854` (`SplitProjectByFolderAsync`, declared in class
  `SolutionStructureEngine`, file name `ProjectStructureEngine.cs`). The whole body is:
  1. resolve the source project from the committed solution (`:1857-1858`; throws `ProjectNotFound` first so a bad name creates nothing);
  2. `await CreateProjectAsync(targetProjectName, "classlib", cancellationToken)` (`:1861`);
  3. `filesToMove = sourceProject.Documents.Where(d => d.Folders.Contains(folderName)).ToList()` (`:1864`);
  4. return `EditOutcome.Modified` with the "Moved N files" message (`:1868-1873`). Nothing moves, copies, deletes or rewrites a file or csproj.
- The existing marker at `:1867`: `// TODO SplitProjectByFolderAsync is a simulation: it creates the project and reports "Moved N files" without moving any file; throw ToolNotImplementedException or implement the move (plan_document_edit_result_non_nullable_updated_text risks)`.
- `CreateProjectAsync` (same file, `:1766-1849`) shells out to `dotnet new <projectType> -n <name> -o <dir>` then `dotnet sln <path> add <csproj>`.
  This explains the minimal csproj and `Class1.cs` (the classlib template defaults), so the csproj content is the template's, not a decision the split tool made.
- The `<BuildType ... />` slnx line: no occurrence of `BuildType` as slnx text exists in the C# source (`Search` text for `DebugVisualStudio` and `BuildType` finds only unrelated `BuildTypeSignature`),
  so it most likely comes from `dotnet sln add` for a `.slnx`. Hypothesis, not traced into the dotnet CLI.
- Not traced: why the spike counted 32 documents against 60 files on disk (hypothesis: `Document.Folders` for files in `Git/GitWorktree/` is `["Git","GitWorktree"]` and does contain `Git`, so
  the gap may be non-`.cs` or excluded files, or a stale committed snapshot in the spike worktree). It does not change the verdict: the count is of a snapshot, not of moves.

## What is and is not confirmed
Confirmed from source: the engine moves nothing and reports success; it never touches a ProjectReference, IVT line, PackageReference or subfolder; the target project is created via `dotnet new classlib`.
Confirmed by the spike run (not re-measured here): 0 files moved, 60 files left in `Common/Git/`, message "Moved 32 files", the new csproj and `Class1.cs`, the slnx entry with the BuildType line.
Not traced: the 32-versus-60 difference and the origin of the BuildType line (both above).
Not tested: whether `Tests.Git` (Step 18) would behave differently; it is the same code path, so no.

## Why this matters
The tool lies: a caller (or a weak model under test) reads "Moved 32 files" and a success outcome, and proceeds with an empty project plus a stray `Class1.cs`.
The description ("Moves all files ... preserving structure") and the result text both say more than the code does; the only hint is the trailing "References will need manual updating", which does not say that no file moved.
Effect on the plan: Steps 14 and 18 cannot run as written, because they depend on this one call moving the folder.

## Suggested direction (not implemented)
Either of:
1. Implement the move: move each document under the folder (keeping the folder structure, including nested subfolders and non-compiled files), add a `ProjectReference` from the new project to the source project, validate the
   result as one compile (a project edge is crossed, see [blocking_error_movefile_has_no_multi_file_cross_project_move](blocking_error_movefile_has_no_multi_file_cross_project_move.md)), and report what it did not do
   (`InternalsVisibleTo` lines, package references the moved files need such as `LibGit2Sharp`, the reverse reference from the source project when it still uses the moved types).
2. Until then, throw `ToolNotImplementedException` (the TODO's own suggestion) so the tool cannot report a success it did not perform, and make the description say it is not available.

Whichever is chosen, the target project must not be created before the move is known to be possible (today the project is created first, leaving debris on any later failure).

## Staged route that stays possible
The owner is away; no bypass (hand-edited csproj plus per-file `MoveFile`) is pre-authorized (plan decision D-e), so Step 14 waits.
Step 13 (the intra-`Common` down-move into `RoslynSentinel.Common/Git/`, a 31-call single-file `MoveFile` sequence that the spike showed works once `Common.csproj` has `<Using Include="RoslynSentinel.Common" />`) is unaffected and proceeds.
The dispatcher continues the Steps 15-18 prerequisites that do not depend on the new project.
Related blocker: [blocking_error_movefile_has_no_multi_file_cross_project_move](blocking_error_movefile_has_no_multi_file_cross_project_move.md). Plan: [plan_isolate_git_area_into_own_project](../plans/plan_isolate_git_area_into_own_project.md).
