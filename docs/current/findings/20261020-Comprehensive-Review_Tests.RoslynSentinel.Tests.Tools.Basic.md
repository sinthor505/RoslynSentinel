# Finding: RoslynSentinel.Tests.Tools.Basic has about 65 weak-assertion tests, 6 of them genuine false-passes

**Status:** OPEN 2026-10-20. Audit complete; no fix decided, strengthening is the owner's call.

## Context

Read-only audit of `RoslynSentinel.Tests.Tools.Basic` (NUnit 5) for tests whose only meaningful assertion is
`Is.Null` / `Is.Not.Null`, `Is.Not.Empty`, `GreaterThan(0)`, `Assert.Pass`, or a "does not throw" with no outcome
check, applied to a rich result (engine result, tool result, status/flags, message text). Method: MCP text search
over the project (regexes for the weak forms, plus `DoesNotThrow|Assert.NotNull|Assert.Null|Assert.Inconclusive|
Is.Not.EqualTo(0)|Has.Count.GreaterThan` which found nothing weak), then reading each candidate method and the DTO
or engine method it calls. Nothing was executed. `BatteryEighteenTests.cs` and `BatteryTwentyTests.cs` had
uncommitted edits by another session at audit time; they were read, not touched.

Totals:

- Test methods: about 978 distinct (1,090 `[Test]`/`[TestCase*]` attribute lines) in 69 files.
- Weak-only tests: about 65 (about 6.6%); 56 of them are strict (null-only, one-branch-conditional-only, empty-only).
- `Is.Null` / `Is.Not.Null` asserts: 279 (186 `Is.Not.Null`, 93 `Is.Null`) in 227 methods across 38 files.
- Guarded by a follow-up on the same value, or a legitimate null contract (optional lookup, "no X on success"):
  roughly 75-78%. About 60-70 of the 279 sit inside weak-only tests.
- Real `Assert.Pass`: `BatteryTwentyTests.cs:108`. The `Assert.Pass` at `TestCategoryTaggingToolTests.cs:41` and the
  `Assert.True(true)` at `RunTestTests.cs:83` are inside fixture source strings, not test assertions.

## What is broken

### 1. Genuine false-passes (the assertion passes even when the operation failed)

1. **Likely pass on an error result, `BatteryEighteenTests.cs`:** `FormatDocumentSafe_ValidFile_ReturnsResult` (:211),
   `AnalyzeForeachForLinqConversion_WithForeach_ReturnsAnalysis` (:229),
   `ExtractConstantSafe_WithLiteralValue_ReturnsResult` (:285). Each asserts only `Is.Not.Null`. The engine reads these
   files from disk (`MsToolAugmentEngine.cs:693`, `:758`, `:1065`: `File.ReadAllTextAsync(filePath)`), but the test uses
   `SetSource` (in-memory solution) and `Setup` deletes stray top-level `*.cs` from the working directory, so
   `Test.cs` should not exist on disk. The engine then returns `MsAugmentResult.Fail("Could not read file ...")` (or a
   `ForeachLinqAnalysis` with `BlockingReason`), and the test still passes. Hypothesis: derived from source, not
   executed. Assert `IsError == false` plus `UpdatedContent` text (e.g. contains `const string HelloLabel`); for the
   foreach case `BlockingReason == null` and `CollectionVariableName`.
2. **`RegressionTests.cs:309-310`, `FindCallersSafe_OverloadedMethod_ContextSnippetSelectsCorrectOverload`:** asserts
   only that `intCallers` and `strCallers` are `Is.Not.Empty`. If `contextSnippet` were ignored, both would still return
   all callers and the test would pass. Assert the two lists differ (call-site line or `CallerMethod`).
3. **`BatteryTwentyTests.cs:914`, `SafeDelete_ValidPosition_ReturnsString`:** calls
   `DeleteUnusedSymbolTool(line: 1, column: 1)` and asserts only `result != null`; a tool result is never null. Assert
   `IsError`, then `SuccessData` as `AppliedChangeSummary` or `ErrorData.ErrorCode`.
4. **`GitToolsSmokeTests.cs:215, 226, 343`** (`Git_Status_/Git_Log_/Git_Diff_RespondsWithinBoundAsync`): `Is.Not.Null`
   plus elapsed time only. An instant error response passes. Assert `!IsError` and `SuccessData is GitStatusResult` /
   `GitLogResult` / the diff result.
5. **`RealProjectPatternTests.cs:527` and `:747`** (`ExtractConstant_SimpleLiteralString_ExtractedToConst`,
   `GenerateToStringSafe_PartialClass_HandledGracefullyOrSucceeds`): content is asserted only inside
   `if (!result.IsError)` with no `else`; any engine failure passes.
6. **`BatteryTwentyTests.cs:806, 819, 842, 884`** (ApplyDiff/ApplyUnifiedDiff validate and empty-changes): `Is.Not.Null`
   on a `SentinelCallToolResult<object>` whose `IsError`/`Code` is never read; a `DiffApplyFailed` or `InvalidArgument`
   result passes.

### 2. Pattern categories

| Pattern | Count | Examples |
| --- | --- | --- |
| A. `Is.Not.Null` as the only assert on an engine or tool result (no `IsError`, `ErrorCode`, or content) | 48 | `BatteryEighteenTests.cs:122,130,...,329` (23); `BatteryTwentyTests.cs:85,92,99,107,118,136,155,806,819,842,884,894,904,914,1000,1008` (16); `RealProjectPatternTests.cs:225,327,598` (3); `NewToolTests.cs:172,216-217` (2); `AdminToolsTests.cs:64-65` (1); `GitToolsSmokeTests` timing tests are counted in D |
| B. `Is.Not.Null`, then asserts only on one branch (`if IsError` message, or `if !IsError` content) | 7 | `RealProjectPatternTests.cs:499,527,566,747,945`; `FiveStarToolTests.cs:303,867` |
| C. Error path asserts only `ErrorData != null`, no `ErrorCode` or message | 8 | `BatteryTwentyTests.cs:128,164,830,875,992,1157,1168`; `GetLargeResultTests.cs:322` |
| D. Timing-only "responds within bound" | 3 | `GitToolsSmokeTests.cs:215,226,343` |
| E. `Is.Not.Empty` only where the test name promises more | 1 | `RegressionTests.cs:309-310` |
| F. Mild: content or type unchecked | 2 | `SemanticFindReplaceToolTests.cs:81` (preview `SuccessData` not inspected); `AdminToolsTests.cs:64-65` (string checked only for non-empty and not starting with "Cleared") |

Note: the A subtotal of 48 includes the 3 Git timing tests only if D is not also counted; the headline figure of about
65 weak-only tests is A(45) + B(7) + C(8) + D(3) + E(1) + F(1), with `AdminToolsTests.cs:64` placed in A and
`SemanticFindReplaceToolTests.cs:81` in F. Counts are approximate (hand-classified).

### 3. Offenders table

Result type for tool calls is `SentinelCallToolResult<object>` (`RoslynSentinel.Common/SentinelCallToolResult.cs:150`;
members `IsError`, `Code`, `Message`, `SuccessData`, `ErrorData`, `StatusMessage`, `WarningDetails`, `LargeResult`).

**`BatteryEighteenTests.cs`** (23 of 24 tests weak-only; the exception is `SortAndDeduplicateUsings_NonExistentFile_Throws`).
Names and assertions contradict each other: `*_NonExistentFile_ReturnsNull` and `*_ReturnsNotNull` all assert
`Is.Not.Null`.

| File:line | Test | DTO | Assert instead | Effort |
| --- | --- | --- | --- | --- |
| :122, :130 | `EncapsulateFieldSafe_PublicField_ReturnsResult`, `_NonExistentFile_ReturnsNull` | `MsAugmentResult(IsError, Error, UpdatedContent, ErrorCode)` | Public field: `IsError == false`, `UpdatedContent` contains the `OrderId` property. Missing file: `IsError`, `ErrorCode == ToolErrorCode.NotFound`, `Error` contains "File not found" | trivial |
| :138, :146 | `AnalyzeSwitchForPatternConversion_WithSwitchStatement_ReturnsAnalysis`, `_NonExistentFile_ReturnsNotNull` | `SwitchConversionAnalysis(IsSafeToConvert, TotalCases, Cases, BlockingReason)` | Real switch: `TotalCases == 3`, `IsSafeToConvert`. Missing file: `IsSafeToConvert == false`, `TotalCases == 0`, `BlockingReason` contains "File not found" | trivial |
| :156, :164 | `ConvertSwitchToPatternSafe_WithSwitchStatement_ReturnsResult`, `_NonExistentFile_ReturnsNotNull` | `MsAugmentResult` | Missing file: `IsError`, `Error` contains "File not found". Real-switch outcome not traced | moderate |
| :174, :182 | `ConvertStringFormatToInterpolatedSmart_WithFormatCall_ReturnsResult`, `_NonExistentFile_ReturnsNotNull` | `MsAugmentResult` | `UpdatedContent` contains `$"{OrderId}: {CustomerName}"`. Missing file: `ErrorCode == NotFound` | trivial |
| :193 | `SortAndDeduplicateUsings_FileWithUsings_ReturnsResult` | `UsingsCleanupResult(OriginalCount, RemovedDuplicates, UpdatedContent, WrittenToDisk)` | `OriginalCount == 5`, `RemovedDuplicates == 0`, `WrittenToDisk == false` | trivial |
| :211, :219 | `FormatDocumentSafe_ValidFile_ReturnsResult`, `_NonExistentFile_ReturnsNotNull` | `MsAugmentResult` | See false-pass 1. Missing file: `IsError`, `Error` starts with "Could not read file" | trivial |
| :229, :237 | `AnalyzeForeachForLinqConversion_WithForeach_ReturnsAnalysis`, `_NonExistentFile_ReturnsNotNull` | `ForeachLinqAnalysis(IsSafeToConvert, CollectionVariableName, StatementsBeforeForeach, BlockingReason, Recommendation)` | Real foreach: `CollectionVariableName == "items"`. Missing file: `BlockingReason` starts with "Could not read file" | moderate |
| :248, :256 | `GetWorkspaceHealth_WithLoadedSolution_ReturnsReport`, `_NoSolution_ReturnsReport` | `WorkspaceHealthReport` | `HasLoadedSolution` true/false, `ProjectCount`, `IsOperational`. `RegressionTests.cs:1160` already covers the no-solution case, so these are partly redundant | trivial |
| :266, :274 | `PreviewAddMissingUsings_ValidFile_ReturnsPreview`, `_NonExistentFile_ReturnsNotNull` | `AddUsingsPreview(SolutionRequired, UsingsToAdd, Warning, UpdatedContent)` | Valid: `SolutionRequired == false`, `UsingsToAdd` empty. Missing: `Warning` contains "not part of the loaded solution" | trivial |
| :285, :293 | `ExtractConstantSafe_WithLiteralValue_ReturnsResult`, `_NonExistentFile_ReturnsNotNull` | `MsAugmentResult` | See false-pass 1 | moderate |
| :303, :311 | `GenerateToStringSafe_ValidClass_ReturnsResult`, `_NonExistentFile_ReturnsNull` | `MsAugmentResult` | `UpdatedContent` contains `ToString()`. Missing file: `Error` starts with "Could not read" | trivial |
| :321, :329 | `ExtractMethod_WithMethodBody_ReturnsResult`, `_NonExistentFile_ReturnsNotNull` | `MsAugmentResult` | `IsError` and `UpdatedContent` (snippet resolution not traced). Missing file: `Error` contains "File not found in solution" | moderate |

**`BatteryTwentyTests.cs`** (16 weak-only tests plus 7 in category C; 76 tests in the file).

| File:line | Test | DTO | Assert instead | Effort |
| --- | --- | --- | --- | --- |
| :85, :92, :99, :107-108 | `Features_List_ReturnsList`, `Features_UpdateEmpty_ReturnsResult`, `Features_GetEmptyList_ReturnsResult`, `Features_GetWithFeatureName_ReturnsResult` | `SentinelCallToolResult<object>` | `!IsError`; for `list`, `SuccessData` content (flag names); drop the `Assert.Pass` at :108 | moderate |
| :118 | `List_Projects_WithLoadedSolution_ReturnsList` | `List<ProjectInfoEntry>` | `Name == "TestProj"` present | trivial |
| :136 | `List_Files_KnownProject_ReturnsFileList` | `List<string>` | contains `Test.cs` | trivial |
| :155 | `List_Dependencies_KnownProject_ReturnsReport` | dependency engine report | `!IsError` plus report fields | moderate |
| :806, :819, :842, :884 | `ApplyDiff_Diff_Validate_ReturnsDiagnosticReport`, `ApplyDiff_Files_Validate_ReturnsDiagnosticReport`, `ApplyUnifiedDiff_Validate_ReturnsDiagnosticReport`, `ApplyDiff_Files_Apply_EmptyChanges_ReturnsResult` | `SentinelCallToolResult<object>` | `!IsError`; for validate, the diagnostic report content; for empty changes, the expected `IsError`/`ErrorCode` (probably `InvalidArgument`, hypothesis) | moderate |
| :894 | `RetryFailedChanges_NoFailedChanges_ReturnsResult` | `SentinelCallToolResult<object>` | `!IsError` plus message or `StatusMessage` | trivial |
| :904, :1000, :1008 | `GetDiagnostics_File_ValidFile_ReturnsSummary`, `GetDiagnostics_Project_KnownProject_ReturnsSummary`, `GetDiagnostics_Solution_ReturnsSummary` | `DiagnosticSummary(Errors, Warnings, Details, BuildVerification)` | `Errors == 0`, `Details` empty (as the source implies) | trivial |
| :914 | `SafeDelete_ValidPosition_ReturnsString` | `SentinelCallToolResult<object>` | See false-pass 3 | trivial |
| :128, :164, :830, :875, :992, :1157, :1168 | `List_Projects_NoSolution_ReturnsStructuredError`, `List_SolutionItems_NoSolutionLoaded_...`, `ApplyDiff_Diff_Apply_NonExistentFile_...`, `ApplyUnifiedDiff_Apply_NonExistentFile_...`, `CreateProject_NewProjectName_...`, `Build_FullBuild_NoRealSolutionOnDisk_...`, `SplitProjectByFolder_NonExistentFolder_...` | `ResultError` | Add `ErrorData.ErrorCode` (e.g. `SolutionNotLoaded`, `NotFound`, `DiffApplyFailed`; exact codes unverified for the last four) and a message check | trivial |

**Other files**

| File:line | Test | DTO | Assert instead | Effort |
| --- | --- | --- | --- | --- |
| `GitToolsSmokeTests.cs:215, 226, 343` | `Git_Status_/Git_Log_/Git_Diff_RespondsWithinBoundAsync` | `SentinelCallToolResult<object>` | `!IsError` and `SuccessData is GitStatusResult` / `GitLogResult` / diff result | trivial |
| `RealProjectPatternTests.cs:225` | `GenerateToStringSafe_InternalStaticClassWithConstants_ReturnsFail` | `MsAugmentResult` | Name says Fail: assert `IsError` and `Error` text | trivial |
| `RealProjectPatternTests.cs:327` | `EncapsulateField_AlreadyPrivateField_ReturnsFail` | `MsAugmentResult` | Same | trivial |
| `RealProjectPatternTests.cs:598` | `ExtractMethod_AsyncCacheLogic_ExtractedWithCorrectReturnType` | `MsAugmentResult` | Name says return type: `UpdatedContent` contains `Task<List<string>> FetchItems` | moderate |
| `RealProjectPatternTests.cs:499, 527, 566, 747, 945` | "accept either outcome" tests | `MsAugmentResult` | Pick the deterministic outcome and assert it | moderate |
| `FiveStarToolTests.cs:303` | `ExtractConstant_InterpolatedString_FailsCleanly` | `MsAugmentResult` | Assert the actual outcome (`IsError` plus `ErrorCode`) | moderate |
| `FiveStarToolTests.cs:867` | `FormatDocumentSafe_SyntaxErrors_StillReturnsWithoutThrowing` | `MsAugmentResult` | `IsError == false`, `UpdatedContent` contains `class Broken` | trivial |
| `NewToolTests.cs:172` | `FormatDocumentPreview_AlreadyFormatted_ReturnsNoChanges` | `FormatPreviewResult` | Asserts only `Hunks != null`: assert `Changed == false`, `Hunks` empty | trivial |
| `NewToolTests.cs:216-217` | `GetDiagnosticsSummary_GroupsByDiagnosticId` | `EngineResultWrapper<DiagnosticSummary>` | `Data.Errors >= 2`, `Details` contains `CS0103` entries | trivial |
| `AdminToolsTests.cs:64-65` | `Acknowledge_MalformedJsonArray_ReturnsParserError` | `string` | The actual parser-error text, not just non-empty | trivial |
| `RegressionTests.cs:309-310` | `FindCallersSafe_OverloadedMethod_ContextSnippetSelectsCorrectOverload` | caller list | See false-pass 2 | trivial |
| `SemanticFindReplaceToolTests.cs:81` | `SemanticFindReplace_PreviewMode_ReturnsSitesWithoutWriting` | preview `SuccessData` | Inspect site count and lines | moderate |
| `GetLargeResultTests.cs:322` | `T6_GetLargeResult_FilePathOutsideScansDir_ReturnsError` | `ResultError` | `ErrorData.ErrorCode` | trivial |

Reviewed and found clean (guard followed by real member asserts, or null is the contract): `RefactoringToolHelpersTests`,
`QualifyCrefsToolTests`, `ConstructorParameterCallSiteFixupTests`, `BuildToolResultShapeTests`, `GetLargeResultTests`
(other than :322), `McpServerStatusToolListingTests`, `TestCategoryApplyTests`, `DocumentationToolsTests`,
`LoadSolutionWaitTests`, `ListSolutionItemsAllTests`, `NewToolTests` caller/implementation tests.

## NotFound-style outcomes that let failures slip through

These are engine or tool paths where a "not found" / "cannot do it" result is returned as a value (a `Fail` with no
specific code, a `BlockingReason` string, a `Warning`, a `null` or empty `UpdatedText`) instead of an exception or a
distinct typed code. That is what lets the weak tests above pass. Whether the engine should throw is the owner's call.

1. **Missing file handled as data, inconsistently, in `MsToolAugmentEngine`:**
   - `EncapsulateFieldSafeAsync` (`:101-104`) and `ConvertStringFormatToInterpolatedSmartAsync` (`:445-448`) return
     `MsAugmentResult.Fail(..., ToolErrorCode.NotFound)`.
   - `ExtractMethodSafeAsync` (`:1437-1441`) returns `Fail("File not found in solution: ...")` with no code, so `Code`
     falls back to `ToolErrorCode.Exception` (`:20`, `Code => ErrorCode ?? (IsError ? ToolErrorCode.Exception : null)`).
   - `FormatDocumentSafeAsync` (`:693-694`), `ExtractConstantSafeAsync` (`:1065-1066`) and `GenerateToStringSafeAsync`
     (`:1233-1234`) return `Fail("Could not read ...")` with no code, so a missing file is indistinguishable from an
     I/O error and reports as `Exception`.
   - `SortAndDeduplicateUsingsAsync` (`:592`) throws `InvalidOperationException` instead, and
     `BatteryEighteenTests.cs` asserts that throw while asserting `Is.Not.Null` for every sibling. Same failure, three
     different contracts.
2. **Failure encoded in a DTO field, not an error:** `AnalyzeSwitchForPatternConversionAsync` (`:243-246`) returns
   `SwitchConversionAnalysis(false, 0, [], "File not found: ...")`; snippet-resolution `ToolException` is caught and
   turned into `BlockingReason` (inside the same method, `:239-296`), as is "No switch statement found".
   `AnalyzeForeachForLinqConversionAsync` (`:758-763`) does the same with `ForeachLinqAnalysis.BlockingReason`.
   `PreviewAddMissingUsingsAsync` (`:913-921`) reports a missing file as `Warning` with `SolutionRequired == false`.
   A caller (or test) that checks only non-null sees success. `ConvertSwitchToPatternSafeAsync` (`:311-315`) then
   re-wraps an unsafe analysis as `Fail(analysis.BlockingReason)` with no code, so "file not found" surfaces as
   `Exception`, not `NotFound` (hypothesis on the final surfaced code: derived from `MsAugmentResult.Code`, not run).
3. **Null-forgiving after a NotFound-style branch:** `ConvertSwitchToPatternSafeAsync` `:318`
   (`...FirstOrDefault()!`) relies on the analysis call having already rejected a missing document; if that
   pre-check ever returns `IsSafeToConvert == true` for a missing doc, the failure becomes a `NullReferenceException`
   rather than `NotFound`. Not triggered by current tests (hypothesis).
4. **Disk read where the workspace is authoritative:** `FormatDocumentSafeAsync`, `AnalyzeForeachForLinqConversionAsync`
   and `ExtractConstantSafeAsync` read the file from disk even when it is in the loaded solution
   (`GenerateToStringSafeAsync` prefers the workspace, `:1205-1235`). With an in-memory test solution the
   "file exists in the solution" case silently takes the "cannot read" branch, which the weak tests accept
   (false-pass 1).
5. **Empty `UpdatedText` collapses every outcome into a generic error:** `DeleteUnusedSymbol`
   (`RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs`, local `ApplyAndRespondAsync`, about `:474-482`)
   treats `string.IsNullOrEmpty(result.UpdatedText)` as `ToolErrorCode.Exception` with the `Outcome` interpolated
   into the message, so `TargetNotFound`, `NoCandidatesFound` and `CannotRemove` all become `Exception`; and an
   unresolved handle is also reported as `Exception` (`resolution.Error`), not `NotFound`. The `SafeDelete_ValidPosition`
   test (`BatteryTwentyTests.cs:914`) cannot tell these apart because it asserts only non-null. (The newer
   `RefactoringToolHelpers.RequireUpdatedText`/`GuardEngineResult` do map outcomes to codes and are well tested in
   `RefactoringToolHelpersTests`; `DeleteUnusedSymbol` appears not to use them - hypothesis, not traced.)
6. **Engine contracts that differ on the same condition:** `DiagnosticEngine.GetFileDiagnosticsAsync` throws
   `ToolNotFoundException` for a missing file (`RoslynSentinel.Engines.Basic/DiagnosticEngine.cs`, about `:42`) while the
   augment engine methods above return a value. Tests for the former can assert a throw; tests for the latter fall back
   to `Is.Not.Null`.
7. **Tests that accept both outcomes, so a NotFound-style failure is indistinguishable from success:**
   `RealProjectPatternTests.cs:499, 527, 566, 747, 945`, `FiveStarToolTests.cs:303`. Each branches on `IsError` and
   asserts nothing (or only "message non-empty") on one side, so a regression to an unrelated failure passes.

## Root cause

Two causes, both in the test layer plus one design inconsistency:

- Legacy "battery" tests were written as smoke tests ("returns a result without throwing") when the engines still
  threw; the contract later changed to return result DTOs, and the assertions were not strengthened.
- The augment engine has no single not-found contract (items 1-2 above), so a test author has no one code or field to
  assert, and `Is.Not.Null` was the only thing common to all paths. Not traced further: git history of when the
  assertions were last touched.

## Why it matters

A refactor that makes an engine or tool fail (or ignore `contextSnippet`) keeps these tests green, so the suite gives
false confidence for exactly the tools agents use to edit code. The inconsistent not-found contract also means agents
receive `ErrorCode == Exception` for conditions that are really `NotFound`, which weakens the structured-error design.

## Recommendation

Ranked by leverage; none is decided.

1. Fix the six false-passes first (section "Genuine false-passes"); each is a one- or two-line assert change.
2. Strengthen `BatteryEighteenTests.cs` and `BatteryTwentyTests.cs` (46 tests) using the members in the offenders table;
   mostly trivial. Rename tests whose names contradict their assertion (`*_ReturnsNull` asserting `Is.Not.Null`).
3. Owner decision: pick one not-found contract for `MsToolAugmentEngine` (throw `ToolNotFoundException`, or always
   return `Fail(..., ToolErrorCode.NotFound)`), then assert it in all the `_NonExistentFile_` tests.
4. Make the three disk-reading methods prefer the workspace document like `GenerateToStringSafeAsync`, or make the
   tests write a real temp file (as `FiveStarToolTests` and `RegressionTests.cs:1011` already do).
5. Replace the "accept either outcome" tests in `RealProjectPatternTests.cs` and `FiveStarToolTests.cs` with a
   deterministic expectation per input.

## Out of scope

- Other test projects; the engines' own behaviour beyond what the weak tests touch.
- Tests that assert only `!IsError` / `Is.True` (outside the brief's patterns).

## Not checked

- No test was executed; false-pass 1 and every "Assert instead" value that is not a quoted engine string is derived
  from source reading, not from a run.
- The real-switch and real-extract outcomes in `BatteryEighteenTests.cs` (`ConvertSwitchToPatternSafe_WithSwitchStatement`,
  `ExtractMethod_WithMethodBody`) were not traced.
- `GitToolsSmokeTests.cs` (3,121 lines) was read only at the regex-matched lines plus the reset, status and commit tests.
- Tests with no null/empty/`GreaterThan(0)` assertion were not read; the regex can miss unusual shapes (a split-line
  `Is.Not.Null`, a bare `Assert.That(x)`).
- Exact `ErrorCode` values for several tool error paths (`CreateProject`, `SplitProjectByFolder`, `ApplyDiff` on a
  missing file, `BuildTool` fullBuild on an in-memory solution) were not verified.
- Counts are hand-classified and approximate (about +/- 3 in the weak-only total).