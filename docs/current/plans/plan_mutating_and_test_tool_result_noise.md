# Plan: cut result noise and misleading wording in Member and RunTest

**Status:** DRAFT 2026-10-08 (amended same day for user decision A: compact result by default; revised again: changed content offloaded to a large-result file, inline only by opt-in, see `plan_mutating_tools_compact_result_default.md`). Priority P2. Source: journal digest `.claude/journal/digest_20261008-1509.md` (window 2026-10-02..08), clusters "Member", "Member addMember", and "RunTest".

## Problem

Three small, separate defects with one shared theme: the tool's answer is larger, vaguer or more misleading than it needs to be, and the agent pays turns to compensate.

**A. `Member` add operations echo the whole changed file.** Three success sites in `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs` build
`new AppliedChangeSummary(..., ChangedContent: <dictionary of file -> full updated text>, Validated: true)` for an applied (not dry-run) change:
`addTopLevelType` (line 729, `ChangedContent: topLevelChanges`), enum add (line 802, `ChangedContent: enumAddChanges`), `addMember`/`addTypedMember` (line 883, `ChangedContent: addChanges`).
The statusMessage already carries the description and the apply result carries `LineChanges` (computed at `RoslynSentinel.Common/ValidateAndApplyHelper.cs:95,116,153,156`; `RefactoringStructuralImpl.cs:112`, the static-conversion path, already passes it. An earlier draft of this plan wrongly said `ModifyModifier` did: `ModifyModifier` at line 1202 passes `ChangedContent` too). Result: a 30-line add on an 80-line file returns the whole file, and larger files are offloaded to a 66-75 KB large-result file even though "the statusMessage is all I need".
Entries: `[47b2c93d:L9]`, `[a08be84f:L24]` (~5k tokens per edit), `[a08be84f:L59]`, `[50e0e6ac:L21]` ("even with returnDiff=false - ~10-20 KB noise per call"). The `!autoStage` path (lines ~852-868) and ModifyEnum (line 928) legitimately return content because nothing was written; they are not changed.
Other tools echo `ChangedContent` too (29 applied-change sites across `AdvancedRefactoringTools.cs`, `RefactoringExtractionDocsImpl.cs`, `RefactoringSignatureImpl.cs`, `RefactoringStructuralImpl.cs`). User decision A (2026-10-08) makes the compact result the default for all of them through one central gate: see `plan_mutating_tools_compact_result_default.md`. This plan's Step 1 depends on that plan's Step 1.

**B. Batch error text in `MemberRefactoringEngine` is stale and, in two places, corrupted.** The tool parameter is `batchEdits`, but `ApplyModifierBatchAsync`, `ApplyAttributeBatchAsync` and `ApplyBaseTypeBatchAsync` in `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` emit `edits[{n}] ...`
(lines 1082, 1089, 1105, 1132, 1142, 1161, 1693, 1700, 1716, 1742, 1752, 1758, 1764, 1780, 4931, 4938, 4952; neighbouring messages at 4977 and 4988 already say `batchEdits[`).
Lines 1105 and 4952 additionally read `"edits[{firstIndex}] and edits[{kvp.Key}] both _symbolNavigationEngine. Resolve to the same target ..."`, a find/replace accident that injected an identifier into prose (also noticed by the agent in `[51989d5e:L4]`).
Hypothesis to prove first (see step 3): existing tests assert `Does.Contain("batchEdits[0]")` (`ModifyAttributeBatchTests.cs:442`, `ModifyBaseTypeBatchTests.cs:349`), so some layer or branch may already rewrite or pre-empt these messages; the corrupted text may be reachable only on the same-target collision path.

**C. RunTest hides which project failed and why.** Traced to source:
- When a project does not complete (no TRX, e.g. a file-lock or a build error), `WorkspaceBuildTestImpl.RunTest` returns `IsError = true` with the cause in `ErrorData.Message` only (`WorkspaceBuildTestImpl.cs`, the `!testRunResult.RunCompleted` branch) and does NOT set `StatusMessage`.
  The offload filter relays only `statusMessage` into its pointer envelope (`Server.Basic/ServiceRegistrationExtensionsBasic.cs:647-651`), and the payload carries up to 40 stdout lines per project, so the response is offloaded and the project name and cause are inside a 31-40 KB file.
  Entries: `[b1dcd60b:L5]` ("the real cause took 3 calls to find"), `[a08be84f:L17]` ("the cause was buried at offset ~3000 of a 40k offloaded result"), `[6406d612:L8]`.
- `TestRunEngine.RunOneProjectAsync` recognises a file-lock only for `MSB3027` and `MSB3021` (`TestRunEngine.cs`, `lockDetail`); the journal reports an `MSB3026` retry log (`[b1dcd60b:L5]`). Hypothesis: MSB3026 lines alone are present when quiet verbosity cuts the final error; adding it costs nothing.
- A run where every test passed but a project exited non-zero without a failing test (`[ebd9923b:L9]`: "runSucceeded=false/exitCode=1 with 1 passed 0 failed") is summarised as `Tests FAILED: 1 tests, 1 passed, 0 failed` (`SummarizeTestRun`), which is self-contradictory and names no project.
- The parameters `scope` and `scopeName` carry no `[Description]` on either `RunTest` declaration (`WorkspaceBuildTestTools.cs:48+`, `WorkspaceTools.cs:498+`); agents guessed `projectName` (`[50e0e6ac:L27]`, `[0017ac93:L10]`) and, with a name `filter` under scope=solution, waited ~1.5 min while all 13 test projects were built and probed (`[a08be84f:L15]`, `[a08be84f:L25]`; `RunAsync` enumerates every `IsTestProject` and runs `dotnet test` on each). The `WorkspaceTools` copy of the tool uses the one-line description "Runs dotnet test and reports structured pass/fail results with failure grouping." and so never mentions `scope=project`.

## Decision

- A (decision 2026-10-08): applied Member add operations stop echoing the full file; no opt-in parameter. The drop itself is done centrally by the `AppliedChangeSummary` gate in `plan_mutating_tools_compact_result_default.md` Step 1 (which also adds the "call ReadFile, GetMethodSource or Member(view)" hint to `Note`). This plan only adds `LineChanges` and `ChangedContentResultId` at the three `Member` add sites so the compact result reports what changed and where the content can be fetched, and tests the result through `Member`. Dry runs and `autoStage: false` keep the content.
- B: prove first, then fix: write a test per batch method asserting the user-visible text uses `batchEdits[`; fix the engine strings where the test fails.
- C: put the failing project and cause into `StatusMessage`; name non-zero-exit projects in the summary; recognise MSB3026; document `scope`/`scopeName` and the cost of a solution-wide `filter`.

## Execution rules

- One compile-green slice per step; `Build` (0 errors) after each. Steps are independent except that Steps 1 and 2 need `plan_mutating_tools_compact_result_default.md` Steps 1 and 2 first; do them in order.
- Each behaviour change lands with its named regression test in the same or the next step as stated.
- ASCII-only punctuation.

## Steps

### Step 1 - Member add: report LineChanges (the echo itself is dropped by the central gate)
- Precondition: `plan_mutating_tools_compact_result_default.md` Steps 1 and 2 have landed (`AppliedChangeSummary` drops `ChangedContent` when `Validated && !DryRun`, and gains a `ChangedContentResultId` parameter that `ApplyOutcome` supplies; revised 2026-10-08 for the opt-in/offload decision: on a real apply the content is stored in a large-result file and the summary carries its resultId; these three sites also pass through `ForPossiblyLargeDataAsync`, which stays inline because the summary is now small). Check with `Member(operation: view)` or `GetFileOutline` on `RoslynSentinel.Common/AppliedChangeSummary.cs` that it declares an explicit `ChangedContent` property; if it does not, stop and reply `RESCOPE:`.
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\RefactoringStructuralImpl.cs`
- Symbols: `RefactoringStructuralImpl.Member`, three `new AppliedChangeSummary(...)` expressions; add only a named argument and leave `ChangedContent:` as written:
  - line 729: `ChangedContent: topLevelChanges, Validated: true` -> `ChangedContent: topLevelChanges, Validated: true, LineChanges: topLevelApply.LineChanges, ChangedContentResultId: topLevelApply.ChangedContentResultId`
  - line 802: `ChangedContent: enumAddChanges, Validated: true` -> `ChangedContent: enumAddChanges, Validated: true, LineChanges: enumAddApply.LineChanges, ChangedContentResultId: enumAddApply.ChangedContentResultId`
  - line 883: `ChangedContent: addChanges, Validated: true` -> `ChangedContent: addChanges, Validated: true, LineChanges: addApply.LineChanges, ChangedContentResultId: addApply.ChangedContentResultId`
  Do not touch the `!autoStage` branches (lines ~712, ~788, ~867) or ModifyEnum (~928). Confirm the `AppliedChangeSummary` constructor parameter is named `LineChanges` (`RoslynSentinel.Common/AppliedChangeSummary.cs`; used by name at line 112). The other `RefactoringStructuralImpl` sites belong to the other plan's Step 5.
- Call sites: the three expressions above are the only edits; `Member` callers are unchanged.
- Tool: `ReplaceSnippet` with `batchEdits` (three edits, each anchored with its unique variable name).
- Done when: `Build` reports 0 errors.

### Step 2 - Test: applied Member add returns line changes, not the file
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Tools.Basic\MemberAddResultSizeTests.cs` (new)
- Change: model on `MemberSingleDeclarationTests.cs` (`InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource))`, `BuildTools(workspace.Manager)`, `tools.Member(reason: ..., operation: MemberAction.addMember, filePath: workspace.PathOf(...), containerName: ..., position: "end", newMemberSource: ...)`). Fixture: a class of about 200 lines. Tests:
  - `Member_AddMember_AppliedResultDoesNotEchoWholeFileAsync` - assert `((AppliedChangeSummary)result.SuccessData!).ChangedContent` is null, `LineChanges` is non-null/non-empty, `Note` contains `ReadFile`, `result.LargeResult` is null; and, when the fixture manager has a solution root, `ChangedContentResultId` is non-null (if the in-memory workspace has no root the id is null by design: fail-closed offload).
  - `Member_AddMember_DryRunStillReturnsChangedContentAsync` - `dryRun: true`, assert `ChangedContent` is non-null.
  - `Member_AddTopLevelType_AppliedResultDoesNotEchoWholeFileAsync` - same for `MemberAction.addTopLevelType`.
  - `Member_AddMember_NoAutoStageStillReturnsChangedContentAsync` - `autoStage: false`, assert `ChangedContent` is non-null (nothing was written, the content is the result).
  Before editing, run `Search(text, "ChangedContent")` over `RoslynSentinel.Tests.Tools.Basic` and `RoslynSentinel.Tests.Basic` to confirm no existing test asserts the old echo (this planner's searches on 2026-10-08 found only `MassiveRefactoringTests.cs:89,164`, both `autoStage: false`).
- Done when: the four named tests pass (`RunTest` scope=project `RoslynSentinel.Tests.Tools.Basic`, filter `FullyQualifiedName~MemberAddResultSizeTests`).

### Step 3 - Prove which batch messages are user-visible (test first)
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Tools.Basic\ModifyAttributeBatchTests.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Tools.Basic\ModifyBaseTypeBatchTests.cs`
- Change: add `ModifyAttribute_BatchTwoEditsSameTarget_ErrorUsesBatchEditsNameAndNoInternalIdentifierAsync` and `ModifyBaseType_BatchTwoEditsSameTarget_ErrorUsesBatchEditsNameAndNoInternalIdentifierAsync`: build a batch with two edits that resolve to the same target (copy the existing `..._BatchOneEditTargetNotFound_...` setup at `ModifyAttributeBatchTests.cs:139` / `ModifyBaseTypeBatchTests.cs:146`), then assert the error text contains `batchEdits[0]` and `batchEdits[1]` and does NOT contain `_symbolNavigationEngine` and does not match the regex `(?<!batch)edits\[`. Also add one for `ModifyModifier` (find an existing modifier batch test with `Search(text, "ModifyModifier")` in `RoslynSentinel.Tests.Tools.Basic`; if none, add the test in a new `ModifyModifierBatchWordingTests.cs`).
- Done when: the tests are written and run. A test that FAILS is the proof for step 4/5/6 for that method; a test that already PASSES means a layer rewrites the text, so skip the matching engine step and note that in the Status line.

### Step 4 - Fix stale wording in `ApplyModifierBatchAsync`
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Engines.Basic\MemberRefactoringEngine.cs`
- Symbols: `ApplyModifierBatchAsync`, six string literals at lines 1082, 1089, 1105, 1132, 1142, 1161: `edits[` -> `batchEdits[`. At 1105 also replace `both _symbolNavigationEngine. Resolve to the same target` with `both resolve to the same target` (match the correct sentence at line 1716).
- Done when: the step 3 modifier test passes and `Build` reports 0 errors.

### Step 5 - Fix stale wording in `ApplyAttributeBatchAsync`
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Engines.Basic\MemberRefactoringEngine.cs`
- Symbols: `ApplyAttributeBatchAsync`, literals at lines 1693, 1700, 1716, 1742, 1752, 1758, 1764, 1780: `edits[` -> `batchEdits[` (line 1716 has two occurrences in one string).
- Done when: the step 3 attribute test passes and `Build` reports 0 errors.

### Step 6 - Fix stale wording in `ApplyBaseTypeBatchAsync`
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Engines.Basic\MemberRefactoringEngine.cs`
- Symbols: `ApplyBaseTypeBatchAsync`, literals at lines 4931, 4938, 4952: `edits[` -> `batchEdits[`; at 4952 also replace `both _symbolNavigationEngine. Resolve to the same target` with `both resolve to the same target`.
- Done when: the step 3 base-type test passes and `Build` reports 0 errors.

### Step 7 - RunTest: put the failing project and cause into StatusMessage; name silent non-zero exits
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceBuildTestImpl.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Tools.Basic\RunTestTests.cs`
- Symbols:
  - `WorkspaceBuildTestImpl.RunTest`, the `!testRunResult.RunCompleted` branch: add `StatusMessage = testRunResult.Detail ?? "Test run did not complete."` to the returned `SentinelCallToolResult<object>` (so the offload envelope relays it, per `ServiceRegistrationExtensionsBasic.cs:647-651`). `Detail` already reads "One or more projects did not complete their run: <Project>: <cause>" (`TestRunEngine.cs`, `overallDetail`).
  - `WorkspaceBuildTestImpl.SummarizeTestRun` (currently `private static`): make it `public static` and, when `!run.RunSucceeded && run.FailedCount == 0 && run.ProjectSummaries is { } projects`, append `" No test failed, but " + string.Join(", ", projects.Where(p => !p.RunSucceeded).Select(p => p.ProjectName)) + " exited non-zero (build or adapter error); see StdoutTail."`.
  - Tests in `RunTestTests.cs` (existing tests at lines 67-307 use `workspaceTools.RunTest(reason: "test message", ToolScope.solution, timeoutSeconds: 120)` over a generated fixture; copy that setup): `RunTest_ProjectFailsToBuild_StatusMessageNamesProjectAndCauseAsync` (fixture test project with a compile error -> no TRX -> assert `IsError`, `StatusMessage` contains the project name and "TRX"), and `SummarizeTestRun_NonZeroExitWithNoFailedTests_NamesTheProject` (direct call with a hand-built `TestRunResult` having one `ProjectTestSummary(..., RunSucceeded: false, ...)`; assert the text contains the project name and "exited non-zero").
- Call sites: `SummarizeTestRun` is called once, from `RunTest` (`WorkspaceBuildTestImpl.cs`, success branch); making it public changes no caller.
- Done when: both named tests pass.

### Step 8 - RunTest: recognise MSB3026 as a lock, test the detector
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Engines.Basic\TestRunEngine.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Basic\TestRunEngineLockDetectionTests.cs` (new)
- Symbols: extract the existing inline check in `RunOneProjectAsync` (the `string? lockDetail = null; if (stderrText.Contains("MSB3027") || ... "MSB3021" ...)` block) into `public static string? DetectFileLock(string stdout, string stderr)` declared on `TestRunEngine` (above `RunOneProjectAsync`; definition before the call), add `MSB3026` to the codes, and make `RunOneProjectAsync` call it. Keep the message text; append "A running Visual Studio Test Explorer testhost is a common holder." (journal `[b1dcd60b:L5]`).
  Tests: `DetectFileLock_Msb3026InStdout_ReturnsLockDetailAsync`, `DetectFileLock_Msb3027InStderr_ReturnsLockDetailAsync` (guards the existing behaviour), `DetectFileLock_CleanOutput_ReturnsNull`.
- Done when: the three named tests pass.

### Step 9 - RunTest descriptions: document scope/scopeName and the cost of a solution-wide filter
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceBuildTestTools.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceTools.cs`
- Symbols: both `RunTest` declarations (WorkspaceBuildTestTools.cs:48, WorkspaceTools.cs:498). Strings only:
  - add `[Description("solution (default) runs every test project one after another; project runs only the project named in scopeName; file is not supported.")]` to `scope`;
  - add `[Description("Project name (for example RoslynSentinel.Tests.Basic) when scope=project; not a parameter called projectName.")]` to `scopeName`;
  - append to the `filter` doc and the tool description: "With scope=solution a filter still builds and probes every test project (about 1.5 minutes for 13 projects); pass scope=project and scopeName to run one project in seconds."
  - replace the `WorkspaceTools.RunTest` one-line description with the same text as the richer `WorkspaceBuildTestTools.RunTest` description (copy it verbatim, including the clause about an unresolvable filter being a distinct error).
- Done when: `Build` reports 0 errors.

### Step 10 - Verification
- `Build` (0 errors).
- `RunTest` at solution scope; compare to the known-failure baseline (`reference_known_failing_tests`); report only new failures. If `ArchitectureDocFreshnessTests` fails because descriptions are embedded in `docs/generated/*.md`, regenerate with `scripts/Generate-ArchitectureMap.ps1`.
- `McpServerControl` stop (confirm guard), `LoadSolution`, then live: one `Member(addMember)` on a scratch file returns a short result with `lineChanges`, a `note` naming `ReadFile`, and no `changedContent`.

## Out of scope

- `ChangedContent` echoes in the other tools: covered by `plan_mutating_tools_compact_result_default.md` (central gate plus `LineChanges` at each site).
- `TestContext.Out` output not surfaced by RunTest (`[47b2c93d:L31]`, `[47b2c93d:L36]`): `TestRunEngine`'s TRX parsing keeps no per-test output (`TestCaseResult` has only name, outcome, duration, message, stack trace). Adding an `Output` field changes a public record: *needs design*.
- Pre-existing failure of unknown cause in TODO.md L48 (`SubAgentEval` child's full RunTest, 1 failed test).
- Compile-gate ordering (`Member` add of a caller before its helper is rejected with CS0103, `[0734e848:L6]`, `[50e0e6ac:L21]`): the error does not hint "add definitions first"; a hint is a separate small change to the `ValidationFailed` message and was not traced here.
- `Member` rejecting multi-member `newMemberSource` (`[ebd9923b:L6]`, `[f768a4e8:L11]`): the rejection is clean; batching is a feature.

## Risks and open decisions

- Resolved by user decision A (2026-10-08, revised): the echo is removed by default, the content is offloaded (fetch with `GetLargeResult`) and inline echo is a server-wide opt-in, not a per-tool parameter; the result tells the agent to call `ReadFile`, `GetMethodSource` or `Member(view)`. The TODO.md L336 entry that pulled the other way is proposed for closure in `plan_mutating_tools_compact_result_default.md` Step 7 (this plan does not edit TODO.md).
- Sequencing: Steps 1 and 2 need the other plan's Steps 1 and 2 first; Step 2's assertion `ChangedContent is null` fails otherwise.
- Step 3 is deliberately test-first because the journal may reflect an old binary or a different code path than the engine strings; do not edit steps 4-6 for a method whose test already passes.
- Step 7's first test needs a fixture solution containing a test project that fails to compile; confirm `dotnet test` on it yields no TRX (`TestRunEngine.RunOneProjectAsync` returns the "No TRX result file was produced" detail) before relying on the assertion text.
- Whether the cause of `[ebd9923b:L9]` (exit 1 with 1 passed, 0 failed) is another project's non-zero exit is a hypothesis (the entry says "likely"); step 7's wording names the projects precisely so the next occurrence is diagnosable rather than guessed.
