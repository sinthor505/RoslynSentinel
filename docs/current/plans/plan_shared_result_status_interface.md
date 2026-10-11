# Plan: adopt IResultStatus across the remaining status-carrying result types (phases 2-5)

**Status:** IN PROGRESS 2026-10-11. Done: phase 1 (24499e55), step 2 (b406f44a), step 3 (333f3147). Step 4 done: `FromFlag` + Engines.Basic records (4c82fa12), Engines.Advanced (68502897), Tools.Basic (2e428075), Common records (this commit). Step 5 EngineResultWrapper done (10018cdd). Step 6 in progress (SIMPLE types via implementer, small batches): 6a ImpactReport/TypeHierarchyReport (f2642445), 6b VariableLifetimeReport/ToolsetChange (770748d2), 6c Engines.Basic ChangeSignatureResult/RenameSymbolResult/FormatPreviewResult/SymbolHoverInfo (70322b91), 6d NamedArgumentsOutcome/SemanticReplaceOutcome (1122f2c3), 6e ControlFlowAnalysisResult/DataFlowAnalysisResult/TestScaffoldResult (a4c733c7; TestScaffoldResult.Code is an explicit interface member because its positional `Code` is the generated source). 6f Engines.Advanced FluentBuilderResult/MemberCommentOutcome/PropagateCtResult/UpliftBatchMultiMethodResult (00abfd09; full suite 3823 passed, 0 failed, 26 skipped), 6h aggregates, part 1: `FromFailureCount`/`FailureCountMessage` helpers, OperationSummary, AsyncifyLoopResult, BridgeAsyncMethodsResult, UpliftCallersResult, UpliftBatchResult, UpliftBatchMultiResult, PropagateCtFileResult, PropagateCtBatchResult (527a7287; rule: any failure => IsError/Failed/Exception/"N of M failed"; UpliftBatchResult errors only when everything was skipped and StopReason is not no_callers/dry_run), 6i aggregates, part 2: ProjectTestSummary, TestRunResult (error iff !RunSucceeded or !RunCompleted; stricter than the tool's isError, which only keys on !RunCompleted), TestCategoryProjectApplyResult, TestCategoryApplyResult, TestCategoryProjectOutcome and TestCategoryAppliedSummary (string Status explicit), AddConstructorParameterCascadeResult (a ClassEdit of AlreadyInTargetState reports Success, not AlreadyInState), SubAgentResult, SubAgentEvalResult, AppliedChangeSummary (this commit; full suite 3888 passed, 0 failed, 26 skipped). Deferred to Step 7: the JSON-text read of the build `outcome` at SubAgentEvalResult.cs:~71 (it has no typed BuildResult). 6g DocReadResult/DocListResult/DocumentLookupResult/BuildResult (18ce4ef0; DocumentLookupResult.Status is explicit because the positional `Status` is a DocumentLookupStatus; BuildResult NotRun counts as an error). All SIMPLE types are done. The Asyncify project's T3/T4 large-result tests flake with an NRE on a full-suite run and pass on rerun (seen twice). Then NEEDS-DESIGN aggregates (decision for open question 8: keep return shapes, aggregate IResultStatus via a shared helper, partial -> Failed with message).

## Problem
`proposals/proposal_shared_result_status_interface.md` defines `IResultStatus` (`IsError`, `Status`, `Code`,
`Message`) and `ResultStatus`. Phase 1 put them in `Common` and made `SentinelCallToolResult` the only
implementer. Every other status-carrying type still has its own vocabulary and the lossy conversions
remain (`finding_result_status_vocabularies_fragmented.md`).

Verified against source on 2026-10-11 (the proposal text is partly stale):
- `EditOutcome.AlreadyInTargetState` exists and `NoChange` is `[Obsolete]` with no producers
  (`DocumentEditResult.cs`); open question 1 is settled in code, `ResultStatus.AlreadyInState` stays.
- A failed build returns `isError: true` with `ToolErrorCode.BuildFailed` (`WorkspaceBuildTestImpl.cs:273-275`);
  `RunTest` uses `TestRunFailed`. Open question 4 is done.
- `RefactoringToolHelpers.ErrorCodeFor(EditOutcome)` (`RefactoringToolHelpers.cs:33`) still maps 10 of 13
  outcomes to `ToolErrorCode.Exception`.
- `EngineResultWrapper<T>` (`Common/EngineResultWrapper.cs`) is untouched; `EngineError.ToToolResponse`
  still emits `success = false`. `ModelAgentRunner.cs:420/:436` still synthesize `{"success":false,...}` bodies.
- `ToolErrorCode` has no `Cancelled` or `TimedOut` member.
- Bespoke records already carry `bool IsError`: `ExtractMethodResult`, `MsAugmentResult`
  (also `ErrorCode`), `OutParamConversionResult`, `ApplyChangesResult`, `DiagnosticReport`, `DocWriteResult`,
  `GitResult`. Adoption there is mostly additive.
- Remaining `IsSuccess` identifiers: an uncapped count was not obtained (search capped at 300 hits, mostly test
  fixtures and `ReferenceRoleClassifier`). Live ones to inspect: `WorkspaceTools.cs` (27),
  `SubAgentChildServerLauncher.cs` (2).

## Decision
Keep the proposal's design (interface, `ResultStatus`, `IsErrorStatus()` as the single error table, computed
`IsError` where a type has its own status, stored where it has none). Add `ToolErrorCode.Cancelled` and
`TimedOut` (decided 2026-10-11). Order: central mapping first, then types by layer, then agent consumers.

## Execution rules
- One compile-green commit per step; `Build` 0 errors, then `RunTest` compared to the known baseline
  (`reference_known_failing_tests`).
- Dispatch edits sequentially, never parallel subagents (CLAUDE.md). Measure each slice with
  `InspectSymbol(aspect: blastRadius)` first; `implementer` for steps 2 and 4, `implementer-senior` for 3, 5 and 7.
- `git status` before touching `CodemodTools.cs` / `AdvancedRefactoringTools.cs`.
- Per the proposal's fallback order, if a member's blast radius is too big, drop `Message`, then `Code`, then
  `Status`; never `IsError`.
- Restart the server (`McpServerControl`) before any live check; `isServerBinaryStale` is true.
- ASCII-only punctuation in code comments and commit messages.

## Steps

### Step 1 - Refresh the docs
- Files: `proposals/proposal_shared_result_status_interface.md`.
- Change: mark phase 1 DONE (24499e55); resolve open questions 1 (AlreadyInTargetState exists, NoChange obsolete)
  and 4 (done); drop the `IsSuccess` alias text; record the Cancelled/TimedOut decision (question 5); point at
  this plan. Inspect the live `IsSuccess` hits listed above and note whether any remain.
- Done when: the proposal's status line and open-question list match this plan; no code changed.

### Step 2 - Phase 2: one EditOutcome -> ResultStatus mapping
- Files: new `Common/EditOutcomeExtensions.cs` (`ToResultStatus`, `ToErrorCode`); `Common/EngineResultBase.cs`;
  `Engines.Basic/RefactoringToolHelpers.cs`; tests.
- Change: `Modified` -> Success; `AlreadyInTargetState` -> AlreadyInState; `TargetNotFound`, `DocumentNotFound`,
  `NoCandidatesFound` -> NotFound; `SourceInvalid` -> InvalidInput; `FeatureDisabled` -> FeatureDisabled;
  `CannotOptimize/Edit/Move/Remove/Convert` -> NotApplicable; `Error`, `Unset`, `NoChange` -> Failed.
  `ErrorCodeFor(EditOutcome)` delegates to it (FeatureDisabled -> `ToolErrorCode.FeatureDisabled`, `Cannot*` ->
  `TargetIneligible`). `EngineResultBase : IResultStatus`: `Status` from `Outcome`, `IsError` computed,
  `Code` = `DocumentEditResult.ErrorCode ?? mapped code` (null on non-error), `Message` = existing `Message`.
- Done when: table test over every `EditOutcome` member; `IsError` agrees with `Status`; tests that asserted
  `Exception` for `Cannot*` / `FeatureDisabled` updated; Build 0 errors; baseline suite.
- Also closes step 2 of `plan_error_codes_follow_cause.md` (outcome mapping).

### Step 3 - Add Cancelled and TimedOut codes
- Files: `Common/SentinelCallToolResult.cs` (`ToolErrorCode`), `Common/IResultStatus.cs` (`FromErrorCode`),
  `Common/ToolException.cs` (`ToolErrorMapper.ToCodeAndMessage`), tests.
- Change: add the two constants; map `OperationCanceledException` -> `Cancelled`, `TimeoutException` and
  `EngineOutcome.Timeout` -> `TimedOut`; `FromErrorCode` returns the matching `ResultStatus`. Check
  `SolutionLoadTimeout` and decide whether it maps to `TimedOut` too.
- Done when: mapper tests for both exceptions; `FromErrorCode` tests; Build 0 errors.

### Step 4 - Phase 3a: bespoke records
- Files: `ExtractMethodResult` (`Engines.Basic/BasicRefactoringEngine.cs:15`), `MsAugmentResult`
  (`Engines.Basic/MsToolAugmentEngine.cs:13`), `OutParamConversionResult` (`Engines.Advanced`),
  `ApplyChangesResult`, `DiagnosticReport` (`Common`), `DocWriteResult` (`Tools.Basic/DocumentationTools.cs:36`),
  `GitResult` (`Tools.Basic/GitImpl.cs:10`).
- Change: each gains `: IResultStatus`. `IsError` is already stored; `Status` = Success or
  `FromErrorCode(Code)` (Failed when no code); `Code`/`Message` forward the existing error members (`Error`,
  `ErrorMessage`, `ErrorCode`). `GitResult` maps `GitErrorCodes` through a small table.
- Done when: per-type agreement test; Build 0 errors. Commit per project (Engines.Basic, Engines.Advanced,
  Tools.Basic).

### Step 5 - Phase 3b: EngineResultWrapper
- Files: `Common/EngineResultWrapper.cs`, its tests.
- Change: `EngineResultWrapper<T> : IResultStatus`: `Failure`/`InternalError` -> Failed, `DocumentNotFound`/
  `TargetNotFound` -> NotFound, `InvalidInput` -> InvalidInput, `Timeout` -> TimedOut; `Message` from
  `Error.Message`. `EngineError.ToToolResponse` emits `isError` instead of `success`.
- Done when: users (`BuildEngine`, `DiagnosticEngine`) build; tests assert the new body key.

### Step 6 - Phase 4: remaining status-carrying types
- Files: the 82-type inventory in the fragmentation finding, by layer (engines, then tools).
- Change: first re-read the 51 UNREAD matrix cells (open question 6) and write the final type list into the
  proposal. Then adopt `IResultStatus` a few types per commit; `SubAgentEvalResult` reads `IsError` rather
  than the serialized `"outcome"` (`SubAgentEvalResult.cs:71-72`).
- Done when: every listed type either implements the interface or is recorded as out of scope with a reason.

### Step 7 - Phase 5: agent-side consumers
- Files: `Common/AgentLoop/ModelAgentRunner.cs` (`:420`, `:436`, `:465`), `Tools.Advanced/SubAgentChildServerLauncher.cs`
  (`:419`), eval fixtures and prompts.
- Change: read `CallToolResult.IsError` from the MCP client instead of re-parsing the body; synthetic error
  bodies use `isError`. The server filters keep reading the body's `isError` (resolved question 7). Search
  Markdown and prompt files for `"success"` and `isSuccess` first.
- Done when: Build/RunTest baseline; one model-eval smoke run (planstep-runner or modeleval-runner, since a model
  is under test) shows no change in outcome classification.

### Step 8 - Close out
- Mark the proposal and finding done, move the `TODO.md` entry to `CLOSED.md`, update
  `plan_error_codes_follow_cause.md`, record commit hashes in this plan's status line.

## Out of scope
- Payload-only types and anonymous or untyped return shapes (`finding_anonymous_and_untyped_return_shapes.md`).
- Non-outcome enums (`AgentStopReason`, `TestOutcome`, `FindingSeverity`, ...).
- Error-code taxonomy reorganisation (`project_tool_error_code_taxonomy_proposal`).

## Risks and open decisions
- Step 2 changes the wire `errorCode` for `Cannot*` and `FeatureDisabled` outcomes (from `Exception`); tests and
  any prompt that keys on `Exception` need updating. Hypothesis: few prompts do; search before merging.
- Step 4 positional records need an explicit `Message` property; check serialization output does not gain a
  duplicate field (mark `[JsonIgnore]` like `SentinelCallToolResult`).
- Step 7 is the only step that can change model behaviour; gate it on the smoke run.
- Step 6 scope depends on the unread matrix cells and may split into several plans.
