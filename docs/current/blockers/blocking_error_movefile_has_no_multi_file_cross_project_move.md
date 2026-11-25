# `MoveFile` has no multi-file cross-project move: a mutually dependent cluster cannot cross a project edge

**Status:** OPEN 2026-11-24. Root cause traced to source (single-path parameters plus one per-call compile gate in `MoveFileTool`); the error counts below are from the Step 12 spike in a throwaway worktree and were not re-measured while writing this doc.

## What was being attempted
Step 12 (spike) of [plan_isolate_git_area_into_own_project](../plans/plan_isolate_git_area_into_own_project.md): after
[the `SplitProjectByFolder` stub](blocking_error_splitprojectbyfolder_is_a_stub.md) moved nothing, the spike tried the fallback of moving the files of
`RoslynSentinel.Common/Git/` one at a time into the new project `RoslynSentinel.Git` with `MoveFile(sourcePath, destinationPath, namespaceHandling: keep)`.
It also tried a comma-separated list of paths in `sourcePath`.

## The exact symptom
A comma list in `sourcePath` returns `NotFound` (the whole string is treated as one path). The source's message shape for that
(`WholeFileWriteTools.cs:342`) is:

```
MoveFile: '<resolved absolute path of the whole comma string>' does not exist on disk (resolved from '<sourcePath>'; path comparison ignores case).
```

Single-file calls, top-down from `Common/Git` into the new `RoslynSentinel.Git` project (measured error counts of the compile gate):

| Order of attempt | File | Result |
| --- | --- | --- |
| worked | `GitImpl.cs`, `IGitOperations.cs`, `GitHunkParser.cs` | moved |
| refused | `GitResult.cs` (before its derived records) | 1391 errors |
| refused | `GitProcess.cs` (before the readers that use it) | 45 errors |
| refused | `Libgit2GitReader.Hooks.cs` alone (one of 8 partial-class files `Libgit2GitReader*.cs`) | 178 errors: CS0103, CS0117, CS0433 |

The refusal wraps the compile gate text (`WholeFileWriteTools.cs:398-404`):

```
MoveFile: moving '<source>' (project '<source project>') to '<destination>' (project '<destination project>') would break the build; nothing was moved. <compile gate text>
```

(The exact per-error wording was not captured in the brief; only counts and CS codes are recorded above.)

Within ONE project the same files do move with the single-file tool: a 31-call sequence works (order: `GitErrorCodes`, `GitResult`, the 7 `*Entry` files,
the `*Result` files with `GitStatusResult` before `GitStageHunksResult` and `GitStashResult`, `IGitOperations`, `GitHunkParser`, `GitImpl`), once
`RoslynSentinel.Common.csproj` has `<Using Include="RoslynSentinel.Common" />`.

## Source trace
- `RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs:290-297`: `MoveFileTool` takes one `sourcePath` and one `destinationPath` (string each, no list overload). The description says "Moves or renames a text file".
- `:334-342`: `File.Exists(source.Absolute)` and a directory check run on the one resolved path; a comma list resolves to a nonexistent path and hits the `NotFound` branch at `:342`.
- `:382-396`: the move is a `DocumentChangeSet { [destination] = text }` plus `deletePaths = [source]`, applied through `ValidateAndApplyHelper.ValidateAndApplyAsync(... "move_file" ...)`, i.e. one file, validated against a compile of the
  in-memory solution after that single change; the destination may be in another project (the description says so), but nothing else moves in the same validation.
- Consequence by construction (hypothesis consistent with the measured counts, the compile itself was not traced): a mutually dependent set of files (base type and its derived records, a type and its users, the 8 files of a partial class) has no valid
  intermediate state when it crosses a project edge. After the first file moves, the other files in the old project cannot see it unless the old project references the new one, and the new project cannot see the files left behind unless it references the old one;
  the new project's missing reference to `Common` (see the `SplitProjectByFolder` doc) and Common's missing IVT/reference back make every order fail, while a partial class cannot be split across two assemblies at all (CS0433 type-in-both-assemblies, CS0103/CS0117 missing members).
- Inside one project the intermediate states are valid in the right order, which is why the 31-call route works there.
- Not traced: the individual error lists for the 1391 / 45 / 178 refusals (counts only were recorded by the spike).

## What is and is not confirmed
Confirmed from source: single-path parameters; one compile validation per call; comma list -> `NotFound` branch.
Confirmed by the spike run (not re-measured here): the three successes, the three refusals with their counts and CS codes, and the 31-call intra-project sequence.
Not confirmed: that a multi-file atomic validation would pass for the Git cluster (the new project would still need its `ProjectReference` to `Common`, `LibGit2Sharp` package and IVT; see the other blocker).

## Why this matters
There is no one-call or atomic route for moving a dependent cluster into a new project, so a weak model (or a person) following the tool descriptions is pushed toward hand-editing csproj files and
trial-and-error ordering of dozens of single-file calls, each failing with a four-digit error count that does not say "this file belongs to a cluster that must move together".
The tool description says the destination may be in another project but does not say that only an independently compilable single file can do so. Combined with the stub `SplitProjectByFolder`, the surface has no working cross-project move for connected code.

## Suggested direction (not implemented)
1. A multi-file `MoveFile` (a list of source/destination pairs, or a source folder) that applies all moves and runs ONE compile validation, so the cluster crosses the edge atomically (migration-first: new list parameter beside the old, per the CLAUDE.md tool-gap rule).
2. Or a working `SplitProjectByFolder` ([blocker](blocking_error_splitprojectbyfolder_is_a_stub.md)) that does the same plus the project reference and a fix-up report (IVT lines, package references).
3. Cheaper partial step: when the compile gate refuses a `MoveFile` that crosses a project edge, add a fact to the error naming the source files that reference the moved file (and, for a partial class, its other files), so the cluster is visible without counting errors.

## Staged route that stays possible
The owner is away; no bypass (hand-edited csproj plus per-file `MoveFile`) is pre-authorized (plan decision D-e), so Step 14 waits.
Step 13 (the intra-`Common` down-move into `RoslynSentinel.Common/Git/`, the 31-call sequence above) proceeds, because it does not cross a project edge.
The dispatcher continues the Steps 15-18 prerequisites that do not depend on the new project.
Related blocker: [blocking_error_splitprojectbyfolder_is_a_stub](blocking_error_splitprojectbyfolder_is_a_stub.md). Plan: [plan_isolate_git_area_into_own_project](../plans/plan_isolate_git_area_into_own_project.md).
