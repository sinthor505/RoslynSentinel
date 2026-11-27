# Reference: splitting a cohesive area out of Common (or another project) into its own production project and test project

**Status:** CURRENT 2026-11-26. Verified against source and tool descriptions at HEAD on 2026-11-26; lessons come from [plan_isolate_git_area_into_own_project](../plans/closed/plan_isolate_git_area_into_own_project.md) (IMPLEMENTED 2026-11-24, the only split done so far).

## Purpose

A checklist for the planner (plan-design / plan-writer) and the orchestrators who run a plan that moves one cohesive area (a cluster of files with one job) out of `RoslynSentinel.Common`, or any project, into a new production project plus a new test project. Copy the [Step template](#step-template), apply the rules below, cite this doc in the plan. It is facts and options; the plan decides.

Why bother: the Git split took about 4.8 h of run time for 36 production files (13,925 lines) and 61 test files. Every rule below prevented a red commit, a silently dropped test or a broken ratchet in that run.

## How it works

### 1. The tool route that works today (verified 2026-11-26)

| Need | Tool (toolset) | Facts |
| --- | --- | --- |
| Create the project | `CreateProject(projectName, projectType)` (toolset `projectAdmin`: `McpToolsetControl(toolSet: projectAdmin, enabled: true)`) | `ProjectStructureEngine.cs:1767`: runs `dotnet new <projectType>` into `<solutionDir>/<projectName>` then `dotnet sln add`. No parameters for references, packages or InternalsVisibleTo; the template csproj and `Class1.cs` come with it, so edit the csproj to repo conventions and delete the template file. |
| Move files, any number, across a project edge | `MoveFile(moves: [{sourcePath, destinationPath}], namespaceHandling: keep, addProjectReferences, addInternalsVisibleTo, dryRun)` (core tool, not gated) | `WholeFileWriteTools.cs:290`. A pair may name a folder (contents land at the destination, subfolders kept, same-name file refuses); up to 200 files per call; ONE compile gate and one `UndoChange` for the whole set, so a mutually dependent cluster (base type plus derived records, a partial class in 8 files) moves in one call. Both booleans default false; false means each needed edge is reported as a fact, true writes it in the same undoable change. Package references are facts only. |
| Change namespaces after the move | `ChangeNamespace(filePath, newNamespace or matchFolder, dryRun)` (toolset `moveExtract`: `McpToolsetControl(toolSet: moveExtract, enabled: true)`) | `CoreRefactoringTools.cs:525`. File or folder (every .cs under it, subfolders included, no globs); one target namespace per call; rewrites declarations and every reference in one gate; a partial type needs all parts in the set. |
| Split multi-type files, one file per type | `MoveType`, `MoveAllTypesToFiles` (toolset `moveExtract`) | One file per type named after it; reformats the source file (see Gotchas). |
| Rename a type | `RenameSymbol` | Used for `GitImpl` -> `GitEngine` (118 sites, 3 projects, 23 files; it renamed the file too). |

Per-call recipe, facts table and refusals: [reference_split_folder_into_new_project_with_movefile.md](./reference_split_folder_into_new_project_with_movefile.md) (do not duplicate it in the plan; cite it).

**State of the tools, with evidence.**
- Multi-file `MoveFile` and `ChangeNamespace` SHIPPED 2026-11-25: [plan_multi_file_movefile_and_namespace_change_tool](../plans/closed/plan_multi_file_movefile_and_namespace_change_tool.md), commits 9c8c5663..eacd90c8 (`MoveFile` rewrite 93b1a245, csproj edges 1b7bea2e, `ChangeNamespace` d9cc08b1). `namespaceHandling: update` is still refused ("not supported yet"); `ChangeNamespace` after `MoveFile` is the two-call route.
- `SplitProjectByFolder` throws `ToolNotImplementedException` since bf27d721 (`ProjectStructureEngine.cs:1854-1869`, kept on purpose, owner decision D10). It is not part of any route; the error text points at `CreateProject` and `MoveFile`.
- The shipped route is proven by fixture tests (a connected cluster moved cross-project in one call; the same cluster file by file still refused), a scratch two-project run, and dry runs on this repo. It has NOT yet driven a real production-folder split. The plan must therefore carry a dry-run step (Step 11 below) and treat the first real call as unmeasured for cross-project gate time (60-file same-project dry run: 8.2 s vs 8.1 s for one file; cross-project not measured).
- **Fallback only if the set form refuses a shape it should accept** (record a `TOOL-GAP:`): hand-written csproj for the new project that compiles the old folder through `<Compile Include="..\Old\Folder\**\*.cs" LinkBase="Folder" />` (old project gets `<Compile Remove="Folder\**\*.cs" />`), then one `MoveFile` per file inside that one project (15-20 s each, 121 calls for Git), then drop the link. It edits csproj outside the gate and needs owner authorization; a weak model cannot derive it.

### 2. Planning rules

1. **Inventory by name AND by type, and list every non-moving referencer.** A name glob missed `CommitMessageResult.cs` (found by searching the type). Cover Common, Engines, Tools, Server, Utilities, tests and prose (`<see cref>`) references. Counts from grep are not an inventory: an independent review of the draft against source is part of the plan's approval. In the Git draft the review corrected: hint types placed in the layer above the engine, an "accept one red commit" move, a public interface member that could not take an internal type (CS0051), a claim that existing pins guarded the text (they did not), and a claim about test hosts and the probe. (Four of these were blockers for the move steps; the corrections are in the plan's Decision and Problem sections.)
2. **Measure before choosing a seam.** Count constructor call sites versus static uses of each non-moving referencer. Static class -> an internal static provider installed at startup (`RepoQueryProvider.Current`, `RepoQuery.cs`; installed in `AddRoslynSentinelEnginesBasic`, `EngineServiceCollectionExtensionsBasic.cs:47-49`; null provider = "git cannot answer"). Instance class built by DI -> an optional constructor parameter registered in DI (`PersistentWorkspaceManager.cs:409` takes `IRepoProbe? repoProbe = null`). A public constructor needs a public interface (`IRepoProbe`). Test hosts get the registration through `TestEngines.CreateProvider` (it calls `AddRoslynSentinelEnginesBasic`); unit tests that never build a provider need an install (rule 6.3).
3. **Settle namespaces as "keep during moves, rename in a final step".** `namespaceHandling: keep` gives zero `using` ripple while files travel; rename once at the end with `ChangeNamespace`. Check first that no type or member named like the new namespace's last segment exists (a simple name `Git` would resolve to `RoslynSentinel.Git`); `ChangeNamespace` reports this as a fact only, the gate is the backstop.
4. **Text and data that must not cross layers.** If the area renders tool-surface text (`ToolCallHint`, tool names), extract it to the tool side FIRST, behind golden pins that call the tool classes (not the engine) and assert full sentences with `Is.EqualTo` captured from the pre-change build; a `Does.Contain` fragment does not prove byte-identity. The engine sets keys and facts in `internal` + `[JsonIgnore]` fields (`GitResult.Hints`), so wire output stays byte-identical. Pass-through sinks that copy `Error` into another result must copy the hints too (search for them). A site with no cheap scenario is listed as "renderer-pinned only"; the reviewer signs the pin list.
5. **Place new data types in the layer that will move**, never the layer above it (the engine must set them). A public interface member cannot mention an internal type (CS0051): use a public enum (`GitRootFailure`) and let the tool side render advice.
6. **Plan the slices to the dispatch tiers.** Read `.claude/implementer-tiers.json` (Git run: depth tier 1 = 3 files / 15 edits; sweep tier 0 = 1 operation / 8 files / 25 sites / 2 projects). Open: how the dispatch hook counts a folder pair; state in the brief that the folder pair is one operation, or run it from the dispatcher or `implementer-senior`.

### 3. Move route (the order that kept every Git commit green)

1. Consolidate the area's files into one folder inside their current project: same-project `MoveFile`, green per call. With the set form this is optional for the move itself (a `moves` list can mix paths) but it keeps the inventory auditable (the old glob returns nothing) and gives `matchFolder` a folder to work on.
2. Build the seam (rule 2.2) and the test installs while the code is still in one project.
3. Split multi-type files first (DTO records out of the engine file) with `MoveAllTypesToFiles` / `MoveType` so each type is movable on its own.
4. Move dependents from higher projects DOWN into that folder in dependency order, one `MoveFile` per file where order matters (`GitErrorCodes`, `GitResult`, the `*Entry` types, the `*Result` types with `GitStatusResult` before `GitStageHunksResult`/`GitStashResult`, `IGitOperations`, `GitHunkParser`, `GitImpl`). Needs `<Using Include="RoslynSentinel.Common" />` in the receiving csproj first (213 CS0246 without it). Open (unproven): with the atomic set form this hop may be skippable (move Tools-side engine files straight into the new project in the same call); the Step 11 dry run decides, and the plan names which route it takes.
5. Create the project (`CreateProject` plus csproj edits, wiring checklist 4), `LoadSolution(forceReload: true)`, `MoveFile(moves: [folder pair], dryRun: true)`, act on every fact, then the real call with `addProjectReferences` / `addInternalsVisibleTo` as the facts require.
6. `ChangeNamespace` per source namespace group (dry run first). One target namespace per call and a folder call covers subfolders, so change the parent folder first and a sub-namespace folder second. Run for Git on 2026-11-27: the parent call `ChangeNamespace(RoslynSentinel.Git -> RoslynSentinel.Git)` rewrote 144 files and 1,866 references in one write (it added `using RoslynSentinel.Common;` where needed and rewrote 14 existing fully qualified `RoslynSentinel.Common.GitReaders` references in place); then a same-project folder `MoveFile` (`GitWorktree/` -> `Worktrees/`, namespace kept; the emptied source folder stays on disk) and `ChangeNamespace(RoslynSentinel.Git/Worktrees -> RoslynSentinel.Git.Worktrees)` rewrote 8 files and 19 references. Each call built clean. `ChangeNamespace` has no `returnDiff`, so check the written result with `Git(diff)` ([plan_rename_git_project_namespaces](../plans/plan_rename_git_project_namespaces.md)).

### 4. Wiring checklist: new production project (every item cited; verify each by Search, lists drift)

- **csproj** (`RoslynSentinel.Git/RoslynSentinel.Git.csproj`): copy `TargetFramework`, `ImplicitUsings`, `Nullable`, `Configurations` (`Debug;Release;DebugVisualStudio`), `<Using Include="RoslynSentinel.Common" />`, `ProjectReference` to the layer below. Package references move with the code (LibGit2Sharp 0.32.0): add to the new csproj BEFORE the move (else a MissingPackageReference fact), remove from the old one AFTER.
- **RoslynSentinel.slnx** (`:14` Git, `:22` Tests.Git). `CreateProject` adds it via `dotnet sln add`; a hand-written csproj needs `dotnet sln add` or an slnx edit.
- **InternalsVisibleTo, both directions.** Old project -> new for the internals the new code uses (`Common.csproj:43` grants `RoslynSentinel.Git`: `IRepoQuery`, `ChangedCommit`, `WorkingTreeChange`); new -> every consumer and test project that touches an internal (`Git.csproj:23-31`, nine entries), plus `Tools.Basic.csproj:18` and `Common.csproj:31` for the new test project. Drop an old grant only when the compiler no longer needs it (Git run: `Tests.ModelEval` dropped; `Tests` and `Tests.Server` still needed for non-Git internals). `addInternalsVisibleTo: true` derives the edges the move needs; list each add and drop in the report.
- **ProjectReferences for every consumer**, including `Utilities.*` and test projects: Git run added Engines.Basic, Tools.Basic, Tools.Advanced, Server.Advanced, Utilities.PlanStepRunner, Tests.Harness, Tests.Git. `addProjectReferences: true` derives them; layering stays one-way and `Tools.*` siblings cannot see each other.
- **Restore.** Probe `LoadSolutionHandWrittenCsprojRestoreIntegrationTests` (b89965a5) is GREEN for `LoadSolution` with and without `forceReload` when an existing project references a hand-written csproj added with `dotnet sln add`; use `LoadSolution(forceReload: true)` for a destination in no loaded project. A hand-edited `.slnx` was not probed. If a brand-new csproj still fails ("Unable to find project information"), a shell `dotnet restore RoslynSentinel.slnx` is the known minor bypass (the Git run needed it); report it as a bypass.
- **Server-source lists (a new project missing here makes a stale server look fresh):** `ServerBinaryStaleness.SourcePathPrefixes` (`ServerBinaryStaleness.cs:44-54`), `scripts/server-source-paths.txt` (a test enforces sync), `TestInstanceLauncher` (`GetSourceRoots` `:633`, `ReadGitStamp` `:757`); the tests that pin them are `TestInstanceLauncherTests` and `ServerBinaryStalenessTests`.
- **Path-keyed and project-list ratchets.** Re-key `BareFileWriteRatchetTests` entries (`"RoslynSentinel.Git/InProcessCommitRoute.cs::AppendOutcome"` `:33`; `"RoslynSentinel.Git/GitEngine.cs::StageHunksAsync"` `:65`). Add the project where the ratchet should cover moved code: `ErrorPolicyKindRatchetTests.ScannedProjects` (`:26`), `FirstMatchRatchetTests.IsProductionProject` (`:20`), `NewlineLiteralRatchetTests.IsInScopeProject` (`:61`), `WhitespaceRatchetTests.IsProductionProject` (`:174`), `EnvVarDeclarationGuardTests.EnvReadAllowedFolders` (`Tests.ModelEval`, `:32`; not in the Git plan's list, found by search). `StaleReadPolicyRatchetTests.IsProductionProject` (`:164`) excludes Common by name, so a new project is scanned automatically. Method: `Search(mode: text, query: "RoslynSentinel.<Source>/", includeTests: true)` plus `== "RoslynSentinel.<Source>"` over `RoslynSentinel.Tests*` (63 hits in 14 files for Common). Each edit is listed in the report (no silent coverage drop).
- **Docs and generated files.** `scripts/Generate-ArchitectureMap.ps1` (sets `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1`, runs `ArchitectureDocFreshnessTests` in `Tests.Server`); the CLAUDE.md layering line (`CLAUDE.md:167-170`; budget 15 KB, `scripts/Test-GuidanceHygiene.ps1`; it was already over budget when the Git plan edited it); `reference_architecture_map.md` (Layers table `:14-19`, Tests list `:29`).
- **Native libraries:** `Directory.Build.targets` (`TrimLibGit2NativeRuntimes`, `:8`) trims a native package to win-x64 for every referencing project; check any new native package against it.

### 5. Wiring checklist: new test project

1. `AssemblyInfo.cs` with `[assembly: Parallelizable(ParallelScope.Fixtures)]` (`Tests.Git/AssemblyInfo.cs:7`; its comment states the no-process-wide-state rule).
2. Its own global-namespace `[SetUpFixture]` deriving `RoslynSentinel.Tests.WorkingDirectoryLeakDetector` (`Tests.Git/LeakDetectorSetUp.cs`; every test assembly has one, a SetUpFixture only covers its namespace).
3. Any seam install goes in that class as `[OneTimeSetUp]` (`RepoQueryProvider.Current ??= GitReaders.Default`), not a `[ModuleInitializer]` (runs before NUnit setup ordering, invisible to the fixture lifecycle). NUnit allows one SetUpFixture per namespace, so reuse the existing class. A test that swaps a process-wide static restores it and is `[NonParallelizable]` with a comment.
4. csproj: copy a sibling test csproj (`Tests.Git.csproj`: analyzers off, NUnit package set, `Using NUnit.Framework`), ProjectReferences to the area project, the shared support project (`Tests.Support.Basic`; direction is new project -> support, never the reverse) and the projects the fixtures use. Name it `RoslynSentinel.Tests.<Area>`: `scripts/Test-Parallel.ps1:27` globs `RoslynSentinel.Tests*.csproj` and `scripts/Invoke-WorktreeTests.ps1:251` globs `RoslynSentinel.Tests*` directories, so no list needs an entry.
5. **Helpers first.** Before moving fixtures list every helper a staying file uses from a moving file (a nested type such as `GitReaderParityTests.ScriptedReader`) and every helper a moving file uses from a staying file. Either lift the helper into the support project or move the user with the fixtures; the post-move build is the proof.
6. Test namespaces may stay (Git run kept `RoslynSentinel.Tests.Tools.Basic`; an INBOX line records the mismatch); the plan says so under Out of scope.
7. **Counts.** Sum of old plus new project = pre-move count plus tests the plan added (Git: 3,888 -> 2,576 + 1,324 = 3,900 vs 12,274 -> 12,286 total, +12 new tests). Fewer = a fixture stopped being discovered; more = a duplicate. Also confirm `[Category("OutOfProcess")]` classes are still excluded from the fast suite (`TestSuiteFilter`): an OutOfProcess class run under `-Suite fast` matches 0 tests silently.

### 6. Tests that break when code moves (fold each fix into the slice that breaks it, never a later "fix tests" step)

- Engine-direct tests that asserted text now applied on the tool side (one line-range test asserted `ErrorDetail`; it now asserts the `HunkSelection` hint).
- Reflection "probe table covers every interface method" tests when an interface gains inherited members (`IGitReader : IRepoQuery`): `ProbeTable_CoversEveryIGitReaderMethod` in `GitReaderEnvironmentTests.cs:161` and `GitRelativeWorktreeTests.cs:186` (fixed in decf3ad8).
- `ArchitectureDocFreshnessTests` (regenerate, never hand-edit `docs/generated/`).
- New error text: `MessageTextRatchetTests` (`docs/known-long-message-pins.txt`; growth fails; assert with `ErrorAssert.Is` and a code plus a short fragment) and `ToolHintRatchetTests` (`docs/known-handwritten-tool-hints.txt`; build hints with `ToolCallHint.Next`/`Call`).
- Why a full run matters: the first full run after batch B found 3 deterministic failures (the probe tables twice, one engine-direct test) that ten filtered runs of the pin classes had not touched. Filtered runs prove only the classes they name.

## Usage

### Step template

Copy the order; each row is one or more plan steps. "Done when" is the planner's definition of verified; the reviewer confirms the tests cover the changed path, not just a shared helper.

| # | Step | Done when |
| --- | --- | --- |
| 0 | Inventory (name + type), referencer list, seam measurements, test inventory; independent review of the draft against source | Counts written into an appendix; reviewer's corrections folded into the plan before approval |
| 1 | Contract skeleton: new types in the layer that will move, stubs marked `// TODO implement (Step N)` | `Build` 0 errors; the stub search lists exactly the stubs |
| 2 | Characterization pins (only if the area renders tool text): golden full sentences through the tool classes | Pins pass at the pre-change commit; renderer-pinned-only list signed by the reviewer |
| 3 | Baseline full run on a clean tree, idle machine; heads-up messages to coordinating sessions | Run id and commit recorded; per-project wall, summed seconds, counts, failure set |
| 4 | Extract tool-surface text/data in slices (migration-first, `[Obsolete("Use X")]` beside the old) | Each slice: `Build` 0 errors, pin classes unchanged |
| 5 | Retire the legacy path; migrate test callers in the same slice | No CS0618; search for tool-name strings in the engine returns nothing |
| 6 | Consolidate into one folder (same-project `MoveFile`) | Old name glob returns nothing; folder holds the inventoried count |
| 7 | Seam + test installs | Build green; seam-consumer tests unchanged in every touched assembly |
| 8 | Split multi-type files | `GetFileOutline` shows only the engine class in the engine file |
| 9 | Move dependents down in dependency order | Build green per call; search for MCP/tool-name types in the folder returns nothing |
| 10 | `CreateProject` + csproj/slnx/IVT/reference wiring (checklist 4) | Solution `Build` 0 errors with the empty project; `LoadSolution(forceReload: true)` loads it |
| 11 | `MoveFile` dry run, then the real folder move with edges | Dry run facts all acted on; real call green; `UndoChange` id recorded |
| 12 | Ratchets, staleness lists, scripts (checklist 4) | The named ratchets, `ServerBinaryStalenessTests`, `TestInstanceLauncherTests` pass |
| 13 | Test inventory and shared helpers (checklist 5.5) | Inventory counts equal Search counts; no staying file uses a nested type of a moving fixture |
| 14 | Test project: csproj, AssemblyInfo, SetUpFixture, slnx, IVT; move fixtures with one set `MoveFile` | Counts rule (5.7) satisfied; moved classes pass with unchanged results |
| 15 | Rename step: `ChangeNamespace` (dry run first), `RenameSymbol` for type names | Namespace search returns nothing, or a `TOOL-GAP:` plus TODO and INBOX line exist |
| 16 | Docs wiring: generated map, CLAUDE.md layering line, architecture map | `ArchitectureDocFreshnessTests` and `Test-GuidanceHygiene.ps1` clean |
| 17 | Handoff messages to owners of plans that glob the moved paths | Message sent; reply or absence noted in the next commit |
| 18 | Closing stub sweep | No plan stub, `[Obsolete]` member or untracked `// TODO` remains |
| 19 | Measure and close (below) | Comparison table in the Status line with commit hashes |

### Execution rules

- **Batched orchestrators**, one per batch of related steps (Git: A = 1-4, B = 5-8 in parallel with 9-10, C = 11-14, D = 15-19, E = 20-23 by the main session). Lanes run in parallel only on disjoint files; one hot file (`GitImpl.cs`) serializes its lane. `implementer-senior` for cross-project relocation and slices over tier limits.
- **Spike before any untested tool.** For `MoveFile` / `ChangeNamespace` use `dryRun: true` on the real tree (writes nothing). A throwaway worktree spike is only for a tool with no dry run, and has a hazard: the MCP server holds ONE loaded solution per session, so loading the worktree redirects every tool call of every agent in that session, including `Git` commits (journal 2026-11-24 19:52: a main-session commit went to the spike branch and failed safely with `GitNothingToCommit`). Spike only when nothing else is mid-operation, and reload the main solution afterwards.
- **Tests never block a slice.** Run detached, filtered runs per slice with `scripts/Invoke-WorktreeTests.ps1 -WorktreeRoot <root> -Ref <commit> -Suite fast -Filter "FullyQualifiedName~A|B" -WithRatchets` (about 85 s) for non-OutOfProcess classes; OutOfProcess classes run in their own run (`-Suite full -Projects <project> -Filter "TestCategory=OutOfProcess&FullyQualifiedName~..."`); at most 2 worktree runs at once; a filtered full run does not reset the full-test marker. Take one full run after each move batch and a baseline full run just before the first move. A known load flake is re-run alone before it counts as new. (Recipes are from the run handoff; re-read the script parameters.)
- Move slices touch files other sessions edit: `HOLD`/`RELEASE`, a clean tree, `peer-coordination` skill first.
- Commit each slice at a compile-green point with the `Plan: <id>` trailer; the last carries `Closes:`.

### Coordination

Plans that glob the moved paths (the nightly-suite plan's area watch lists, `plan_full_suite_nightly_and_tail_shard.md`, still DRAFT 2026-11-24; no `scripts/test-areas.json` or watch-list file exists yet) break when files move. `ListAgents`, then `SendMessage` the owner before the folder move and before the test move (heads-up, do not wait), and at the end send the new watch list: the new production project directory, the new test project, and the tool-side files whose fixtures moved (not the hint-text files, so a hint edit does not count as an area change).

### Measure and close

Take one full run at the final commit on an idle machine; compare per-project wall and summed seconds with the Step 3 baseline using `scripts/Get-TestRunTimings.ps1 -Run <id> -View Projects`. Expect the slow fixtures' wall to move with them: the full-run total moves little; the payoff is task-end selection (skip the slow project when its directory is untouched). Worked numbers (Git, Step 23, baseline 042035a6 had 4 overlapping runs so its walls are inflated; final bcc5031d had 1):

| Measure | Baseline 042035a6 | Final bcc5031d |
| --- | --- | --- |
| Failures | 0 | 0 |
| Tests total | 12,274 | 12,286 (+12 added) |
| Tests.Tools.Basic wall / tests / test-seconds | 1,189 s / 3,888 / 8,659 | 277 s / 2,576 / 2,140 |
| Tests.Git wall / tests / test-seconds | - | 760 s / 1,324 / 4,517 |
| Total wall / summed test-seconds | 1,598 s / 9,982 | 1,421 s / 7,926 |

Tests.Git became the critical path (760 s). The run handoff records run elapsed of 1,675 s and 1,499 s for the same two runs; quote one measure consistently.

### Effort (from the Git run, a guide only)

About 1 h planning plus an independent review and revision (planning time is not recorded in the repo), then batches of 54, 53, 94 and 49 min and about 40 min for the closing batch: 4.8 h from the approval commit 48340666 to the close commit 04819570 by reflog timestamps, roughly 6.5 h with planning. Scale: about 36 production files / 14k lines and 61 test files. The multi-file `MoveFile` should shorten the 121 per-file calls of the link-stand-in route (30-40 min of gate time) to two folder calls, but that saving is not measured.

## Gotchas

- `MoveType` / `MoveAllTypesToFiles` create one file per type named after it (no thematic grouping), reformat the whole source file (the Git split's `GitImpl.cs` took +90/-78 whitespace-only lines) and once left stray marker comments; check the diff and the file outline.
- `ChangeNamespace` in moved mode qualifies a name instead of adding a using where a global using of the old namespace exists (INBOX 2026-11-25 03:45); read the dry run.
- After a `MoveFile` that writes a csproj, the next call may wait for a background reload (`ReloadPending` fact); a very long reload fails the next call with a load-timeout that a retry clears.
- `MoveFile` refuses a `.cs` destination inside no loaded project; run `LoadSolution(forceReload: true)` after creating the project.
- Source csproj items that name the moved folder are reported as "not updated" facts; fix them by hand.
- A `<see cref>` to a type in the new project reads as dangling in the old one; doc diagnostics are off, so no CS1574 appears. Make the two or three prose references text (Git run: `ChildProcessCapture.cs`, `RepoQuery.cs`).
- The first LoadSolution after a worktree spike, a restart or a csproj write may report mid-load flakes; re-run alone.
- Pre-existing `// TODO` markers move with files; the closing sweep checks that the plan's own TODOs have INBOX lines.

## Worked example

[plan_isolate_git_area_into_own_project](../plans/closed/plan_isolate_git_area_into_own_project.md): Decision (D-a to D-f), Execution rules, per-step Done lines, Risks, Appendix B (move recipe and spike report), Appendix C (test inventory), Step 23 table. It used the link-stand-in fallback because the multi-file tools did not exist; follow this doc's route instead. Its namespace rename was finished afterwards with `ChangeNamespace`: see [plan_rename_git_project_namespaces](../plans/plan_rename_git_project_namespaces.md).

## Related

- [reference_split_folder_into_new_project_with_movefile.md](./reference_split_folder_into_new_project_with_movefile.md) - per-call recipe and facts table.
- [proposal_isolate_heavy_test_areas_by_project.md](../proposals/proposal_isolate_heavy_test_areas_by_project.md) - the Principle (own production project, own test project, thin tool wrappers); Areas 2 and 3 are the next candidates.
- [proposal_multi_file_movefile_and_namespace_change_tool.md](../proposals/closed/proposal_multi_file_movefile_and_namespace_change_tool.md) - why the set form exists; alternatives rejected.
- Closed blockers: `blockers/closed/blocking_error_movefile_has_no_multi_file_cross_project_move.md`, `blockers/closed/blocking_error_splitprojectbyfolder_is_a_stub.md`.
- [reference_architecture_map.md](./reference_architecture_map.md), [reference_working_conventions.md](./reference_working_conventions.md).
- Skills: `plan-design`, `plan-writer`, `dto-conventions` (new or moved DTOs need `NEW-DTO:` / `DTO-EXEMPT:` blocks).
