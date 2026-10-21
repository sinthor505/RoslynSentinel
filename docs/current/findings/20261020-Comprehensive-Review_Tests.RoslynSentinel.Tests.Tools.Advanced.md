# Finding: RoslynSentinel.Tests.Tools.Advanced - about half the tests assert only "a result exists"

**Status:** OPEN 2026-10-20. Strengthening is proposed, not started; slice order and the throw-vs-NotFound question are undecided.

## Context
Part of the 2026-10-20 comprehensive review of weak `Is.Null` / `Is.Not.Null` style assertions across the test projects. A read-only explore-sonnet agent read every file in `RoslynSentinel.Tests.Tools.Advanced` and inspected the returned DTOs. Counts are regex-based estimates (+/- 10%). No tests were run; claims about return shapes come from reading source. `BatteryTwentyFourTests.cs` carries another session's uncommitted edits, so its line numbers may drift. Line numbers are approximate (~L).

## What is broken

Totals: ~450 `[Test]`/`[TestCase]` attributes in 16 files; ~235 weak-only tests (~52%); 252 `Is.Null`/`Is.Not.Null` asserts in 12 files, of which only ~11% (25-30) are guarded by a follow-up assert.

| File | Approx. weak-only tests |
| --- | --- |
| `BatteryTwentyThreeTests.cs` | ~55 |
| `BatteryTwentyFourTests.cs` | ~55 (3 have no assert) |
| `BatteryTwentyOneTests.cs` | ~45 |
| `BatteryTwentyTwoTests.cs` | ~40 |
| `BatteryNineteenTests.cs` | ~20 |
| `AsyncThreadingToolsTests.cs` | 8 (negative-only `Is.Empty`) |
| `AdvancedToolsTests.cs` | ~5 |
| `ComprehensiveToolTests.cs` | 5 |
| `BlobIntegrityInvariantTests.cs` | 5 (vacuous early return) |
| `ToolGapsTests.cs` | ~2 |
| `BatteryThirtyOneTests.cs` | 1 |

### Genuine false-passes (pass when the operation failed or did nothing)
1. **No assertion at all** (`BatteryTwentyFourTests.cs`): `UpdateXmlDocsFromSignature_ValidMethod_ReturnsString` (~L1790), `ConvertExpressionBody_ToBlockBody_ReturnsString` (~L1800), `ExtractConstant_WithLiteralSnippet_ReturnsString` (~L1810).
2. **Asserts text already in the input** (`AdvancedToolsTests.cs`): `SyncInterfaceToImplementation_AddsMissingMethod` (~L208-212) checks `void DoB()` / `void DoA()`, both already in the source class; `SyncInterfaceToImplementation_AddsMissingProperty` (~L246-252) checks `Name` and `get;`, also already present. Both pass with the interface unchanged.
3. **Tool returns a human error string that satisfies `Is.Not.Null.And.Not.Empty`** (`BatteryNineteenTests.cs`): `GenerateHttpClient_ValidController_ReturnsCode` (L111), `GenerateDefaultConfigJson_ValidProject_ReturnsJson` (~L198; the POCO has no config keys so the tool probably returns "no configuration keys found"), `InterpolateStringSafe_WithFormatCall_ReturnsCode` (~L310), `GenerateClassesFromJson_*` (L92, L101; `Is.Not.Null` on `object`).
4. **Engine returns `Outcome=Modified` with `UpdatedText` even when nothing changed.** Verified for `CodeStyleEngine.UseIndexFromEndAsync` (`Engines.Advanced/CodeStyleEngine.cs:385-428`); other engines are unverified (hypothesis). ~20 `X_ValidFile_ReturnsSource` tests in `BatteryTwentyOneTests.cs` assert only `UpdatedText Is.Not.Null.And.Not.Empty`; several use `RichSource` with no matching pattern (UseIndexFromEnd, UseTimeProvider, SimplifyMemberAccess, CleanupImplicitSpans, UpgradePatternMatching). `BatteryTwentyThreeTests.cs` throw-guard tests (~L143 on) have the same shape: AddGuardClauses, AddBenchmarkStub, AddConfigureAwaitFalse, RemoveConfigureAwaitFalse, ConvertLockToSemaphoreSlim, ConvertToAsyncEnumerable, AddCancellationTokenToMethod, MakeMethodThreadSafe.
5. **Inverted "NonExistentFile" tests** named `_Throws` / `_ReturnsNull` / `_ReturnsNotNull` that assert `Is.Not.Null` on a never-null `DocumentEditResult`: `BatteryNineteenTests.cs` `ImplementInterfaceSafe_NonExistentFile_ReturnsNull` (~L270); `BatteryTwentyOneTests.cs` `OptimizeIndependentAwaits_NonExistentFile_ReturnsNull`, `UpgradeToPrimaryConstructor_NonExistentFile_ReturnsNull`; six `BatteryTwentyThreeTests.cs` tests (AddGuardClauses, AddBenchmarkStub, ConvertLockToSemaphoreSlim, ConvertToAsyncEnumerable, AddCancellationTokenToMethod, MakeMethodThreadSafe). ~20 siblings in `BatteryTwentyOneTests.cs` (L~145-575) use only `Assert.DoesNotThrowAsync`; the engine returns `Outcome=DocumentNotFound`, Message `"// Document not found."`, and nothing checks it. `BatteryNineteenTests.cs` `*_NonExistentFile_Throws` tests (GenerateConstructor L139, AddValidationToPoco, ConvertPropertySafe) assert `UpdatedText Is.Null.Or.Empty` with no Outcome check.
6. **Tool-level `_AutoStageTrue_ReturnsNotNull` / `_ReturnsString` / `_ReturnsDictionary`** tests in `BatteryTwentyFourTests.cs` assert only `result Is.Not.Null` on a `SentinelCallToolResult` or `DocumentEditResult`; a refused or failed call is still non-null. Examples: ExtractSuperclass (L167), SafeDeleteSymbol (L177), ChangeSignature (L188), MoveTypeToFile (L208), ModifyEnum (L304), ~40 more (table below).
7. **`BlobIntegrityInvariantTests.cs:73-85` `AssertChangeIdIsRedeemable`** returns early when no `ChangeId` was issued, so all 5 tests (WrapRange, MoveType, ExtractMembers, SyncInterface, Inline) pass when the tool failed outright. The XML doc says this is deliberate; rated low and judgment-dependent.
8. **Conditional assertion:** `SyncTypeAndFilename_DryRun_NeverDeletesOriginalFileAsync` (`BatteryTwentyFourTests.cs` ~L1190) checks `DryRun` only inside `if (!result.IsError)`. The comment says intentional; the file-exists check is unconditional.
9. **Status-only checks** in `BatteryTwentyTwoTests.cs`: ~40 tests assert `Is.Not.Null` on lists or tool results, several with the real assertion commented out (`//Assert.That(result.Count > 0);`). `ComprehensiveToolTests.cs` `ClassToRecord_ShouldReturnString` (L201) asserts only `result Is.Not.Null`.

### Pattern categories
| # | Pattern | Count | Examples |
| --- | --- | --- | --- |
| A | `UpdatedText Is.Not.Null.And.Not.Empty` on `DocumentEditResult`, no Outcome/content | ~55 | `BatteryTwentyOneTests.cs` ~L140; `BatteryNineteenTests.cs:130`; `BatteryTwentyThreeTests.cs:143` |
| B | `result Is.Not.Null` on `SentinelCallToolResult` / `DocumentEditResult` | ~50 | `BatteryTwentyFourTests.cs:167, 177, 188, 1003` |
| C | `Is.Not.Null` on engine/tool list or report, real assert commented out or absent | ~85 | `BatteryTwentyTwoTests.cs` ~L296, ~L443; `BatteryTwentyThreeTests.cs` ~L290-745 |
| D | NonExistentFile tests asserting nothing about the outcome | ~45 | `BatteryTwentyOneTests.cs` L~145-575; `BatteryNineteenTests.cs:139` |
| E | Status-only success (`!IsError` only) | ~25 | `BatteryTwentyFourTests.cs` Member_*OnEnum*, MethodSignature_*, ConstructorParameter_Add_*; `BatteryTwentyTwoTests.cs` ~L106 |
| F | Refusal asserted as `IsError` / `ErrorData Is.Not.Null` with no `ErrorCode` | ~5 | `BatteryTwentyFourTests.cs:248, ~1120, ~1500` |
| G | Negative-only `Is.Empty` / `Is.False` with no positive pair | ~10 | `AsyncThreadingToolsTests.cs` L52, 60, 97, 116, 135, 165, 202; `AdvancedToolsTests.cs` ~L82 |
| H | Tautological / already-true text assert | 3 | `AdvancedToolsTests.cs` ~L208, ~L246, ~L284 |
| I | Zero-assert tests | 3 | `BatteryTwentyFourTests.cs` ~L1790-1810 |
| J | Double-negative `Assert.That(!x.IsError, Is.False)` | ~6 | `ComprehensiveToolTests.cs:171`; `BatteryTwentyTwoTests.cs` error tests |

### Offenders table
Effort is trivial unless stated. DTO members were read from `DocumentEditResult`, `SentinelCallToolResult` and the generation result records.

| File:line | Test(s) | DTO | Assert instead | Effort |
| --- | --- | --- | --- | --- |
| `BatteryTwentyFourTests.cs` ~L1790, 1800, 1810 | UpdateXmlDocsFromSignature, ConvertExpressionBody_ToBlockBody, ExtractConstant (no asserts) | `DocumentEditResult` | `Outcome==Modified`; `UpdatedText` contains `<param`, a block body, `const string HelloLabel` | trivial |
| `BatteryTwentyFourTests.cs` L1863, 1882, 1901 | ConvertToNullCoalescing, ConvertToSwitch, ConvertToPattern | `DocumentEditResult` | `Outcome==Modified`; text contains `??=`, `switch`, `is` | trivial |
| `BatteryTwentyFourTests.cs` L167-429 (~14 tests) | ExtractSuperclass, SafeDeleteSymbol, ChangeSignature, ExtractInterface, MoveTypeToFile, MoveAllTypesToFiles (+InProject/InSolution L258, 268), ModifyEnum L304, InsertMemberAfter/Before L379/389, AddAttribute L399, AddBaseType L409, RemoveAttribute L419, RemoveBaseType L429 | `SentinelCallToolResult<AppliedChangeSummary>` | `IsError==false`; `Status=="applied"`; `AffectedFiles` non-empty; `Description`/`Diff` content or committed document text | moderate |
| `BatteryTwentyFourTests.cs` L993-1048, L1248-1441 (~25 tests) | MoveMember_ToBaseClass, ChangeAccessibility, AddModifier, RemoveModifier, ConstructorParameter, InlineMethod/Field/Parameter, IntroduceField/Parameter/Variable, MakeMethodStatic, InlineVariable, ExtractMembersToPartial, MoveTypeToOuterScope, ReplaceMember, AddMemberToClass, RemoveMember_ValidMember | same | same, plus the edited text of the target document | moderate |
| `BatteryTwentyFourTests.cs` L1309, 1319, 1366, 1386, 1765, 1775, 1872, 1891, 1910 | ExtensionToStatic, ConvertAbstractToInterface, ConvertPropertyToMethods, ConvertMethodToIndexer, ReduceBlockDepth, OptimizeTaskWait, NonExistentFile variants | `DocumentEditResult` | `Outcome`; `UpdatedText` content | trivial |
| `BatteryTwentyFourTests.cs` L1832, 1842, 1852 | AnalyzeControlFlow, AnalyzeDataFlow, FormatDocumentPreview | `ControlFlowSummary`, `DataFlowSummary`, `FormatPreviewResult` | `AlwaysReturns`/`ExitPathCount`; `ReturnPoints`; `Changed`/`TotalHunks` | moderate |
| `BatteryTwentyFourTests.cs` ~L330-350, L237, ~L1070-1120, ~L1500 | Member_*OnEnum*, RenameSymbol_ValidSymbol, MethodSignature_*, ConstructorParameter_Add_*, RemoveMember_*, refusal tests | `SentinelCallToolResult<AppliedChangeSummary>` | renamed text `GetDisplayLabel`; updated call-site text; `ErrorCode` `UnresolvedCallSites`/`ValidationFailed`/`NotFound` | moderate |
| `BatteryNineteenTests.cs` L92, 101 | GenerateClassesFromJson_* | `object` (tool text) | contains `class Product` / `class Root` and the property names | trivial |
| `BatteryNineteenTests.cs` L111, 120 | GenerateHttpClient_* | `string` | contains `OrdersClient`; NonExistentFile contains "not found in the loaded solution" | trivial |
| `BatteryNineteenTests.cs` L130, 139 | GenerateConstructor_* | `DocumentEditResult` | `Outcome==Modified` and `public Order(`; NonExistentFile gives `DocumentNotFound` | trivial |
| `BatteryNineteenTests.cs` L149, 159, ~168, ~178 | GenerateToString, GenerateRepositoryInterface, GenerateFluentBuilder, GenerateDecoratorClass | `GenerateToStringResult`, `RepositoryInterfaceResult`, `FluentBuilderResult`, `DecoratorResult` | `UpdatedContent` and `IncludedProperties`; `InterfaceName` and snippets; `BuilderClassName`, `Error==null`; `ClassName`, `SourceCode`. Check member names against the declarations first (hypothesis, e.g. `InterfaceName=="IOrderRepository"`). | moderate |
| `BatteryNineteenTests.cs` ~L198-310 | GenerateDefaultConfigJson_*, GenerateAsyncOverload, AddValidationToPoco, ImplementInterfaceSafe, ConvertPropertySafe, InterpolateStringSafe | string / `DocumentEditResult` | `Outcome` and expected text; valid JSON keys; `Contain("$\"")` | moderate |
| `BatteryTwentyOneTests.cs` ~L140-575 (~20 valid-file tests) | FixThreadSleep, AddBraces, UpgradePatternMatching, UseIndexFromEnd, ClassToRecord, RecordToClass, UseTimeProvider, SimplifyMemberAccess, OptimizeToValueTask, UseExceptionExpressions, others | `DocumentEditResult` | `Outcome==Modified` plus transformed text (ClassToRecord `record Point`, UseExceptionExpressions `ThrowIfNull`); use a fixture that actually triggers the pattern | moderate |
| `BatteryTwentyOneTests.cs` ~L145-575 (~20 NonExistentFile tests) | `*_NonExistentFile_ReturnsNullOrEmpty` | `DocumentEditResult` | capture result; `Outcome==DocumentNotFound`, Message `"// Document not found."` | trivial |
| `BatteryTwentyOneTests.cs` ~L355, 460, 590 | UpgradeToModernGuards, InvertBooleanLogic_BoolField, FindUseFrozenCollections | `DocumentEditResult`, `SentinelCallToolResult`, list | `Outcome` and message text; `IsError==false` and `SuccessData`; `Pattern=="UseFrozenCollection"` | trivial |
| `BatteryTwentyTwoTests.cs` ~L280-700 (~40 tests) | engine-list and tool tests | lists and `SentinelCallToolResult<T>` | concrete counts/names from the known fixtures (e.g. `ProcessAsync` callers, the `OrderService` implementor, `OrderId`/`CustomerName` readonly candidates); `SuccessData` content | moderate |
| `BatteryTwentyThreeTests.cs` ~L143-250 (~8 tests) | throw-guard valid-file tests | `DocumentEditResult` | `Outcome==Modified` and expected edit text | moderate |
| `BatteryTwentyThreeTests.cs` ~L290-745 (~40 tests) | AnalyzeSecurity, CheckForSqlInjection, FindHardcodedPaths, CheckForEmptyCatchBlocks, FindTaskVoidUsage, FindBlockingCallsInAsync, FindUnawaitedFireAndForget, GenerateTestScaffold, AnalyzeMethodControlFlow, GetDiagnosticsSummary, others | lists / result DTOs | the planted defect appears (`IssueType`/`Description`/`Line`); `TestClassName`, `Code`, `Error==null`; `EndPointIsReachable`; `Outcome==Success` | moderate |
| `BatteryTwentyThreeTests.cs` NonExistentFile tests (6) | see false-pass 5 | `DocumentEditResult` | copy `ConvertLockToSemaphoreSlim_NonExistentFile_ReportsNotFoundCode` (`Outcome==Error`, `ErrorCode==NotFound`) | trivial |
| `AdvancedToolsTests.cs` ~L208, ~L246, ~L233, ~L284, ~L82 | SyncInterface_AddsMissingMethod/Property/NoChange, IntroduceParameterObject_CreatesRecord, DetectValueTaskMisuse_DoesNotFlagImmediateAwait | `DocumentEditResult`, `AsyncSafetyReport` list | interface slice contains the member; `Outcome`; `request.Name` not the `Name` alternative; pair with a flagged case | trivial |
| `AsyncThreadingToolsTests.cs` L52, 60, 97, 116, 135, 165, 202 | DoesNotFlag tests | `List<AsyncSafetyReport>` | add a paired positive on the same fixture | trivial |
| `ComprehensiveToolTests.cs` L182, 191, 201, 211, 220 | GetBlastRadius, GetComprehensiveHealthReport, ClassToRecord, FindBoxingAllocations, DeadCode | impact report, `SentinelCallToolResult<T>`, `DocumentEditResult`, lists | `SymbolName`/`TotalCallSites`; `IsError==false`; `Outcome` and `record C`; `_unused` named in dead-code list | trivial |
| `BatteryThirtyOneTests.cs` ~L84-95 | InlineClass_SameFile_ReturnsDictionaryNotThrow | `Dictionary<FilePathWrapper,string>` | `ContainKey`; `Not.Contain "class HelperClass"` | trivial |
| `BlobIntegrityInvariantTests.cs` L73-85 | all 5 `*_IssuedChangeIdResolvesToABlobAsync` | `SentinelCallToolResult<T>` | assert `IsError==false`, or that a `ChangeId` was issued, before the early return | trivial |

Not weak: `BatteryThirtyThreeTests.cs` L~230-288 (6 tests) - the `Is.Not.Null` guards are redundant but the following `Any(...)` asserts are meaningful.

## NotFound-style outcomes that let failures slip through
1. **Engines return a not-found outcome and tests prove only "no exception".** `CodeStyleEngine.cs:385-428` returns `Outcome=DocumentNotFound`, Message `"// Document not found."` for a missing file. ~20 `*_NonExistentFile_ReturnsNullOrEmpty` tests in `BatteryTwentyOneTests.cs` (L~145-575) discard the result inside `Assert.DoesNotThrowAsync`, so they pass whether the engine reports not-found or silently does nothing.
2. **Inverted tests:** names say `ReturnsNull` / `Throws` but assert non-null on a never-null `DocumentEditResult` (list in false-pass 5). The names imply the contract is "throw" or "null"; the engine returns a not-found outcome, so names and engine disagree.
3. **Null `UpdatedText` accepted as the not-found signal:** `BatteryNineteenTests.cs` L139, AddValidationToPoco, ConvertPropertySafe (`Is.Null.Or.Empty`); `BatteryThirtyOneTests.cs` `ConvertMethodToIndexer_UnknownFile_ReturnsEmpty` (`Is.Null`). A real failure, empty result or no-op also satisfies these. Contrast: `GenerateAsyncOverload_NonExistentFile_Throws` uses `ThrowsAsync<ToolNotFoundException>`, so the Generation tool can already throw; the engine-vs-tool contract looks inconsistent (hypothesis from names and assertions only).
4. **Tool-level not-found returned as a human string** (`Tools.Advanced/GenerationTools.cs:78-240`: "file not found", "controller class ... not found") that satisfies `Is.Not.Null.And.Not.Empty` (`BatteryNineteenTests.cs` L111, L120, ~L310, ~L198 on). Whether these should return `ResultError` with `ErrorCode.NotFound` is open.
5. **`DecoratorResult?` null when the interface is not found** (`GenerateDecoratorClass_NonExistentInterface_Throws` asserts `Is.Null`). Acceptable as a contract, but the test name says `Throws` and null cannot distinguish "not found" from "generation failed".
6. **Not-found is the only refusal signal:** `BatteryTwentyFourTests.cs` `RenameSymbol_NonExistentSymbol` (L248, `ErrorData Is.Not.Null` only) and the ConvertToNullCoalescing/Switch/Pattern `NonExistentFile_Throws` variants (L1872, 1891, 1910). Strong templates already exist: `BatteryTwentyOneTests.cs` `InvertBooleanLogic_MissingFile` / `_UnknownBoolName` (`ErrorCode==NotFound`) and `BatteryTwentyThreeTests.cs` `ConvertLockToSemaphoreSlim_NonExistentFile_ReportsNotFoundCode`.
7. **`AssertChangeIdIsRedeemable` early return** (`BlobIntegrityInvariantTests.cs:73-85`): no `ChangeId` means all 5 tests pass vacuously.
8. **"Does not flag" tests pass when analysis found nothing** (`AsyncThreadingToolsTests.cs` L52, 60, 97, 116, 135, 165, 202; `AdvancedToolsTests.cs` ~L82): a missing file or no-op analysis also yields `Is.Empty`.
9. **Engines returning `Outcome=Modified` regardless of change** (verified only for `UseIndexFromEndAsync`): a "no match" is never reported as `AlreadyInTargetState` / `NoChange`, so the `X_ValidFile_ReturnsSource` tests cannot detect it. Candidate for the engine owner.

## Root cause
Not traced beyond the above. The common mechanism is that engines and tools return a never-null DTO whose failure signal lives in `Outcome` / `Message` / nullable `UpdatedText`, and the tests were written to the "does not throw, result exists" level. Only `CodeStyleEngine.UseIndexFromEndAsync`, the Generation tools and a few engines were read to confirm return shapes.

## Why it matters
~52% of this project's tests cannot detect a regression in the operation they name. Several would stay green if the tool refused every call or the engine did nothing. Weak models rely on these tools; a silent regression here is invisible until a model-eval fails.

## Recommendation
Ranked by leverage (slices run one at a time, build and baseline check after each):
1. Fix the false-passes listed first (zero-assert tests, text-already-in-input, vacuous early returns, inverted-name tests).
2. Valid-file smoke tests: assert `Outcome` plus the transformed text; trace each engine's real outcome first (expected values here were inferred from source, not run).
3. NonExistentFile tests: assert `Outcome==DocumentNotFound` and message, or `ThrowsAsync` if the engines move to throwing. Decide that question first to avoid rewriting these twice.
4. Detector tests: assert the planted finding by name/line; pair every "does not flag" test with a positive case.
5. Decision open: whether engines should throw typed exceptions for caller-precondition failures (see the throw-vs-NotFound discussion in the session) and whether `Outcome=Modified` should be returned on a no-op.

## Out of scope
Helper and fixture files; other test projects; running the tests to confirm which weak tests currently pass.
