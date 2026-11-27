# Plan: Create RoslynSentinel.Engines.Core and move snippet matching into it

**Status:** IMPLEMENTED 2026-11-27. Steps 1 to 7 at 373ff903, e968ccd4, 4545a2af, 87931adb, 1e509d72 (5a), d04798a6 (5b), 64088182 (6), 6c573134 (7); full run 6c573134-20261127-022016-39260 (suite full, 1,519 s): 12,633 tests, 12,585 passed, 48 skipped, 0 failures, test count equal to the baseline cc147f9e-20261127-004211-40584 (12,633, 0 failures). Filtered runs per step: 942 tests each; the only failure, ArchitectureDocFreshness at d04798a6 and 64088182, was the expected stale generated docs, fixed by the Step 7 regeneration. Tool gaps recorded in INBOX (MoveFile gate adds only direct edges; MoveMember refuses a record as source type). CLAUDE.md is 15,073 bytes after Step 7; the hygiene script reports 0 findings.
(Decision, last entry); Steps 5 to 8 left.

## Problem

Phase 2 of [plan_roadmap_layering_and_project_splits.md](../plan_roadmap_layering_and_project_splits.md): the first mechanical split of engine-like logic
out of `RoslynSentinel.Common`. Owner decision Q42 (2026-11-27): snippet matching goes into a new `RoslynSentinel.Engines.Core`, the shared engine
project of Q40. The route and checklists are in
[reference_splitting_functionality_out_of_common.md](../../references/reference_splitting_functionality_out_of_common.md).

Grounding (2026-11-27, HEAD 39a209c2, re-checked by the readiness review at e3df18ef; "symbol" = Roslyn references, "text" = name search):

- **The area:** `RoslynSentinel.Common/SnippetMatching/` (15 files, namespace `RoslynSentinel.Common.SnippetMatching`) plus the root files
  `SnippetMatchOutcome.cs` and `SnippetSpan.cs`, both in namespace `RoslynSentinel.Common` and used inside Common only by the area (symbol).
  Inventory in Appendix A.
- **Three uses from Common code that stays:**
  1. `ToolException.cs:71, 89` (`ToolNotFoundException`) and `:183, 193` (`ToolAmbiguousMatchException`) carry `SnippetMatching.SnippetFailureDetail?`.
     `SnippetFailureDetail` is a data record whose members use the data records `SnippetClosestWindow` and `SnippetFilterStep`
     (`SnippetMatchResult.cs:85, 94`; both have only primitive and `IReadOnlyList<string>` members). Its only code tie to the moving types is the
     factory `public static SnippetFailureDetail From(SnippetMatchResult result)` (`SnippetFailureDetail.cs:24`), called from 9 sites, all in
     `SnippetErrorFormatter.cs` (symbol).
  2. `DiffEngine.cs:313, 324` uses the internal `LineBlockAligner` (`RoslynSentinel.Common/LineBlockAligner.cs`), which `SnippetCandidateFinder.cs:31, 164`
     also uses. By name search it is the only internal Common type the area uses.
  3. `ContextHelper.cs:5` has a dead `using RoslynSentinel.Common.SnippetMatching;` (no snippet type used in the file).
- **A second namespace in one file:** `SnippetErrorFormatter.cs:14` declares `ToolMatchedLooserTierException` in a `namespace RoslynSentinel.Common`
  block; its only users are the formatter (`:197, :241`) and `RoslynSentinel.Tests` (`ContextHelperTests.cs:325`, `SnippetErrorFormatterTests.cs:241, 828, 864`).
  `ChangeNamespace` refuses a file that declares types in two namespaces, so the split of Step 2 is required.
- **What the area uses from Common** (text; the `MoveFile` dry run is the proof): `ToolException`, `ToolNotFoundException`,
  `ToolAmbiguousMatchException`, `ToolErrorCode`, `MissingTargetKinds`, `ToolCallHint` (all public), the internal `LineBlockAligner`; Roslyn packages
  only (they flow through the reference to Common). No MCP SDK, `ToolCallContext`, `ActiveToolSurface` or `*Options` use.
- **Consumers outside Common** (text): Engines.Basic 11 files, Engines.Advanced 4, Tools.Basic 1 (`WorkspaceFileEditImpl.cs`); none in Git, AgentLoop,
  Utilities, Tools.Advanced, Tools.Experimental, Server.Advanced. `RoslynSentinel.Common.SnippetMatching` is mentioned on 57 lines: 37 using
  directives (one of them the dead one), 15 namespace declarations, 4 fully qualified uses, 1 comment (`SnippetMatchResult.cs:10`). No production code
  outside Common uses an internal member of the area; of the test projects only `RoslynSentinel.Tests` does.
- **Tests:** about 269 `[Test]` methods in 15 files of `RoslynSentinel.Tests`, all in-process, plus 3 in
  `Tests.Tools.Basic/SnippetResolverCrossAssemblyTests.cs` (Appendix A). No ratchet or baseline is keyed on `SnippetMatching/` paths.
- **Wiring:** the server-source lists match the `RoslynSentinel.Engines.` prefix (`ServerBinaryStaleness.cs:46-50`, `scripts/server-source-paths.txt:6-12`,
  `TestInstanceLauncher.cs:635` `GetSourceRoots`), so they need no edit. `ErrorPolicyKindRatchetTests.ScannedProjects` (`ErrorPolicyKindRatchetTests.cs:26`,
  5 entries) is an explicit list with the floor `MinimumKindSites = 194` (`:22`); the formatter's 8 `ToolNotFoundException` constructions leave a scanned
  project, so without the edit the count falls below the floor.
- **Tool-surface text travels with the area** (recorded for roadmap Phase 5, not fixed here): 2 `ToolCallHint.Call("Member", ...)` uses
  (`SnippetErrorFormatter.cs:163-164`), 10 exception constructions in the formatter, tool and parameter names in message text.
- **Churn:** at least 14 commits touched the area since 2026-11-14 (latest 7b34e891, 2026-11-26); 4 `// TODO` markers in `SnippetErrorFormatter.cs`
  (`:180, :194, :476, :629`) move with it.

## Decision

Dated entries; later entries do not override earlier ones unless they say so.

- **2026-11-27, Q42 (owner):** the target is a new project `RoslynSentinel.Engines.Core`.
- **2026-11-27, mechanical (owner, roadmap):** file moves, wiring, a namespace rename; no redesign. Lightweight DTOs stay in Common when moving them is
  not low friction.
- **2026-11-27, planner defaults** (each low risk and reversible; the owner may reopen any):
  - **P1 placement:** `Engines.Core` references only Common. `Engines.Basic` and `Engines.Advanced` reference it; `Tools.Basic` gets an explicit reference.
  - **P2 the three DTO records stay in Common** (`SnippetFailureDetail`, `SnippetClosestWindow`, `SnippetFilterStep`): the exception types in Common
    carry them. They move to Common's root folder and namespace `RoslynSentinel.Common`, so the namespace `RoslynSentinel.Common.SnippetMatching`
    disappears with the area and every project sees them through its global using.
  - **P3 `SnippetFailureDetail.From` moves to the area** as a static method of `SnippetErrorFormatter` named `ToFailureDetail` (9 call sites, one file).
  - **P4 `LineBlockAligner` stays in Common** (`DiffEngine` uses it); Common grants `InternalsVisibleTo` to `RoslynSentinel.Engines.Core`. Its test
    class `LineBlockAlignerTests` stays.
  - **P5 `ToolMatchedLooserTierException` travels with the area** in its own file, like the build-specific exceptions of the Build/RunTest design (its Q31).
  - **P6 tests stay in `RoslynSentinel.Tests`** (in-process and fast, so a test project of their own buys no selection; same principle as Q7 of the
    Build/RunTest design). `Engines.Core` grants `InternalsVisibleTo` to `RoslynSentinel.Tests`; no other test project uses the area's internals.
  - **P7 namespace:** keep during the moves; one `ChangeNamespace` to `RoslynSentinel.Engines.Core.SnippetMatching` afterwards (folder
    `RoslynSentinel.Engines.Core/SnippetMatching/`).
  - **P8 `SnippetMatchOutcome` and `SnippetSpan` move** with the area into that folder and namespace.
  - **P9 baseline:** full run `cc147f9e-20261127-004211-40584` (12,633 tests, 0 failures; `docs/current/CLOSED.md:15`) if no `.cs` commit landed since
    cc147f9e (true at e3df18ef), else a fresh run.
- **2026-11-27, Q45 (owner): roadmap Phase 0.2 is dropped.** `PromptTokenEstimator` is used by `Common/LmStudioClient.cs:90-91`; `ErrorPolicyDocument`
  is shared by AgentLoop and a `Server.Advanced` filter. Both are placed later with their groups (roadmap Phase 6).
- **2026-11-27, Q46 (owner): admission rule for `Engines.Core`, accepted as worded:** "Engine logic that both engine flavors use, or that an engine and
  a tool project use, with no MCP type, no tool name and no tool-surface check. One folder and one namespace per area. A class with one flavor as its
  only user stays in that flavor. Data types with users below the engines stay in Common." Snippet matching arrives as a recorded exception to "no
  tool name" until roadmap Phase 5 removes its text.
- **2026-11-27, Q47 (owner): CLAUDE.md wording, accepted as worded.** In the Architecture bullets: after "`Git`: git readers, Git engine, worktree
  harness." add "`Engines.Core`: engine logic shared by both engine flavors, one folder per area; references only `Common`."; replace "Shared
  helpers go one layer down: `Engines.Basic` for engine logic, `Common` for result/MCP plumbing." with "Shared engine logic goes to `Engines.Core`,
  never to `Common`; `Common` keeps result/MCP plumbing."
- **2026-11-27, Readiness (plan-design): independent review clean at 9bfab1e7.** Round 1 found 5 blocking items (slice size, three checks that could
  not run as written, a shorthand move list), all fixed; round 2 found none. It did not compile or dry-run anything; the tool shapes listed in Risks
  item 2 remain untested until their steps.
- **2026-11-27, Step 5 route corrected (planner):** the first dry run of the move was refused. With `addProjectReferences: true` the gate adds only
  the three direct edges it flags (`RoslynSentinel.Tests`, `Tools.Basic`, `Engines.Basic`) and then fails with 50 errors in projects that reach the new
  project only through another one (`Engines.Advanced`, `Tests.Tools.Basic`, `Tests.Server`, `Tests.Advanced`); recorded as a tool gap
  (`MoveSetEdgeAnalyzer.cs:67`, INBOX 2026-11-27 09:25). The plan had relied on the toggle where the splitting reference (wiring checklist, "ProjectReferences
  for every consumer") and the Git run wire every consumer by hand before the move. Step 5 now does that: a direct `ProjectReference` in every
  project whose code names a moved type, written with Edit (csproj is a non-C# file), then the move. No owner decision changes: P1 already named the
  engine and tool references; the test projects are added.
- **2026-11-27, results of Steps 1 to 4:** `MoveMember` refuses a record as source type, so Step 2 used its fallback (new method, 9 callers switched,
  old method removed). `MoveType` left an empty namespace block (removed) and reformatted auto-properties in `SnippetMatchResult.cs` (whitespace only,
  left). All three `ChangeNamespace` calls of Step 3 matched their dry runs and rewrote the relative-qualified names in `ToolException.cs`, so the
  records are in namespace `RoslynSentinel.Common` as planned. Filtered runs after Steps 1, 2 and 3: 942 tests, 0 failures each.

## Current design

- Built: `RoslynSentinel.Engines.Core` (new; references Common) holding `SnippetMatching/` with 17 files: the folder's 15 minus `SnippetFailureDetail.cs`,
  plus `ToolMatchedLooserTierException.cs` (split out), `SnippetMatchOutcome.cs` and `SnippetSpan.cs`; namespace `RoslynSentinel.Engines.Core.SnippetMatching`.
- Left in Common: `SnippetFailureDetail.cs`, `SnippetClosestWindow.cs` (new file), `SnippetFilterStep.cs` (new file) in the root folder, namespace
  `RoslynSentinel.Common`; `LineBlockAligner.cs`.
- Exceptions to the admission rule (Q46): the area arrives with tool-surface text (Problem); roadmap Phase 5 removes it.
- Simplest version considered (checklist item 7): leave the three DTO records where they are, in a three-file `Common/SnippetMatching/` folder and
  namespace. Rejected: two namespaces named `SnippetMatching` would exist. The price of P2 is Step 1 and Step 3 (two type splits, one same-project file
  move, three small namespace changes with a fallback), all made while everything is still in one project.

## Execution rules

- C# changes only through the RoslynSentinel MCP tools; `.csproj`, `.slnx`, `.md` with Edit. One `Build` or `RunTest` at a time, subagents included.
- Gated tools: `MoveType` and `ChangeNamespace` are in toolset `moveExtract`, `CreateProject` and `ProjectDoc` in `projectAdmin`. The dispatcher enables
  both with `McpToolsetControl` before the first dispatch; subagents share the server, so the toolsets are on for them. `MoveFile`, `MoveMember`,
  `RenameSymbol`, `UsingDirective`, `DeleteFile` are core tools.
- **Acceptance per slice is one check: `Build` 0 errors.** The lines marked "Dispatcher checks" are run by the dispatcher after the slice, not by the
  implementer.
- A `MoveFile` set call or a `ChangeNamespace` call is ONE semantic operation over the sweep tier by design (`.claude/implementer-tiers.json`): lane
  `implementer-senior`, and the brief says so. Dry run before every real call; record every `UndoChange` id.
- `ChangeNamespace` has no diff preview: after a real call and before the commit, read `Git(operation: diff)`. A refused call or an unexpected rewrite
  is `UndoChange`, `TOOL-GAP:` and stop; no manual edits stand in for a tool.
- `MoveType` reformats its source file and has left stray marker comments before: the dispatcher reads `Git(operation: diff, stat: true)` and the file
  outline after it and reports whitespace-only changes.
- A folder `MoveFile` leaves the emptied source folder on disk. The dispatcher removes an empty leftover folder with PowerShell `Remove-Item` (an empty
  directory is not a tracked file).
- Other sessions share the tree and this area changes often. Before each real call: `Git(operation: status)`; a dirty `.cs` file in the affected set
  is resolved with its owner first (`peer-coordination` skill). The dispatching session sends the heads-up messages.
- **Filtered run** (dispatcher, where a step names it; parameters re-read from the script first):
  `scripts/Invoke-WorktreeTests.ps1 -WorktreeRoot <root> -Ref <commit> -Suite fast -WithRatchets -Filter "FullyQualifiedName~Snippet|FullyQualifiedName~ContextHelper|FullyQualifiedName~NewContentIndenter|FullyQualifiedName~LineBlockAligner|FullyQualifiedName~ErrorPolicyKindRatchet|FullyQualifiedName~ServerBinaryStalenessTests"`.
  `TestInstanceLauncherTests` is `[Category("OutOfProcess")]`, so the fast suite excludes it; it is covered by the full run of Step 8. The name
  filter is deliberately wide: `~Snippet` and `~ContextHelper` also match the tool-level `ReplaceSnippet*` and snippet-miss classes in
  `Tests.Tools.Basic` and `Tests.Server` (about 33 test files in all), which exercise the area through the tools.
- File listings for the dispatcher's checks use PowerShell (`Get-ChildItem <folder> -Name`, `Test-Path`); a listing reads no C# content.
- Commit each slice at a compile-green point with `Git` (scope `listed`), trailers `Plan: plan_move_snippet_matching_into_engines_core` and
  `Co-Authored-By`; the last commit carries `Closes:`. The MCP server is not restarted by the run.
- The owner is present: a decision point stops the step with `DECISION NEEDED:` and the evidence.

## Steps

### Lane map

Single lane (every step touches the same folder). Steps 1 to 3 are in Common only and each ends compile-green; Step 5 is the only cross-project move.

### Step 1 - Split the two DTO records out of SnippetMatchResult.cs
- Files: `RoslynSentinel.Common/SnippetMatching/SnippetMatchResult.cs`; `SnippetClosestWindow.cs`, `SnippetFilterStep.cs` (new, same folder).
- Depends on: none
- Effect: text-neutral.
- Lane: `implementer` (3 files, 2 edits).
- Change: `MoveType` with `destination: ownFile` for `SnippetClosestWindow`, then for `SnippetFilterStep` (namespace unchanged).
- Done when: `Build` 0 errors.
- Dispatcher checks: `GetFileOutline` of `SnippetMatchResult.cs` no longer lists the two records; `Git(operation: diff, stat: true)` shows no change to
  `SnippetMatchResult.cs` beyond the removed records (whitespace-only lines are reported, not fixed).
- Effort: S

### Step 2 - Give the formatter its own exception file and the failure-detail factory
- Files: `RoslynSentinel.Common/SnippetMatching/SnippetErrorFormatter.cs`, `SnippetFailureDetail.cs`; `ToolMatchedLooserTierException.cs` (new, same folder).
- Depends on: Step 1 (same folder; one writer at a time)
- Effect: text-neutral.
- Lane: `implementer` (3 files, 3 edits).
- Change, in this order: (a) `MoveType` with `destination: ownFile` for `ToolMatchedLooserTierException` (it keeps namespace `RoslynSentinel.Common` for
  now). (b) `MoveMember` of the static method `From` from the record `SnippetFailureDetail` to the static class `SnippetErrorFormatter` (the tool
  rewrites the 9 call sites, all in the formatter). (c) `RenameSymbol` of the moved method to `ToFailureDetail` (no member of that name exists).
  Fallback if `MoveMember` refuses a record as the source type: report `TOOL-GAP:`, then do it migration-first inside the two files: add
  `ToFailureDetail` to the formatter with `Member`, switch the 9 callers with one batched `ReplaceSnippet`, delete `From` with `Member`.
- Done when: `Build` 0 errors.
- Dispatcher checks: `GetFileOutline` of `SnippetFailureDetail.cs` shows no method; `Search(mode: text, query: "^namespace ", fileGlob:
  "RoslynSentinel.Common/SnippetMatching/SnippetErrorFormatter.cs")` returns one line; the filtered run passes.
- Effort: S

### Step 3 - Move the three DTO records to Common's root namespace
- Files: `SnippetFailureDetail.cs`, `SnippetClosestWindow.cs`, `SnippetFilterStep.cs` (moved to `RoslynSentinel.Common/`); `ContextHelper.cs`;
  `ToolException.cs` and the other referencing files the tool rewrites (symbol: `SnippetClosestWindow` 27 references in 10 files, `SnippetFilterStep` 16 in 4).
- Depends on: Step 2
- Effect: text-neutral.
- Lane: `implementer-senior` (several operations, one slice; over the sweep tier by reference count).
- Change, in this order: (a) remove line 5 of `ContextHelper.cs` (`using RoslynSentinel.Common.SnippetMatching;`) with `UsingDirective`. (b) In the
  three DTO files turn doc-comment `<see cref>` references to types that will move into plain `<c>` text (`SnippetFailureDetail.cs:4`
  `SnippetMatchResult`; the `SnippetClosestWindow` summary's `SnippetMatch` and `SnippetMatchResult.ClosestWindows`), because they would dangle across
  the project edge. (c) `MoveFile(moves: [{ sourcePath: "RoslynSentinel.Common/SnippetMatching/SnippetFailureDetail.cs", destinationPath:
  "RoslynSentinel.Common/SnippetFailureDetail.cs" }, the same pair form for `SnippetClosestWindow.cs` and `SnippetFilterStep.cs`], namespaceHandling: keep)`.
  (d) `ChangeNamespace(filePath: "RoslynSentinel.Common/SnippetFilterStep.cs", newNamespace: "RoslynSentinel.Common")`, then the same for
  `SnippetClosestWindow.cs`, then for `SnippetFailureDetail.cs`; dry run before each.
- Done when: `Build` 0 errors.
- Dispatcher checks: a listing of `RoslynSentinel.Common/SnippetMatching/` shows exactly 15 files (the 14 existing files Appendix A marks "moves" plus
  `ToolMatchedLooserTierException.cs`); `Search(mode: text, query: "SnippetMatching\\.SnippetFailureDetail")` returns nothing; the filtered run passes.
- Decision point: if a `ChangeNamespace` dry run does not validate (the relative-qualified `SnippetMatching.SnippetFailureDetail` in `ToolException.cs`
  is an untested shape), leave the three records in namespace `RoslynSentinel.Common.SnippetMatching` in the root folder, add a `// TODO` at the first
  record and an INBOX line, and continue; Step 5 is unaffected.
- Effort: M

### Step 4 - Create the project and its wiring
- Files: `RoslynSentinel.Engines.Core/RoslynSentinel.Engines.Core.csproj` (new), `RoslynSentinel.slnx`, `RoslynSentinel.Common/RoslynSentinel.Common.csproj`.
- Depends on: Step 3 (single lane)
- Effect: text-neutral.
- Lane: the dispatcher itself (MCP calls plus non-C# edits).
- Change, in this order: (a) `CreateProject(projectName: "RoslynSentinel.Engines.Core", projectType: "classlib")`. (b) Edit the new csproj to the
  conventions of `RoslynSentinel.Engines.Basic.csproj`: `net10.0`, `ImplicitUsings`, `Nullable`, `Configurations` `Debug;Release;DebugVisualStudio`,
  `<Using Include="RoslynSentinel.Common" />`, one `ProjectReference` to Common, and `<InternalsVisibleTo Include="RoslynSentinel.Tests" />` (new content,
  not copied: the area's tests live there). (c) Add `<InternalsVisibleTo Include="RoslynSentinel.Engines.Core" />` to the list in the Common csproj
  (`:29-43`). (d) Check `RoslynSentinel.slnx`: the tool runs `dotnet sln add`; move the new `<Project Path=... />` line next to the other `Engines.*`
  lines (`:12-13`) if it landed elsewhere. (e) `LoadSolution(forceReload: true)`. (f) `DeleteFile` the template `RoslynSentinel.Engines.Core/Class1.cs`
  (after the reload, so it is a loaded document).
- Done when: solution `Build` 0 errors with the empty project; `McpServerStatus` reports the solution loaded with one more project than before.
- Effort: S

### Step 5 - Move the area across the project edge
- Files: the 17 files of Appendix A marked "moves" -> `RoslynSentinel.Engines.Core/SnippetMatching/`; the csproj of every consumer project
  (`Engines.Basic`, `Engines.Advanced`, `Tools.Basic`, `Tests`, `Tests.Tools.Basic`, `Tests.Server`, `Tests.Advanced`, and any other the dry run still
  names); `RoslynSentinel.Tests.Basic/ErrorPolicyKindRatchetTests.cs`; `RoslynSentinel.Engines.Basic/MoveSetEdgeAnalyzer.cs` and the `MoveMember` tool
  code (one `// TODO` marker each for the two tool gaps in INBOX).
- Depends on: Step 4
- Effect: text-neutral.
- Lane: the dispatcher for the csproj wiring (5a); `implementer-senior` for the move, the ratchet edit and the two markers (5b).
- Change 5a (wiring, per the splitting reference's checklist): add `<ProjectReference Include="..\RoslynSentinel.Engines.Core\RoslynSentinel.Engines.Core.csproj" />`
  to each consumer csproj listed above with Edit, `LoadSolution(forceReload: true)`, solution `Build` 0 errors (the project is still empty), commit.
  Then dry-run the move with both toggles false; every project that still shows errors or an edge fact gets the same reference line and the dry run
  is repeated, until it reports no error and no missing edge. More than 12 consumer projects in total, or an error that a reference line does not
  explain, is a decision point.
- Change 5b: `MoveFile(moves: [{ sourcePath: "RoslynSentinel.Common/SnippetMatching", destinationPath: "RoslynSentinel.Engines.Core/SnippetMatching" },
  { sourcePath: "RoslynSentinel.Common/SnippetMatchOutcome.cs", destinationPath: "RoslynSentinel.Engines.Core/SnippetMatching/SnippetMatchOutcome.cs" },
  { sourcePath: "RoslynSentinel.Common/SnippetSpan.cs", destinationPath: "RoslynSentinel.Engines.Core/SnippetMatching/SnippetSpan.cs" }],
  namespaceHandling: keep, addProjectReferences: false, addInternalsVisibleTo: false)`, the real call of the dry run that 5a ended on (the toggles
  stay false because 5a and Step 4 already wrote every edge, so no csproj line is written twice). In the same slice add
  `"RoslynSentinel.Engines.Core"` to `ErrorPolicyKindRatchetTests.ScannedProjects` (`:26`), and one comment line at each tool gap:
  `// TODO the gate adds only direct edges; projects that reach the destination through another project still fail (INBOX 2026-11-27)` above
  `AugmentBaseline` (`MoveSetEdgeAnalyzer.cs:67`), and `// TODO a record is refused as the source type (INBOX 2026-11-27)` where `MoveMember` resolves
  its source type.
- Done when: `Build` 0 errors.
- Dispatcher checks: `RoslynSentinel.Common/SnippetMatching/` holds no file and the empty folder is removed (`Test-Path` then reports false);
  `RoslynSentinel.Engines.Core/SnippetMatching/` lists 17 files; the filtered run passes.
- Decision point: a dry-run fact that needs more than a reference line, an `InternalsVisibleTo` line or a package line stops the step.
- Effort: M

### Step 6 - Rename the namespace
- Files: the 17 moved files; the 36 files with `using RoslynSentinel.Common.SnippetMatching;`; the users of `SnippetMatchOutcome`, `SnippetSpan` and
  `ToolMatchedLooserTierException`, which are in the global-using namespace `RoslynSentinel.Common` today (the dry run gives the file count).
- Depends on: Step 5
- Effect: text-neutral.
- Lane: `implementer-senior`, one operation plus one comment edit.
- Change: `ChangeNamespace(filePath: "RoslynSentinel.Engines.Core/SnippetMatching", newNamespace: "RoslynSentinel.Engines.Core.SnippetMatching")`, dry
  run first. Then correct the one comment the tool does not rewrite (`SnippetMatchResult.cs:10`, `<c>RoslynSentinel.Common.SnippetMatching</c>`) with
  `ReplaceSnippet`. Before the commit read `Git(operation: diff)`: consumers should gain or swap a using line; the four existing fully qualified uses
  (`AdvancedStructuralEngine.cs:1299, 1315`, `ModernizationEngine.cs:131`, `MemberRefactoringEngine.cs:458`) are rewritten in place. A qualified
  `RoslynSentinel.Engines.Core.SnippetMatching.` name on any other code line means the tool qualified a simple name instead of adding a using:
  `UndoChange`, stop and report with the count.
- Done when: `Build` 0 errors.
- Dispatcher checks: `Search(mode: text, query: "RoslynSentinel\\.Common\\.SnippetMatching", includeTests: true)` returns nothing; the filtered run passes.
- Effort: M

### Step 7 - Docs and guidance
- Files: `CLAUDE.md` (Architecture bullets, lines 168 and 172-173), `docs/current/references/reference_architecture_map.md` (Layers table),
  `docs/current/references/reference_not_found_error_inventory.md` (line 122, the one mention that carries the folder path),
  `docs/current/references/reference_splitting_functionality_out_of_common.md`, `docs/generated/*`,
  [plan_roadmap_layering_and_project_splits.md](../plan_roadmap_layering_and_project_splits.md) (Phase 2 row: 17 files moved, not 18).
- Depends on: Step 6
- Effect: text-neutral.
- Lane: the dispatcher itself.
- Change: CLAUDE.md gets exactly the wording of Q47 (the sentence to replace wraps across lines 172-173). Architecture map: an `Engines.Core` row with
  the admission rule of Q46. Splitting reference: this run's numbers for the first cross-project multi-file `MoveFile`, the lesson "expect masked
  coupling; record each instance as a finding with its own step or proposal", the leftover-folder gotcha, and the corrected `TestInstanceLauncher`
  anchors. Regenerate `docs/generated/` (`scripts/Generate-ArchitectureMap.ps1`, `pwsh -NoProfile -File scripts/Get-TodoMarkers.ps1`). Run
  `scripts/Test-GuidanceHygiene.ps1` and report the CLAUDE.md size against its 15 KB budget.
- Done when: the hygiene script reports no new finding and the regenerated files are committed (`ArchitectureDocFreshnessTests` runs in Step 8).
- Effort: S

### Step 8 - Verify and close
- Files: this plan, `docs/current/TODO.md`, `docs/current/CLOSED.md`.
- Depends on: Step 7
- Effect: text-neutral.
- Lane: the dispatcher itself.
- Change: one full run at the Step 7 commit against the baseline (P9). Outcome classes: **adopted** (0 failures, test count equal to the baseline);
  **counts differ** (fewer = a fixture stopped being discovered, more = a duplicate: trace before closing); **name pin** (a failure from a namespace or
  type name held as text: report with `file:line` and stop); **load flake** (re-run that fixture alone first); **other** (report as new). On adopted:
  Status IMPLEMENTED with hashes, `ProjectDoc(action: close)`, closing commit with `Closes:`.
- Done when: run id, commit, counts and failure set are in the Status line; the plan is in `plans/closed/`.
- Effort: S

## Out of scope

- Removing tool-surface text from the area (roadmap Phase 5) and renaming the `ToolException` family (Phase 3).
- A test project for the area (P6) and any change to test namespaces.
- `LineBlockAligner`, `DiffEngine` and the rest of edit application.
- `ErrorPolicyDocument` and `PromptTokenEstimator` (roadmap Phase 0.2, dropped by Q45).

## Risks and open decisions

Q45, Q46 and Q47 are settled (Decision). What remains is unverified fact, each with its check:

1. **The list of Common types the area uses is a name search, not a compile.** An internal Common type missed by it shows as a `MoveFile` dry-run fact
   in Step 5 (resolved by the `InternalsVisibleTo` line Step 4 adds, or a decision point).
2. **Untested tool shapes, each with a fallback or stop rule in its step:** `MoveMember` with a record as the source type (Step 2); `ChangeNamespace` on
   a type whose references are relative-qualified (Step 3); `DeleteFile` on a template file of a just-created project (Step 4, after the reload);
   `dotnet sln add` on the `.slnx` (Step 4 checks the line).
3. **Active area.** 14 commits in two weeks; `plan_unknown_parameter_rejection_reduction.md` (IN PROGRESS, its last step left) names
   `SnippetFailureDetail`; INBOX has an open line asking for a typed closest-window score in `SnippetErrorFormatter`. Heads-up before Step 1 and
   Step 5; no hold.
4. **`StaleReadPolicyRatchetTests.IsProductionProject` (`:164`) excludes Common by name**, so the area is newly scanned there. It has no
   `ApplyProposedChangesAsync` call, so the ratchet should pass; the full run of Step 8 is the proof.
5. **Namespace shadowing is checked:** no type, member or alias named `Core` or `SnippetMatching` is declared in the solution, tests included, and no
   code in `Engines.*` or `Tools.*` uses a simple name `Core` (readiness review, 2026-11-27).

## Tests and measurement

No test is added: every step is text-neutral and the area's behaviour is pinned by its existing classes (Appendix A, about 272 `[Test]` methods, all
in-process), run whole. Each behaviour of this plan maps to an existing check:

| Behaviour | Check (run by the dispatcher) | Pass criterion |
| --- | --- | --- |
| The area still works after each prep step and after the move | the filtered run of Execution rules (the area's own classes in `RoslynSentinel.Tests`, `SnippetResolverCrossAssemblyTests`, and the tool-level snippet classes its name filter also matches) | 0 failures; the run's test count does not drop from one step to the next |
| Error-policy ratchet still covers the moved throw sites | `ErrorPolicyKindRatchetTests` (in the filtered run) | passes with `RoslynSentinel.Engines.Core` in `ScannedProjects` |
| A stale server is still detected for the new project | `ServerBinaryStalenessTests` (filtered run); `TestInstanceLauncherTests` (OutOfProcess: full run only) | pass unchanged (prefix match) |
| Generated architecture docs match the solution | `ArchitectureDocFreshnessTests` (`RoslynSentinel.Tests.Server`, full run) | passes after regeneration |
| Nothing else moved | one full run at the final commit against baseline P9 | 0 failures, test count equal to the baseline |

Size: `SnippetFailureDetail.From` 9 call sites in 1 file (symbol); `SnippetClosestWindow` 27 references in 10 files, `SnippetFilterStep` 16 in 4
(symbol); namespace rename 36 files with the using plus the users of the two root types and the exception (the dry run gives the exact figure).

## Appendix A - Inventory (2026-11-27)

| File (under `RoslynSentinel.Common/`) | Lines | Types | Fate |
| --- | --- | --- | --- |
| `SnippetMatching/ISnippetMatcher.cs` | 25 | `ISnippetMatcher` (internal) | moves |
| `SnippetMatching/NewContentIndenter.cs` | 647 | `NewContentIndentResult`, `NewContentIndenter` | moves |
| `SnippetMatching/SnippetCandidateFinder.cs` | 361 | `ClosestWindowSearch`, `SnippetCandidateFinder` (internal) | moves |
| `SnippetMatching/SnippetErrorFormatter.cs` | 967 | `SnippetErrorFormatter` (public static class); `ToolMatchedLooserTierException` (split out in Step 2) | moves |
| `SnippetMatching/ToolMatchedLooserTierException.cs` (new in Step 2) | - | `ToolMatchedLooserTierException` | moves |
| `SnippetMatching/SnippetFailureDetail.cs` | 41 | `SnippetFailureDetail` | stays (Common root, Step 3) |
| `SnippetMatching/SnippetLookup.cs` | 98 | `SnippetLookup` | moves |
| `SnippetMatching/SnippetMatchers.cs` | 240 | `SnippetMatcherSupport`, `ExactMatcher`, `LineEndingMatcher`, `CollapsedWhitespaceMatcher`, `IndentationMatcher` (internal) | moves |
| `SnippetMatching/SnippetMatchResult.cs` | 346 | `SnippetMatch`, `SnippetDifferenceKind`, `SnippetDifference`, `SnippetNearMatch`, `SnippetMatchResult`; `SnippetClosestWindow`, `SnippetFilterStep` (split out in Step 1) | moves |
| `SnippetMatching/SnippetClosestWindow.cs`, `SnippetFilterStep.cs` (new in Step 1) | - | the two records | stay (Common root, Step 3) |
| `SnippetMatching/SnippetNodeKindError.cs` | 69 | `SnippetNodeKindError` | moves |
| `SnippetMatching/SnippetResolver.cs` | 210 | `SnippetResolver` | moves |
| `SnippetMatching/SnippetSelectors.cs` | 188 | `SnippetSelectors` (record), `SnippetSelectorFilter` (internal) | moves |
| `SnippetMatching/SnippetStages.cs` | 211 | `SnippetStages` (internal) | moves |
| `SnippetMatching/SnippetTokenEquivalence.cs` | 302 | `SnippetTokenEquivalence` | moves |
| `SnippetMatching/ToolCallMarkup.cs` | 35 | `ToolCallMarkup` | moves |
| `SnippetMatching/WhitespaceTolerance.cs` | 84 | `WhitespaceTolerance`, `SnippetCaseMode` | moves |
| `SnippetMatchOutcome.cs` | 39 | `SnippetMatchOutcome` | moves |
| `SnippetSpan.cs` | 7 | `SnippetSpan` | moves |
| `LineBlockAligner.cs` | 78 | `LineBlockAligner`, `Alignment` (internal) | stays |

After Step 3 the folder `RoslynSentinel.Common/SnippetMatching/` holds 15 files (the 14 original files marked "moves" plus
`ToolMatchedLooserTierException.cs`); with the two root files that is the 17 files of Step 5.

Consumers (text): Engines.Basic `SyntaxTargetResolver`, `MsToolAugmentEngine`, `BasicRefactoringEngine`, `SemanticRefactoringEngine`,
`SymbolNavigationEngine`, `ImpactAnalyzer`, `SnippetSymbolLocator`, `StructuralRefinementEngine`, `SyntaxUpgradeEngine`, `DiscoveryEngine`,
`MemberRefactoringEngine`; Engines.Advanced `AdvancedStructuralEngine`, `MappingEngine`, `CodeGenerationEngine`, `ModernizationEngine`; Tools.Basic
`WorkspaceFileEditImpl`.

Tests (all stay; `[Test]` counts by text): in `RoslynSentinel.Tests`: `SnippetErrorFormatterTests` 41, `SnippetCandidateTests` 32,
`NewContentIndenterTests` 25, `SnippetSelectorTests` 25, `ContextHelperTests` 24, `SnippetResolverTests` 20, `SnippetTokenEquivalenceTests` 20,
`ContextHelperSelectorTests` 19, `SnippetMatcherTests` 17, `SnippetMatchResultTests` 17, `SnippetLookupTests` 12, `ContextHelperLineAfterTests` 7,
`LineBlockAlignerTests` 5, `SnippetNodeKindErrorTests` 3, `ContextHelperIndentationDiagnosisTests` 2; in `RoslynSentinel.Tests.Tools.Basic`:
`SnippetResolverCrossAssemblyTests` 3. Users of the area in mixed test files: `Tests.Tools.Basic/RegressionTests.cs`, `Tests.Server/CallToolFailureFilterTests.cs`,
`Tests.Server/SchemaLeanProfileTests.cs`, `Tests.Advanced/BugFixTests.cs`. Tests in `RoslynSentinel.Tests` use internals (`SnippetMatchResult.Create`,
`MaxMessageChars`, the matcher classes), hence its `InternalsVisibleTo`.
