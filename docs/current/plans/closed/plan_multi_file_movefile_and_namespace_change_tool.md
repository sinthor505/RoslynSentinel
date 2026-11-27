# Plan: multi-file MoveFile (a `moves` list, one compile gate, optional csproj edges) and a `ChangeNamespace` tool

**Status:** IMPLEMENTED 2026-11-25. 9c8c5663..eacd90c8 (Steps 1-28, 30-37; Step 29 skipped, probe green); full suite green at eacd90c8. Implements [proposal_multi_file_movefile_and_namespace_change_tool.md](../../proposals/closed/proposal_multi_file_movefile_and_namespace_change_tool.md) (IMPLEMENTED 2026-11-25). Revised 2026-11-25 with the owner's decisions D12-D16 and an independent review (P5-P16). Commit trailer for every slice: `Plan: plan_multi_file_movefile_and_namespace_change_tool` (the last slice adds `Closes:`).

## Problem

Two gaps, both hit while running [plan_isolate_git_area_into_own_project](plan_isolate_git_area_into_own_project.md)
and both environment gaps (the tool surface had no one-call route, so the run fell back to hand-edited project files):

1. **`MoveFile` moves one file per call, and the compile gate sees one file per call.** A connected cluster across a
   project edge has no valid order: the Git run measured 1391 errors moving `GitResult` before its derived records, 45 for
   `GitProcess`, 178 for one of 8 partial-class files (counts only; per-error lists were not kept). The workaround was 121
   `MoveFile` calls (15-20 s each) plus a hand-written csproj with a temporary link stand-in. Evidence and blocker:
   [blocking_error_movefile_has_no_multi_file_cross_project_move.md](../../blockers/closed/blocking_error_movefile_has_no_multi_file_cross_project_move.md).
   Source today: `MoveFileTool` takes `string sourcePath, string destinationPath` and refuses a directory
   (`RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs:290-433`, directory refusal `:329-331`).
2. **No tool changes a namespace together with its reference sites.** `RenameSymbol` on a namespace renames the whole
   namespace by its last segment; `MoveFile namespaceHandling: update` is declared (`RoslynSentinel.Common/ToolEnums.cs:150-157`)
   and refused; `FixMismatchedNamespacesAsync` (`RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs:20`) edits one
   file and updates no caller. The Git namespace rename is still a `// TODO` at `RoslynSentinel.Git/GitEngine.cs:13` and
   `RoslynSentinel.Git/GitProcess.cs:7`. Blocker:
   [blocking_error_splitprojectbyfolder_is_a_stub.md](../../blockers/closed/blocking_error_splitprojectbyfolder_is_a_stub.md).

The proposal's reasoning, alternatives (link-stand-in recipe, shell `git mv`, `[Obsolete]` forwarders, scoped ledger,
`MoveFiles` as a new tool, Roslyn Features refactoring) and verification expectations are not repeated here; read it for
"why not". What reading the code for planning found (verified 2026-11-25):

- **`GitStatusRenameHints` assumes one pair per `move_file` blob** and would silently lose its definitive rename hints for a
  multi-pair blob (not in the proposal). `ReadMovePair` (`RoslynSentinel.Tools.Basic/GitStatusRenameHints.cs:279-340`) returns null
  for a blob with more than one created item or one source; `ReadMovePairs` (`:167`) returns an unnamed tuple list.
  Worse, `OperationBlobWriter.BuildApplyItems` (`RoslynSentinel.Common/OperationBlobWriter.cs:213-250`) pairs a created destination
  to a deleted source by identical text with `FirstOrDefault`, so two sources with identical text both pair to the first one:
  the stored pairing is wrong, not just ambiguous (Step 17).
- **A csproj write fits the chokepoint and undo, but its reload is fire-and-forget and untracked.** A csproj key in the
  `DocumentChangeSet` passes the compile gate untouched (existing non-document keys are skipped, `ValidationEngine.cs:150-271`), is written
  through `PersistentWorkspaceManager.ApplyProposedChangesAsync` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:2237`) with a pre-image, so one
  blob and one `changeId` undo it. It sets `ApplyChangesResult.NeedsFullReload`, and the `finally` of `ApplyProposedChangesCoreAsync` then starts
  `Task.Run(ReloadWorkspaceFromDiskAsync)` (`:2902-2917`, method at `:3397`). That reload does not call `BeginLoadTracking` (called only by
  `LoadSolutionAsync`, `:875`), so `WaitForLoadAsync` and `SolutionLoadStatus` do not see it; `ApplyOutcome` has no `NeedsFullReload`
  member. See Step 14 and R16.
- **The gate cannot see in-memory project edges.** `ValidateAndApplyHelper` compiles against the workspace's committed solution, and the
  re-validation after a foreign write (`RevalidateAfterForeignWriteAsync`, about `PersistentWorkspaceManager.cs:3007-3018`) uses plain
  `ValidationEngine.ValidateChangesAsync(CurrentSolution, ...)`. D13 settles the design (a tool-level gate, no hot-path signature changes).
- `CopyAndObsoleteCallerRewrite` / `CopyAndObsoleteEdits` need the two changes the proposal claims (confirmed by reading): one source document
  and one type list (`RewriteAsync`, `CopyAndObsoleteCallerRewrite.cs:34` and `:67`; source excluded as a caller at `:105`); the "declares code
  inside the old namespace" qualify-reason at `:306` assumes the old types stay behind. `CopyAndObsoleteEdits.Apply` (`:332`, shared with the
  caller-rewrite pass, commit 1c1c1280) silently skips an overlapping edit (`if (edit.Position < cursor)`, `:341`), which is unsafe for a
  set; `CollectUsings.AddNeeded` excludes only the file's own declarations (`:179`).

Anchors in the proposal found stale (use these): `ToolsetCatalog.cs:51` is `:53` (`SplitProjectByFolder`); `BasicRefactoringEngine.cs:1635` is the
method `RenameSymbolAsync` at `:1618-1671` (Renamer call `:1646`); `ValidationEngine`, `PersistentWorkspaceManager`, `ToolsetCatalog` and
`ErrorPolicyIntentTable` live in `RoslynSentinel.Common`, and `ToolsetControlTools` in `RoslynSentinel.Tools.Basic` (`:13`), not where the
proposal's wording implies. `RoslynSentinel.Engines.Basic` has no subfolders (flat project root), so new engine files go in the root, not in a new folder.

Tracker: TODO.md lines 56 and 61 ("No tool changes a namespace across files", "MoveFile namespaceHandling `update`") should link here; on close they
move to CLOSED.md (this plan does not edit TODO.md). `RoslynSentinel.Tests.Tools.Basic/BatteryToolFactory.cs:11` carries a `// TODO ... once MoveFile
namespaceHandling update exists` that belongs in that TODO.md note too.

## Decision

Copied from the proposal's `## Decision`, settled with the owner 2026-11-25 (implement-plan, phase 2). Where the proposal's Proposal text differs, these win.

- **D1 (Q1, parameter shape): `moves` list only, required.** `MoveFile` drops `sourcePath`/`destinationPath` and takes a
  required `moves` list of `{sourcePath, destinationPath}` pairs; a single move is a list of one. Breaking change on a core
  tool, accepted: the plan carries a caller-sweep slice (tests, eval fixtures, prompts, skills, agent docs, generated
  tables). Not the either/or `batchEdits` shape and not "first pair plus additionalMoves".
- **D2 (folder pairs): a pair may name a folder; no `include` globs.** Kind is derived per pair from disk; file and folder
  pairs may mix in one list. A folder pair moves the source folder's contents to the destination path (merge into an
  existing folder; a same-name file refuses), subfolders preserved. The description states that rule in one line;
  `dryRun` returns the expanded file-by-file pairs and the result lists them. No operation enum (`MoveFiles`/`MoveFolder`):
  it adds no information the tool cannot derive; revisit only if an eval measures weak-model confusion.
- **D3 (Q2, csproj edits): two opt-in booleans, default false: `addProjectReferences` and `addInternalsVisibleTo`.**
  Needed edges are derived by analysis, not a probe: symbols each moved file references (destination must reference
  their projects), reference sites of moved types (their projects must reference the destination), internal symbols
  used across the new boundary (IVT edges). They are added to the in-memory candidate before the single compile
  (`Solution.AddProjectReference`; IVT as an in-memory `[assembly: InternalsVisibleTo]` document); the csproj items are
  written only when the gate passes, under the same `changeId` so `UndoChange` reverts them. A cycle is refused as a fact.
  With a toggle off, each needed edge is reported as a fact naming the edge and the parameter; the tool never widens
  visibility unasked. Package references stay facts (a restore cannot be modelled in memory).
- **D4 (Q3, cap): soft cap 200 files per call**, refusal names splitting; calibrate with a measured 60-file dry run.
- **D5 (Q4, restore): probe, fix only if red.** `LoadSolution` already restores before loading (71c0c512: always with
  `forceReload`, else when a project's assets are missing or stale; `dotnet sln add` alone does not restore, probed
  2026-11-25), yet the Git run hit NU1105 on 2026-11-24. The plan adds an integration test (hand-written csproj that an
  existing project references, `dotnet sln add`, `LoadSolution(forceReload)`); `LoadSolution` is fixed only if it fails.
  The "destination in no loaded project" refusal names `LoadSolution(forceReload)` as the option.
- **D6 (Q5): `ChangeNamespace` first**, a method on `CoreRefactoringTools` in the `moveExtract` toolset (no new tool
  class); `MoveFile namespaceHandling: update` later as a thin composition, refused until then.
- **D7 (Q6): one target namespace per call** (`newNamespace` or `matchFolder: true`); shadowing check either way.
- **D8 (Q7): source csproj items naming a moved folder are reported as "not updated" facts** (text scan), never edited.
- **D9 (Q8): one shared path.** `MoveFileTool`'s inline validations move into one `Engines.Basic` planner used by every
  move; `MoveFileTests` is the regression net.
- **D10 (part 3): keep `SplitProjectByFolder` as the `NotImplemented` stub**, with a code comment naming the wrapper idea
  (compose `CreateProject` + `MoveFile` + `ChangeNamespace`) as an option for later. Not removed.
- **D11 (run): owner away during the run**; mid-run decisions are deferred to the tracker with a recommendation.

Owner decisions added 2026-11-25 (from the plan review; they override the plan text above where they differ):

- **D12 (was R1, 2026-11-25): an awaitable full-reload signal now (option b), not "accept and measure".** The workspace exposes an awaitable
  signal that a post-write full reload is pending, and `ApplyOutcome` carries `ReloadPending`. `MoveFile` itself does NOT block until the reload
  ends (the rejected option c). Verified before designing (see Step 14): `ISolutionProvider.WaitForLoadAsync`/`SolutionLoadStatus` exist
  (`ISolutionProvider.cs:41-51`) and the read entries already wait on them (`PersistentWorkspaceManager.GetCurrentSolutionAsync`, `:2049-2060`, which
  `GetSolutionAsync` delegates to), but only `LoadSolutionAsync` feeds them. Who awaits and through which API is a design call, listed as the
  deferred decision R16 with a recommendation. Step 35a verifies the result rather than deciding it.
- **D13 (was R2, 2026-11-25): option A, a tool-level gate, used only when a D3 toggle is on AND analysis found an edge. HARD CONSTRAINT: no existing
  hot-path method signature changes.**
  (a) The `MoveFile` TOOL (not the static planner) builds `augmented` = committed + `AddProjectReference` + an in-memory `[assembly: InternalsVisibleTo]` document.
  (b) A NEW static `ValidationEngine.ValidateChangesAgainstBaselineAsync(Solution errorBaseline, Solution candidateBase, changes, removePaths, ct)` builds
  the candidate from `candidateBase`, takes the error delta against `errorBaseline` (the committed solution), and expands affected projects via
  `candidateBase`'s dependency graph; the existing static core `ValidateChangesWithCandidateAsync(baseline, ...)` DELEGATES to it as `(baseline, baseline)`
  (body change, signatures unchanged).
  (c) On pass, apply through the existing `ValidateAndApplyAsync(..., preValidatedAgainst: committed)`; only its doc comment widens to "or a candidate derived from it".
  (d) On fail, the refusal uses the helper's rejection block (`ValidateAndApplyHelper.cs:207-227`) extracted into an internal static builder (Step 4, text-neutral, no signature change).
  (e) One automatic retry when the apply is refused because the workspace moved: the committed instance changed so the helper recompiled without edges, or the result is
  `ConcurrentEditRefused` from the foreign-write re-validation. The retry recomputes `augmented` from the new committed solution and recompiles. A second miss returns the
  safe refusal plus a fact ("the workspace changed during the call; the check ran without the added references; a retry recomputes them").
  (f) An in-process lease with a bounded, configurable wait. The edge-gate call holds it from its compile to its apply, keyed by call via `ToolCallContext.Current`
  (AsyncLocal). The chokepoint BODY (`ApplyProposedChangesAsync`, no signature change) makes other calls' writes in this server wait up to the bound, then proceed; the holder's own
  apply passes through. Do not hold `_solutionLock` or `CrossProcessWriteLock` across the compile (rule (c) in `CrossProcessWriteLock.cs`; `_solutionLock` is not re-entrant).
  Folds of external disk changes are not gated; the retry covers them. Peer sessions are covered by the `HOLD` protocol.
- **D14 (was R10, 2026-11-25): confirmed.** A move into a folder inside no loaded project is refused, single file included (`.cs` files; a non-`.cs` file under no project still moves).
- **D15 (2026-11-25): schema budget.** `ClaudeLeanSchemaBudgetTests` (`RoslynSentinel.Tests.Server`, `AdvancedTotalBudget` `:44` and `BasicTotalBudget` `:49`, both 49000 today, about 400
  headroom; `MoveFile` is 1053 chars today) is raised in the Step 13, Step 26 and Step 32 slices by the measured growth + 300, rounded up to the next 500, with the measurement in the
  comment line of the raise (the file's existing convention). Pre-authorized: no per-raise approval needed. Added to the ratchets of those steps and of Step 36.
- **D16 (2026-11-25): the partial-type rule for `keep` moves is dropped.** The compile gate catches a cross-project partial split; a same-project part move stays legal. (`ChangeNamespace`
  keeps its own rule: all parts in the set.)

Plan-level decisions made while writing and from the independent review (planner's and reviewer's, not the owner's; reviewable):

- **P1. Shared-core generalisation is additive overloads in place** (`CopyAndObsoleteCallerRewrite`, `CopyAndObsoleteEdits`, `CopyAndObsoleteMigration`), not moving about 20 private statics. A
  later optional `RenameSymbol` of the classes to a neutral name is a single call and is out of scope.
- **P2. Facts are typed, not text.** The planner collects `MoveFact(Kind, Subject, ...)` values; the tool lifts them to wire `Finding`s. Tests assert on `MoveFactKind`, never on wording.
- **P3. Core API is per-file `NamespaceMove(path, oldNamespace, newNamespace)`**, so D7's single target is the degenerate case.
- **P4. Test call sites are funnelled through one helper first** (Steps 2-3, text-neutral) so the breaking D1 change edits one helper.
- **P5. The existing tests are the compatibility contract for Step 13.** Only `MoveFileTests.MoveFile_DirectorySource_FailsAsNotAFileAsync` (`:274-287`) changes (it asserts D2 folder behaviour
  instead). Every other refusal keeps its legacy `ErrorCode` and message fragment: `NotFound` "does not exist on disk"; `TargetIneligible` "NUL byte" and "not valid UTF-8"; `InvalidArgument`
  "resolve to the same path" and "already exists as a file/directory"; `InvalidArgument`/`NotImplemented` "only 'keep' is supported". `MoveFileCopyAndObsoleteTests.cs:179`
  (`TargetIneligible`, "partial", "Split.Other.cs") stays, so the planner must not preempt `copyAndObsolete`'s own checks: the planner is two-stage (Step 7 expansion and eligibility; Step 10
  project check and facts) and the tool runs the `copyAndObsolete` branch between them, so the engine keeps its own no-loaded-project text (`CopyAndObsoleteChecks.cs:51`, `InvalidArgument`).
  Per file the planner records the first applicable reason in the legacy order (same path, missing, destination exists, binary, not UTF-8).
- **P6. Each `MoveOffenderReason` maps to one `ErrorCode`** (a static `MoveOffender.ErrorCodeOf(...)`, one definition): `Missing` -> `NotFound`; `Binary`, `NotUtf8` -> `TargetIneligible`;
  `SamePath`, `DestinationExists`, `DuplicateDestination`, `OutsideLoadedProject`, `OverCap` -> `InvalidArgument`. A refusal with several offenders uses the shared code when all agree, else `TargetIneligible`.
- **P7. The eligibility refusal is a typed exception, not a result.** The planner throws `MoveSetRefusedException : ToolException` (new, `Engines.Basic`) carrying the offender list as `StructuredDetail`
  (pattern: `ToolBatchEditException`, `ToolException.cs:420-436`, and `DiffApplyException`, `:249-262`); the tool's existing outer catch maps it through `ToolErrorMapper.ToResultError`. There is no
  `MoveSetPlanResult.Refused` (dto-conventions section 4).
- **P8. (was R5) `ChangeNamespace` extends `CallerRewriteResult`** (positional record `(Files, Facts, Refusal)`, `CopyAndObsoleteCallerRewriteResult.cs:29`) by one defaulted property
  `IReadOnlyList<DanglingUsing>? DanglingUsings = null`; every changed file, set member or caller, is a `CallerFileRewrite`; the overlap refusal rides `Refusal.StructuredDetail` as `EditOverlap` items.
  Existing constructions stay valid. No `ChangeNamespacePlanResult`.
- **P9. (was R6) List items carry their schema text with `[BatchItemField(description, Required = ...)]`** on the `FileMovePair` properties (precedent: `ModifierEdit`, `BatchTypes.cs:599-630`;
  applied by `McpToolSchemaPatcher.ApplyBatchItemFields`, `:297`). `ApplyConsumesTags` (`:413`) tags top-level parameters only, so the `[Consumes]`/`[ExternalInputRequired]` tags that sit on
  today's string parameters are not carried by `moves`; the tool validates each element itself and comments the deviation. `dryRun` follows `[Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false` (`CoreRefactoringTools.cs:422`).
- **P10. The D5 probe also covers `LoadSolution` WITHOUT `forceReload`** (`RestoreBeforeLoadAsync` restores only when `AnalyzeRestoreState` flags), reusing `RunDotnet` from `LoadSolutionForceReloadRestoreIntegrationTests`.
- **P11. Goldens and `docs/generated` are regenerated after Steps 13, 26 and 32**, not only at the end, so the parity tests are red for at most one slice.
- **P12. The edge analyzer (Step 24) is `implementer-senior`**, covers the reverse IVT direction (remaining code in A that uses types that became internal in B means B grants A), and short-circuits
  when every pair stays inside one project (no `FindReferences` cost for same-project moves).
- **P13. One definition per shared value.** `MoveFileParameterNames` (new, beside `FileMovePair`) holds the camelCase wire names `sourcePath`, `destinationPath`, `addProjectReferences`,
  `addInternalsVisibleTo` used by the planner, the five engine refusal texts that hard-code `destinationPath` (`CopyAndObsoleteChecks.cs:51,109`; `CopyAndObsoleteEdits.cs:55`;
  `CopyAndObsoleteCallerRewrite.cs:72`; `CopyAndObsoleteMigration.cs:364`) and the tests; a test pins each constant to the tool's actual parameter name. `nameof(FileMovePair.DestinationPath)` is
  NOT used because it yields `DestinationPath` (capital D), which changes wire-visible text. `MoveFileOperation` (new, `Tools.Basic`) holds `Name = "move_file"` and the blob search pattern
  `move_file_*.json`; sites: `WholeFileWriteTools.cs:387`, `GitStatusRenameHints.cs:293` and `:183`.
- **P14. Simplest alternative (checklist item 7).** The simplest design is Part 1 only, with `moves`, folder pairs, the cap and facts, D3 off, no reload tracking, no lease, no `ChangeNamespace`. That alone
  would have removed the 121-call workaround for a same-edge move but not the csproj hand-edit or the namespace TODO. The chosen design adds D3 (owner D3), the reload signal (D12), the lease and tool-level gate
  (D13) and Part 2 (D6). Each is separable: Steps 8, 11, 14, 18, 20, 22-27 (D3 and its machinery) can be dropped without touching the Part 1 steps, and Part 2 (Steps 5, 28, 30-33) does not depend on Part 1.
- **P15. Layout.** New engine files go in the `RoslynSentinel.Engines.Basic` project root (namespace `RoslynSentinel.Engines.Basic`), prefixed `MoveSet...` / `ChangeNamespace...`.
- **P16. Placement of size-limit exceptions (checklist item 4).** `PersistentWorkspaceManager.cs` is 3897 lines (over 1500): Steps 14 and 20 add only call sites there (the tracking state is
  private to the class, so it cannot be extracted without exposing it); the new lease logic is a new small class in `Common`. No IN PROGRESS plan found that is splitting it (re-check before Steps 14 and 20).
  `WholeFileWriteTools.cs` (1076) and `CoreRefactoringTools.cs` (610) are under the limit. New code in `Common` (the lease class, `ValidateChangesAgainstBaselineAsync`, `ReloadPending`) sits beside the
  chokepoint and validation code it extends, which cannot be reached from an engine.
- **D17 (R16, owner 2026-11-25): b1.** Step 14 tracks the post-write reload as a load, so the existing read entries (which already wait on `WaitForLoadAsync`/`SolutionLoadStatus`) are the waiters; no
  interface or fake change; `EndLoadTracking` must not clobber `_lastLoadFailure`. b3 (also track the debounce reload) only if Step 35a reports `STALE-READ-OBSERVED` for a peer-edited csproj.
- **D18 (R15, owner 2026-11-25): accept, with clearer text.** A reload longer than the load wait makes the next call fail with the existing load-timeout error; Step 14 makes that text name the
  background reload after a project-file write and state that a retry works (facts and options only).
- **D19 (R17, owner 2026-11-25): all three as proposed.** Narrow `IEdgeGateLeaseSource` implemented by `PersistentWorkspaceManager`, reached with an `as` check; holder = `ToolCallContext.Current`,
  else a fallback `AsyncLocal` token (comment the deviation from "keyed by call id"); default max wait 30 s, configurable, compared against Step 35b's gate time.
- **D20 (R12, owner 2026-11-25): in-process first, fall back.** Step 27 first tries a real-manager in-process load of the third project; if it needs a restore, T27.8/T27.11 become `OutOfProcess`
  tests (reusing `RunDotnet`) and Step 27 is split; note that `OutOfProcess` rows run only in the full suite.

## Execution rules

- Build (0 errors) before every commit; one `Build`/`RunTest` at a time (agents share `bin/`/`obj/`; two agents in one round only touch disjoint files). `RunTest` is the dispatcher's call after a slice,
  never an implementer's. Compare against `docs/known-failing-tests.*.txt` (baseline is zero failures; any failure is new). Load the `dispatch-implementer` skill before the first dispatch.
- Text-neutral steps first: Steps 1-12 add or extract only. Step 13 is the first behaviour-changing one (with Step 14 in the same round, disjoint files).
- Hint text from the new code goes through `ToolCallHint.Next(...)` with the `IsCallable` guard; a hint to a gated tool names `McpToolsetControl(toolSet: moveExtract, enabled: true)`. Error and hint
  text carries facts and options, never policy.
- Every name marked `(new)` is created by this plan; every other name was verified to exist on 2026-11-25. Re-measure a file before editing it (other sessions may have uncommitted edits; `Search` the
  file for `TODO` first).
- Coverage roles: the planner (this document) defines what each test must cover (test table); the reviewer confirms the tests exercise the changed path, not a shared helper; an implementer's green
  build says only that the stubs and tests it was given pass. `MoveFileTests` and `MoveFileCopyAndObsoleteTests` exercise the set-of-one path only; a green run of them is not evidence for the set feature.
- New out-of-process test files that spawn `dotnet` call `BuildIsolation.RemoveInheritedMsBuildPins(` and are added to the
  `BuildIsolationTests.DotnetSpawningCallers_StripTheInheritedMsBuildPinsThroughTheSharedList` TestCase list (`RoslynSentinel.Tests.Tools.Basic/BuildIsolationTests.cs`, about `:93`).
- After Steps 13, 26 and 32 the dispatcher regenerates the surface artifacts (P11) with plain calls: goldens via `ROSLYNSENTINEL_UPDATE_TOOL_SURFACE_GOLDEN=1` (the run writes then fails by design; re-run
  without the variable), `docs/generated` via `scripts/Generate-ArchitectureMap.ps1` and `scripts/Get-TodoMarkers.ps1`, then `ClaudeLeanSchemaBudgetTests` per D15.
- A server restart is needed after Steps 13, 26 and 32 before any live use, and before Step 35 (CLAUDE.md, Verification). No new `[McpServerToolType]` class is added anywhere, so no fresh-chat requirement.
- Before Step 13: check whether `plan_error_codes_follow_cause.md` (IN-PROGRESS) has finished its Step 4 leftovers (TODO.md "Step 4 leftovers": `DeleteFile` code in `WholeFileWriteTools.cs`); if not, either
  sequence it first or `HOLD WholeFileWriteTools.cs` with its session.

## NEW-DTO blocks

One block per new type; each step that creates a type repeats its block in the creating call's `reason`. Layer.domain `Engines.Basic.MoveSet` unless stated. Services, exceptions and
constants use `DTO-EXEMPT: <why>` in the creating call's `reason` (`MoveSetPlanner`, `MoveSetEdgeAnalyzer`, `CsprojEdgeWriter`, `MoveSetErrorGrouper`, `MoveSetRefusedException`,
`ChangeNamespaceEngine`, `MoveFileParameterNames`, `MoveFileOperation`, `EdgeGateLease`, `IEdgeGateLeaseSource`).

```
NEW-DTO: FileMovePair (Item, Engines.Basic.MoveSet)
Evaluated: ModifierEdit (Common/BatchTypes.cs:599) - modifier shape, not a path pair; FileMoveMemory entries - a lookup store recording pairs after the fact, not a request; AppliedChangeSummary.AffectedFiles - output, flat list
Wire impact: new tool input field (the element type of MoveFile.moves; properties SourcePath, DestinationPath with [BatchItemField])
```
```
NEW-DTO: MoveSetRequest (Request, Engines.Basic.MoveSet)
Evaluated: FileMovePair - one pair, no flags; the tool's parameter list - cannot be handed to an engine without a type (no tuples, checklist item 1)
Wire impact: none
```
```
NEW-DTO: MoveSetPlanResult (Result, Engines.Basic.MoveSet)
Evaluated: CopyAndObsoleteMigrationResult - copy/obsolete fields, one file; DocumentEditResult - one file; AppliedChangeSummary - the apply outcome, built later by the tool from ApplyOutcome (planning happens before any write)
Wire impact: none (the tool maps it to AppliedChangeSummary; a refusal is MoveSetRefusedException, not a result)
```
```
NEW-DTO: PlannedFileMove (Item, Engines.Basic.MoveSet)
Evaluated: AppliedChangeSummary.AffectedFiles - flat list, loses the pairing and the two projects; FileMovePair - the request side, no project names or kind
Wire impact: none
```
```
NEW-DTO: MoveOffender (Item, Engines.Basic.MoveSet) with enum MoveOffenderReason (new)
Evaluated: Finding (Source, Message, Severity) - free text, no typed reason or path; ToolErrorCode - one code per call, here one reason per offending file
Wire impact: none (carried as the exception's StructuredDetail, which is wire-visible as errorData.structuredDetail)
```
```
NEW-DTO: MoveFact (Item, Engines.Basic.MoveSet) with enum MoveFactKind (new)
Evaluated: Finding - the wire shape the fact is lifted to, no typed kind for tests; CompilerErrorLookupHelper hints - keyed by compiler code, not by move facts
Wire impact: none (lifted to Finding)
```
```
NEW-DTO: NeededProjectEdge (Item, Engines.Basic.MoveSet) with enum ProjectEdgeKind (new: ProjectReference, InternalsVisibleTo)
Evaluated: Roslyn ProjectReference - a workspace object that cannot carry a "needed but absent" edge and its reason; MoveFact - would hide the from/to projects from the csproj writer
Wire impact: none
```
```
NEW-DTO: MoveSetErrorGroup (Item, Engines.Basic.MoveSet)
Evaluated: EditImpactDetail (Verdicts, Diagnostics, NearMisses) - verdicts are per untouched file, not grouped by set member; DiagnosticReport - flat
Wire impact: new tool output field(s) (a second errorData.structuredDetail item after EditImpactDetail on a gate refusal)
```
```
NEW-DTO: MovePair (Item, Tools.Basic.Git)
Evaluated: the unnamed tuple (string Source, string Destination, string ChangeId) returned by GitStatusRenameHints.ReadMovePairs - a tuple across a boundary (checklist item 1); FileMovePair - absolute wire paths and no change id
Wire impact: none (source-compatible: the same Source, Destination, ChangeId property names)
```
```
NEW-DTO: NamespaceMove (Item, Engines.Basic.Refactoring)
Evaluated: CallerFileRewrite - a result, not a request; TopLevelType (CopyAndObsoleteCheckResult.cs) - a type, not a file-level old/new namespace pair
Wire impact: none
```
```
NEW-DTO: ChangeNamespaceRequest (Request, Engines.Basic.Refactoring)
Evaluated: MoveSetRequest - file moves, not namespace moves; the tool's parameters - cannot be handed to an engine without a type
Wire impact: none
```
```
NEW-DTO: EditOverlap (Item, Engines.Basic.Refactoring)
Evaluated: ToolErrorCode - one code, no spans; CollateralChangeViolation - a pre/post text mismatch, not two overlapping edits
Wire impact: new tool output field(s) (errorData.structuredDetail of the overlap refusal)
```
```
NEW-DTO: DanglingUsing (Item, Engines.Basic.Refactoring) with enum DanglingUsingResolution (new: Removed, RefusedGlobal)
Evaluated: CallerFileRewrite - one rewritten file, no per-using resolution; Finding - free text
Wire impact: none (summarised into the description and findings)
```
`ChangeNamespace` returns `AppliedChangeSummary` via `AppliedChangeSummary.From(...)`; no new wire result. `CallerRewriteResult` gains one defaulted property (P8).

## Steps

The slice map (two agents at most, disjoint files per round) is at the end of this section.

### Step 1 - Contract skeleton for Part 1 (compile-green, no behaviour)
- Done: 9c8c5663
- Files: new `RoslynSentinel.Engines.Basic/FileMovePair.cs` (`FileMovePair`, `MoveSetRequest`, `MoveFileParameterNames`); new `RoslynSentinel.Engines.Basic/MoveSetPlanResult.cs`
  (`MoveSetPlanResult`, `PlannedFileMove`, `MoveOffender`, `MoveOffenderReason`, `MoveFact`, `MoveFactKind`, `NeededProjectEdge`, `ProjectEdgeKind`, `MoveSetErrorGroup`, `MoveSetRefusedException`); new
  `RoslynSentinel.Engines.Basic/MoveSetPlanner.cs`. Three files, one write each (`CreateFile`), definitions before users.
- Depends on: none
- Effect: text-neutral (new types with no callers)
- Change: all types of the NEW-DTO blocks with final properties. `FileMovePair` properties `SourcePath`, `DestinationPath` carry `[BatchItemField(..., Required = true)]` (P9).
  `MoveSetPlanResult`: `Moves` (`IReadOnlyList<PlannedFileMove>`), `Changes` (`DocumentChangeSet`), `DeletePaths`, `Facts`, `Edges`; no refusal member (P7). `MoveOffenderReason`: `Missing`, `Binary`, `NotUtf8`,
  `SamePath`, `DestinationExists`, `DuplicateDestination`, `OutsideLoadedProject`, `OverCap`; `MoveOffender.ErrorCodeOf(MoveOffenderReason)` is implemented now (P6, a switch, no stub).
  `MoveFactKind`: `KeptNamespace`, `SourceCsprojItemNamesFolder`, `MissingProjectReference`, `MissingInternalsVisibleTo`, `MissingPackageReference`, `EdgeWouldCloseCycle`, `CsprojEdgeAdded`,
  `EmptySourceFolderLeft`, `ReloadPending`, `WorkspaceChangedDuringGate`. `MoveSetRefusedException : ToolException` (pattern `ToolBatchEditException`) holds `IReadOnlyList<MoveOffender> Offenders`, derives its
  error code with P6's rule and overrides `StructuredDetail`. `MoveFileParameterNames` constants per P13. `MoveSetPlanner` is a `static class` with two contract methods, bodies
  `throw new NotImplementedException(); // TODO implement (Step 7)` / `(Step 10)`:
  `public static Task<MoveSetPlanResult> ExpandAndCheckAsync(MoveSetRequest request, CancellationToken cancellationToken)` and
  `public static Task<MoveSetPlanResult> CheckProjectsAndCollectFactsAsync(MoveSetPlanResult plan, Solution committed, CancellationToken cancellationToken)`.
  Each new type's NEW-DTO block (or `DTO-EXEMPT`) goes in the creating call's `reason`.
- Done when: `Build` 0 errors (default `fullBuildThrowaway`).
- Dispatcher check after: `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Engines.Basic/MoveSet*.cs")` lists only `MoveSetPlanner.cs`.
- Lane: `implementer` (D1: 3 files, 3 writes).

### Step 2 - Funnel the `MoveFileTool` test call sites, part 1: the helper and the `tools.MoveFileTool(` form
- Done: 4ddbabd2
- Files: `RoslynSentinel.Tests.Tools.Basic/MoveFileTests.cs`. Measured 2026-11-25: 16 `MoveFileTool` call sites in this file, in two textual forms: `tools.MoveFileTool(` (8: lines 65, 88, 107, 128, 148, 187, 211, 235)
  and `BuildTools(manager).MoveFileTool(` (8: lines 267, 281, 297, 315, 331 and three more). `MoveFileCopyAndObsoleteTests.cs:106` is the single call site of its private helper `CopyAsync` (used 17 times), so it
  needs no funnel; Step 13 edits that one line.
- Depends on: none
- Effect: text-neutral
- Change: add one helper (a `private static` method in `MoveFileTests`, signature `(WholeFileWriteTools tools, string source, string destination, MoveFileNamespaceHandling handling)` mirroring today's arguments
  and returning what `MoveFileTool` returns) and replace the 8 `tools.MoveFileTool(` sites with it in one `ReplaceSnippet` batch (9 edits).
- Done when: `Build` 0 errors.
- Lane: `implementer` (D1: 1 file, 9 edits; split from Step 3 because two textual forms plus the helper is two operations, over the S0 limit).

### Step 3 - Funnel the `MoveFileTool` test call sites, part 2: the `BuildTools(manager).MoveFileTool(` form
- Done: 6be9c93a
- Files: `RoslynSentinel.Tests.Tools.Basic/MoveFileTests.cs`.
- Depends on: Step 2 (same file, helper must exist)
- Effect: text-neutral
- Change: replace the 8 `BuildTools(manager).MoveFileTool(` sites with the helper (the helper takes the tools instance, so `BuildTools(manager)` becomes its first argument), one `ReplaceSnippet` batch.
- Done when: `Build` 0 errors.
- Dispatcher check after: `Search(mode: callers, containingType: "WholeFileWriteTools", query: "MoveFileTool", includeTests: true)` lists only the helper and `MoveFileCopyAndObsoleteTests.CopyAsync`.
- Lane: `implementer` (D1: 1 file, 8 edits).

### Step 4 - Extract the helper's rejection builder (text-neutral prerequisite for D13(d))
- Done: 1b9a55bc
- Files: `RoslynSentinel.Common/ValidateAndApplyHelper.cs` (rejection block `:207-227`, measured; the review cited `:205-226`), plus a new `RoslynSentinel.Common/ValidationRejection.cs` only if the builder
  cannot be an `internal static` method in place. `plan_shared_tool_execution_pipeline.md` (DRAFT, parked) targets the same spot (`:199-202`, its Step at line 101); re-check its status before editing.
- Depends on: none
- Effect: text-neutral (same rejection text and shape, no signature change, no caller change)
- Change: move the block that builds the `ValidationFailed` `ResultError` (the `describeEditImpact`/`describeValidationFailure` composition and the foreign-agent findings) into an `internal static` builder taking
  `(operationName, report, changes, workspaceManager, describeValidationFailure, describeEditImpact, cancellationToken)`; `ValidateAndApplyAsync` calls it. Update the `preValidatedAgainst` doc comment
  (`:112-119`, measured; the review cited `:112-118`) to "the committed solution, or a candidate derived from it that the caller compiled exactly `changes` against" (D13(c)).
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the existing rejection tests (`EditAttributionFindingsSurfaceTests`, `WriteChokepointGuardrailTests` and the gate tests in `RoslynSentinel.Tests.Basic`) pass unchanged.
- Lane: `implementer` (D1: 1-2 files).

### Step 5 - Contract skeleton for Part 2, the D10 comment and the stale stub hint
- Done: 8ff1ff03
- Files: new `RoslynSentinel.Engines.Basic/NamespaceMove.cs` (`NamespaceMove`, `ChangeNamespaceRequest`, `EditOverlap`, `DanglingUsing`, `DanglingUsingResolution`); new
  `RoslynSentinel.Engines.Basic/ChangeNamespaceEngine.cs`; `RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs` (comment and hint text). Three files.
- Depends on: none (Part 2 does not need Part 1)
- Effect: text-neutral, except one corrected hint string on a stub that throws `NotImplemented` (no test pins the old fragment: `Search` for "to move each file into it" in tests found none)
- Change: the types of the NEW-DTO blocks; `CallerRewriteResult` is NOT touched here (Step 28). `ChangeNamespaceEngine.PlanAsync(ChangeNamespaceRequest request, Solution committed, CancellationToken cancellationToken) : Task<CallerRewriteResult>`
  with body `throw new NotImplementedException(); // TODO implement (Step 30)`. D10: at the `SplitProjectByFolderAsync` stub (`ProjectStructureEngine.cs:1854-1870`, TODO at `:1862`) add a comment naming the
  wrapper idea (compose `CreateProject` + `MoveFile` + `ChangeNamespace`) as an option for later; keep the stub and its `ToolNotImplementedException`. Fix the stale hint at `:1868`
  (`ToolCallHint.Next("MoveFile", "to move each file into it.")`) to say a folder pair in `moves` moves the folder's files in one call. The `ChangeNamespace` tool method is NOT added here: a method
  carrying `[McpServerTool]` on `CoreRefactoringTools` would appear in tools/list on the next build, before it works (Step 32 adds it).
- Done when: `Build` 0 errors.
- Dispatcher check after: `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Engines.Basic/ChangeNamespaceEngine.cs")` lists exactly the stub.
- Lane: `implementer` (D1: 3 files).

### Step 6 - D5 probe: does `LoadSolution` restore a hand-written csproj that an existing project references?
- Done: b89965a5 (GREEN for forceReload and default LoadSolution; Step 29 skipped)
- Files: new `RoslynSentinel.Tests.Tools.Basic/LoadSolutionHandWrittenCsprojRestoreIntegrationTests.cs`; `RoslynSentinel.Tests.Tools.Basic/BuildIsolationTests.cs` (one `TestCase` row).
- Depends on: none
- Effect: text-neutral (tests only)
- Change: `[Category("OutOfProcess")]` fixture building a scratch solution under the scratch root: an existing project A, a hand-written `B.csproj` (not created by a tool), A gets
  `<ProjectReference Include="..\B\B.csproj" />`, `dotnet sln add B`, then two probes: `LoadSolution(forceReload: true)` and `LoadSolution` WITHOUT `forceReload` (P10; `RestoreBeforeLoadAsync` at
  `WorkspaceProjectManagementImpl.cs:702` restores only when `AnalyzeRestoreState` flags). Reuse `RunDotnet` from `LoadSolutionForceReloadRestoreIntegrationTests` (which tests the OPPOSITE direction, new B
  references existing A; it stays untouched). The fixture calls `BuildIsolation.RemoveInheritedMsBuildPins(`. Why out-of-process: restore and `sln add` are the behaviour under test (checklist item 10).
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on this class only. Outcome classes reported per probe: `GREEN` (D5 closed for that probe, Step 29 skipped for it, recorded in the plan), `RED-NU1105` or a workspace failure
  naming B's assets (Step 29 applies), `RED-OTHER` (a test or environment fault: fix the fixture, do not start Step 29).
- Lane: `test-writer`.

### Step 7 - Planner stage 1: expansion, eligibility, cap
- Done: 0f572308
- Files: `RoslynSentinel.Engines.Basic/MoveSetPlanner.cs`.
- Depends on: Step 1
- Effect: text-neutral (no caller yet; the tool still runs its inline code)
- Change: implement `ExpandAndCheckAsync` (disk only; no solution): derive kind per pair from disk (file or directory); expand a folder pair to file pairs with subfolders preserved, merge into an existing folder;
  per file record the first applicable reason in the legacy order (P5): same path (`SamePath`), missing source (`Missing`), destination exists case-insensitively as a file or directory (`DestinationExists`),
  NUL byte in the first 8000 bytes (`Binary`), strict UTF-8 decode (`NotUtf8`); also `DuplicateDestination` within the set and more than `MoveSetPlanner.MaxFilesPerCall` (new constant, 200, D4) files after
  expansion (`OverCap`). Carry the rules and message fragments over from `MoveFileTool` (`WholeFileWriteTools.cs:326-370`), reading them there. Every offender is collected, not the first; when any exist, throw
  `MoveSetRefusedException` (message states facts and options, including the fragments of P5). Messages use `MoveFileParameterNames` (P13). Do not delete the tool's inline code in this step.
- Done when: `Build` 0 errors.
- Lane: `implementer` (D1: 1 file, about 8 edits).

### Step 8 - `ValidationEngine.ValidateChangesAgainstBaselineAsync` (new static; existing core delegates)
- Done: c5b83276
- Files: `RoslynSentinel.Common/ValidationEngine.cs` (289 lines).
- Depends on: none
- Effect: text-neutral (same results for every existing caller). Hot path: the static core is called by 4 production sites (`CopyAndObsoleteMigration.cs:191`, `BasicRefactoringEngine.cs:1096`, `MemberRefactoringEngine.cs:5329`
  and the two forms inside `ValidationEngine`) and 6 sites in `MoveBindCheckTests`; the change is a body delegation, no signature changes (D13 hard constraint).
- Change: add `public static Task<ValidationCandidateResult> ValidateChangesAgainstBaselineAsync(Solution errorBaseline, Solution candidateBase, IReadOnlyDictionary<FilePathWrapper, string> fileChanges, IReadOnlyCollection<FilePathWrapper>? removePaths = null, CancellationToken cancellationToken = default)`.
  It resolves documents by path in `candidateBase` (an `AddProjectReference` solution keeps the document ids), builds the candidate from `candidateBase`, expands affected projects via `candidateBase.GetProjectDependencyGraph()`,
  takes baseline errors from `errorBaseline`'s compilation of the same project id, and ALSO adds every project whose `ProjectReferences` or documents differ between `errorBaseline` and `candidateBase` to the affected set (a project
  that only gained a reference has no changed document but can gain errors, for example CS0433 from a duplicate type the new reference makes visible). The existing static `ValidateChangesWithCandidateAsync(baseline, ...)`
  (`:150-271`) becomes `return await ValidateChangesAgainstBaselineAsync(baseline, baseline, ...)`; its signature and the `DiagnosticKey` delta rule are unchanged.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on `MoveBindCheckTests` and the `CopyAndObsolete*` migration tests passes unchanged.
- Lane: `implementer` (D1: 1 file, about 5 edits).

### Step 9 - Planner tests, part A (expansion, eligibility, cap)
- Done: 483758ca
- Files: new `RoslynSentinel.Tests.Basic/MoveSetPlannerEligibilityTests.cs`; new fixture helper `RoslynSentinel.Tests.Basic/MoveSetFixture.cs` (writes real files under the scratch root; stage 1 reads disk only, so no solution is needed).
  Placement is subject to `TestPlacementRatchetTests`.
- Depends on: Step 7
- Effect: text-neutral
- Change: rows T9.1-T9.6.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the class: all rows pass.
- Lane: `test-writer`.

### Step 10 - Planner stage 2: project check and facts
- Done: ad3ae76b (deviation: added trailing defaulted `SourceFolders` to `MoveSetPlanResult`; stage 2 needs folder-pair source roots)
- Files: `RoslynSentinel.Engines.Basic/MoveSetPlanner.cs`.
- Depends on: Step 7
- Effect: text-neutral
- Change: implement `CheckProjectsAndCollectFactsAsync`: (a) D14: a moved `.cs` file whose destination is inside no loaded project (`SolutionProjectLocator.FindContainingProject`) refuses
  (`OutsideLoadedProject`, throwing `MoveSetRefusedException`; the option names `LoadSolution(forceReload)`, D5); a non-`.cs` file under no project still moves (as `MoveFileTests` pins today);
  (b) the kept-namespace fact once per distinct (declared namespace, expected namespace) mismatch via `FolderNamespace.Expected(Project, FilePathWrapper)` (`FolderNamespace.cs:19`), moving the logic of
  `DescribeKeptNamespace` (private static, `WholeFileWriteTools.cs:444-462`) into the planner as a typed `MoveFactKind.KeptNamespace` fact plus one text builder (the tool's copy stays until Step 13 deletes it);
  (c) D8: a text scan of each source project's csproj for `<Compile Remove|Include`, `<None`, `<Content`, `<EmbeddedResource` items whose path names a moved folder, as `SourceCsprojItemNamesFolder` facts
  ("not updated"); (d) `EmptySourceFolderLeft` per folder pair. There is no partial-type rule (D16). Stage 2 is skipped for the `copyAndObsolete` branch, which keeps the engine's own refusal text (P5).
- Done when: `Build` 0 errors.
- Lane: `implementer` (D1: 1 file, about 8 edits).

### Step 11 - Tests for `ValidateChangesAgainstBaselineAsync`
- Done: ae9c5994, fix ca33514d
- Files: new `RoslynSentinel.Tests.Basic/ValidationEngineBaselineSplitTests.cs`.
- Depends on: Step 8
- Effect: text-neutral
- Change: rows T11.1-T11.4. The existing `MoveBindCheckTests` (`ValidateChangesWithCandidate_*`) are the delegation regression net and are not edited.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the class and on `MoveBindCheckTests`.
- Lane: `test-writer`.

### Step 12 - Planner tests, part B (project check, facts)
- Done: b6cffe60
- Files: new `RoslynSentinel.Tests.Basic/MoveSetPlannerFactsTests.cs` (uses `MoveSetFixture` and `TestSolutionBuilder.CreateSolutionWithProject("Demo", csprojPath, documents)`).
- Depends on: Step 10
- Effect: text-neutral
- Change: rows T12.1-T12.6.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the class.
- Lane: `test-writer`.

### Step 13 - Rewrite `MoveFileTool` to `moves` (behaviour-changing)
- Done: 93b1a245, hint fix 48352081, regeneration + budget 49000->49500 (measured 49136) 88b921a2. `FileMoveMemory` is recorded after a successful apply, not in `recordEffects`.
- Files: `RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs`; new `RoslynSentinel.Tools.Basic/MoveFileOperation.cs` (P13); `RoslynSentinel.Common/ToolParams.cs` (a description constant `MoveFileMoves` (new), the
  convention for list-parameter text, as `ModifierEdits` at `:123`); `RoslynSentinel.Common/ToolEnums.cs` (the `MoveFileNamespaceHandling` doc comment at `:150-154`, already stale: it says `copyAndObsolete` is
  not supported); `RoslynSentinel.Tests.Tools.Basic/MoveFileTests.cs` (the helper body and the one directory test); `RoslynSentinel.Tests.Tools.Basic/MoveFileCopyAndObsoleteTests.cs` (`CopyAsync`, line 106);
  `RoslynSentinel.Tests.Server/ClaudeLeanSchemaBudgetTests.cs` (D15). Also the five engine refusal texts that hard-code `destinationPath` switch to `MoveFileParameterNames` (P13): `CopyAndObsoleteChecks.cs:51,109`,
  `CopyAndObsoleteEdits.cs:55`, `CopyAndObsoleteCallerRewrite.cs:72`, `CopyAndObsoleteMigration.cs:364`. Eleven files, one repeated edit pattern in the last five.
- Depends on: Steps 3, 10; precondition in Execution rules (`plan_error_codes_follow_cause` Step 4)
- Effect: behaviour-changing. The tool schema changes: `sourcePath`/`destinationPath` removed, `moves` and `dryRun` added; a folder pair works; a `.cs` move into a folder inside no loaded project now refuses (D14);
  the result description gains planner facts; a caller still passing the old parameters gets the pipeline's unknown-parameter refusal (existing behaviour).
- Change: signature (from today's, changed): `MoveFileTool([Description(ToolParams.Reason)] ToolCallReason reason, [Description(ToolParams.MoveFileMoves)] List<FileMovePair> moves, [Description("keep: ...")] MoveFileNamespaceHandling namespaceHandling, [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, CancellationToken cancellationToken = default)`
  (the `namespaceHandling` text is today's `WholeFileWriteTools.cs:297`, kept). Flow: `ResolveFromWire` per pair; the `SolutionNotLoaded` check; the `namespaceHandling` rules (`keep` for any set; `copyAndObsolete` only for a set that expands to exactly one file pair,
  else `InvalidArgument` stating why; `update` refused with `NotImplemented`, its hint naming `ChangeNamespace` through `ToolCallHint.Next` with the `IsCallable` guard; non-`.cs` accepts only `keep`);
  `MoveSetPlanner.ExpandAndCheckAsync`; if `copyAndObsolete`, branch to `MoveFileCopyAndObsolete.RunAsync` (unchanged text, P5); else `CheckProjectsAndCollectFactsAsync`; build one `DocumentChangeSet`
  (destination = decoded source text) and `deletePaths`; one `ValidateAndApplyHelper.ValidateAndApplyAsync(..., MoveFileOperation.Name, dryRun, deletePaths: ..., verbatim: true, describeEditImpact: EditImpactGate.Describer(...), recordEffects: ...)`;
  `recordEffects` records one `EditEffectKind.FileMoved` per pair and `FileMoveMemory.Shared.Record(source, destination, "MoveFile")` per pair; return `AppliedChangeSummary.From(apply, affectedFiles, description)` with both
  paths of every pair in `AffectedFiles` and the facts lifted to `Findings`. `dryRun` returns the expanded pairs and the gate result without writing. The description is one line: a pair may name a folder (its contents land at the
  destination path, subfolders kept, a same-name file refuses), a single move is a list of one, up to 200 files, one build check, one undo. Per-element validation replaces the `[Consumes]`/`[ExternalInputRequired]` tags (P9, commented).
  Delete the inline validations and `DescribeKeptNamespace`. The `copyAndObsolete` branch keeps `copy_and_obsolete_file` as its operation name (`MoveFileCopyAndObsolete.cs:18`: `GitStatusRenameHints` reads `move_file_*`).
  Test edits: the helper body wraps one pair into a one-element list; `CopyAsync` likewise; `MoveFile_DirectorySource_FailsAsNotAFileAsync` is rewritten to assert the D2 folder behaviour (P5); the budget constants are raised per D15
  (measure after the schema change; comment line carries the measurement).
- Done when: `Build` 0 errors (solution). Dispatcher: `RunTest` on `MoveFileTests` and `MoveFileCopyAndObsoleteTests` passes with no assertion edits other than the one directory test (P5); then regenerate surfaces (Execution rules) and
  run `ClaudeLeanSchemaBudgetTests`, `ToolSurfaceParityTests`, `EvalToolSurfaceParityTests`.
- Lane: `implementer-senior` (behaviour-changing rewrite of the most used core write tool, 11 files, a schema change and removal of inline code).

### Step 14 - Awaitable reload signal: track the post-write reload as a load, and `ApplyOutcome.ReloadPending` (D12)
- Done: 6d20e875, ratchet fix 5f4d7cc5. `LoadSolution` and `SubAgentModelRun.AwaitSolutionLoadAsync` still time out with the plain text (the flag is not on `ISolutionProvider`; TODO, left unless Step 35a shows it matters).
- Files: `RoslynSentinel.Common/PersistentWorkspaceManager.cs` (call sites only, P16); `RoslynSentinel.Common/ApplyOutcome.cs`; `RoslynSentinel.Common/ValidateAndApplyHelper.cs` (the two post-apply returns).
  `ISolutionProvider` and `FakeWorkspaceManager` (`RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`, the only other `IWorkspaceManager` implementer; `ISolutionProvider` has the same two) are NOT changed under the
  recommended option (R16, b1).
- Depends on: none
- Effect: behaviour-changing. After a structural (csproj, props, targets) write, `SolutionLoadStatus` is `Loading` until the background reload ends, so the NEXT call's read entry waits (bounded, `SolutionLoadWait.DefaultTimeout`)
  instead of racing; `McpServerStatus` shows loading; the writing call itself returns without waiting.
- Change: in the `finally` of `ApplyProposedChangesCoreAsync` (`:2863-2917`), for both background reloads (the post-write reload at `:2902` and the fold-failure reload at `:2878`) call `BeginLoadTracking()` synchronously before
  `Task.Run`, so the call returns with the status already `Loading`, and call `EndLoadTracking(...)` in the task's `finally`. The terminal status is `Loaded` when the reload succeeded or failed with the old workspace kept
  (the rule `LoadSolutionAsync` applies at `:1068-1070`); `EndLoadTracking` (`:308`) must not overwrite `_lastLoadFailure` with null for a kept-old failure (add a parameter or a second overload; read it first).
  The debounce reload (`OnDebounceTimerElapsed`, `:1711`, external edits) stays untracked here (R16). `ApplyOutcome` gains a trailing defaulted `bool ReloadPending = false` (non-breaking: positional constructions
  elsewhere, for example `QualifyCrefsImpl.cs:214`, stay valid); the helper sets it from `applyResult.NeedsFullReload` on the apply-success returns. Read, before editing, whether anything in the writing call after the apply
  reads the solution (a self-wait hazard, R16).
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the existing `WorkspaceRefreshTests` (pins `NeedsFullReload` for a csproj write) passes unchanged.
- Lane: `implementer-senior` (3 files, a chokepoint and load-tracking interaction in a 3897-line class).

### Step 15 - Grouped refusal: errors grouped by the set file they bind to
- Done: 2eff9aea, bce7456b, fd379c8f
- Files: new `RoslynSentinel.Engines.Basic/MoveSetErrorGrouper.cs`; `RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs` (the describer wrapper in `MoveFileTool`).
- Depends on: Step 13
- Effect: behaviour-changing (a gate refusal for a set carries one more structured item)
- Change: `MoveSetErrorGrouper.Group(DiagnosticReport, IReadOnlyList<PlannedFileMove>) : IReadOnlyList<MoveSetErrorGroup>`: group each new compile error by the file the diagnostic is reported in, mapping a
  destination path back to its pair, plus one "outside the set" group (v1 rule, R7). In `MoveFileTool` wrap the existing `EditImpactGate.Describer(...)` so `describeEditImpact` returns the same text plus
  `structuredDetail = [EditImpactDetail, ..., MoveSetErrorGroup list]` (delegate shape: `Func<DiagnosticReport, CancellationToken, Task<(string? Text, IReadOnlyList<object>? StructuredDetail)>>`, `ValidateAndApplyHelper.cs`
  parameter `describeEditImpact`). The text names, per group, the files of the set and the option "include these files in `moves`".
- Done when: `Build` 0 errors.
- Lane: `implementer` (D1: 2 files).

### Step 16 - Tool-level tests for the set path
- Done: 4ef73106
- Files: new `RoslynSentinel.Tests.Tools.Basic/MoveFileSetTests.cs`.
- Depends on: Step 13
- Effect: text-neutral
- Change: rows T16.1-T16.10 (fixture shapes (1), (2), (3), (5), (6), (7) of the proposal at the tool level). Real `PersistentWorkspaceManager` via `TestSolutionFixture` as `MoveFileTests` does (T16.5-T16.7 need it:
  undo, `FileMoveMemory`, bytes).
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the class: all rows pass.
- Lane: `test-writer`.

### Step 17 - `GitStatusRenameHints`: read multi-pair `move_file` blobs
- Done: ba01c3f1, legacy single-move blob fix 914ab7d6
- Files: `RoslynSentinel.Tools.Basic/GitStatusRenameHints.cs`; `RoslynSentinel.Tools.Basic/GitTools.cs` (`:53`, the consumer). Existing test consumers: `GitStatusRenameHintTests` (2 sites) and `MoveFileCopyAndObsoleteTests.cs:235`.
- Depends on: Step 13 (for `MoveFileOperation`)
- Effect: behaviour-changing (a multi-pair `move_file` blob now yields definitive rename hints instead of none)
- Change: introduce the named `MovePair` record (NEW-DTO) with the property names `Source`, `Destination`, `ChangeId`, replacing the tuple in `ReadMovePairs` (`:167`), `JoinChains` (`:213-277`) and `ReadMovePair` (`:279-340`), so consumers stay source-compatible.
  Generalise `ReadMovePair` to return every pair of a blob: pair created items to deleted items by `afterSourceFromFilePath` (`OperationBlobWriter.BuildApplyItems`). Because that pairing uses `FirstOrDefault` on identical text, detect two deleted
  items with the same `BeforeSource` from the deleted items themselves and treat those pairs as ambiguous: fall back to the existing tier 2 hint for them only. `JoinChains` keeps working with N pairs per blob (A->B in one blob,
  B->C in a later blob, visited-set cycle guard unchanged). Replace the literals at `:183` and `:293` with `MoveFileOperation`. Leave the pending `// TODO Step 15` at `:106` (the Git plan's) untouched.
- Done when: `Build` 0 errors.
- Lane: `implementer` (D1: 2 files).

### Step 18 - Tests for the reload signal
- Done: 942cf03f
- Files: new `RoslynSentinel.Tests/WorkspaceReloadTrackingTests.cs` (beside `WorkspaceRefreshTests.cs`).
- Depends on: Step 14
- Effect: text-neutral
- Change: rows T18.1-T18.5. Deterministic: the existing `PersistentWorkspaceManager.OpenSolutionOverride` seam (`:3464`, internal) gates the reload on a `TaskCompletionSource`, so no test sleeps.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the class and on `WorkspaceRefreshTests`.
- Lane: `test-writer`.

### Step 19 - Rename-hint tests for multi-pair blobs
- Done: a28a2ed9
- Files: new `RoslynSentinel.Tests.Git/GitStatusRenameHintMultiPairTests.cs` (beside `GitStatusRenameHintTests.cs`).
- Depends on: Step 17
- Effect: text-neutral
- Change: rows T19.1-T19.4.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the class and on `GitStatusRenameHintTests`.
- Lane: `test-writer`.

### Step 20 - In-process edge-gate lease (D13(f))
- Done: 6990d6b0
- Files: new `RoslynSentinel.Common/EdgeGateLease.cs` (class, `IEdgeGateLeaseSource`); `RoslynSentinel.Common/PersistentWorkspaceManager.cs` (a field, the interface implementation and one `await` at the top of `ApplyProposedChangesAsync`,
  `:2237`, before the call to `ApplyProposedChangesCoreAsync`; no signature change).
- Depends on: Step 14 (same large class, sequenced)
- Effect: behaviour-changing for other writers only while a lease is held; a no-op (one volatile read) otherwise
- Change: `EdgeGateLease` is an in-process object (no `_solutionLock`, no `CrossProcessWriteLock`: rule (c) of `CrossProcessWriteLock.cs`, and `_solutionLock` is not re-entrant) with `AcquireAsync(CancellationToken)` returning a disposable
  holder and `WaitForOtherHolderAsync(CancellationToken)` that completes at once when no lease is held or the current flow is the holder, and otherwise waits up to a bounded, configurable `MaxWait` (a property with a default constant, set the way
  `PersistentWorkspaceManager.DefaultLoadTimeout` is, `:350`; proposed default 30 s, R17), then proceeds. Holder identity is the ambient flow: `ToolCallContext.Current` (an `AsyncLocal`, `ToolCallContext.cs:13`) when set, with a fallback `AsyncLocal` token
  because `Current` is null in tests and in-process hosts. A waiting writer gets a `Finding` naming the holding tool and the time waited (facts only). `IEdgeGateLeaseSource { EdgeGateLease Lease { get; } }` is implemented by `PersistentWorkspaceManager` and is
  NOT added to `IWorkspaceManager`, so `FakeWorkspaceManager` and its tests are unaffected; the tool reaches it with an `as` check (R17).
- Done when: `Build` 0 errors.
- Lane: `implementer-senior` (new concurrency primitive wired into the write chokepoint).

### Step 21 - Tests for the grouped refusal
- Done: a3bde8f6
- Files: new `RoslynSentinel.Tests.Tools.Basic/MoveFileSetErrorGroupTests.cs`.
- Depends on: Step 15
- Effect: text-neutral
- Change: rows T21.1-T21.2.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the class passes.
- Lane: `test-writer`.

### Step 22 - Contract skeleton for D3 (edge analysis, csproj writer)
- Done: b5c8f634
- Files: new `RoslynSentinel.Engines.Basic/MoveSetEdgeAnalyzer.cs`; new `RoslynSentinel.Engines.Basic/CsprojEdgeWriter.cs`.
- Depends on: Step 1
- Effect: text-neutral
- Change: `MoveSetEdgeAnalyzer.AnalyzeAsync(MoveSetPlanResult plan, Solution committed, CancellationToken) : Task<IReadOnlyList<NeededProjectEdge>>` and
  `MoveSetEdgeAnalyzer.AugmentBaseline(Solution committed, IReadOnlyList<NeededProjectEdge> edges, bool addReferences, bool addInternalsVisibleTo) : Solution`;
  `CsprojEdgeWriter.Splice(byte[] csprojBytes, IReadOnlyList<NeededProjectEdge> edgesForThisProject) : string` (returns the new text with the BOM and EOL of the file kept; reading the csproj as bytes and writing it verbatim is the caller's job);
  all bodies `throw new NotImplementedException(); // TODO implement (Step 24/25)`.
- Done when: `Build` 0 errors.
- Dispatcher check after: `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Engines.Basic/MoveSet*.cs")` lists exactly these two files.
- Lane: `implementer` (D1: 2 files).

### Step 23 - Tests for the lease
- Done: a29a769a
- Files: new `RoslynSentinel.Tests.Basic/EdgeGateLeaseTests.cs`.
- Depends on: Step 20
- Effect: text-neutral
- Change: rows T23.1-T23.5. The chokepoint rows need the real `PersistentWorkspaceManager` (the hook is in its body): `TestSolutionFixture`, justified in the rows.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on the class.
- Lane: `test-writer`.

### Step 24 - Edge analysis body
- Done: aa9e9f5b, fd379c8f. Cycles and missing packages come from public `FindCycleEdgesAsync` / `FindMissingPackageFactsAsync` (Step 26 calls both); IVT `FromProject` = granting project; in-memory IVT uses the receiver's assembly name.
- Files: `RoslynSentinel.Engines.Basic/MoveSetEdgeAnalyzer.cs`.
- Depends on: Step 22
- Effect: text-neutral
- Change: (a) short-circuit: when every pair stays inside one project, return no edges (no `FindReferences` cost for same-project moves, P12); (b) per moved `.cs` file, walk the committed semantic model collecting the project of every referenced
  symbol outside the set: the destination needs a reference to each, checked against the destination compilation's `References` so transitive flow is honoured (R8); (c) reference sites of moved types (`SymbolFinder.FindReferencesAsync`): that project needs a
  reference to the destination; (d) internal symbols used across the new boundary give `InternalsVisibleTo` edges, decided with `IAssemblySymbol.GivesAccessTo`; (e) the reverse direction: remaining code in project A that uses a type that became internal in project B
  means B grants A (an IVT edge B -> A) and A needs a `ProjectReference` to B; (f) packages are `MissingPackageReference` facts only; (g) a cycle in the resulting project graph is detected on the dependency graph (never rely on `Solution.AddProjectReference` throwing) and
  yields `EdgeWouldCloseCycle`, no edge. `AugmentBaseline` applies edges with `Solution.AddProjectReference` and adds an in-memory `[assembly: InternalsVisibleTo("<dest>")]` document to the granting project.
- Done when: `Build` 0 errors.
- Lane: `implementer-senior` (P12: a multi-part semantic analysis with a measured-cost short-circuit).

### Step 25 - Csproj splice body
- Done: 3bbe79e6. `Splice` gained optional `projectReferenceInclude`; Step 26 must pass the real csproj path (default `..\{name}\{name}.csproj` is a placeholder, TODO in code).
- Files: `RoslynSentinel.Engines.Basic/CsprojEdgeWriter.cs`.
- Depends on: Step 22
- Effect: text-neutral
- Change: implement `Splice`: decode the bytes keeping a leading BOM as U+FEFF (as `MoveFileTool` does, the decode block after its NUL check, about `WholeFileWriteTools.cs:352-366`), insert a new `ItemGroup` before `</Project>` with `<ProjectReference Include="..." />` and
  `<InternalsVisibleTo Include="..." />` items in the file's own EOL, skip an edge whose `Include` is already present (idempotent). There is no shared EOL helper (`DetectEol` is duplicated at `CopyAndObsoleteEdits.cs:355` and `TestCategoryTextEditor.cs:129`);
  reuse `CopyAndObsoleteEdits.DetectEol` (internal, same project) rather than writing a third one.
- Done when: `Build` 0 errors.
- Lane: `implementer` (D1: 1 file).

### Step 26 - Wire D3 and the tool-level gate into `MoveFile` (D13)
- Done: 1b7bea2e, refusal-facts fix bd52884d; regeneration 690c74f2 (budget 49500->50000, measured 49677). TODO: `AnalyzeEdgesAsync` runs the analysis three times for a cross-project move; csproj IVT item uses the project name while the in-memory attribute uses the assembly name.
- Files: `RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs`; `RoslynSentinel.Engines.Basic/MoveSetPlanner.cs` (only to expose the analyzer result in the plan); `RoslynSentinel.Common/ToolParams.cs` (two description constants (new));
  `RoslynSentinel.Tests.Server/ClaudeLeanSchemaBudgetTests.cs` (D15).
- Depends on: Steps 4, 8, 13, 14, 15, 20, 24, 25
- Effect: behaviour-changing. `MoveFile` gains `addProjectReferences` and `addInternalsVisibleTo`, both `bool`, default false (the owner's D3: an explicit exception to the "mandatory param" rule, so the tool never widens visibility unasked). With a toggle off,
  each needed edge is a `Finding` naming the edge and the parameter (`MoveFileParameterNames`); with a toggle on and at least one edge found, the gate below runs; with no toggle or no edge the Step 13 path is untouched.
- Change: when a toggle is on and `MoveSetEdgeAnalyzer.AnalyzeAsync` returned an edge: take the lease (`(_workspaceManager as IEdgeGateLeaseSource)?.Lease`; a null source means no lease, as with the fake); `augmented = AugmentBaseline(committed0, ...)`;
  `ValidationEngine.ValidateChangesAgainstBaselineAsync(errorBaseline: committed0, candidateBase: augmented, changes, deletes)`; on errors, reject through the Step 4 builder (no retry for a genuine error, unless the committed instance changed during the compile:
  then it is a window miss); on pass, splice each affected csproj (`CsprojEdgeWriter.Splice`, bytes read from disk, written verbatim) into the same `DocumentChangeSet` and apply through `ValidateAndApplyAsync(..., preValidatedAgainst: committed0)` (D13(c)).
  One automatic retry when the apply is refused and either the committed solution is no longer `committed0` (the helper recompiled without edges) or the error code is `ConcurrentEditRefused`: recompute `augmented` from the new committed solution, recompile, re-apply
  with `preValidatedAgainst` set to the new instance. A second miss returns the safe refusal plus a `WorkspaceChangedDuringGate` fact (D13(e)). A `CsprojEdgeAdded` fact per added edge and, from `apply.ReloadPending`, a `ReloadPending` fact are added to the description.
  A cycle refuses with the cycle named. A `dryRun` compiles against `augmented` and takes no lease. A test seam `internal Func<CancellationToken, Task>? BetweenEdgeGateAndApply` (new, null in production, the precedent is `PostWriteFoldOverride`,
  `PersistentWorkspaceManager.cs:3467`) runs between the compile and the apply on each attempt so the retry is testable. Raise the budget per D15.
- Done when: `Build` 0 errors (solution). Dispatcher: regenerate surfaces, run `ClaudeLeanSchemaBudgetTests`, `ToolSurfaceParityTests`, `EvalToolSurfaceParityTests`.
- Lane: `implementer-senior` (4 files, hot-path interaction with the write chokepoint and a deliberate retry protocol).

### Step 27 - D3 and gate tests
- Done: 2ae10997 (T27.1-T27.7), e566cfeb (T27.8-T27.12); in-process third project worked, no OutOfProcess split (D20)
- Files: new `RoslynSentinel.Tests.Basic/MoveSetEdgeAnalyzerTests.cs`; new `RoslynSentinel.Tests.Tools.Basic/MoveFileProjectEdgeTests.cs`.
- Depends on: Step 26
- Effect: text-neutral
- Change: rows T27.1-T27.12. Analyzer rows use the in-memory `TestSolutionBuilder`; tool rows that write csproj, undo, retry or take the lease need the real `PersistentWorkspaceManager` (`TestSolutionFixture`, as `MoveFileTests` does), because the csproj write,
  `UndoChange`, the lease and the foreign-write re-validation are all chokepoint behaviour.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on both classes.
- Lane: `test-writer`.

### Step 28 - Generalise the shared core ("moved, not copied" mode, sets, overlap refusal)
- Done: 1dbda9c4, a023b55f (moved-not-copied is a private bool, not a new `CallerRewriteMode` value)
- Files: `RoslynSentinel.Engines.Basic/CopyAndObsoleteCallerRewrite.cs` (620 lines); `RoslynSentinel.Engines.Basic/CopyAndObsoleteEdits.cs` (437 lines); `RoslynSentinel.Engines.Basic/CopyAndObsoleteCallerRewriteResult.cs` (the one defaulted
  property, P8); `RoslynSentinel.Tools.Basic/MoveFileCopyAndObsolete.cs` (`WithDiskBomsAsync` generalisation).
- Depends on: Step 5
- Effect: text-neutral for the existing `copyAndObsolete` path (additive overloads; existing entry points and their tests untouched), behaviour-new for the new overloads.
- Change: (a) `CallerRewriteMode` gains "moved, not copied": the `DeclaresWithin(root, oldNamespace)` qualify-reason (`CopyAndObsoleteCallerRewrite.cs:306`) is skipped because the old types are gone and a using is safe; (b) a set overload of `RewriteAsync` taking
  `IReadOnlyList<NamespaceMove>`: set members are rewritten once for both roles (declaration owner and caller); the `wrapped == source` exclusion (`:105`) becomes "excluded from the caller role only when the file's own edits already cover it"; (c) `CollectUsings.AddNeeded`
  (`CopyAndObsoleteEdits.cs:179`) also excludes set members so a moved file gets no using for a sibling that moved with it; the `selfQualified` and partial-qualification refusals do not fire for names that resolve to set members; (d) the shared `Apply` (`:332`) is left
  untouched (commit 1c1c1280 made it shared with the caller-rewrite pass); an overlap-refusing variant is added BESIDE it and reports `EditOverlap` items through `Refusal.StructuredDetail`; (e) a dangling-using sweep: after the set moves, a `using Old;` or
  `global using Old;` that no longer resolves (old namespace emptied) is returned as a `DanglingUsing` with resolution `Removed`, or `RefusedGlobal` when a `global using` cannot be removed safely (an untested hypothesis, covered by Step 31 and backed by the compile gate);
  (f) BOM restoration stays at tool level: `MoveFileCopyAndObsolete.WithDiskBomsAsync` is generalised to `(DocumentChangeSet, paths)` so `ChangeNamespace` shares it (a workspace document's text does not hold the BOM).
- Done when: `Build` 0 errors (solution).
- Dispatcher check after: `RunTest` on `CopyAndObsoleteCallerRewriteTests` and `CopyAndObsoleteEditsTests` (about 11 references each, `RoslynSentinel.Tests.Basic`) and on `MoveFileCopyAndObsoleteTests`: all pass unchanged.
- Lane: `implementer-senior` (over D1 limits: two 400-600 line files, six changes, shared by the live `copyAndObsolete` path).

### Step 29 - Conditional: `LoadSolution` restore fix (only if a Step 6 probe was RED-NU1105)
- Skipped: Step 6 probes GREEN (b89965a5)
- Files: probably `RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs` (`RestoreBeforeLoadAsync` at `:702`); the exact file is decided by the Step 6 failure text, which the dispatcher puts in the brief.
- Depends on: Step 6 (only if red)
- Effect: behaviour-changing (a load that previously failed now restores first), only if the probe is red; if both probes are GREEN, record "D5 closed: probes green" here and skip.
- Change: make the restore cover a project that an existing project references (the Git-run failure shape), then re-run the Step 6 test. If the cause is MSBuild project-reference evaluation order rather than the restore trigger, write the finding as a blocker per CLAUDE.md and stop.
- Done when: `Build` 0 errors; the Step 6 test is GREEN after the fix.
- Lane: `implementer` (D1), conditional.

### Step 30 - `ChangeNamespaceEngine` body
- Done: cbd6b9bf, 532fc143
- Files: `RoslynSentinel.Engines.Basic/ChangeNamespaceEngine.cs`.
- Depends on: Step 28
- Effect: text-neutral (no tool yet)
- Change: compose the Step 28 core into one `DocumentChangeSet`: per file `NamespaceMove`; a file whose top-level types sit in several namespaces refuses with facts and options; a partial type needs all parts in the set (else refuses; the 8-file `Libgit2GitReader*.cs`
  shape is the motivating case); `newNamespace` or `matchFolder` (the latter yields one `FolderNamespace.Expected(...)` per file; D7 requires one target per call, so a set whose files expect different namespaces refuses with the distinct targets listed); the shadowing
  check is a fact only in v1: any symbol named like a segment of the new namespace that would now shadow (Git plan Risk 5, `plans/closed/plan_isolate_git_area_into_own_project.md:754`; the compile gate is the backstop); `dryRun` support; compile rounds follow
  `CopyAndObsoleteMigration.MaxCompileRounds = 3` only if the existing loop is reusable as written. Every changed file is a `CallerFileRewrite` in the returned `CallerRewriteResult` (P8).
- Done when: `Build` 0 errors.
- Dispatcher check after: `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Engines.Basic/ChangeNamespace*.cs")` lists nothing.
- Lane: `implementer-senior` (a new engine composing five behaviours over a shared core).

### Step 31 - Core and engine tests for `ChangeNamespace`
- Done: 47dfa097, afc46dc9 (T31.17 lives in `Tests.Tools.Basic/ChangeNamespaceCoreTests.cs`: `WithDiskBomsAsync` is internal to Tools.Basic)
- Files: new `RoslynSentinel.Tests.Basic/ChangeNamespaceCoreTests.cs`; new `RoslynSentinel.Tests.Basic/ChangeNamespaceEngineTests.cs`.
- Depends on: Step 30
- Effect: text-neutral
- Change: rows T31.1-T31.17 (fixture shape (8) of the proposal, one row per reference form of the proposal's Motivation list plus the hazards found in planning).
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on both classes.
- Lane: `test-writer`.

### Step 32 - `ChangeNamespace` tool method, toolset catalog, text and policy rows (behaviour-changing)
- Done: d9cc08b1, 3b1c0d6f; regeneration d94d5349, d2ee0705 (budget unchanged at 49500); hint ratchet green
- Files: `RoslynSentinel.Tools.Basic/CoreRefactoringTools.cs` (610 lines; the method beside `MoveAllTypesToFilesTool` at `:416`); `RoslynSentinel.Common/ToolsetCatalog.cs` (`moveExtract` at `:43`, `Summaries` entry about `:77`);
  `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs` (the moveExtract text, `:26-27`); `RoslynSentinel.Common/ErrorPolicyIntentTable.cs` (new `["ChangeNamespace"]` row beside `MoveFile` at `:120`);
  `RoslynSentinel.Tests.Server/ClaudeLeanSchemaBudgetTests.cs` (D15, only if the measurement shows growth: `ChangeNamespace` is in no startup surface, so the claude-lean total may not move).
- Depends on: Step 30
- Effect: behaviour-changing. A new tool `ChangeNamespace` exists in the on-demand `moveExtract` toolset (zero startup tokens: not in `ToolClassRegistry.ClaudeLeanToolNames`, `:55`); the toolset text and summary name it.
- Change: `ChangeNamespace(ToolCallReason reason, string filePath, string? newNamespace = null, bool matchFolder = false, [ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, CancellationToken cancellationToken = default)` (defaults per the repo pattern);
  `filePath` is a file or a folder (all `.cs` under it; no globs, same rule as D2); exactly one of `newNamespace` and `matchFolder: true` (an either/or pair, so add `// CONDITIONAL-PARAM-REVIEW-REQUIRED:` per the repo convention and refuse neither/both with
  `InvalidArgument` naming both options). The method validates, calls `ChangeNamespaceEngine`, calls `ValidateAndApplyHelper.ValidateAndApplyAsync(..., "change_namespace", ...)` with `EditImpactGate.Describer`, and returns `AppliedChangeSummary.From(...)`. No new
  `[McpServerToolType]` class, so no `ToolClassRegistry.cs` row (`CoreRefactoringTools` is already registered). `ErrorPolicyIntentTable` row `Fixed(E)` like `MoveFile` unless `ErrorPolicyKindRatchetTests` says otherwise. The description states: moves a namespace
  declaration and every reference to its types, one build check, undoable; one target namespace per call; partial types need all parts in the set.
- Done when: `Build` 0 errors (solution). Dispatcher: regenerate surfaces, run `ClaudeLeanSchemaBudgetTests`, `ToolSurfaceParityTests`, `EvalToolSurfaceParityTests`.
- Lane: `implementer-senior` (4-5 files across two projects and a registry-adjacent text surface).

### Step 33 - Surface and tool tests for `ChangeNamespace`
- Done: a1c6d116 (`ToolEffectList` left unchanged: its 55-tool header is frozen by `ToolEffectListTests`)
- Files: `RoslynSentinel.Tests.Server/ClaudeLeanModeTests.cs` (`ClaudeAdvancedBaseline` `:56`; `Claude_Mode_StillExposesItsPreExistingToolSet` `:113`; template `QualifyCrefs_IsRegisteredInModeMap_AndOnDemandOnlyInClaudeLean` `:258`);
  `RoslynSentinel.Tests.ErrorVariants/ToolEffectList.cs` (`WriteNames`: add `ChangeNamespace`; check whether `MoveFile` appears further down and whether its parameter shape is pinned); new `RoslynSentinel.Tests.Tools.Basic/ChangeNamespaceToolTests.cs`.
- Depends on: Step 32
- Effect: text-neutral
- Change: rows T33.1-T33.4.
- Done when: `Build` 0 errors.
- Dispatcher check after: `RunTest` on `ClaudeLeanModeTests`, `McpToolsetControlTests` (`Enable_AddsTheWholeSet_AndAccountsForEveryName`, `:61`) and the new class.
- Lane: `test-writer`.

### Step 34 - Recipe: split a folder into a new project
- Files: new `docs/current/references/reference_split_folder_into_new_project_with_movefile.md` (reference template, `templates/reference.md`).
- Depends on: Steps 13, 26, 32
- Effect: text-neutral (documentation)
- Change: one procedure: `CreateProject`; `MoveFile(moves: [{folder pair}], addProjectReferences, addInternalsVisibleTo)`; `ChangeNamespace(newNamespace)`; each with the call shape, the facts the tools return and what to do with each (missing package reference, source
  csproj items "not updated", `LoadSolution(forceReload)` for a destination in no loaded project, a `ReloadPending` fact). States the workflow positively; names the gated tool only through `McpToolsetControl(toolSet: moveExtract, enabled: true)`. Marks the interim
  link-stand-in recipe as superseded.
- Done when: the file exists, is ASCII-only, and is linked from this plan.
- Done 2026-11-25: [reference_split_folder_into_new_project_with_movefile.md](../../references/reference_split_folder_into_new_project_with_movefile.md) (ASCII-only; the interim link-stand-in recipe is marked superseded).
- Lane: `implementer` (D1: 1 file).

### Step 35 - LIVE: reload wait, 60-file timing, `ChangeNamespace` dry run
- Partial 2026-11-25 (server 3b1c0d6f): (c) CLEAN - `GitEngine.cs` dry run validated, 23 files / 98 references; folder `RoslynSentinel.Git/` dry run validated, 144 files. Triaged defect: in moved mode `WhyQualify` qualifies (global using of the old namespace; old using must stay) where an added using is safe, and cites another project's `GlobalUsings.g.cs` (TODO in `CopyAndObsoleteCallerRewrite.cs`, INBOX 2026-11-25 03:45). (The client's ToolSearch listing still shows the pre-Step-13 `MoveFile` schema, but the server accepts `moves`/`dryRun`; no reconnect needed.)
- Done 2026-11-25 (server bd52884d, no agents running): (a) FRESH - scratch two-project solution, `MoveFile(addProjectReferences: true)` moved `A/Worker.cs` to `B` in 2.8 s (no `SELF-WAIT`), wrote the ProjectReference and reported the reload fact; the next read (`GetDiagnostics` project B) saw 0 errors, i.e. the new reference. The reload had finished before the read was issued (small solution), so the wait path itself is covered by T18, not by this run. (b) KEEP 200 - same-project dry runs on the repo, warm and sequential: 1 file 8.1 s, 60 files 8.2 s, ratio 1.02 (gate cost is the affected-project compile, not file count; a first, cold, concurrent pair took 37 s and 9 s and is discarded). Gate time is well under the 30 s lease default (D19). Cross-project dry run not measured; `AnalyzeEdgesAsync` runs three passes there (TODO from Step 26).
- Files: none (a dispatcher run after commit and a server restart; results recorded in this plan's Risks).
- Depends on: Steps 13, 14, 26, 32 (all committed, server restarted; `McpServerStatus` `lastLaunch.servedSha` shows the commit)
- Effect: text-neutral (measurement; dry runs only, nothing is written to the repo)
- Change: three measurements.
  (a) **Reload wait (D12, verifies rather than decides).** On a scratch solution (not the repo) apply `MoveFile(addProjectReferences: true)` that writes a csproj, then immediately call a read tool. Outcome classes: `WAITED-AND-FRESH` (the read waited, then saw the new
  reference: ideal), `STALE-READ-OBSERVED` (the read saw the old project graph: tracking missed a path, escalate R16 to the explicit member and to the debounce path), `SELF-WAIT` (the writing call itself blocked on its own reload: a defect in Step 14), `TIMEOUT` (the reload outlasted
  the load wait: record the seconds and the `SolutionLoadTimeoutException` text).
  (b) **60-file timing (D4).** On the repo solution, `MoveFile(dryRun: true)` of one file and of 60 files from one folder into a new subfolder of the same project; record seconds for each. Outcome classes by ratio T60/T1: `<=2x` keep the 200 cap, `2-5x` lower
  `MoveSetPlanner.MaxFilesPerCall` to 100, `>5x` lower it to 60 (thresholds are this plan's proposal, R4); `REFUSED-FOR-OTHER-REASON` (re-pick the folder). A same-project dry run does not show cross-project compile cost; an optional cross-project dry run is recorded as such.
  (c) **ChangeNamespace dry run.** `ChangeNamespace(filePath: RoslynSentinel.Git/GitEngine.cs, newNamespace: RoslynSentinel.Git, dryRun: true)`, then the same over `RoslynSentinel.Git/` once: `CLEAN` (gate passes, line counts plausible), `REFUSED-WITH-FACTS` (record the facts; each is a
  defect or a legitimate refusal, triaged), `GATE-RED` (a defect in Step 28/30: findings doc). The actual namespace change of the Git files is the Git plan's follow-up and is NOT applied here.
- Done when: each of (a)-(c) has one named outcome class recorded in Risks; if (b) lowers the cap, the constant is changed in the same slice and the Step 9 cap test (which reads the constant) stays green.
- Lane: dispatcher (plain calls; not a supervisor job).

### Step 36 - Final regeneration, caller-sweep confirmation and doc annotations
- Done: no drift (filtered run with ratchets on f8846852, 93/93); R9 delta recorded (eacd90c8: `MoveFile` catalog entry 1053 -> 2113 chars); sweep clean; annotations in the two docs. T27.12b tightened to `ValidationFailed` (f8846852).
- Files: `RoslynSentinel.Tests.Harness/ToolSurfaceGolden/*.txt` and `_catalog.json`; `RoslynSentinel.Tests.ModelEval/ToolSurfaceGolden/eval-Harness_default__toolset_full.txt` and `_catalog.json`; `docs/generated/architecture_tools.md` (about `:105`) and
  `docs/generated/todo_markers.md` (regenerate with `scripts/Generate-ArchitectureMap.ps1` and `scripts/Get-TodoMarkers.ps1`, never by hand); annotations (not rewrites) in `docs/current/plans/plan_test_placement_guardrail_and_shared_test_helpers.md` (about `:336`, the old
  `MoveFile(sourcePath, destinationPath, namespaceHandling: keep)` call shape) and `docs/current/proposals/closed/proposal_move_file_tool.md` (its one `sourcePath`/`destinationPath` mention).
- Depends on: Steps 13, 26, 32
- Effect: text-neutral (regenerated artifacts; one-line annotations)
- Change: (a) confirm the P11 regenerations after Steps 13, 26 and 32 left no drift: `ToolSurfaceParityTests`, `EvalToolSurfaceParityTests`, `ArchitectureDocFreshnessTests`, `ClaudeLeanSchemaBudgetTests` (D15); (b) record the schema-size delta: the `MoveFile` entry in
  `_catalog.json` before (git HEAD) and after, because `MoveFile` is a `claude-lean` core tool (R9); (c) re-run the D1 sweep inventory and confirm no remaining mention of the old shape: `Search(mode: text, query: "sourcePath|destinationPath")` over `.cs` and Grep over `*.md`, `.claude/`,
  `scripts/`, AgentLoop and ModelEval fixtures. Known inventory on 2026-11-25: no skill, agent doc or prompt names `MoveFile` outside handoff and journal history; `RetypeSliceExactLinesScenario.cs` mentions `MoveFile` only as seeded source text (not a caller); the hint at
  `ProjectStructureEngine.cs:1868` is fixed in Step 5; `MoveFileCopyAndObsolete.cs:84` names the tool, not the parameters (confirm); the `WholeFileWriteTools.cs` messages and the `[Description]` ("a directory ... is refused") are rewritten in Step 13 (confirm);
  `ToolEnums.cs:150-157` is rewritten in Step 13 (confirm); `docs/current/archive/tool_documentation.md` is frozen history and is not edited.
- Done when: both parity tests, `ArchitectureDocFreshnessTests` and `ClaudeLeanSchemaBudgetTests` pass; the schema-size delta (bytes) is recorded in Risks R9.
- Lane: dispatcher (plain `Build`/`RunTest` calls); `implementer-sweep` only if (c) finds sites.

### Step 37 - Closing: stub sweep, ratchets, bookkeeping
- Done: (a) no stub in the plan's files (glob widened to `CsprojEdge*`); (b) ratchets green in the f8846852 filtered run; (c) full suites green at bd52884d and eacd90c8; (d) bookkeeping in the closing commit. Deviation: TODO.md "MoveFile namespaceHandling `update`" stays open, re-scoped to the D6 composition (unbuilt; `ChangeNamespace` after `MoveFile` is the two-call route meanwhile), so the `BatteryToolFactory.cs:11` TODO stays too.
- Files: none to edit except bookkeeping docs (`docs/current/TODO.md`, `docs/current/CLOSED.md`, blockers) by the dispatcher.
- Depends on: all
- Effect: text-neutral
- Change: (a) `Search(mode: text, query: "NotImplementedException", fileGlob: "RoslynSentinel.Engines.Basic/{MoveSet*,ChangeNamespace*,FileMovePair,NamespaceMove}.cs")` plus `// TODO implement (Step` over the plan's files: any hit fails the step (stubs build clean, nothing else
  catches them); `ProjectStructureEngine.cs` keeps its deliberate `ToolNotImplementedException` (D10) and is not a hit; (b) run the ratchets on the changed paths: `BareFileWriteRatchetTests`, `PathComparisonRatchetTests`, `AppliedChangeSummaryConstructionRatchetTests`,
  `ErrorPolicyKindRatchetTests`, `MessageTextRatchetTests`, `StaleReadPolicyRatchetTests`, `EolAuditRatchetTests`, `TestPlacementRatchetTests` (all `RoslynSentinel.Tests.Basic`), `ToolHintRatchetTests`, `ToolReturnShapeRatchetTests`, `ArchitectureDocFreshnessTests`,
  `ClaudeLeanSchemaBudgetTests` (all `RoslynSentinel.Tests.Server`); (c) full `Build`, then `RunTest` against the baselines; (d) TODO.md lines 56 and 61 and the `BatteryToolFactory.cs:11` note move to `CLOSED.md` with the closing commit hash, the two blockers move to
  `blockers/closed/`, the proposal status becomes IMPLEMENTED, this plan's status becomes IMPLEMENTED and the file moves to `plans/closed/`; the closing commit carries `Closes: plan_multi_file_movefile_and_namespace_change_tool`.
- Done when: (a) finds no stub; (b) and (c) show no new failures.
- Lane: dispatcher.

### Slice map (two agents at most, disjoint files per round)

| Round | Agent 1 | Agent 2 | Why paired |
| --- | --- | --- | --- |
| 1 | Step 1 skeleton (Engines.Basic, 3 new files) | Step 2 test funnel, part 1 (`MoveFileTests.cs`) | text-neutral, disjoint projects |
| 2 | Step 4 rejection-builder extraction (`ValidateAndApplyHelper.cs`) | Step 3 test funnel, part 2 (`MoveFileTests.cs`, after Step 2) | text-neutral, disjoint |
| 3 | Step 5 Part 2 skeleton, D10 comment (Engines.Basic) | Step 6 D5 probe (new test file + `BuildIsolationTests`) | text-neutral; the probe needs nothing else |
| 4 | Step 7 planner stage 1 (`MoveSetPlanner.cs`) | Step 8 `ValidationEngine` split (`ValidationEngine.cs`) | text-neutral, disjoint |
| 5 | Step 10 planner stage 2 (`MoveSetPlanner.cs`, after Step 7) | Step 9 tests for Step 7 | tests written while the next slice is built |
| 6 | Step 12 tests for Step 10 | Step 11 tests for Step 8 | two new test files, disjoint |
| 7 | Step 13 `MoveFileTool` rewrite (senior; first behaviour-changing) | Step 14 reload tracking (senior; Common) | disjoint files, disjoint projects |
| 8 | Step 15 grouped refusal | Step 16 tool-level set tests | both need only Step 13 |
| 9 | Step 17 rename hints | Step 18 reload-tracking tests | disjoint projects |
| 10 | Step 20 lease (senior; `PersistentWorkspaceManager.cs` after Step 14) | Step 19 rename-hint tests | disjoint |
| 11 | Step 22 D3 skeleton | Step 21 grouped-refusal tests | disjoint |
| 12 | Step 24 edge analyzer (senior) | Step 25 csproj writer | disjoint new files, both need only Step 22 |
| 13 | Step 26 wire D3 and the gate (senior) | Step 23 lease tests | disjoint |
| 14 | Step 28 shared core (senior) | Step 27 D3 and gate tests | disjoint |
| 15 | Step 30 `ChangeNamespaceEngine` (senior) | Step 29 conditional `LoadSolution` fix (only if Step 6 was red) | disjoint; Step 30 alone if Step 6 was green |
| 16 | Step 32 tool method and catalogs (senior) | Step 31 core and engine tests | disjoint |
| 17 | Step 34 recipe doc | Step 33 surface and tool tests | disjoint |
| 18 | Step 35 LIVE (dispatcher, one lane; needs a restart on committed HEAD) | | |
| 19 | Step 36 final regeneration and sweep confirmation (dispatcher) | | |
| 20 | Step 37 closing (dispatcher) | | |

Steps 1-12 are text-neutral (Step 5 corrects one stub hint string), so a hold (another plan's baseline, a review) does not idle the run. Part 2 (Steps 5, 28, 30-33) does not depend on Part 1 and may move earlier into any round with an idle agent, subject to the file lists.
Steps 13, 14, 15, 17, 20, 26, 28-29 (if red), 32 are the behaviour-changing ones. The budget test is edited only in Steps 13, 26 and 32 (rounds 7, 13, 16), never in the same round twice.

## Test table

The planner defines the rows; the reviewer confirms each row exercises the named changed path; the implementer's obligation is a green build and the rows it was given. Pass criteria are structured (error code, enum, counts, byte equality); a text assert
appears only where the wording is under test, and then against the shared constant. "Heavy" fixtures are justified in the row.

| Id | Class.method (new unless noted) | Behaviour covered | Fixture | Asserts | Pass criterion |
| --- | --- | --- | --- | --- | --- |
| T6.1 | `LoadSolutionHandWrittenCsprojRestoreIntegrationTests.ForceReload_RestoresHandWrittenProjectReferencedByExistingProject` | D5 probe with `forceReload` | scratch solution, real `dotnet` (heavy: restore and `sln add` are the behaviour under test; `OutOfProcess`) | B is in the loaded solution; no workspace-failure diagnostic | outcome class GREEN / RED-NU1105 / RED-OTHER (Step 6) |
| T6.2 | `...LoadWithoutForceReload_RestoresHandWrittenProjectWhenAssetsAreMissing` | D5 probe, plain load (P10) | same | same | same |
| T9.1 | `MoveSetPlannerEligibilityTests.Expand_TwoFilePairs_ReturnsTwoPlannedMoves` | a set of 2 file pairs | `MoveSetFixture` (real files) | `Moves.Count == 2` | count equal |
| T9.2 | `...Expand_FolderPair_SubfoldersPreserved_NonCsFileIncluded` | D2 expansion (fixture shape (2)) | folder with a nested folder and a `.txt` | destinations equal destination root plus relative path | all paths match |
| T9.3 | `...Expand_FolderPairIntoExistingFolder_SameNameFileRefuses` | D2 merge, same name refuses | destination folder holding a same-named file | `MoveSetRefusedException` with one `DestinationExists` offender naming it | reason and path match |
| T9.4 | `...Expand_OneBinaryOneNonUtf8OneExistingDestination_ListsAllThreeOffenders_DiskUntouched` | all-or-nothing eligibility lists every offender (shape (3)) | three bad files plus one good | offenders `Binary`, `NotUtf8`, `DestinationExists`; exception error code is `TargetIneligible` (mixed, P6); disk bytes unchanged | three reasons, mixed code, unchanged bytes |
| T9.5 | `...Expand_MoreThanMaxFilesPerCall_RefusesOverCap_BoundaryAccepted` | D4 cap | `MoveSetPlanner.MaxFilesPerCall + 1` and exactly `MaxFilesPerCall` tiny files | `OverCap` for N+1; none for N | reason and boundary match |
| T9.6 | `...Expand_DuplicateDestination_SamePath_Missing_EachMapsToItsErrorCode` | P6 map for single-reason refusals | one case per reason | exception error code equals `MoveOffender.ErrorCodeOf(reason)`: `NotFound`, `InvalidArgument` | codes match |
| T11.1 | `ValidationEngineBaselineSplitTests.Validate_EdgeMakesDuplicateTypeVisible_RefusesCs0433` | D13(b): an edge-induced error IS refused | in-memory solution, project X gains a reference to a project holding a type X already declares | report contains a CS0433; the project with no changed document is in the compiled set | error reported |
| T11.2 | `...Validate_BaselineEqualsCandidateBase_MatchesExistingCore` | delegation `(baseline, baseline)` | same inputs as `MoveBindCheckTests` | outcome equals `ValidateChangesWithCandidateAsync` | equal reports |
| T11.3 | `...Validate_AddedReferenceFixesMissingNamespace_NoNewErrorsAgainstCommitted` | the delta is against `errorBaseline` | file using a type of another project; `candidateBase` adds the reference | report not an error | passes |
| T11.4 | `...Validate_DependentsOfAnEdgeProject_AreCompiledViaCandidateBaseGraph` | dependents expansion uses `candidateBase` | A depends on B depends on C; edge makes B depend on a new D | errors in A introduced by the edge are reported | error reported |
| T12.1 | `MoveSetPlannerFactsTests.Check_ThreeFilesSameNamespaceMismatch_OneKeptNamespaceFactPerDistinctMismatch` | kept-namespace fact once per distinct mismatch | 3 files, same mismatch, plus 1 with another | `KeptNamespace` facts: 2 | count 2 (not 4) |
| T12.2 | `...Check_SourceCsprojCompileRemoveNamesMovedFolder_FactNotEdit` | D8 | csproj text with `<Compile Remove="Git\**" />` | `SourceCsprojItemNamesFolder` fact; no csproj key in `Changes` | fact present, no edit |
| T12.3 | `...Check_DestinationInNoLoadedProject_CsFileRefuses_NonCsMoves` | D14 | destination folder without csproj | `OutsideLoadedProject` for the `.cs` (`InvalidArgument`); the `.txt` is planned | one offender, one move |
| T12.4 | `...Check_PartialTypePartMovedWithinOneProject_Plans` | D16: no partial rule for `keep`; same-project part move legal | one part of a 3-file partial class moved inside one project | no refusal | plans |
| T12.5 | `...Check_FolderPair_EmptySourceFolderLeftFact` | folder left in place fact | folder pair | `EmptySourceFolderLeft` | present |
| T12.6 | `...Check_NonCsFile_NoKeptNamespaceFact` | non-`.cs` has no namespace fact | `.txt` | no `KeptNamespace` | absent |
| T16.1 | `MoveFileSetTests.MoveCluster_MutuallyReferencingTypesAndPartialClass_CrossProject_OneCall_Succeeds` | the original blocker: a connected cluster across a project edge in one call (shape (1)) | `TestSolutionFixture` (real manager); cluster files (two mutually referencing classes, a partial class in 3 files) written into the Tests project folder before `LoadAsync` and moved to Core (Tests references Core, no edge needed) | `IsError` false; one `ChangeId`; all files at destination; solution compiles (heavy: only the real gate proves the cluster compiles as a set) | success, no new diagnostics |
| T16.2 | `...MoveSameClusterFileByFile_StillRefused` | regression pin: the original blocker still refuses per file | same fixture, one file per call | `ErrorCode` is `ValidationFailed`; nothing moved | refused, disk unchanged |
| T16.3 | `...MoveSet_EligibilityFailure_LeavesEverythingUntouched` | tool-level all-or-nothing (shape (3)) | three bad files | every offender in `StructuredDetail`; file bytes unchanged | unchanged bytes, 3 offenders |
| T16.4 | `...MoveSet_DestinationInNoLoadedProject_Refuses` | shape (5) at the tool, D14, single file included | destination outside any csproj, one `.cs` | `ErrorCode.InvalidArgument`; the hint calls `ToolCallHint.Next` for `LoadSolution` | code matches; hint present |
| T16.5 | `...UndoChange_RestoresWholeSet` | one changeId undoes N files (shape (6)) | real manager, 5-file set | after `UndoChange`: sources present with original bytes, destinations absent | byte equality for all 5 |
| T16.6 | `...MoveSet_CrlfLfBom_ByteIdentical` | verbatim bytes (shape (7)) | real manager; one CRLF, one LF, one BOM file | destination bytes equal source bytes | equal |
| T16.7 | `...MoveSet_RecordsFileMoveMemoryAndEffectPerPair` | `FileMoveMemory` and `FileMoved` per pair | real manager; 3-pair set | `FileMoveMemory.Shared` lookup returns the destination for each source; 3 `FileMoved` effects | 3 and 3 |
| T16.8 | `...DryRun_ReturnsExpandedPairs_WritesNothing` | `dryRun` | folder pair | `DryRun` true, expanded pairs listed, disk unchanged | disk unchanged |
| T16.9 | `...NamespaceHandling_CopyAndObsoleteWithTwoFiles_IsInvalidArgument` | D1/D6 rule | two pairs | `ErrorCode.InvalidArgument` | code matches |
| T16.10 | `...NamespaceHandling_Update_IsNotImplemented_AndHintNamesChangeNamespace` | `update` refused | one pair | `ErrorCode.NotImplemented`; message contains the output of the same `ToolCallHint.Next("ChangeNamespace", ...)` call | code matches; hint equal |
| T16.11 | `...MoveFileParameterNames_MatchTheToolsActualParameterNames` | P13: one definition, pinned to reality | reflection on `MoveFileTool` and `FileMovePair` | each constant equals the camelCase JSON name | equal |
| T18.1 | `WorkspaceReloadTrackingTests.CsprojWrite_StatusIsLoadingWhenTheApplyReturns_ThenLoaded` | D12: the post-write reload is tracked | real manager; `OpenSolutionOverride` gated on a `TaskCompletionSource` (heavy: the reload path is the behaviour under test) | `SolutionLoadStatus == Loading` after the apply returns and before the gate opens; `WaitForLoadAsync` returns `Loaded` after | status values |
| T18.2 | `...ReadDuringReload_WaitsAndReturnsTheSwappedSolution` | the next call's read entry waits | same | `GetSolutionAsync` issued during the reload returns the new instance (not equal to the pre-reload instance) | reference inequality, no timing assert |
| T18.3 | `...ReloadFailsWithOldWorkspaceKept_TerminalStatusLoaded_LastLoadFailureUnchanged` | failure handling | `OpenSolutionOverride` throws | `SolutionLoadStatus == Loaded`; `ReloadFailure` recorded; `LoadState.LastLoadFailure` unchanged | values match |
| T18.4 | `...ApplyOutcomeReloadPending_TrueForCsprojWrite_FalseForCsWrite` | `ApplyOutcome.ReloadPending` | helper call with a csproj key, then a `.cs` key | `ReloadPending` true / false | values match |
| T18.5 | `...WritingCallReturnsWhileReloadIsStillGated` | no self-wait; MoveFile does not block (rejected option c) | same | the apply task completes while the gate is closed | task completed |
| T19.1 | `GitStatusRenameHintMultiPairTests.MultiPairBlob_YieldsOneDefinitiveHintPerPair` | blob with N pairs | blob written by the real apply path for a 3-pair `MoveFile` (not hand-built JSON, so the pairing hook is exercised) | 3 `MovePair`s with correct source and destination | 3 correct pairs |
| T19.2 | `...MultiPairBlob_TwoSourcesWithIdenticalText_FallsBackToTier2ForThoseOnly` | the `FirstOrDefault` mis-pairing | two sources with identical text plus one unique | the ambiguous pair is not returned as tier 1; the unique one is | tiers match |
| T19.3 | `...ChainAcrossBlobs_AToBInOneBlob_BToCInAnother_YieldsJoinedPair` | `JoinChains` with N pairs per blob | two blobs | joined A->C present | present |
| T19.4 | `...MoveCycle_DoesNotHang` (existing `ReadMovePairs_MoveCycle_...` stays green) | cycle guard | existing | existing | existing |
| T21.1 | `MoveFileSetErrorGroupTests.GateRefusal_GroupsErrorsBySetFile` | errors grouped by set file | a set missing one dependent file | `StructuredDetail` contains a `MoveSetErrorGroup` list; the missing file's group is non-empty | group keyed by the destination file |
| T21.2 | `...GateRefusal_ErrorOutsideSet_GoesToOutsideGroup` | "outside the set" group | break in a non-set file | outside group present | present |
| T23.1 | `EdgeGateLeaseTests.SecondWriter_WaitsWhileLeaseHeld_ThenProceedsOnTimeout` | D13(f): waiter proceeds after the bound | real manager (the hook is in its body), `MaxWait` set to a short value | the second apply completes after the bound; a waiting `Finding` is present | completed; finding present |
| T23.2 | `...Holder_PassesThroughItsOwnApply` | the holder is not blocked | same | the holder's apply completes immediately | completed |
| T23.3 | `...SecondWriter_ProceedsAtOnceWhenLeaseReleased` | release wakes the waiter | same | completes after release, before the bound | completed |
| T23.4 | `...LeaseDoesNotHoldSolutionLock_ReadsProceedWhileHeld` | rule (c): no lock across the compile | same | `GetSolutionAsync` returns while the lease is held | returned |
| T23.5 | `...NoLeaseHeld_ApplyHasNoWait` | no-op when idle | same | apply completes with no waiting finding | no finding |
| T27.1 | `MoveSetEdgeAnalyzerTests.Analyze_ClusterFromTestsToCore_TestsOnlyDependency_MissingReferenceEdge` | missing-reference edge (shape (4)) | in-memory builder: move a cluster to a project lacking a reference the cluster needs | `NeededProjectEdge` of kind `ProjectReference` | edge present |
| T27.2 | `...Analyze_AddedEdgeWouldCloseCycle_NoEdgeAndFact` | cycle | same direction reversed | `EdgeWouldCloseCycle` fact, no edge | fact present |
| T27.3 | `...Analyze_InternalSymbolAcrossBoundary_InternalsVisibleToEdge` | IVT edge | internal helper used across the new boundary | `InternalsVisibleTo` edge | present |
| T27.4 | `...Analyze_RemainingCodeUsesTypeNowInternalInDestination_DestinationGrantsSource` | reverse IVT direction (P12) | type moved A to B, A's remaining code uses it, type internal | IVT edge B grants A; plus `ProjectReference` A to B | edges present |
| T27.5 | `...Analyze_AllPairsInOneProject_ReturnsNoEdgesEvenWhenAnotherProjectReferencesTheMovedType` | same-project short-circuit (behaviour pin; the cost is not asserted) | one project, another project references the moved type | no edges | empty |
| T27.6 | `...Analyze_DestinationAlreadySeesProjectViaTransitiveReference_NoRedundantEdge` | R8 fixture | A references B references C; destination A uses C | no edge A to C | empty |
| T27.7 | `CsprojEdgeWriterTests.Splice_PreservesCrlfAndBom_Idempotent` (in the analyzer test file or its own) | byte-preserving splice | CRLF+BOM csproj bytes | only the added lines differ; a second splice is a no-op | equality |
| T27.8 | `MoveFileProjectEdgeTests.AddProjectReferences_On_WritesCsprojUnderSameChangeId_UndoRevertsIt` | D3: csproj written only on gate pass, same changeId, undo reverts (shape (4)) | real manager via `TestSolutionFixture` plus a third project (heavy: the positive edge needs a third project; csproj write, undo and chokepoint are behaviour under test) | csproj text contains the new item; `UndoChange` restores the original bytes; one `ChangeId` | bytes equal after undo |
| T27.9 | `...AddProjectReferences_Off_ReportsFactNamingParameter` | toggle off is facts only | same | csproj bytes unchanged; fact kind `MissingProjectReference`; the parameter name is `MoveFileParameterNames.AddProjectReferences` (the shared constant, T16.11 pins it) | unchanged; fact present |
| T27.10 | `...GateRefusal_CsprojNotWritten` | csproj untouched when the gate fails | a set that fails for a non-edge reason | csproj bytes unchanged | unchanged |
| T27.11 | `...EdgeInducedError_IsRefusedAndNothingWritten` | D13: an error the new reference itself causes (a CS0433 duplicate type) is refused | real manager; third project declaring a type the moved file's project also declares | `ErrorCode.ValidationFailed`; csproj and files unchanged | refused, unchanged |
| T27.12 | `...WorkspaceMovesBetweenCompileAndApply_RetrySucceeds` / `..._SecondMiss_ReturnsRefusalWithWorkspaceChangedFact` | D13(e) one retry, then the fact | real manager; the `BetweenEdgeGateAndApply` seam performs a foreign write (lease `MaxWait` short) on attempt 1 only / on both attempts | retry: success and a changed `ChangeId`; second miss: refusal plus `WorkspaceChangedDuringGate` fact | outcomes match |
| T31.1-T31.8 | `ChangeNamespaceEngineTests.Change_<Form>` | each reference form of the proposal's Motivation list: using directive added and the old one removed when unused; fully qualified; partially qualified; `global::`; alias directive; `cref`; `nameof`/`typeof`; attribute, base list, generic argument | in-memory solution via `TestSolutionBuilder` | rewritten text contains the new form; solution compiles; untouched files byte-identical | compiles; edits only where expected |
| T31.9 | `...Change_GlobalUsingOfOldNamespace_Qualifies` | `global using` | file with `global using Old;` | no dangling using; compiles | compiles |
| T31.10 | `...Change_PartialTypeInThreeFiles_AllPartsInSet_Works` / `..._PartOutsideSet_Refuses` | partial rule (ChangeNamespace keeps it) | 3-file partial | works with all parts; refuses with 2 | outcome enum |
| T31.11 | `...Change_CallerInsideOldNamespace_GetsUsing_MovedNotCopied` | "moved, not copied" mode | a type in the old namespace using the moved type | `using New;`, not qualification; compiles | compiles |
| T31.12 | `...Change_SetMembersReferenceEachOther_NoUsingBetweenThem` | `AddNeeded` excludes set members | 2 moved types referencing each other | no `using` for a sibling | absent |
| T31.13 | `...Change_OldNamespaceEmptied_DanglingUsingRemovedOrFlagged` | dangling-using hazard (hypothesis; this row turns it into a fact) | caller with `using Old;` where Old becomes empty | a `DanglingUsing` with resolution `Removed`; no CS0246 | compiles |
| T31.14 | `...Change_NameShadowing_FactReported` | D7 shadowing check | symbol named like a new-namespace segment | shadowing fact | present |
| T31.15 | `...Change_MultiNamespaceFile_Refuses` | multi-namespace file refusal | file declaring two namespaces | typed refusal with facts and options | refused |
| T31.16 | `...Change_OverlappingEdits_RefusesWithEditOverlap_NotSkipped` | the overlap-refusing `Apply` variant | a moved member whose span overlaps a caller edit | `Refusal` with `EditOverlap` items; the shared `Apply` still skips (existing tests green) | refused |
| T31.17 | `ChangeNamespaceCoreTests.RewrittenCaller_KeepsDiskBom` | BOM restoration (`WithDiskBomsAsync`, generalised) | BOM-prefixed caller | BOM still present | byte equal |
| T33.1 | `ClaudeLeanModeTests.ChangeNamespace_IsRegisteredInModeMap_AndOnDemandOnlyInClaudeLean` (template `:258`) | registration and gating | existing fixture | `ChangeNamespace` in the Claude advanced baseline and not in `CoreTools` | lists match |
| T33.2 | `McpToolsetControlTests.Enable_AddsTheWholeSet_AndAccountsForEveryName` (existing, `:61`) | `moveExtract` accounting with the added name | existing | passes with the catalog edit only | passes |
| T33.3 | `ChangeNamespaceToolTests.DryRun_ReturnsFileCountsAndWritesNothing` and `.UndoChange_RestoresAll` | tool wiring, `dryRun`, undo | `TestSolutionFixture` (undo and the chokepoint are under test) | `DryRun`; disk unchanged; after undo all files equal the originals | equal |
| T33.4 | `ChangeNamespaceToolTests.NeitherOrBothOfNewNamespaceAndMatchFolder_InvalidArgument` | either/or pair | no args / both | `ErrorCode.InvalidArgument` | code matches |

## Out of scope

- `MoveFile namespaceHandling: update` as a thin composition of the two tools (refused, naming `ChangeNamespace`, until a later plan; D6).
- `matchFolder` with several target namespaces per call (D7: one target; P3 keeps the core API ready for it).
- Prefix mapping (`fromPrefix`/`toPrefix`); the Git run's three source namespaces are three `ChangeNamespace` calls.
- Implementing `SplitProjectByFolder` or removing it from `projectAdmin` (D10: stays a stub with a comment).
- A restore tool and `CreateProject` parameters (`references`, `packages`, `internalsVisibleTo`): a separate small proposal.
- Applying the Git namespace change itself (`RoslynSentinel.Git/GitEngine.cs:13`, `GitProcess.cs:7`): the Git plan's follow-up, after Step 32 exists.
- Tracking the debounce reload (`OnDebounceTimerElapsed`, external edits) as a load (R16); `MoveType`/`MoveAllTypesToFiles` defects (INBOX 2026-11-24 19:45/19:50); a Roslyn Features-based refactoring; renaming the `CopyAndObsolete*` classes (P1).
- Editing `docs/current/archive/tool_documentation.md` (frozen).

## Risks and open decisions

Marked **RESOLVED** (with the decision that closed it), **DEFERRED** (the owner is away, D11: the dispatcher proceeds with the recommendation and records it in the tracker) or open.

- **R1. The csproj write triggers a fire-and-forget full reload. RESOLVED by D12.** The signal is built (Step 14); Step 35a verifies it. What remains is R16.
- **R2. How the gate sees D3's in-memory edges. RESOLVED by D13.** A tool-level gate, a new static `ValidationEngine` method with the existing core delegating, `preValidatedAgainst` with a widened doc comment, one automatic retry, and an in-process lease; no existing
  hot-path signature changes. Residual costs, stated: after a peer session's write between gate and apply the re-validation (`RevalidateAfterForeignWriteAsync`) lacks the edges and fails safe with a refusal, which the retry covers once; the `preValidatedAgainst`
  contract is stretched from "the committed instance" to "committed or a candidate derived from it" (the doc comment says so, Step 4); a cycle in `Solution.AddProjectReference` is not relied upon (Step 24 checks the graph itself).
- **R3. D3's default-false toggles are an owner exception** to the "a param added to stop a known model failure is mandatory" rule (CLAUDE.md); recorded in D3.
- **R4. The cap thresholds in Step 35b (2x and 5x) are this plan's proposal**, not measured and not owner-set. The cap constant has one definition (`MoveSetPlanner.MaxFilesPerCall`).
- **R5. Result type for `ChangeNamespaceEngine`. RESOLVED by P8:** `CallerRewriteResult` plus one defaulted property.
- **R6. Data-tag attributes on a list parameter. RESOLVED by P9:** `[BatchItemField]` on the element type; `ApplyConsumesTags` tags top-level parameters only, so the tool validates each element itself.
- **R7. "Errors grouped by the set file they bind to"** is specified in the proposal without a binding rule. v1 groups by the reporting file with an "outside the set" group (Step 15); a diagnostic in a non-set file caused by a set file (a caller that lost its
  reference) lands in "outside"; a later refinement could map it by symbol. Open to review.
- **R8. Transitive reference visibility.** Whether the destination compilation's `References` already include a project reachable only transitively (SDK-style transitive `ProjectReference`) was not verified in `MSBuildWorkspace`; T27.6 checks it before the analyzer
  relies on it, because a false "missing edge" would add a redundant `ProjectReference`.
- **R9. Schema cost on a core tool.** `MoveFile` is in `ClaudeLeanToolNames`; `moves` (a list of objects) plus `dryRun`, and under D3 two booleans, are larger than two strings (the `MoveFile` entry is 1053 chars today). D15 pre-authorizes the budget raises; Step 36 records the measured delta.
  **Measured 2026-11-25 (Step 36b, `_catalog.json` `MoveFile` entry, ca33514d vs HEAD):** whole entry 1053 -> 2113 chars (+1060, 2.0x); description 229 -> 396 (+167); `inputSchema` 773 -> 1656 (+883).
- **R10. A `.cs` move into no loaded project refuses. RESOLVED by D14.** `copyAndObsolete` is allowed only for a set that expands to exactly one file pair; folder contents that are `bin/`, `obj/`, project files or binary get no special-casing (the eligibility
  refusal lists them), because D2 rejected `include` globs and the proposal's default exclusion list went with them. The planner is two-stage so `copyAndObsolete` keeps its own refusal text (P5).
- **R11. `ChangeNamespace` hazards that are hypotheses until Step 31 runs:** a `using Old;`/`global using Old;` dangles (CS0246) when the old namespace is emptied (T31.13); moved files lose implicit access to remaining old-namespace siblings (handled by `CollectUsings`, T31.11/T31.12);
  set members rewritten once for two roles (T31.16 overlap).
- **R12. RESOLVED (D20). Fixtures for Step 27 need a third project.** `TestSolutionFixture` is Tests -> Core, no IVT; a positive `addProjectReferences` case needs a third project loaded by the real manager (T27.8, T27.11). If an `MSBuildWorkspace` load of a hand-written third project needs a
  restore, T27.8/T27.11 become `OutOfProcess` tests (reusing `RunDotnet`) and Step 27 is split. Unverified.
- **R13. Cross-plan baseline and collisions.** Steps 13-36 change the tool schema, so any other plan's tool-surface golden or A/B eval baseline taken before Step 36 differs in the `MoveFile` entry. `plan_json_toolset_definitions.md` (DRAFT 2026-11-23, toolset catalog)
  and `plan_shared_tool_execution_pipeline.md` (DRAFT, targets `ValidateAndApplyHelper.cs` around `:199-202` and `EditApplier.cs`, which Step 4 also edits) touch the same files; re-check both for an IN PROGRESS status before Steps 4 and 32. `plan_error_codes_follow_cause.md`
  (IN-PROGRESS) may still be editing `WholeFileWriteTools.cs` (its Step 4 leftovers): see the Execution rules precondition before Step 13.
- **R14. Coverage gap named by role.** The existing `MoveFileTests` and `MoveFileCopyAndObsoleteTests` stay green through Steps 13 and 26 by construction (set of one); that proves the regression net held and proves nothing about sets. The planner (this document) owns the set-path rows
  T9-T27; the reviewer must confirm T16.1/T16.2 (cluster both ways), T27.8 (csproj under one `changeId`), T27.11 (edge-induced error refused) and T27.12 (retry) exist and exercise the changed path before the plan closes. The implementer's green build covers none of them by itself.
- **R15. RESOLVED (D18, accept with clearer text). Reload-tracking side effects (Step 14).** `Loading` now also describes a background reload: `McpServerStatus` (`ServerStatusTools.cs`), `LoadSolution` (`WorkspaceProjectManagementImpl.cs:811`) and `SubAgentModelRun.AwaitSolutionLoadAsync` (`:175-180`) will wait on or report it. A reload
  longer than the 30 s load wait makes the next call fail with `SolutionLoadTimeoutException` (the existing, honest text), where today it would race. T18.5 and Step 35a (`SELF-WAIT`, `TIMEOUT`) cover it.
- **R16. RESOLVED (D17, b1). Who awaits the reload signal, and through which API (D12 left this open).** Verified: `ISolutionProvider.WaitForLoadAsync`/`SolutionLoadStatus` exist; the read entries (`GetCurrentSolutionAsync` `:2049-2060`, `ResolveFromWire` `:3714-3722`, and through them `GetSolutionAsync`) already wait on
  them; only `LoadSolutionAsync` feeds them (`BeginLoadTracking` `:875`); there are two implementers of `ISolutionProvider`/`IWorkspaceManager` (`PersistentWorkspaceManager`, `FakeWorkspaceManager`).
  Options: **(b1, recommended)** track the post-write reload as a load (Step 14), so the existing read entries are the awaiters, no interface change, the fake is untouched; risk: `Loading` widens in meaning (R15), and `EndLoadTracking` must not clobber `_lastLoadFailure`.
  **(b2)** a dedicated `Task WaitForPendingReloadAsync(TimeSpan, CancellationToken)` and `bool ReloadPending` on `ISolutionProvider`: explicit and keeps `Loading` for loads only, but adds code at every awaiting entry and one fake to update (one implementer beyond production).
  **(b3)** b1 plus tracking the debounce reload (`OnDebounceTimerElapsed`), which is where a peer's csproj edit reloads; larger blast radius, not needed for MoveFile. Default taken in the plan: b1; b3 is a follow-up if Step 35a reports `STALE-READ-OBSERVED` for a peer-edited csproj.
- **R17. RESOLVED (D19, as proposed). Lease shape (D13(f) left details open).** (1) The lease source is a new narrow interface `IEdgeGateLeaseSource` implemented by `PersistentWorkspaceManager`, reached with an `as` check; alternative: a member on `IWorkspaceManager` (then `FakeWorkspaceManager` must implement it).
  Recommended: the narrow interface, so no existing fake or interface changes (the owner's hard constraint). (2) Holder identity is `ToolCallContext.Current`, with a fallback `AsyncLocal` token, because `Current` is null in tests and in-process hosts (`ToolCallContext.cs`, `Current`) and `ToolCallId` is null when no echo filter runs;
  this deviates from "keyed by call id" and is flagged. (3) Default `MaxWait`: proposed 30 s (the order of `SolutionLoadWait.DefaultTimeout`); not measured; Step 35b gives a gate time for a 60-file set to compare against.
- **R18. Placement exceptions (P16)**: `PersistentWorkspaceManager.cs` (3897 lines) gets call-site edits in Steps 14 and 20 only; if a reviewer wants the tracking extracted first, that is a separate prerequisite slice and Step 14 waits.
