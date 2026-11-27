# Plan: Create RoslynSentinel.Engines.Core and move snippet matching into it

**Status:** DRAFT 2026-11-27. plan-design phase 8: 0 of 3 owner questions open; 9 planner defaults; independent readiness review running.

## Problem

Phase 2 of [plan_roadmap_layering_and_project_splits.md](plan_roadmap_layering_and_project_splits.md): the first mechanical split of engine-like logic
out of `RoslynSentinel.Common`. Owner decision Q42 (2026-11-27): snippet matching goes into a new `RoslynSentinel.Engines.Core`, the shared engine
project of Q40. The route and checklists are in
[reference_splitting_functionality_out_of_common.md](../references/reference_splitting_functionality_out_of_common.md).

Grounding (2026-11-27, HEAD 39a209c2; "symbol" = Roslyn references, "text" = name search):

- **The area:** `RoslynSentinel.Common/SnippetMatching/` (15 files, 3,821 lines, namespace `RoslynSentinel.Common.SnippetMatching`) plus the root files
  `SnippetMatchOutcome.cs` (39) and `SnippetSpan.cs` (7), both in namespace `RoslynSentinel.Common` and used inside Common only by the area (symbol).
  Inventory in Appendix A.
- **Three uses from Common code that stays:**
  1. `ToolException.cs:71, 89` (`ToolNotFoundException`) and `:183, 193` (`ToolAmbiguousMatchException`) carry `SnippetMatching.SnippetFailureDetail?`.
     `SnippetFailureDetail` is a data record whose members use the data records `SnippetClosestWindow` and `SnippetFilterStep`
     (`SnippetMatchResult.cs:85, 94`); its only tie to the moving code is the factory `SnippetFailureDetail.From(SnippetMatchResult)`
     (`SnippetFailureDetail.cs:24`), called from 9 sites, all in `SnippetErrorFormatter.cs` (symbol).
  2. `DiffEngine.cs:313, 324` uses the internal `LineBlockAligner` (`RoslynSentinel.Common/LineBlockAligner.cs`), which `SnippetCandidateFinder.cs:31, 164`
     also uses.
  3. `ContextHelper.cs:5` has a dead `using RoslynSentinel.Common.SnippetMatching;` (no snippet type used in the file).
- **A second namespace in one file:** `SnippetErrorFormatter.cs:5-22` declares `ToolMatchedLooserTierException` in namespace `RoslynSentinel.Common`;
  its only users are the formatter (`:197, :241`) and tests in `RoslynSentinel.Tests` (symbol).
- **What the area uses from Common** (text; the `MoveFile` dry run is the proof): `ToolException`, `ToolNotFoundException`,
  `ToolAmbiguousMatchException`, `ToolErrorCode`, `MissingTargetKinds`, `ToolCallHint` (all public), the internal `LineBlockAligner`; Roslyn packages
  only (they flow through the reference to Common). No MCP SDK, `ToolCallContext`, `ActiveToolSurface` or `*Options` use.
- **Consumers outside Common** (text): Engines.Basic 11 files, Engines.Advanced 4, Tools.Basic 1 (`WorkspaceFileEditImpl.cs`); none in Git, AgentLoop,
  Utilities, Tools.Advanced, Tools.Experimental, Server.Advanced. `using RoslynSentinel.Common.SnippetMatching;` appears in 54 files. No production
  code outside Common uses an internal member of the area; tests in `RoslynSentinel.Tests` do.
- **Tests:** about 269 `[Test]` methods in 15 files of `RoslynSentinel.Tests`, all in-process, plus 3 in
  `Tests.Tools.Basic/SnippetResolverCrossAssemblyTests.cs` (Appendix A). No ratchet or baseline is keyed on `SnippetMatching/` paths.
- **Wiring:** the server-source lists match the `RoslynSentinel.Engines.` prefix (`ServerBinaryStaleness.cs:46-50`, `scripts/server-source-paths.txt:6-12`,
  `TestInstanceLauncher.cs:625`), so they need no edit; `ErrorPolicyKindRatchetTests.ScannedProjects` (`ErrorPolicyKindRatchetTests.cs:26`) is an explicit
  list and does.
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
  - **P3 `SnippetFailureDetail.From` moves to the area** as a static method of `SnippetErrorFormatter` named `ToFailureDetail` (`MoveMember`, then
    `RenameSymbol`; 9 call sites, one file).
  - **P4 `LineBlockAligner` stays in Common** (`DiffEngine` uses it); Common grants `InternalsVisibleTo` to `RoslynSentinel.Engines.Core`. Its test
    class `LineBlockAlignerTests` stays.
  - **P5 `ToolMatchedLooserTierException` travels with the area** in its own file (split out first with `MoveType`), like the build-specific exceptions
    of the Build/RunTest design (its Q31).
  - **P6 tests stay in `RoslynSentinel.Tests`** (in-process and fast, so a test project of their own buys no selection; same principle as Q7 of the
    Build/RunTest design). `Engines.Core` grants `InternalsVisibleTo` to `RoslynSentinel.Tests`.
  - **P7 namespace:** keep during the moves; one `ChangeNamespace` to `RoslynSentinel.Engines.Core.SnippetMatching` afterwards (folder
    `RoslynSentinel.Engines.Core/SnippetMatching/`).
  - **P8 `SnippetMatchOutcome` and `SnippetSpan` move** with the area into that folder and namespace.
  - **P9 baseline:** full run `cc147f9e-20261127-004211-40584` (12,633 tests, 0 failures) if no `.cs` commit landed since cc147f9e, else a fresh run.
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

## Current design

- Built: `RoslynSentinel.Engines.Core` (new; references Common) holding `SnippetMatching/` (17 files: the 14 that remain of the folder after the DTO
  file leaves, `ToolMatchedLooserTierException.cs`, `SnippetMatchOutcome.cs`, `SnippetSpan.cs`), namespace `RoslynSentinel.Engines.Core.SnippetMatching`.
- Left in Common: `SnippetFailureDetail.cs`, `SnippetClosestWindow.cs` (new file), `SnippetFilterStep.cs` (new file) in the root folder, namespace
  `RoslynSentinel.Common`; `LineBlockAligner.cs`.
- Exceptions to the admission rule (Q46): the area arrives with tool-surface text (Problem); roadmap Phase 5 removes it.
- Simplest version considered (checklist item 7): leave the three DTO records where they are, in a three-file `Common/SnippetMatching/` folder and
  namespace. Rejected: two namespaces named `SnippetMatching` would exist, and P2 costs three small tool calls made while everything is still in one
  project.

## Execution rules

- C# changes only through the RoslynSentinel MCP tools; `.csproj`, `.slnx`, `.md` with Edit. One `Build` or `RunTest` at a time, subagents included.
- Gated tools: `MoveType`, `ChangeNamespace` need `McpToolsetControl(toolSet: moveExtract, enabled: true)`; `CreateProject` needs `projectAdmin`.
  `MoveFile`, `MoveMember`, `RenameSymbol`, `UsingDirective`, `DeleteFile` are core tools.
- A `MoveFile` set call or a `ChangeNamespace` call is ONE semantic operation over the sweep tier by design (`.claude/implementer-tiers.json`): lane
  `implementer-senior`, and the brief says so. Dry run before every real call; record every `UndoChange` id.
- `ChangeNamespace` has no diff preview: after a real call and before the commit, read `Git(operation: diff)` for the files whose line counts are more
  than a using or namespace line. A refused call or an unexpected rewrite is `UndoChange`, `TOOL-GAP:` and stop; no manual edits stand in for a tool.
- `MoveType` reformats its source file and has left stray marker comments before: check `Git(operation: diff, stat: true)` and the file outline after it.
- A folder `MoveFile` leaves the emptied source folder on disk: remove the empty folder afterwards.
- Other sessions share the tree and this area changes often. Before each real call: `Git(operation: status)`; a dirty `.cs` file in the affected set
  is resolved with its owner first (`peer-coordination` skill). The dispatching session sends the heads-up messages.
- Tests never block a slice: the dispatcher runs the named classes filtered (`scripts/Invoke-WorktreeTests.ps1 ... -Suite fast -Filter ... -WithRatchets`,
  parameters re-read from the script) and takes one full run at the end.
- Commit each slice at a compile-green point with `Git` (scope `listed`), trailers `Plan: plan_move_snippet_matching_into_engines_core` and
  `Co-Authored-By`; the last commit carries `Closes:`. The MCP server is not restarted by the run.
- The owner is present: a decision point stops the step with `DECISION NEEDED:` and the evidence.

## Steps

### Lane map

Single lane (every step touches the same folder). Steps 1 to 3 are in Common only and each ends compile-green; Step 5 is the only cross-project move.

### Step 1 - Split the two DTO records out of SnippetMatchResult.cs; drop the dead using
- Files: `RoslynSentinel.Common/SnippetMatching/SnippetMatchResult.cs`; `SnippetClosestWindow.cs`, `SnippetFilterStep.cs` (new, same folder);
  `RoslynSentinel.Common/ContextHelper.cs`.
- Depends on: none
- Effect: text-neutral.
- Lane: `implementer`.
- Change: `MoveType` for `SnippetClosestWindow` and for `SnippetFilterStep` (one file per type, namespace unchanged). Remove line 5 of `ContextHelper.cs`
  (`using RoslynSentinel.Common.SnippetMatching;`) with `UsingDirective`.
- Done when: `Build` 0 errors; `GetFileOutline` of `SnippetMatchResult.cs` no longer lists the two records; the diff of `SnippetMatchResult.cs` shows no
  change other than the two removed records (whitespace-only lines are reported).
- Effort: S

### Step 2 - Give the formatter its own exception file and the failure-detail factory
- Files: `RoslynSentinel.Common/SnippetMatching/SnippetErrorFormatter.cs`, `SnippetFailureDetail.cs`; `ToolMatchedLooserTierException.cs` (new, same folder).
- Depends on: Step 1 (same folder; keep one writer at a time)
- Effect: text-neutral.
- Lane: `implementer`.
- Change: `MoveType` for `ToolMatchedLooserTierException` (it keeps namespace `RoslynSentinel.Common` for now). `MoveMember` of the static method
  `From` from `SnippetFailureDetail` to `SnippetErrorFormatter` (call sites updated by the tool: 9, all in the formatter), then `RenameSymbol` of the
  moved method to `ToFailureDetail`.
- Done when: `Build` 0 errors; `Search(mode: references)` on `SnippetFailureDetail` shows no reference from `SnippetFailureDetail.cs` to
  `SnippetMatchResult`; `SnippetErrorFormatter.cs` declares one namespace.
- Effort: S

### Step 3 - Move the three DTO records to Common's root namespace
- Files: `SnippetFailureDetail.cs`, `SnippetClosestWindow.cs`, `SnippetFilterStep.cs` (moved to `RoslynSentinel.Common/`); `ToolException.cs` and the
  other referencing files the tool rewrites (measured: `SnippetClosestWindow` 27 references in 10 files, `SnippetFilterStep` 16 in 4).
- Depends on: Step 2
- Effect: text-neutral.
- Lane: `implementer-senior` (four operations, one slice).
- Change: `MoveFile(moves: [the three files -> RoslynSentinel.Common/<same name>], namespaceHandling: keep)` (same project), then
  `ChangeNamespace(filePath: <file>, newNamespace: "RoslynSentinel.Common")` for each of the three, dry run first.
- Done when: `Build` 0 errors; `RoslynSentinel.Common/SnippetMatching/` holds exactly the 14 files of Appendix A marked "moves" plus
  `ToolMatchedLooserTierException.cs`; `Search(mode: text, query: "SnippetMatching\\.SnippetFailureDetail")` returns nothing.
- Decision point: if a dry run does not validate, leave the three records in namespace `RoslynSentinel.Common.SnippetMatching` in the root folder,
  record a `// TODO` and an INBOX line, and continue; Step 5 then moves files by explicit list as planned.
- Effort: M

### Step 4 - Create the project and its wiring
- Files: `RoslynSentinel.Engines.Core/RoslynSentinel.Engines.Core.csproj` (new), `RoslynSentinel.slnx`, `RoslynSentinel.Common/RoslynSentinel.Common.csproj`.
- Depends on: none (pairs with Steps 1 to 3 only if a second lane exists; default is after Step 3)
- Effect: text-neutral.
- Lane: the dispatcher itself (one MCP call plus non-C# edits).
- Change: `CreateProject(projectName: "RoslynSentinel.Engines.Core", projectType: "classlib")`; `DeleteFile` the template `Class1.cs`; edit the csproj to
  the conventions of `RoslynSentinel.Engines.Basic.csproj` (`net10.0`, `ImplicitUsings`, `Nullable`, `Configurations` `Debug;Release;DebugVisualStudio`,
  `<Using Include="RoslynSentinel.Common" />`, `ProjectReference` to Common only, `InternalsVisibleTo` for `RoslynSentinel.Tests`); add
  `<InternalsVisibleTo Include="RoslynSentinel.Engines.Core" />` to the Common csproj; confirm the slnx entry (`dotnet sln add` by the tool) sits with
  the other `Engines.*` lines; `LoadSolution(forceReload: true)`.
- Done when: solution `Build` 0 errors with the empty project; `McpServerStatus` shows the solution loaded with the new project.
- Effort: S

### Step 5 - Move the area across the project edge
- Files: the 17 files of Appendix A marked "moves" -> `RoslynSentinel.Engines.Core/SnippetMatching/`; csproj files the tool writes
  (`Engines.Basic`, `Engines.Advanced`, `Tools.Basic` references; `InternalsVisibleTo`); `RoslynSentinel.Tests.Basic/ErrorPolicyKindRatchetTests.cs`.
- Depends on: Steps 3 and 4
- Effect: text-neutral.
- Lane: `implementer-senior`, one operation plus the ratchet edit.
- Change: `MoveFile(moves: [{ sourcePath: "RoslynSentinel.Common/SnippetMatching", destinationPath: "RoslynSentinel.Engines.Core/SnippetMatching" },
  { SnippetMatchOutcome.cs }, { SnippetSpan.cs } -> the same folder], namespaceHandling: keep, addProjectReferences: true, addInternalsVisibleTo: true,
  dryRun: true)`; act on every fact; then the real call. Add `"RoslynSentinel.Engines.Core"` to `ErrorPolicyKindRatchetTests.ScannedProjects` (`:26`) in
  the same slice (8 `ToolNotFoundException` sites leave a scanned project; the floor is `MinimumKindSites`). Remove the emptied
  `RoslynSentinel.Common/SnippetMatching/` folder.
- Done when: `Build` 0 errors; `Glob` `RoslynSentinel.Common/SnippetMatching/**` returns nothing; the dispatcher's filtered run of
  `ErrorPolicyKindRatchetTests`, `ServerBinaryStalenessTests`, `TestInstanceLauncherTests` and the `Snippet*Tests`, `ContextHelper*Tests`,
  `NewContentIndenterTests`, `LineBlockAlignerTests` classes passes.
- Decision point: a fact the dry run reports that needs more than a reference, an `InternalsVisibleTo` line or a package line stops the step.
- Effort: M

### Step 6 - Rename the namespace
- Files: the 17 moved files and every file with `using RoslynSentinel.Common.SnippetMatching;` (54 by text count) or a use of `SnippetMatchOutcome`,
  `SnippetSpan`, `ToolMatchedLooserTierException`.
- Depends on: Step 5
- Effect: text-neutral.
- Lane: `implementer-senior`, one operation.
- Change: `ChangeNamespace(filePath: "RoslynSentinel.Engines.Core/SnippetMatching", newNamespace: "RoslynSentinel.Engines.Core.SnippetMatching")`, dry
  run first. Fully qualified uses exist at `AdvancedStructuralEngine.cs:1299, 1315`, `ModernizationEngine.cs:131`, `MemberRefactoringEngine.cs:458`
  (rewritten in place, as the Git rename did).
- Done when: `Build` 0 errors; `Search(mode: text, query: "RoslynSentinel\\.Common\\.SnippetMatching", includeTests: true)` returns nothing; the same
  filtered run as Step 5 passes.
- Effort: M

### Step 7 - Docs and guidance
- Files: `CLAUDE.md` (Architecture bullets), `docs/current/references/reference_architecture_map.md` (Layers table),
  `docs/current/references/reference_not_found_error_inventory.md` (15 path mentions),
  `docs/current/references/reference_splitting_functionality_out_of_common.md`, `docs/generated/*`,
  [plan_roadmap_layering_and_project_splits.md](plan_roadmap_layering_and_project_splits.md) (Phase 2 row).
- Depends on: Step 6
- Effect: text-neutral.
- Lane: the dispatcher itself.
- Change: CLAUDE.md gets the wording of Q47 (owner-approved text only). Architecture map: an `Engines.Core` row with the admission rule of Q46.
  Splitting reference: this run's numbers for the first cross-project multi-file `MoveFile`, the "expect masked coupling; record each instance as a
  finding with its own step or proposal" lesson, and the leftover-folder gotcha. Regenerate `docs/generated/` (`scripts/Generate-ArchitectureMap.ps1`,
  `scripts/Get-TodoMarkers.ps1`). Run `scripts/Test-GuidanceHygiene.ps1` and report the CLAUDE.md size against its 15 KB budget.
- Done when: `ArchitectureDocFreshnessTests` passes; the hygiene script reports no new finding.
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

1. **The list of Common types the area uses is a text check.** An internal Common type missed by it shows as a `MoveFile` dry-run fact in Step 5
   (resolved by the `InternalsVisibleTo` line Step 4 already adds, or a decision point).
2. **`ChangeNamespace` on a file whose references are relative-qualified** (`SnippetMatching.SnippetFailureDetail` in `ToolException.cs`) is untested;
   Step 3's dry run decides and has a fallback.
3. **Active area.** 14 commits in two weeks; `plan_unknown_parameter_rejection_reduction.md` (IN PROGRESS, Step 17 left) names `SnippetFailureDetail`;
   INBOX has an open line asking for a typed closest-window score in `SnippetErrorFormatter`. Heads-up before Step 1 and Step 5; no hold.
4. **A simple name `Core`** inside `RoslynSentinel.Engines.*` code would bind to the new namespace segment. A text sweep found none in production code
   but reviewed only part of its matches; the Step 6 dry run and build (the first point at which the namespace `RoslynSentinel.Engines.Core` exists)
   are the proof.

## Tests and measurement

No test is added: every step is text-neutral and the area's behaviour is pinned by its existing classes (Appendix A, about 272 `[Test]` methods, all
in-process). Each behaviour of this plan maps to an existing check:

| Behaviour | Check (run by the dispatcher) | Pass criterion |
| --- | --- | --- |
| The area still works after each prep step and after the move | `Snippet*Tests`, `ContextHelper*Tests`, `NewContentIndenterTests`, `LineBlockAlignerTests` (`RoslynSentinel.Tests`), `SnippetResolverCrossAssemblyTests` (`Tests.Tools.Basic`) | all pass, same counts as before the step |
| Error-policy ratchet still covers the moved throw sites | `ErrorPolicyKindRatchetTests` | passes with `RoslynSentinel.Engines.Core` in `ScannedProjects` |
| A stale server is still detected for the new project | `ServerBinaryStalenessTests`, `TestInstanceLauncherTests` | pass unchanged (prefix match) |
| Generated architecture docs match the solution | `ArchitectureDocFreshnessTests` | passes after regeneration |
| Nothing else moved | one full run at the final commit against baseline P9 | 0 failures, test count equal to the baseline |

Size: `SnippetFailureDetail.From` 9 call sites in 1 file (symbol); `SnippetClosestWindow` 27 references in 10 files, `SnippetFilterStep` 16 in 4
(symbol); namespace rename about 54 files with the using plus users of the two root types (text; the dry run gives the exact figure).

## Appendix A - Inventory (2026-11-27)

| File (under `RoslynSentinel.Common/`) | Lines | Types | Fate |
| --- | --- | --- | --- |
| `SnippetMatching/ISnippetMatcher.cs` | 25 | `ISnippetMatcher` (internal) | moves |
| `SnippetMatching/NewContentIndenter.cs` | 647 | `NewContentIndentResult`, `NewContentIndenter` | moves |
| `SnippetMatching/SnippetCandidateFinder.cs` | 361 | `ClosestWindowSearch`, `SnippetCandidateFinder` (internal) | moves |
| `SnippetMatching/SnippetErrorFormatter.cs` | 966 | `SnippetErrorFormatter`; `ToolMatchedLooserTierException` (split out in Step 2) | moves |
| `SnippetMatching/ToolMatchedLooserTierException.cs` (new in Step 2) | - | `ToolMatchedLooserTierException` | moves |
| `SnippetMatching/SnippetFailureDetail.cs` | 40 | `SnippetFailureDetail` | stays (Common root, Step 3) |
| `SnippetMatching/SnippetLookup.cs` | 98 | `SnippetLookup` | moves |
| `SnippetMatching/SnippetMatchers.cs` | 240 | `SnippetMatcherSupport`, `ExactMatcher`, `LineEndingMatcher`, `CollapsedWhitespaceMatcher`, `IndentationMatcher` (internal) | moves |
| `SnippetMatching/SnippetMatchResult.cs` | 345 | `SnippetMatch`, `SnippetDifferenceKind`, `SnippetDifference`, `SnippetNearMatch`, `SnippetMatchResult`; `SnippetClosestWindow`, `SnippetFilterStep` (split out in Step 1) | moves |
| `SnippetMatching/SnippetClosestWindow.cs`, `SnippetFilterStep.cs` (new in Step 1) | - | the two records | stay (Common root, Step 3) |
| `SnippetMatching/SnippetNodeKindError.cs` | 69 | `SnippetNodeKindError` | moves |
| `SnippetMatching/SnippetResolver.cs` | 210 | `SnippetResolver` | moves |
| `SnippetMatching/SnippetSelectors.cs` | 188 | `SnippetSelectors`, `SnippetSelectorFilter` (internal) | moves |
| `SnippetMatching/SnippetStages.cs` | 211 | `SnippetStages` (internal) | moves |
| `SnippetMatching/SnippetTokenEquivalence.cs` | 302 | `SnippetTokenEquivalence` | moves |
| `SnippetMatching/ToolCallMarkup.cs` | 35 | `ToolCallMarkup` | moves |
| `SnippetMatching/WhitespaceTolerance.cs` | 84 | `WhitespaceTolerance`, `SnippetCaseMode` | moves |
| `SnippetMatchOutcome.cs` | 39 | `SnippetMatchOutcome` | moves |
| `SnippetSpan.cs` | 7 | `SnippetSpan` | moves |
| `LineBlockAligner.cs` | 78 | `LineBlockAligner`, `Alignment` (internal) | stays |

Consumers (text): Engines.Basic `SyntaxTargetResolver`, `MsToolAugmentEngine`, `BasicRefactoringEngine`, `SemanticRefactoringEngine`,
`SymbolNavigationEngine`, `ImpactAnalyzer`, `SnippetSymbolLocator`, `StructuralRefinementEngine`, `SyntaxUpgradeEngine`, `DiscoveryEngine`,
`MemberRefactoringEngine`; Engines.Advanced `AdvancedStructuralEngine`, `MappingEngine`, `CodeGenerationEngine`, `ModernizationEngine`; Tools.Basic
`WorkspaceFileEditImpl`.

Tests (all stay; `[Test]` counts by text): in `RoslynSentinel.Tests`: `SnippetErrorFormatterTests` 41, `SnippetCandidateTests` 32,
`NewContentIndenterTests` 25, `SnippetSelectorTests` 25, `ContextHelperTests` 24, `SnippetResolverTests` 20, `SnippetTokenEquivalenceTests` 20,
`ContextHelperSelectorTests` 19, `SnippetMatcherTests` 17, `SnippetMatchResultTests` 17, `SnippetLookupTests` 12, `ContextHelperLineAfterTests` 7,
`LineBlockAlignerTests` 5, `SnippetNodeKindErrorTests` 3, `ContextHelperIndentationDiagnosisTests` 2; in `RoslynSentinel.Tests.Tools.Basic`:
`SnippetResolverCrossAssemblyTests` 3. Users of the area in mixed test files: `Tests.Tools.Basic/RegressionTests.cs`, `Tests.Server/CallToolFailureFilterTests.cs`,
`Tests.Server/SchemaLeanProfileTests.cs`, `Tests.Advanced/BugFixTests.cs`. Tests use internals (`SnippetMatchResult.Create`, `MaxMessageChars`, the
matcher classes), hence the `InternalsVisibleTo` for `RoslynSentinel.Tests`; whether `Tests.Tools.Basic` needs one is settled by the Step 5 dry run.
