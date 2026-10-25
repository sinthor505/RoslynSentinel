# Plan: UpdatedText becomes a non-nullable member of DocumentEditResult only (S4, option B)

**Status:** IN PROGRESS 2026-10-24. Option B chosen and the 12 steps approved by the user 2026-10-24. Done: prep `db42dc87`, step 1 (probe), step 2 `e08331ea`, step 3 `3dde4347`, step 4a `35bf3db2`, step 4b `071960a2`, step 5 (empty: the probe found no other producer), step 6 `c32774eb`, step 7-pre `b6d3696b`, step 7 (see the deviation note under Step 7). Remaining: steps 8-12.

## Problem
`EngineResultBase.UpdatedText` is `string?` (`RoslynSentinel.Common/EngineResultBase.cs:19`). The rule from
[proposal_throw_typed_exceptions_not_failure_results.md](../proposals/proposal_throw_typed_exceptions_not_failure_results.md)
is that a `DocumentEditResult` always carries text for `Modified`, `AlreadyInTargetState` and
`NoCandidatesFound`, and that failures throw. The type does not say so, so consumers defend themselves:
89 `UpdatedText!` uses and 31 `// TODO S4` markers in production code, 21 `IsNullOrEmpty(...UpdatedText)`
checks and 9 `??`/null comparisons that cannot fire. The same missing guarantee caused the S3 (c)/(d) bugs
(`Modified` returned with no text, so guards silently never applied a result).

Two things block a plain `string?` -> `string` flip:
- Three producers return `Modified` for a project-level or preview answer with no single-file text:
  `SolutionManagementEngine.CreateProjectAsync` (`:18`, result at `:38`),
  `SolutionManagementEngine.SplitProjectByFolderAsync` (`:49`, result at `:60`) and
  `SolutionStructureEngine.PreviewMoveFileToNamespaceFolderAsync` (`ProjectStructureEngine.cs:57`, the
  `MOVE_REQUIRED` result at `:92`; it is a read-only preview answer, not a refusal, so it must not throw).
- The property sits on `EngineResultBase`, shared with five records that never set it (`BatchResultSummary`,
  `BridgeBatchResult`, `BestInsertionResult`, `GenerateToStringResult`, `CancellationTokenResult`).

## Decision
Option B. `UpdatedText` moves to `DocumentEditResult` as a non-nullable, `required` member (restate
`[JsonIgnore]`). The three project-level producers return a new `ProjectEditResult` that has no
`UpdatedText`. `StructuralRefinementEngine.SyncTypeAndFilenameAsync` stays a `DocumentEditResult` and also
sets `UpdatedText` (the renamed file's content, as `RemoveMethodParameterAsync` already does). Failures
keep throwing typed exceptions; no-ops keep returning `AlreadyInTargetState`/`NoCandidatesFound` with the
unchanged text. `required` makes the compiler prove every producer sets the text; one empty-text check
stays at the write boundary (`RequireUpdatedText`) because `""` is still a legal `string`.

## Execution rules
- C# edits only through dispatched `implementer` slices (D0: files<=3, edits<=10), sequential, one
  `Build`/`RunTest` at a time; the main session reviews and commits. Commit trailer on each commit:
  `Plan: plan_document_edit_result_non_nullable_updated_text`; the last commit carries `Closes:`.
- Migration-first: the new type lands beside the old before any caller moves; the property move comes last
  among the type changes. `CS8602`/`CS8604` and `CS0618` warnings during the migration are expected
  (no `TreatWarningsAsErrors`).
- Run the touched test fixtures per slice. Run `Tests.Advanced` and `Tests.Basic` in full at steps 5, 7, 8
  and 10. Known failures not caused by this work: `RawFormatterCalls_AreOnlyInTheAllowlist`,
  `InlineMethod_ValidMethod_ChangesSource`, `CheckPackageInconsistencyAsync_InMemorySolution_ReturnsNonNull`.
- Another session may be editing `RoslynSentinel.Tests.Basic/MemberRefactoringTests.cs` and
  `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs`: check `Git status` before each dispatch and never
  edit or commit a file that is dirty from another session.
- The `dto-conventions` skill applies to step 3 (new result type): the brief carries a NEW-DTO block.

## Steps

### Step 1 - Probe: list every producer that omits UpdatedText
- Files: `RoslynSentinel.Common/DocumentEditResult.cs` (temporary edit, reverted in the same slice).
- Change: temporarily add `required` to a non-nullable `UpdatedText` on `DocumentEditResult` (and
  `[SetsRequiredMembers]` on its constructors), run `Build` on the solution, copy the full list of
  CS9035 "required member must be set" errors (file:line) into the report, then revert with
  `UndoLastApply` so nothing is left in the tree. Do not fix any site.
- Done when: the report lists every initializer that omits the text, grouped by file; `Git status` shows no
  change. The list replaces step 5's estimate.
- Why first: a text search could not prove that only three producers return `Modified` without text (about
  1,300 `DocumentEditResult` returns exist and the search is per line).

### Step 2 - SyncTypeAndFilename carries the text
- Files: `RoslynSentinel.Engines.Basic/StructuralRefinementEngine.cs`, `RoslynSentinel.Tests.Basic/BatteryNineTests.cs` (path: use the real project; tests at `:254,266,283`).
- Change: the `Modified` return at `StructuralRefinementEngine.cs:75-80` also sets
  `UpdatedText = sourceText` (the content already stored in `Changes[newPath]`). Add an assertion to one
  existing rename test that `UpdatedText` equals the `Changes` value.
- Done when: `BatteryNineTests`, `BugFixTests` (`:495`) and `FinalRegressionTests` (`:57`) fixtures pass.

### Step 3 - Add ProjectEditResult beside DocumentEditResult
- Files: `RoslynSentinel.Common/ProjectEditResult.cs` (new).
- Change: `public sealed record ProjectEditResult : IOperationResult` with `Outcome` (`EditOutcome`),
  `Message`, `FilePath` (nullable, the created project or the previewed target), `Status`/`IsError` derived
  from `Outcome` like `EngineResultBase`, and a `ToJsonSummary()` matching `DocumentEditResult`'s shape for
  the fields it has. No `UpdatedText`, no `Changes`. Reuse `EditOutcome`. NEW-DTO block in the brief.
- Done when: `Build` 0 errors; nothing references the type yet.

### Step 4 - Move the three project-level producers and their callers
Two slices, each with its tool caller and tests, definitions first.
- 4a Files: `RoslynSentinel.Engines.Basic/SolutionManagementEngine.cs`,
  `RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs`, `RoslynSentinel.Tests.Battery.Basic/BatteryNineTests.cs`
  (verify the project). Change: `CreateProjectAsync` and `SplitProjectByFolderAsync` return
  `ProjectEditResult`; the tool callers at `WorkspaceProjectManagementImpl.cs:405` and `:428` pass it as
  `SuccessData` (the wire shape loses the empty `Changes`/`IsCommitted` fields: accepted); tests at
  `BatteryNineTests.cs:136,147` follow. Done when: the `BatteryNineTests` fixture passes.
- 4b Files: `RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs`, `RoslynSentinel.Tools.Advanced/IntelligenceTools.cs`
  (`:215`), `RoslynSentinel.Tests.Advanced/BugFixTests.cs` (`:2518` reads `.Message`) - the sync
  characterization test `ProjectStructureNormalizeCharacterizationTests.cs:106` only asserts a throw and
  needs no change. Change: `PreviewMoveFileToNamespaceFolderAsync` returns `ProjectEditResult` for both the
  `MOVE_REQUIRED` answer (`Modified`) and the already-in-place answer (`NoCandidatesFound`, previously with
  `UpdatedText = root.ToFullString()`: drop the text, the preview has no use for it). Done when: the
  `BugFixTests` fixture and the Intelligence tool tests pass.

### Step 5 - Fix any further producers found by the step 1 probe
- Files: per the step 1 list, grouped <=3 per slice.
- Change: set `UpdatedText` to the unchanged or produced text for each; a producer that has no text because
  it is project-level moves to `ProjectEditResult` as in step 4.
- Done when: re-running the step 1 probe reports zero omissions. Then run `Tests.Advanced` and `Tests.Basic` in full.

### Step 6 - Guards take DocumentEditResult
- Files: `RoslynSentinel.Engines.Basic/RefactoringToolHelpers.cs`, `RoslynSentinel.Tests.Basic/RefactoringToolHelpersTests.cs`.
- Change: `GuardEngineResult`, `GuardNothingToDo`, `GuardEngineFailure` and `EngineReason` (`:143-236`) take
  `DocumentEditResult` instead of `EngineResultBase` (all 31 `CodeTransformTools` call sites already pass a
  `DocumentEditResult`). `RequireUpdatedText` already does. Note `EngineReason` reads a `// WARNING:` first
  line out of `UpdatedText` (`:236`); keep that behaviour and add a comment that it relies on it.
- Done when: `Build` 0 errors; `RefactoringToolHelpersTests` passes.

### Step 7 - Move the property and make it required
- Files: `RoslynSentinel.Common/EngineResultBase.cs`, `RoslynSentinel.Common/DocumentEditResult.cs`,
  `RoslynSentinel.Engines.Advanced/MappingEngine.cs` (doc cref at `:121`).
- Change: remove `UpdatedText` from `EngineResultBase` (carry its DEFERRED comment over); add
  `[JsonIgnore] public required string UpdatedText { get; init; }` to `DocumentEditResult`; the
  parameterless and 3-arg constructors get `[SetsRequiredMembers]` and set `string.Empty`, the 5-arg
  constructor's `updatedText` parameter becomes non-null; point the cref at `DocumentEditResult.UpdatedText`.
  The five other `EngineResultBase` records are unaffected.
- DEVIATION (2026-10-24): `required` could not be kept. System.Text.Json throws `InvalidOperationException:
  JsonPropertyInfo 'UpdatedText' ... is marked required but does not specify a setter` when serializing
  `DocumentEditResult` because the member is `[JsonIgnore]` (found by
  `EditOutcome_EveryMember_ErrorStatusAgreesWithCode_AndUnsetIsError`). The member is now
  `[JsonIgnore] public string UpdatedText { get; init; } = string.Empty;`; the compiler no longer proves
  every initializer sets it. The step 1 probe (run with `required`) proved that at this commit no
  initializer omits it; the write-boundary guard in `RequireUpdatedText` (step 10) is the backstop.
- Done when: `Build` of the solution has 0 errors (warnings only for the remaining `!`); tests that build
  results with `new DocumentEditResult(...)` (`ResultStatusTests.cs:106,120,123`,
  `RefactoringToolHelpersTests.cs:21,35,56,69,84`) compile and pass.

### Step 8 - Tools cleanup: drop `!`, `// TODO S4` and the dead null halves
Slices of <=3 files each, tools first because they hold most uses:
- 8a `RoslynSentinel.Tools.Experimental/CodeTransformTools.cs` (32 uses, 13 markers).
- 8b `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs` (21 uses, 4 markers, 7 dead checks) and `WorkspaceProjectManagementImpl.cs` (`:478`).
- 8c `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs` (20 uses, 7 dead checks) and `AsyncifyTools.cs` (the three `updatedSource!` markers at `:1487`, `:3024`, `:3517` stay: they are a local, not this property).
- 8d `RefactoringExtractionDocsImpl.cs`, `RefactoringSignatureImpl.cs`, `GenerationTools.cs`.
- Change: remove `!`, `// TODO S4` and `string.IsNullOrEmpty(x.UpdatedText) ? ... :` forms inside the kept `!autoStage` blocks (keep those blocks: the autoStage functionality returns later) without changing their behaviour.
- Done when: per slice `Build` 0 errors and the matching fixtures pass; no `UpdatedText!` remains in the file.

### Step 9 - Engines cleanup
- Files: `RoslynSentinel.Engines.Advanced/AsyncBatchEngine.cs` (`:543` `?? throw`, `:557`, `:609`, `:808`), `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` (`:399`), `RoslynSentinel.Engines.Advanced/CommentingEngine.cs` (`:346`), `MappingEngine.cs` (`:97`).
- Change: delete the dead null throws and null comparisons on `UpdatedText`.
- Done when: `Build` 0 errors; `Tests.Asyncify` and `Tests.Advanced` pass.

### Step 10 - Helper dead branches
- Files: `RoslynSentinel.Engines.Basic/RefactoringToolHelpers.cs`, `RoslynSentinel.Tests.Basic/RefactoringToolHelpersTests.cs`.
- Change: `GuardEngineResult`'s `Modified`-without-text path (`:145`) collapses to the empty-string check; `RequireUpdatedText` keeps one `string.IsNullOrEmpty(UpdatedText)` check as the write-boundary guard (an empty file text on `Modified` is still a bug). The `default` (`Unset`) branch stays until step 12.
- Done when: both test fixtures pass; `Tests.Basic` in full shows only the known failure.

### Step 11 - Test cleanup
- Files: sweep lanes by project (`Tests.Advanced` 523 `UpdatedText` lines, `Tests.Basic` 342, `Tests.Battery.Advanced` 200, `Tests.Tools.Advanced` 122, `Tests.Asyncify` 41, `Tests.Tools.Basic` 14).
- Change: remove the ~235 `UpdatedText!` in tests and the meaningless `Is.Not.Null` asserts on `UpdatedText`; rewrite the two null asserts (`AdvancedToolsTests.cs:178`, `BugFixTests.cs:2007`) to assert the unchanged text instead.
- Done when: `Build` 0 errors with no `UpdatedText` nullability warnings; full test runs match the baseline.

### Step 12 - Optional: retire Unset
- Files: `BatchTypes.cs`, `BridgeBatchResult.cs`, `BestInsertionResult.cs`, `GenerateToStringResult.cs`, `CancellationTokenResult.cs` (delete: 0 references), `EngineResultBase.cs`, `EditOutcomeExtensions.cs`.
- Change: re-parent the four live records off `EngineResultBase` (they use only `ChangeId`/`FilePath`), then remove `Unset` and `GuardEngineResult`'s `default` branch.
- Done when: `Build` 0 errors; `ResultStatusTests` and `RefactoringToolHelpersTests` (which reference `Unset` at `:100,120` and `:84,188,202,204,210`) updated and passing. Do this step only after checking the serialized shape of `BatchResultSummary` and `BestInsertionResult` (see risks).

## Out of scope
- S5 (weak failure assertions in tests), the held `class_to_record`, `convert_property_safe` and `replace_constructor_with_factory` decisions, `GetMemberSourceAsync` tuple removal, `MsAugmentResult.Fail`/`ImpactReport.Error` retirement (the remaining S4 error-field items).
- Making `PreviewMoveFileToNamespaceFolderAsync` throw: it is a preview answer, not a failure.
- Removing the unreachable `!autoStage` blocks (kept deliberately).

## Risks and open decisions
- **Unproven producer count (hypothesis).** Only four `Modified`-without-text producers were found by line search; step 1 replaces that with a compiler-proven list and may enlarge steps 4-5.
- **`SplitProjectByFolderAsync` is a simulation.** It creates a project, then reports "Moved N files..." without moving anything (`SolutionManagementEngine.cs:60-65`). Step 4a only changes its result type. Open: should it throw `ToolNotImplementedException` or be implemented? Leave a `// TODO` at the site in step 4a.
- **Wire shape of the project tools changes.** `create_project`/`split_project` stop returning `Changes`, `IsCommitted` and `ChangeId`. No in-repo consumer reads them (tests read `Message`/`Outcome`); an external client is unverified.
- **`EngineReason` parses a `// WARNING:` line out of `UpdatedText`.** `OptimizeToValueTask` puts its reason there, not in `Message`. Step 6 keeps this; moving the reason to `Message` is a separate cleanup.
- **`string.Empty` default.** The parameterless and short constructors set `""`; the `required` member covers object initializers only. Step 10's write-boundary check is the backstop.
- **Step 12 hypothesis.** `Outcome` is not `[JsonIgnore]`, so `BatchResultSummary` and `BestInsertionResult` may serialize `"Outcome": "Unset"` today; verify before re-parenting.
