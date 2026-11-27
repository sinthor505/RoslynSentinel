# Plan: Rename the namespaces in RoslynSentinel.Git to RoslynSentinel.Git and RoslynSentinel.Git.Worktrees

**Status:** IMPLEMENTED 2026-11-27. Step 2 c2b3acb9 (`ChangeNamespace` 84b068a4: 144 files, 1,866 references), Step 3 fdd7a862 (`MoveFile` 486e44a6, `ChangeNamespace` 00ef606d: 8 files, 19 references), Step 4 cc147f9e. Full run `cc147f9e-20261127-004211-40584` at cc147f9e: 12,633 tests (equal to the last full run on record), 0 failures, 48 skipped, 1,411 s.

## Problem

The Git split ([plan_isolate_git_area_into_own_project.md](plan_isolate_git_area_into_own_project.md), IMPLEMENTED 2026-11-24) moved 60 files into
`RoslynSentinel.Git/` but left their namespaces, because no namespace-change tool existed (its Step 19, namespace half). Counted 2026-11-26: 29 files
declare `RoslynSentinel.Common`, 28 declare `RoslynSentinel.Tools.Basic` (`GitEngine`, `IGitOperations`, `GitHunkParser`, `GitErrorCodes`, the
`Git*Result`/`*Entry` DTOs) and 3 declare `RoslynSentinel.Common.GitWorktree` (`RoslynSentinel.Git/GitWorktree/GitWorktreeManager.cs`,
`IStepBranchStrategy.cs`, `IWorktreeStep.cs`). Markers: `// TODO rename namespace to RoslynSentinel.Git` at `RoslynSentinel.Git/GitEngine.cs:13` and
`RoslynSentinel.Git/GitProcess.cs:7` (verified 2026-11-27), `docs/current/INBOX.md:13`.

`ChangeNamespace` (toolset `moveExtract`) shipped 2026-11-25. Probe 2026-11-26:
`ChangeNamespace(filePath: "RoslynSentinel.Git", newNamespace: "RoslynSentinel.Git", dryRun: true)` returned `dry_run_ok`, `validated: true`,
"144 file(s) rewritten, 1866 reference(s) updated". The dry run goes stale as code changes, so this runs first among the roadmap's items.

## Decision

Settled 2026-11-26 in the design session recorded in [plan_isolate_build_test_area_into_own_project.md](../plan_isolate_build_test_area_into_own_project.md)
(its Q2, Q4, Q17, Q18, Q30); on 2026-11-27 the owner split the rename into this plan and asked for it to be dispatched.

- **Targets (owner):** every file under `RoslynSentinel.Git/` gets namespace `RoslynSentinel.Git`; the worktree harness gets
  `RoslynSentinel.Git.Worktrees` and its folder is renamed `GitWorktree/` -> `Worktrees/`.
- **Why `Worktrees`, plural (owner):** a folder named `Worktree` matches the harness-clone exemption `[\\/]Worktree[\\/]` in
  `.claude/hooks/enforce-dogfood.ps1` (lines 632, 999, 1035, 1089, 1417, 1451) and CLAUDE.md's "exclude `*/Worktree/` folders" rule.
- **No shadowing (investigated):** no type or member named `Git` exists in production code; the five `Git` symbols in `RoslynSentinel.Tests.Git` are
  private methods (`GitCommitHashRaceTests.cs:81` and four others) and tests use no `Git.X` access.
- **No path or string pins (verified 2026-11-27):** `Search(mode: text)` over C# including tests finds no `GitWorktree/` path and no string literal
  naming `Common.GitWorktree` or `RoslynSentinel.Tools.Basic.Git`.
- **Usings (planner default):** accept what `ChangeNamespace` writes (a `using RoslynSentinel.Git;` per consumer file). No global
  `<Using Include="RoslynSentinel.Git" />` is added to any csproj.
- **Owner is present:** a step that hits a decision point stops and reports the question to the dispatching session; it does not pick a route itself.

## Execution rules

- C# changes only through the RoslynSentinel MCP tools; `.md` files with Edit. One `Build` or `RunTest` at a time, subagents included.
- `ChangeNamespace` needs `McpToolsetControl(toolSet: moveExtract, enabled: true)`. `MoveFile` is a core tool.
- Each `ChangeNamespace` or folder `MoveFile` call is ONE semantic operation whose file count is over the sweep tier
  (`.claude/implementer-tiers.json`: sweep tier 0 = 1 operation, 8 files, 25 call sites, 2 projects) by design: lane `implementer-senior`, and the
  brief states that the call is one operation. No manual edits, text replace or shell loop stands in for a refused call: report `TOOL-GAP:` and stop.
- Dry run before every real call; record the `UndoChange` id of every real call in the step report.
- Other sessions share this tree. Before each real call load the `peer-coordination` skill, take `Git(operation: status)` and compare the dry run's
  `affectedFiles` with the dirty `.cs` files. Known on 2026-11-27: `RoslynSentinel.Tests.ModelEval/TranscriptionScenarioTests.cs` is modified by
  another session (peer `roslynsentinel-15`). A file that is both dirty and affected is resolved with its owner (`OWNS`, then `COMMIT <files>`)
  before the call; never overwrite or commit another session's edits.
- Commit each slice at a compile-green point with `Git` (scope `listed`, the files the tool reported plus the step's own), trailers
  `Plan: plan_rename_git_project_namespaces` and `Co-Authored-By`; the last commit carries `Closes: plan_rename_git_project_namespaces`.
- The MCP server is not restarted by this run. The final report says that a restart of the dispatching session's server is pending.

## Steps

### Step 1 - Preflight and parent dry run
- Files: none written.
- Depends on: none
- Effect: text-neutral (no write).
- Lane: the dispatcher itself.
- Change: enable `moveExtract`; `Git(operation: status)`; `ListAgents`; run
  `ChangeNamespace(filePath: "RoslynSentinel.Git", newNamespace: "RoslynSentinel.Git", dryRun: true)` and page the whole `affectedFiles` list with
  `GetLargeResult`. From the per-file `lineChanges`, list the files whose change is more than a using added or swapped or a namespace line changed
  (known behaviour to watch for: where a global using of the old namespace exists the tool may qualify a name instead of adding a using). Resolve
  dirty-and-affected files with their owners (Execution rules).
- Done when: the dry run reports `validated: true`; no affected file has another session's uncommitted edits; the list is in the report. Decision
  point: a refusal (for example the doc-comment cref case) stops the plan with the quoted refusal for the owner.
- Done 2026-11-27: `dry_run_ok`, `validated: true`, 144 files, 1,866 references (same as 2026-11-26); no dirty `.cs` file. `ChangeNamespace` has no
  `returnDiff` parameter although its dry-run note names one (INBOX 2026-11-27), so the diff is checked after the real call instead (Step 2). 14
  lines hold a qualified name, all of them existing `RoslynSentinel.Common.GitReaders...` references rewritten in place
  (`ResidualMentionScannerTests.cs:234,242-244`, `DocTrailerEngineTests.cs:142,151-153`, `NotFoundEvidenceTests.cs:313,321-323`, line 9 of both
  `LeakDetectorSetUp.cs`): intended, not tool-introduced.
- Effort: S

### Step 2 - Rename the parent folder's namespaces
- Files: the dry run's `affectedFiles` (about 144, all projects); `RoslynSentinel.Git/GitEngine.cs`, `RoslynSentinel.Git/GitProcess.cs` (marker comments).
- Depends on: Step 1
- Effect: text-neutral (namespaces and usings only; no wire text, DTO property or tool schema changes).
- Lane: `implementer-senior`, one operation.
- Change: `ChangeNamespace(filePath: "RoslynSentinel.Git", newNamespace: "RoslynSentinel.Git")`. Before committing, check the written result with
  `Git(operation: diff)`: the patch of `CliGitReader.cs`, `GitReader.cs`, `FallbackGitReader.cs`, `GitBackendWarmup.cs` and `Libgit2GitReader.cs`
  (the dry run showed 2 or 3 added lines each) and a `Search(mode: text, query: "RoslynSentinel\\.Git\\.[A-Z]", includeTests: true)`; qualified names
  beyond the 14 lines listed in Step 1 mean the tool introduced them: `UndoChange`, stop and report. After it, the three worktree files are in
  `RoslynSentinel.Git` too (a folder call covers subfolders); Step 3 gives them their sub-namespace. Then delete the two
  `// TODO rename namespace to RoslynSentinel.Git ...` comment lines (`GitEngine.cs:13`, `GitProcess.cs:7`; one `ReplaceSnippet` each).
- Done when: `Build` 0 errors; `Search(mode: text, query: "^namespace RoslynSentinel\\.(Common|Tools\\.Basic)", fileGlob: "RoslynSentinel.Git/**/*.cs")`
  returns nothing; `Search(mode: text, query: "TODO rename namespace")` returns nothing; the slice is committed.
- Effort: M

### Step 3 - Rename the worktree harness folder and give it its sub-namespace
- Files: `RoslynSentinel.Git/GitWorktree/GitWorktreeManager.cs`, `IStepBranchStrategy.cs`, `IWorktreeStep.cs` (moved to `RoslynSentinel.Git/Worktrees/`);
  their users: `RoslynSentinel.Tools.Advanced` (`SubAgentChildServerLauncher.cs`, `SubAgentEvalImpl.cs`, `SubAgentRunNaming.cs`),
  `RoslynSentinel.Utilities.PlanStepRunner` (`Program.cs`, `PlanStepFile.cs`, `RunnerOptions.cs`), `RoslynSentinel.Tests.Harness/WorktreeTeardownTests.cs`
  (the user list is the Git plan's; the tool call finds the real set).
- Depends on: Step 2
- Effect: text-neutral.
- Lane: `implementer-senior`, two operations in one slice.
- Change: (a) `MoveFile(moves: [{ sourcePath: "RoslynSentinel.Git/GitWorktree", destinationPath: "RoslynSentinel.Git/Worktrees" }], namespaceHandling: keep, dryRun: true)`,
  then the real call (same project, so no csproj edge is expected; act on every fact the dry run reports).
  (b) `ChangeNamespace(filePath: "RoslynSentinel.Git/Worktrees", newNamespace: "RoslynSentinel.Git.Worktrees", dryRun: true)`, then the real call.
  The order "parent first, sub-namespace folder second" is derived from the tool description and has not been run before: if (b) refuses or the
  build breaks, `UndoChange` the slice's calls, report `TOOL-GAP:` and stop.
- Done when: `Build` 0 errors; `Glob` `RoslynSentinel.Git/GitWorktree/**` returns nothing; `Search(mode: namespace, query: "Worktrees")` shows
  `RoslynSentinel.Git.Worktrees` with the three types; the slice is committed.
- Effort: S

### Step 4 - Docs and leftovers
- Files: `docs/current/INBOX.md` (line 13), `docs/current/references/reference_architecture_map.md` (line 19, Git row),
  `docs/current/references/reference_splitting_functionality_out_of_common.md` (line 47 and the "Worked example" paragraph), `docs/generated/*` (regenerated).
- Depends on: Step 3
- Effect: text-neutral.
- Lane: the dispatcher itself (non-C# edits).
- Change: delete INBOX line 13 (the rename is done) and add its closing note to `docs/current/CLOSED.md` per the INBOX header rule; check INBOX line 37
  for a `GitWorktree` path and append a correction line if it now points at a moved file (INBOX is append-only for other lines). Architecture map
  Git row: namespaces are `RoslynSentinel.Git` and `RoslynSentinel.Git.Worktrees`, folder `Worktrees/`. Splitting reference: line 47 becomes a
  statement of what was run and what happened (parent call, then sub-folder call, with the file and reference counts); the worked-example sentence
  about the pending rename becomes a pointer to this plan. Search prose leftovers the tool does not rewrite:
  `Search(mode: text, query: "Common\\.GitWorktree|Tools\\.Basic\\.Git(Engine|Result|ErrorCodes)", includeTests: true)` and `Grep` the same pattern over
  `docs/current/references`, `.claude/skills`, `.claude/agents` and `scripts`; fix doc-comment mentions through the MCP edit tools. Regenerate
  `docs/generated/todo_markers.md` with `pwsh -NoProfile -File scripts/Get-TodoMarkers.ps1` and the architecture docs with
  `scripts/Generate-ArchitectureMap.ps1` (only if `ArchitectureDocFreshnessTests` reports a difference).
- Done when: the two searches return nothing outside `closed/` docs; `Build` 0 errors; the slice is committed.
- Effort: S

### Step 5 - Verify and close
- Files: this plan; `docs/current/TODO.md`, `docs/current/CLOSED.md`.
- Depends on: Step 4
- Effect: text-neutral.
- Lane: the dispatcher itself.
- Change: one full-suite run at the Step 4 commit (a detached worktree run with `scripts/Invoke-WorktreeTests.ps1 -Suite full`, parameters re-read from
  the script, or `RunTest(suite: full)` when nothing else is building). Outcome classes: **clean** (0 failures, test count equal to the last full run
  on record) -> close; **name pin** (a failure caused by a namespace or full type name held as text, for example a golden file or a reflection
  lookup) -> report each with its `file:line` and stop for the owner, because a changed wire value would make this behaviour-changing; **load flake**
  (a known full-run flake) -> re-run that fixture alone before it counts; **other** -> report as new. On clean: set the Status line to IMPLEMENTED with
  the commit hashes and close with `ProjectDoc(action: close)` (toolset `projectAdmin`), which moves the plan to `plans/closed/` and its TODO.md line
  to CLOSED.md; the closing commit carries `Closes: plan_rename_git_project_namespaces`.
- Done when: the run id, commit, counts and failure set are in the Status line; the plan is in `plans/closed/`.
- Effort: S

## Out of scope

- Test namespaces in `RoslynSentinel.Tests.Git` (they stay `RoslynSentinel.Tests.Tools.Basic` and `RoslynSentinel.Tests.Battery.Basic`).
- Renaming any type, adding global usings, or changing any csproj reference.
- The Build/RunTest area and every other roadmap item.

## Risks and open decisions

1. **Tool limits (open INBOX lines about `ChangeNamespace`):** a partial qualification inside a doc-comment cref is refused, and the engine plans a
   single pass with no compile rounds. The 2026-11-26 dry run passed; Step 1 repeats it on the current tree. A refusal is a decision point, not a
   reason for manual edits.
2. **Qualified names instead of usings** where a global using of the old namespace exists (every project has `<Using Include="RoslynSentinel.Common" />`).
   Step 1 measures it. Option if the count is high: add `<Using Include="RoslynSentinel.Git" />` to the consumer csprojs first and repeat the dry
   run (owner decision; it changes the "no global usings" default above).
3. **Names held as text** (golden files, reflection, serialized type names) are found only by the full run in Step 5.
4. **Concurrent sessions:** the parent call rewrites about 144 files in one write; another session editing one of them afterwards sees a stale read
   and re-reads. Send a one-line heads-up to the busy peers before Step 2.
5. **`MoveFile` into a folder that does not exist yet** inside the same project is untested here; the dry run in Step 3 decides.
