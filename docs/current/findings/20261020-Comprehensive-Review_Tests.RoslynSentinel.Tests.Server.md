# Finding: RoslynSentinel.Tests.Server has ~35 tests with weak, vacuous or dead assertions (false-pass risk)

**Status:** OPEN 2026-10-20. No fix decided; this is an audit for owner triage of which tests to strengthen.

## Context

Read-only audit of the NUnit project `RoslynSentinel.Tests.Server` (all `*.cs` under `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Server\`, excluding bin/obj and any `*/Worktree/` clone), part of the 2026-10-20 comprehensive test review. Goal: find tests whose only meaningful assertion is `Is.Null`/`Is.Not.Null` (also `Assert.IsNotNull`, `Is.Not.Empty`-only, `Count.GreaterThan(0)`-only, `Assert.Pass`, "does not throw" with no outcome check) or another weak/negative-only check on a rich result, so they can pass when the operation failed.

Method: text search for the null/empty/flag patterns, then every candidate test and most files were read in full via the RoslynSentinel MCP tools. DTO members were checked in `RoslynSentinel.Common/SentinelCallToolResult.cs` and `RoslynSentinel.Common/SourceTransformResult.cs`. No tests were run; everything below comes from reading source.

DTOs involved:
- Tool-level tests (`CodeTransformToolsOutcomeTests`): `SentinelCallToolResult<object>` -- members `IsError`, `Status` (`ResultStatus`), `Code`, `Message`, `SuccessData`, `ErrorData` (`ResultError.ErrorCode`/`Message`), `Findings`.
- `SourceTransformResult`: `UpdatedSource`, `WroteToFile`, `WorkspaceUpdated`, `filePath`.
- Wire-level tests: the MCP SDK `CallToolResult` (`IsError`, `Content`, `StructuredContent`) plus parsed JSON text.

## What is broken

### Verdict

The project is mostly clean. It is infrastructure and wire-level tests (filters, schema, registry, startup parsing), and most are well asserted. No engine-DTO test relies on a null-only check. About 35 of ~239 test methods (~15%) have weak-only assertions: 29 live plus 6 dead. The problem is not the `Is.Null` pattern (only ~7 of 51 null asserts are weak) but negative-only text checks and nested `if`/`foreach` assertions that run zero times.

### 1. Totals

- Test methods: ~239 (209 `[Test]` + 30 methods using `[TestCase]`/`[TestCaseSource]`). Attribute lines: 306 (209 `[Test]` + 97 `[TestCase*]` lines), before `TestCaseSource` expansion. Derived from attribute counts, not from a test run.
- Tests with weak-only assertions: ~35 (29 live, 6 dead via `Assert.Ignore`).
- `Is.Null` / `Is.Not.Null` asserts overall: 51. The wider pattern (adding `Is.Empty`, `Is.Not.Empty`, `GreaterThan(0)`, `DoesNotThrow`) matched 90.
- Guarded by a follow-up assert on the same value: 29 of 51 (57%).
- Null/non-null is the contract (no richer status to assert): 15 more. Examples: "no note returned" (`ApplyLegacySearchReferencesKindTests.cs:51,63,75,85`), "Validate returns no error" (`CoerceStringToStructuredTests.cs:129,170`), `GetService` resolution (`ExperimentalModeTests.cs:98`, `McpToolsetControlTests.cs:552,553,608`), `DescribeNoActiveToolsFailure` null on success (`NoActiveToolsStartupTests.cs:73,83,130`), echoed JSON null (`ToolCallEchoFilterTests.cs:394`), `Code` null on success (`CodeTransformToolsOutcomeTests.cs:242`).
- Guarded plus contract: 44 of 51 (86%).
- Weak or redundant: 7.
  - `TestServiceProviderBuilderTests.cs:16,24,40,43` -- `GetRequiredService` never returns null, so the null asserts add nothing (a commented-out assert remains at :20).
  - `HaltStampFilterTests.cs:127,128` -- `sessionHaltReason` and `sessionHaltRecovery` only checked non-empty in helper `AssertStampedAs`.
  - `McpServerStatusStructuredContentTests.cs:103` -- `Breakers` only non-null (test is dead anyway).

### 2. Pattern categories

| Pattern | Count | Examples |
| --- | --- | --- |
| Negative-only text check (`!IsPathResolutionError`, `Does.Not.Contain(...)`) with no `IsError` or content assert | 14 tests | `RelativePathInputAuditTests.cs:129-300` (11 tests), `McpToolsetControlTests.cs:482`, `TestCategoryToolSchemaTests.cs:133` |
| Assertions inside nested `if`/`foreach` that run zero times when the shape differs | 3 tests | `WirePathRelativizeFilterTests.cs:117,197,232` |
| Only `result.IsError` asserted, no content/outcome | ~8 tests | `CodeTransformToolsOutcomeTests.cs:172,182`; `OrientationBreakerFilterTests.cs:96`; `McpToolsetControlTests.cs:495,612` |
| `Is.TypeOf<SourceTransformResult>` with no member check | 2 tests | `CodeTransformToolsOutcomeTests.cs:161,264` |
| Dead tests (`Assert.Ignore` first line, `[Explicit]`) | 6 tests | `McpServerStatusStructuredContentTests.cs:61-66,78-83`; `StructuredContentDataTagTests.cs:99-103,128-132,164-168,181-185` |
| Redundant DI null asserts | 2 tests | `TestServiceProviderBuilderTests.cs:7-25,28-44` |

### 3. Genuine false-passes (flagged first)

1. `RelativePathInputAuditTests.cs`, all 11 `*_AcceptsRelativePath` tests. The only assert is `Assert.That(!IsPathResolutionError(text), Is.True)` (helper at the file's `IsPathResolutionError`, matching "NotFound", "FileNotFound", "InvalidArgument", "InvalidPath", "not found"). Any other failure passes: solution not loaded, exception, build failure, unknown tool. `result` is captured but never asserted. Active mode is only `{"Workspace"}` (`RelativePathInputAuditTests.cs:30`).
   - HYPOTHESIS (not verified): `BasicModeToToolClasses["Workspace"]` (`RoslynSentinel.Server.Basic/ToolClassRegistry.cs:93`) lists WorkspaceTools, WorkspaceReadNavigationTools, DocumentationTools, SymbolNavigationTools, SymbolRelationshipTools, GitTools, GitRemoteTools -- not DeclarationTools, ParameterEditTools or RefactoringStructuralTools (which declares `Member`, `RoslynSentinel.Tools.Basic/RefactoringStructuralTools.cs:108`). So `Declaration_View` (:175), `ParameterEdit_View` (:192) and `Member_View` (:242) probably call tools that are not registered in that mode. Each then either passes vacuously or throws, depending on how the SDK client surfaces an unknown tool; that behaviour was not checked.
2. `WirePathRelativizeFilterTests.cs:197` `LargeResultAndSolutionPath_StayAbsolute`: after `IsError` is not true, everything is inside `if (offloaded) { if (largeResult) { if (filePath) ... } }`. If the response is not offloaded the test asserts nothing else.
3. `WirePathRelativizeFilterTests.cs:117` `Search_SymbolMode_ReturnsRelativeFilePaths_AndSolutionRoot`: the `filePath` relative-and-forward-slash loop sits inside `if (successData is JsonArray)` / `if (item is JsonObject && ContainsKey("filePath"))`, so it runs zero times for a non-array, empty array or items without `filePath`. The `solutionRoot` assert is real.
4. `WirePathRelativizeFilterTests.cs:232` `EnvVarOff_ReturnsAbsolutePaths_AndNoSolutionRoot`: same nested-if shape for the absolute-path loop. The `solutionRoot` absence assert is real.
5. `CodeTransformToolsOutcomeTests.cs:260-261` `GenerateConstructor_ClassWithFields_StillSucceedsWithTheGeneratedSummary`: the only content assert is `Assert.That(result.SuccessData as string, Does.Not.Contain("already in the requested state"))`. A null or non-string `SuccessData` yields null from `as string`, and `Does.Not.Contain` on a null actual passes (NUnit's substring constraint returns false for null, which the `Not` inverts -- from knowledge of NUnit behaviour, not run here). The name promises a generated summary that is never checked.
6. `McpToolsetControlTests.cs:482` `EnabledTool_IsCallable_WithoutRelisting`: only `Does.Not.Contain("Unable to resolve")` and `Does.Not.Contain("No service for type")`. Any other failure (including `IsError` true for another reason) passes.
7. `TestCategoryToolSchemaTests.cs:133` `Call_WithExactEnumName_PassesValidation`: `isError` is discarded and the only assert is `Does.Not.Contain("not a valid value for parameter 'framework'")`. An unrelated failure or empty text passes.
8. `SchemaLeanProfileTests.cs:220` `Lean_CallPassingHiddenReturnDiff_IsNotRejectedAsUnknown`: negative-only (`Does.Not.Contain("Unknown parameter")`). The in-test comment says no solution is loaded and any other outcome is acceptable, so this is by design; it would still pass on empty text.

### 4. Offenders table

| file:line | Test(s) | DTO | Should assert (real member names) | Effort |
| --- | --- | --- | --- | --- |
| `RelativePathInputAuditTests.cs:129` (GetFileOutline), `:175` (Declaration_View), `:192` (ParameterEdit_View), `:242` (Member_View), `:300` (Build_FileScope); remaining rows GetMethodSource, ReadFile, DeleteUnusedSymbol, GetDiagnostics_FileScope, ReplaceSnippet_Validate_SingleEdit, ReplaceSnippet_Validate_BatchEdits lie between :129 and :300 (exact lines not recorded) | all 11 `*_AcceptsRelativePath` | `CallToolResult` + text | `IsError` is false and text contains the probe symbol (`AuditTarget` / `RelativePathAuditProbe`). For dry-run/validate rows assert structured `errorData.errorCode` is not `NotFound`/`InvalidArgument`. Drop the unused `result` capture. | moderate |
| `WirePathRelativizeFilterTests.cs:197` | `LargeResultAndSolutionPath_StayAbsolute` | `CallToolResult` | `stub["offloaded"]` true; `largeResult.filePath` present and rooted | trivial |
| `WirePathRelativizeFilterTests.cs:117` | `Search_SymbolMode_ReturnsRelativeFilePaths_AndSolutionRoot` | `CallToolResult` | `successData` is an array with `Count > 0`; every item has `filePath` | trivial |
| `WirePathRelativizeFilterTests.cs:232` | `EnvVarOff_ReturnsAbsolutePaths_AndNoSolutionRoot` | `CallToolResult` | same as above for the rooted-path loop | trivial |
| `McpToolsetControlTests.cs:482` | `EnabledTool_IsCallable_WithoutRelisting` | `CallToolResult` | `IsError` false, or the exact expected `errorCode` | trivial |
| `McpToolsetControlTests.cs:507` | `McpServerStatus_PointsInactiveToolsAtTheToolset_AndReportsEnabledOnesActive` | text | second assert is negative-only; add a positive assert that the post-enable listing is non-error and names `Declaration` as active | trivial |
| `McpToolsetControlTests.cs:495` | `DisabledTool_IsNotCallable` | `CallToolResult` | `IsError` true passes for any error; assert error text names the tool / unavailability | trivial |
| `McpToolsetControlTests.cs:612` | `McpToolsetControl_RejectsAnUnknownToolSet` | `CallToolResult` | `IsError` true plus `ListChangedCount` zero; assert error text names `notASet` | trivial |
| `TestCategoryToolSchemaTests.cs:133` | `Call_WithExactEnumName_PassesValidation` | `(bool IsError, string Text)` | positive marker in text, or the exact expected error | trivial |
| `SchemaLeanProfileTests.cs:220` | `Lean_CallPassingHiddenReturnDiff_IsNotRejectedAsUnknown` | text | text is not empty; optionally the expected `errorCode` | trivial |
| `CodeTransformToolsOutcomeTests.cs:248-262` | `GenerateConstructor_ClassWithFields_StillSucceedsWithTheGeneratedSummary` | `SentinelCallToolResult<object>` | `SuccessData` is a string containing the generated-constructor summary (cast, not `as`) | trivial |
| `CodeTransformToolsOutcomeTests.cs:161-170` | `ConvertLockToSemaphoreSlim_MethodWithoutLock_StaysASuccess` | `SourceTransformResult` | `UpdatedSource` equals the original source; `WroteToFile` and `WorkspaceUpdated` false | trivial |
| `CodeTransformToolsOutcomeTests.cs:264-285` | `ConvertLockToSemaphoreSlim_MethodWithLock_StillReturnsTheTransformedSource` | `SourceTransformResult` | `UpdatedSource` contains `SemaphoreSlim` and not `lock (`; `WroteToFile` false | trivial |
| `CodeTransformToolsOutcomeTests.cs:172-180` | `AddGuardClauses_NoReferenceTypeParameters_StaysASuccess` | `SentinelCallToolResult<object>` | `SuccessData` message text (siblings at :137 and :148 assert exact messages) | trivial |
| `CodeTransformToolsOutcomeTests.cs:182-190` | `GenerateConstructor_ClassWithoutFields_StaysASuccess` | `SentinelCallToolResult<object>` | `SuccessData` message text | trivial |
| `OrientationBreakerFilterTests.cs:96` | `NormalSession_NeverTripped_AllToolsUnaffected` | `CallToolResult` | `IsError` not true and `findings` absent/empty | trivial |
| `LargeResultOffloadFilterTests.cs:189` | `UnderThresholdToolResponse_PassesThroughUnchanged` | `CallToolResult` | `successData` is non-empty (an empty ListAll currently passes) | trivial |
| `HaltStampFilterTests.cs:127-128` (helper `AssertStampedAs`, used by 6 tests) | halt-stamp tests | `JsonObject` | pin `sessionHaltReason` for `mutation` and `orientation` kinds (recovery is pinned by some callers; reason only for `unrecoverable`) | trivial |
| `TestServiceProviderBuilderTests.cs:16,24,40,43` | `Build_ResolvesFullBasicEngineGraph_WithoutThrowing`, `..._WithCustomConfiguration` | `IServiceProvider` | replace null asserts: the custom singleton is the registered instance (`Is.SameAs`); two resolutions of `IWorkspaceReader` are the same singleton | trivial |
| `McpServerStatusStructuredContentTests.cs:61-83` | 2 dead tests | `CallToolResult` | unreachable (`Assert.Ignore` first line); delete or track in `TODO.md` | trivial |
| `StructuredContentDataTagTests.cs:99-185` | 4 dead tests | `CallToolResult` | unreachable; delete or track | trivial |

Per-file count of weak-only tests: RelativePathInputAuditTests 11; McpToolsetControlTests 4; CodeTransformToolsOutcomeTests 6; WirePathRelativizeFilterTests 3; StructuredContentDataTagTests 4 (dead); McpServerStatusStructuredContentTests 2 (dead); TestServiceProviderBuilderTests 2; one each in TestCategoryToolSchemaTests, SchemaLeanProfileTests, OrientationBreakerFilterTests, LargeResultOffloadFilterTests; HaltStampFilterTests helper affects 6 tests partially.

### 5. NotFound-style outcomes that let failures slip through

Engine behaviour below is as described by test names and comments in `CodeTransformToolsOutcomeTests.cs`; the engine sources were NOT read, so these are pointers for the owner, not traced facts. The question for each is whether the engine (or tool) should throw instead of returning a success-shaped result.

1. Method not found reported as success. `AddGuardClauses_MethodNotFound_StaysASuccessWithTheToolsMessage` (`CodeTransformToolsOutcomeTests.cs:137-146`) and `OptimizeIndependentAwaits_MethodNotFound_StaysASuccessWithTheToolsMessage` (`:148-157`) pass method name `"Missing"` (which does not exist) and assert `IsError` false with a "No ... found in 'Missing'" SuccessData string. A typo in the method name is indistinguishable from "nothing to do". Contrast `ConvertLockToSemaphoreSlim_MethodNotFound` (`:194-208`) and `ConvertMethodToIndexer_MethodNotFound` (`:61-76`), which are errors with `ToolErrorCode.NotFound`. The same "not found" condition is a success for two transforms and an error for two others.
2. Engine TargetNotFound carrying the unchanged file as `UpdatedText`. The comment at `:123-133` (`ConvertStaticToExtension_AlreadyAnExtension...`) says the engine returns TargetNotFound with the unchanged file as UpdatedText; the tool had to add a guard to turn that into an error. Likewise the comment at `:90-93` says OptimizeToValueTask puts its reason in `UpdatedText` ("// WARNING: ..." ahead of the whole file) and leaves `Message` empty, and the pre-guard tool shipped that text as the new file. Owner question: should these engines throw `ToolTargetIneligibleException` (as ConvertToBackgroundService does, `:290-302`) instead of overloading `UpdatedText`/`Message`?
3. Engine puts the whole source file in `Message` (`:55-60`, ConvertMethodToIndexer; `:187` ConvertLockToSemaphoreSlim). The tool trims it to one line; a tool that forgets the guard would leak the file as an "error message".
4. "Nothing to do" with unchanged file mapped to success. `ConvertLockToSemaphoreSlim_MethodWithoutLock_StaysASuccess` (`:161-170`), `GenerateConstructor_ClassWithoutFields_StaysASuccess` (`:182-190`) and `AddGuardClauses_NoReferenceTypeParameters_StaysASuccess` (`:172-180`) assert only `IsError` false (see section 4), so they cannot tell "nothing to do" from a silent failure that also returns success.
5. Success with null `Code`: `GenerateConstructor_ClassAlreadyHasConstructor_IsSuccessWithAlreadyInStateMessage` (`:225-245`) relies on `result.Code` null plus a message substring for the AlreadyInTargetState outcome. Reasonable, but the outcome is carried by message text rather than a status value.
6. Non-existent project returns success with zero records. `TrippedBreaker_EmptyListAll_DoesNotResetBreaker` (`OrientationBreakerFilterTests.cs` near :196-211) calls `ListAll` with `projectName = "NoSuchProjectZzz"` and expects a result with `totalRecords: 0` rather than a NotFound error. The orientation breaker deliberately keys off this, but a typo'd project name looks like a legitimate empty answer.
7. Zero-match searches are success. `OrientationBreakerFilterTests.cs:112,155,135` assert `IsError` is not true for guaranteed-no-match `Search(mode: text)`, and the trip is surfaced only as a `findings` entry. Deliberate (comment: "a zero-match search is a valid empty answer"); noted because the same shape lets a broken search index pass.
8. Unknown or unavailable names reported, not thrown. `Enable_ToolFromAnUnsupportedClass_IsReportedUnavailable` (`McpToolsetControlTests.cs` near :165-180) expects `SetEnabled` to return normally with `AddedTools` empty and `UnavailableTools` populated; `Enable_NeverReplacesAnExistingTool_AndDisableLeavesItAlone` likewise reports `ModifyEnum` as unavailable. `ToolClassNameResolutionTests.ExcludeTools_UnknownName_IsLeftAloneAndRemovesNothing` pins that an unknown `--exclude-tools` name is silently ignored. All deliberate, but a misspelled name does nothing and says nothing.
9. Heuristic NotFound text matching. `RelativePathInputAuditTests.IsPathResolutionError` treats "NotFound"/"InvalidArgument"/"not found" in free text as the only failure; all other errors are "valid" per the class comment ("other errors ... are valid and do not fail the audit"). Structured `ResultError.ErrorCode` is not consulted (false-pass 1 above).
10. Null-is-miss contracts that are fine and need no change (listed so the owner can skip them): `ApplyLegacySearchReferencesKindTests` null note, `NoActiveToolsStartupTests` null failure, `CoerceStringToStructuredTests` null `Validate`, `WriteToolAdviceHelper.IsExposed("unknown")` false, `UnrecoverableBreakerPolicy.IsAllowed(null/"")` false.

## Root cause

Not traced to a single cause. Observed contributors, each evidenced above: (a) the audit and echo-style tests were written to check "not a path error" and never tightened to a positive outcome; (b) several wire tests parse JSON defensively with `if`/`as` so a changed response shape silently skips the assert; (c) status-only asserts were kept after the engine/tool outcome split (`CodeTransformToolsOutcomeTests`) because the "StaysASuccess" tests only needed to prove the outcome gate did not turn a no-op into an error; (d) the structured-content tests were parked with `Assert.Ignore` rather than removed. The RelativePathInputAudit wrong-mode concern is a hypothesis only.

## Why it matters

These tests can stay green while the behaviour they name is broken: path-relativizing filters, tool-set enable/disable, and the code-transform outcome mapping. The tool surface is aimed at weak models that depend on clear failures, so success-shaped "not found" outcomes (section 5) are the ones most likely to hide a wrong call from both the test suite and the agent.

## Recommendation

Not decided; ranked by leverage:
1. Fix the false-passes in section 3 (all trivial except RelativePathInputAudit, which is moderate and also needs a check of which tools are registered in `Workspace` mode).
2. Strengthen the status-only `CodeTransformToolsOutcomeTests` and the two `McpToolsetControl` error-only tests (section 4).
3. Delete or ticket the 6 dead structured-content tests.
4. Owner decision on section 5 items 1-3: whether the code-transform engines should throw a typed exception for method-not-found / ineligible targets instead of returning success-shaped or `UpdatedText`-carrying results.

## Out of scope

- Null asserts classified as guarded or contract (44 of 51) -- not weak.
- `ArchitectureDocFreshnessTests` `Assert.Ignore` calls (:98, :112) -- deliberate regenerate/update-mode skips, documented in the test.
- `NoActiveToolsStartupTests.SmokeResolve_DebugModeOnly...` `Assert.Ignore` (:194) -- DEBUG-only check, documented.
- Other projects.

## Not checked

- Setup sections of `WirePathRelativizeFilterTests`, `McpServerStatusStructuredContentTests` and `StructuredContentDataTagTests` fixtures.
- The rest of `MergedToolDescriptionSyncTests` (clause cases from about line 120 onward).
- `ArchitectureDocGenerator.cs` and `TestServiceProviderBuilder.cs` (helpers, not tests).
- The ~239 test count is from attribute-line counts, not a run; `TestCaseSource` expansions are not counted.
- How the MCP SDK client reports a call to an unregistered tool (needed to settle the RelativePathInputAudit wrong-mode hypothesis).
- Engine source for every claim in section 5; those come from test names and comments only.
- NUnit null-actual behaviour of `Does.Not.Contain` (false-pass 5) was not run.