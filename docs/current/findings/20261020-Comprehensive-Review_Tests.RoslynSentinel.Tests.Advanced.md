# Finding: RoslynSentinel.Tests.Advanced has ~110 weak-only tests, including genuine false-passes

**Status:** OPEN 2026-10-20. Audit complete; no fix decided, owner to triage the false-pass list first.

## Context

Read-only audit of the NUnit test project `RoslynSentinel.Tests.Advanced` for weak assertions: tests whose only
meaningful assertion is `Is.Null` / `Is.Not.Null`, `Is.Not.Empty`, `Count > 0`, `Assert.Pass`, or a "does not throw"
with no outcome check, on a value that is a rich DTO or result (engine result, `DocumentEditResult`, finding lists,
etc.). Such tests can pass when the operation failed.

Method: RoslynSentinel MCP tools only (`Search` mode text scoped to the project, `ReadFile`, `Search` mode symbol for
DTO members). Assertion lines were collected by text search and classified per test method; every category below was
then confirmed by reading the test source. All paths are relative to
`C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Advanced\` unless stated. No test was run.

Key DTO members inspected (for the "assert instead" columns):
- `DocumentEditResult` / `EngineResultBase` (`RoslynSentinel.Common`): `Outcome` (`EditOutcome`: Modified,
  TargetNotFound, DocumentNotFound, SourceInvalid, CannotOptimize, CannotEdit, CannotMove, CannotRemove, CannotConvert,
  FeatureDisabled, Error, AlreadyInTargetState, NoCandidatesFound), `IsError`, `Status`, `Code`, `Message`,
  `UpdatedText` (JsonIgnore), `Changes`, `Warnings`, `Candidates`. Factories `DocumentNotFound`, `TargetNotFound`,
  `FeatureDisabled` set `Outcome` and a `Message`.
- `ChangeSignatureResult(Changes, SkippedCallSites, Error)`; `FormatPreviewResult(Changed, TotalHunks, Hunks)` with
  `FormatHunk(StartLine, EndLine, ContextBefore, RemovedLines, AddedLines, ContextAfter)`.
- `CallerInfo(CallerMethod, CallerType, FilePath, Line, CodeSnippet)`; `ImplementationInfo(TypeName, FilePath, Line, Kind)`;
  `ReverseCallGraphNode(MethodName, ContainingType, FilePath, Line, Callers)`.
- Finding records used by detector tests expose `Pattern`, `Description`, `Reason`, `IssueType` (seen in existing
  passing assertions, e.g. `BugHuntSevenTests.cs:59-60`).

## What is broken

### 1. Project totals

- Test methods: 1,022 across 64 files (1,315 `[Test]`/`[TestCase]`/`[TestCaseSource]` attribute rows, so roughly 1,300
  executed cases; 905 `[Test]`, 405 `[TestCase]`, 5 `[TestCaseSource]` rows).
- Weak-pattern assertion lines: 350 across 300 tests (Is.Null, Is.Not.Null, Is.Not.Empty, Null.Or.Empty,
  GreaterThan(0), Assert.Pass). Split by pattern: Is.Not.Empty 109, Is.Not.Null 97, Not.Null.And.Not.Empty 67,
  Is.Null 42, GreaterThan(0) 19, Null.Or.Empty 14, Assert.Pass 2. (Is.Not.Empty includes multi-line continuation
  lines.)
- Is.Null / Is.Not.Null asserts overall: 220 across 196 tests.
  - Guarded (the same test also has a non-weak assertion): 153 (70%).
  - In weak-only tests: 64 (29%). The other 3 are in helper members.
  - "Guarded" is measured per test, not per value, and comes from text search; treat the figures as +/- 5%.
- Weak-only tests (every assertion is weak): 110 (about 10.8% of methods).
  Per file: BugFixTests 48, SentinelAccuracyTests 23, BugHuntTests 10, BugHuntSevenTests 6, StubImplementationTests 4,
  NewFeaturesTests 3, NewImplementationsTests 3, ApiGenerationEngineTests 2, Bug578RegressionTests 2,
  DependencyInjectionTests 2, FinalRegressionTests 2, CodeGenerationTests 1, ConvertToSwitchTests 1,
  MassiveIntelligenceTests 1, MassiveQualityTests 1, UnitTest1 1.
- Tests with weak lines AND other assertions (fine, guarded): 190. Largest: BugFixTests 55, ConvertToSwitchTests 19,
  SentinelAccuracyTests 17, NewFeaturesTests 16, AdvancedResultStatusTests 12, NewImplementationsTests 12,
  ConvertToNullCoalescingTests 9, ModernizationTests 9.
- `AdvancedResultStatusTests.cs` has 24 `Code`/`Message` `Is.Null` asserts. These are the success contract and sit
  beside Status/IsError asserts; not a defect.

### 2. Pattern categories (weak-only tests, approximate counts)

| Category | Count | Examples |
| --- | --- | --- |
| Detector returns `Is.Not.Empty` and nothing else (each source has a single trigger, so low risk) | ~48 | `SentinelAccuracyTests.cs:55`, `BugHuntSevenTests.cs:87`, `BugHuntTests.cs:112` |
| `UpdatedText` is `Is.Null` or `Is.Null.Or.Empty` with no Outcome/Message check | ~17 | `BugFixTests.cs:3552`, `StubImplementationTests.cs:145`, `ApiGenerationEngineTests.cs:127` |
| `Is.Not.Null` on a never-null record or list (tautology) | ~39 | `BugFixTests.cs:2858`, `BugFixTests.cs:1356`, `BugFixTests.cs:3568` |
| Null IS the contract (ignore) | ~7 | `BugFixTests.cs:3424`, `BugFixTests.cs:3448`, `BugFixTests.cs:3485`, `CodeGenerationTests.cs:403`, `DependencyInjectionTests.cs:67`, `DependencyInjectionTests.cs:83` |

The first three categories account for about 105 of the 110; the totals overlap slightly, so they sum to ~111.

### 3. Genuine false-passes (read these first)

1. `FinalRegressionTests.cs:162,182,206,220` -- `DisposalPattern_DocumentCanBeAnalyzed`,
   `StaticReadOnlyDictionary_CanBeIdentified`, `TaskRunPattern_IsDetectable`, `SimpleRefactoring_CompletesSuccessfully`.
   None calls an engine. Each parses the test's own source via `GetSyntaxRootAsync` and asserts the root or a node
   from the string just written. They cannot fail for any engine regression.
2. Placeholders that test nothing:
   - `UnitTest1.cs:13` is `Assert.Pass()`.
   - `BugFixTests.cs:2965` (`BUG_47_ExtractionDoesNotCrash`) is `Assert.That(true, "Extraction completed without
     exception")`; the `ExtractInterfaceAsync` result is discarded.
   - `DiscoveryEngineTests.cs:417-419` (`PreviewRenameImpact_DefinitionOnly_ReportsOneReference`) asserts
     `TotalReferences >= 0` and `FilesAffected >= 0`, always true; the name promises "one reference".
3. `BugFixTests.cs:2858,2867,2876,2885,2894,2903` (six `*DoesNotCrash` tests). Each ends in
   `Assert.That(result, Is.Not.Null)` on `FormatDocumentAsync`, which returns the non-nullable record
   `DocumentEditResult` (`BasicRefactoringEngine.cs:107`). They pass even when `Outcome` is Error. Same pattern at
   `BugFixTests.cs:2917,2927,2937` (`BUG_55/56/57_Generated*Compiles/Complete`, which never compile or inspect the
   generated code) and `:2949,2982` (`BUG_45_RefactoringProducesCorrectResult`, `BUG_50_RefactoringSemanticsPreserved`).
   11 tests.
4. `BugFixTests.cs:3552-3816`, the `*NullReturnRegressionTests` fixtures (about 20 tests):
   - The `*_Tool_*_ThrowsInvalidOperationException` and `*_Tool_*_DoesNotThrow` tests never call the tool. They repeat
     the engine call of the sibling test; `CreateTools()` is declared and unused (`:3466`-ish helpers, e.g.
     `CreateTools` in each fixture). `GenerateDecoratorClass_NonExistentInterface_ToolThrowsInvalidOperation` (`:3494`)
     is a byte-for-byte copy of `:3485`.
   - `UpdatedText Is.Null.Or.Empty` (`:3552,3559,3610,3617,3668,3675,3682,3689,3720,3727,3809,3816`) also passes on a
     successful empty edit.
   - The `*_ReturnsOriginalContent` / `*_ReturnsContent` tests (`:3568,3577,3626,3635,3736,3745,3787,3799`) assert
     only `result Is.Not.Null` on a record that is never null. The name promises original content and nothing checks it.
     `:3799` asserts `Is.Not.Null` on a `SentinelCallToolResult<object>` envelope, also never null.
5. Navigation tests, `BugFixTests.cs:1333,1356,1394,1746,1764,1372,1795`:
   - `Is.Not.Null` on `List<CallerInfo>` (`FindCallersAsync` returns `Task<List<CallerInfo>>`,
     `SymbolNavigationEngine.cs:1479`) can never fail; the comment at `:1355` says "The call from Consumer.Run should
     be found" but that is not asserted.
   - `:1372,1795` assert only `Is.Not.Empty` on `List<ImplementationInfo>`.
   - `:1333` asserts `ReverseCallGraphNode? Is.Not.Null` only.
6. "Either outcome is fine" tests:
   - `BugFixTests.cs:518-520` (`REG_InterpolateStringSafe_ConstFormatString_CorrectlyInterpolates`): comment "Either
     should contain interpolated result or return the code as-is"; asserts `UpdatedText` non-empty and `Length > 0`.
   - `BugFixTests.cs:2453-2454` (`BUG_71`): "error or success", `Is.Not.Null` and `Length > 0`.
   - `BugFixTests.cs:2567-2568` (`BUG_52_ReduceBlockDepth`): `Not.Empty` plus `Not.Contain("// ErrorDetails")` only.
   - `BugFixTests.cs:3273-3290` (`BUG_72_IntroduceField_WithLocalParameter_NoInitializer`): try/catch where both the
     success branch and a caught `ToolException` pass.
   - `ConvertToSwitchTests.cs:614` (`BooleanLiterals_ConvertsBoolCases`): only `Is.Not.Null.And.Not.Empty`, comment
     "May or may not convert".
   - `ConvertToSwitchTests.cs:415` (`ComplexCondition_DoesNotConvert`): an OR-chain that is true for the unmodified input.
   - `SentinelAccuracyTests.cs:1884` (`DoesNotFlag_InnocentComment`): `issues Is.Not.Null` on a list.
   - `NewImplementationsTests.cs:877,1018`: "DoesNotChange" and "ReturnsOriginal" tests that assert only non-empty text.
     The sibling at `:889` asserts `Outcome == TargetNotFound` and is the pattern to copy.
   - `NewImplementationsTests.cs:1006`: `Does.Contain("readonly").Or.Contain("_answer")` is true because `_answer` was
     asserted on the previous line.
7. Likely false-passes (HYPOTHESIS: assumes `UpdatedText` is the whole file, as sibling tests such as
   `CodeGenerationTests` imply):
   - `BugFixTests.cs:570-571` and `:593-594`: `Contain("Name")`, `Contain("Count")`, `Contain("Method2")`,
     `Contain("Method3")`, all of which the interface declaration in the input already contains.
   - `BugFixTests.cs:661-664` (`REG_ChangeSignature_UpdatesCallSiteArguments`): `Contain("Process")` is true of the
     unmodified source. Assert the reordered call `Process(true, "John", 30)`.
8. `Assert.Inconclusive("Document not found")` guards at `BugFixTests.cs:2446,2563,2614,2671,2715,2740,2784,2808`. A
   setup problem turns these tests into silent inconclusive results instead of failures.
9. Error text checked with an OR-chain (`Does.Contain("//").Or.Contain("Error").Or.Contain("not found")`) at
   `BugFixTests.cs:1995,2013,2046` and `NewImplementationsTests.cs:426`. "//" or "Error" match many unrelated messages.

### 4. Offenders table

Effort is trivial unless stated.

| Location | Tests | DTO | Assert instead | Effort |
| --- | --- | --- | --- | --- |
| `BugFixTests.cs:3552-3816` | ~20 NullReturn tests | `DocumentEditResult` | `Outcome` (`DocumentNotFound` / `TargetNotFound`), `IsError`, `Code`, `Message` ("File not found..."), `UpdatedText`/`Changes`; call the tool for the `Tool_` tests | trivial to moderate |
| `BugFixTests.cs:2858-2949,2982` | 11 DoesNotCrash/Generated tests | `DocumentEditResult` | `Outcome == Modified` (or the intended outcome), `UpdatedText` content | trivial |
| `BugFixTests.cs:1333-1394,1746-1795` | 7 navigation tests | `List<CallerInfo>`, `List<ImplementationInfo>`, `ReverseCallGraphNode` | `CallerMethod`/`CallerType`/`Line` (e.g. "Run", "Make"); `TypeName == "Foo"`, `Kind`; `MethodName == "ValidateAsync"`, `Callers` containing "Handle" | trivial |
| `BugFixTests.cs:518-520,634-635,772,800,381-383` | 5 tests | `DocumentEditResult`, `FormatPreviewResult`, `List<DuplicateMethodGroup>` | text contains `$"` and `{value}`; `Changed`, `TotalHunks`, `Hunks[0].AddedLines`/`RemovedLines`; call-tree text names `Add`/`Multiply`; duplicate group contents (members not inspected); `BranchesToTest` count per overload | trivial to moderate |
| `BugFixTests.cs:2453,2567,2719,2788,2812,3273` | 6 tests | `DocumentEditResult` | `Outcome == Modified` plus expected text | trivial |
| `BugFixTests.cs:276-278` | `ChangeSignature_WithSkippedCallSite_RefusesWithError` | `ChangeSignatureResult` | `Error` text, `SkippedCallSites[0]` | trivial |
| `BugFixTests.cs:244-247,255-258` | `ChangeSignature_CallSiteWithNamedArgument_...` | `ChangeSignatureResult` | content of `Caller.cs` (reordered named-arg call), not only the file key | moderate |
| `FinalRegressionTests.cs:162-220` | 4 tautologies | none | rewrite to call an engine, or delete | moderate |
| `DiscoveryEngineTests.cs:417-419`, `UnitTest1.cs:13`, `BugFixTests.cs:2965` | 3 | rename-impact preview, none | exact `TotalReferences`/`FilesAffected`; delete the placeholders | trivial |
| `SentinelAccuracyTests.cs` | 23 `Flags_*` tests: lines 55,104,116,170,182,214,228,253,460,681,713,2448,2895,2923,3065,3100,3201,3231,3279,3461,3620,3675 (+1 `Flags_ThreeWayCycle`) | finding lists (`Reason`/`Pattern`/`Description`) | `Has.Count.EqualTo(1)` plus `[0].Reason` or `Pattern` containing the method name; 17 sibling tests in this file already do (e.g. `:39-40`) | trivial |
| `BugHuntTests.cs` | 10 (`:112,215,281,353,387,416,467,500,529,561`) | finding lists (`Reason`) | the specific finding, e.g. `Reason` containing ".Result accessed" | trivial |
| `BugHuntSevenTests.cs` | 6 (`:87,109,382,635,652,668`) | anti-pattern findings (`Pattern`, `Description`) | `[0].Pattern`, `Description` containing the member; `:59-60` already does this | trivial |
| `StubImplementationTests.cs:145,318,405,438` | 4 | `DocumentEditResult` | `Outcome == DocumentNotFound`; for `:405` the text without `{ DoWork(); }` | trivial |
| `ApiGenerationEngineTests.cs:127,138` | 2 | `DocumentEditResult` | `Outcome` (`DocumentNotFound` / `TargetNotFound`) | trivial |
| `NewFeaturesTests.cs:158,190,343` | 3 | `DuplicateBlockGroup` | group count and occurrence method names; `SnippetPreview` content | trivial |
| `NewImplementationsTests.cs:381,877,1018` | 3 | finding list, `DocumentEditResult` | `Outcome` (`AlreadyInTargetState` / `TargetNotFound`), unchanged text | trivial |
| `ConvertToSwitchTests.cs:614`, `Bug578RegressionTests.cs:99,125` | 3 | `DocumentEditResult`, findings | `Outcome` and text; the contents of `blockingFindings` | trivial |
| `MassiveIntelligenceTests.cs:115-116`, `MassiveQualityTests.cs:42` | 2 (second is 5 TestCases) | metrics, findings | `TotalTypes == 1`, `TotalMethods == 1`; `Count == 1` and the path value | trivial |
| `DependencyInjectionTests.cs:67,70,83` | 2 | tool classes | Null is the resolve contract; low value, leave | n/a |

About 5 remaining weak-only tests are contract-null or low value and are not listed.

### 5. NotFound-style outcomes that let failures slip through

For the owner to judge whether the engine should throw (or return a typed error) instead of returning a quiet
not-found value. Each row cites the test that tolerates the quiet value. Whether each engine really returns the
quiet value for the stated reason is from the test comments and assertions unless marked verified.

| Site | Quiet result the test accepts | What can slip through |
| --- | --- | --- |
| `BugFixTests.cs:3552-3816` (file not in workspace, 12 engine calls: AddGuardClauses, AddBenchmarkStub, AddBraces, UpgradePatternMatching, MakeClassImmutable, ConvertExpressionBody) | `UpdatedText` null or empty. Comments say "tool layer converts to exception". | The tests pin the engine's quiet path; the tool-layer conversion is not tested at all (the `Tool_` tests do not call the tool). A change that returns a success outcome with empty text passes. |
| `BugFixTests.cs:3568,3626,3736` (file exists, method/class missing) | Non-null `DocumentEditResult` ("returns original content"). Comments: "no changes needed is not the same as file not found". | Class/method-not-found is treated as a no-op, not as `TargetNotFound`. The distinction between "nothing to do" and "target missing" is unasserted. |
| `BugFixTests.cs:3787,3799` (`SyncInterfaceToImplementationAsync`, nonexistent file) | Engine "locates the interface in the solution and returns non-empty content (no-op sync rather than a hard error)", per the comment at `:3783-3785`. | A call with a nonexistent file and nonexistent class succeeds quietly; the test enshrines that as correct. Owner decision: should this be `DocumentNotFound`/`TargetNotFound`. |
| `BugFixTests.cs:3424,3448,3485,3494` | `GetCallGraphAsync`, `GetReverseCallGraphAsync`, `GenerateDecoratorClassAsync` return `null` for an unknown method/interface. | Null is the documented engine contract here ("tool layer converts to exception"); the conversion is untested, and `:3494` is a duplicate that never exercises the tool. |
| `ApiGenerationEngineTests.cs:127,138`; `StubImplementationTests.cs:145,318,438` | `UpdatedText` null for an unknown file or controller. | `Outcome` is not asserted, so `DocumentNotFound`, `TargetNotFound` and `Error` are indistinguishable. |
| `BugFixTests.cs:1995,2013,2046`; `NewImplementationsTests.cs:426`; `CodeGenerationTests.cs:494` | Failure signalled in `Message` text (`"//"`, `"Error"`, `"not found"`, `"ErrorDetails:"`) rather than `Outcome`/`IsError`. | An unrelated message containing "//" or "Error" satisfies the assertion; the structured outcome is never checked. |
| `BugFixTests.cs:2567-2568`, `:2453-2454` | `Does.Not.Contain("// ErrorDetails")` implies errors can be embedded in `UpdatedText` as comment text. | An error delivered as text inside `UpdatedText` is accepted by `Not.Empty` and `Length > 0`. |
| `BugFixTests.cs:3273-3290` | `ToolException` for an ambiguous or unresolvable context is caught and accepted. | Success and "snippet not found" both pass, so the bug being regression-tested cannot be detected. |
| `BugFixTests.cs:1356,1394,1746,1764` | `FindCallersAsync` returns a `List<CallerInfo>`; an empty list passes `Is.Not.Null`. | HYPOTHESIS (not traced): an unresolved symbol or a symbol with no callers returns an empty list, indistinguishable from "resolved, found nothing". |
| `Is.Empty`-only negative detector tests (e.g. `BugFixTests.cs:1655-1656`, `BugHuntSevenTests.cs` `DetectMissingNullChecks_With*_IsNotFlagged`, `FindUnsafeTypeCasts_*_ShouldNotBeFlagged`, `SentinelAccuracyTests.cs:2077`) | Empty findings list. | HYPOTHESIS: if a detector returns an empty list when the file or method cannot be resolved, these pass vacuously. The inputs use existing files, so no concrete failure was seen; not traced. |
| `BugFixTests.cs:1674,1942`; `NewImplementationsTests.cs:889` | `UpdatedText` null for no-op / not-found. | `NewImplementationsTests.cs:889` is the good example: it asserts `Outcome == EditOutcome.TargetNotFound`. `:1674` and `:1942` pair null text with a `Message` check only. |
| `ConvertToSwitchTests.cs:381-485`, `ModernizationTests.cs:311,363`, `ConvertToNullCoalescingTests.cs:203` ("DoesNotConvert" / "ReturnsUnchanged") | Non-empty `UpdatedText` equal to or resembling the input. | `Outcome` (`AlreadyInTargetState` / `TargetNotFound`) is not asserted, so "declined to convert" and "failed" look the same. |

## Root cause

Not traced to a single source defect; this is a test-suite pattern. Two environment observations:
- Engines return a rich `DocumentEditResult` (with `Outcome`, `IsError`, `Code`) for failures, but older regression
  tests were written against earlier APIs that returned strings or null, and kept string/null-shaped assertions
  (comments such as "Engine returns file content (not empty)" and "MoveFileToNamespaceFolderAsync became
  PreviewMoveFileToNamespaceFolderAsync, which returns a DocumentEditResult" at `BugFixTests.cs:2413-2416`).
  The assertions were never upgraded to `Outcome`.
- The `*NullReturnRegression` fixtures document a tool-layer exception conversion but test only the engine side.
  Whether the tool layer still converts these to exceptions was not checked.

## Why it matters

These tests are labelled regression guards for specific bug numbers (BUG-45..78, Bug 3/4/9x). Several would stay green
if the guarded bug returned or the engine returned an Error outcome, so the guard gives false assurance. The
`Tool_`-named duplicates add test count without covering the tool. A weak model or reviewer reading the suite would
infer coverage of the tool-layer exception behaviour that does not exist.

## Recommendation

Ranked by leverage; none is decided.
1. Fix the genuine false-pass list (section 3, items 1-5) first: they are trivial to moderate and remove tests that
   cannot fail. Delete `UnitTest1.cs` and the four `FinalRegressionTests.cs` tautologies or make them call an engine.
2. Upgrade `DocumentEditResult` tests to assert `Outcome` (and `IsError`/`Message` for failures) instead of
   null/empty `UpdatedText`. Use `NewImplementationsTests.cs:889` as the template.
3. Make the `Tool_*` tests actually invoke the tool class, or rename and merge them with the engine tests.
4. Replace `Is.Not.Empty`-only detector tests with `Has.Count.EqualTo(1)` plus a `Pattern`/`Reason` check, copying the
   17 sibling tests in `SentinelAccuracyTests.cs` that already do this.
5. Open for the owner: whether engines should throw instead of returning quiet not-found values (section 5), most
   notably `SyncInterfaceToImplementationAsync` on a nonexistent file (`BugFixTests.cs:3787`) and class/method-not-found
   treated as a no-op (`:3568,3626,3736`).

## Out of scope

- Contract-null tests where null or non-null is the contract (`BugFixTests.cs:3424,3448,3485`, `CodeGenerationTests.cs:403`,
  `DependencyInjectionTests.cs:67,83`, `SolutionWideFunctionalTests.cs:66`, `GenericContainerNameTests.cs:395`,
  `BugFixTests.cs:2346`).
- The 24 success-contract `Code`/`Message` `Is.Null` asserts in `AdvancedResultStatusTests.cs`.
- Characterization/ByteExact tests (`*CharacterizationTests.cs`), which assert full-text equality, and
  `MoveTypeToOuterScopeFormatCharacterizationTests.cs`, which asserts `Outcome` before text. Its
  `Assert.Pass("LF case: ...")` at `:242` is a deliberate skip inside a TestCaseSource and was left alone.
- Other test projects.

## Not checked

- Test cases beyond what text search of assertion lines exposed; helper assertions such as `AssertByteExact` were not
  traced.
- `FlagMigrationCandidate_ByteExact` and `ModernizeExceptions_ByteExact` (no assertion lines found; probably helper
  based) and `BatchResults_ComputedStatusMembers_AreJsonIgnored`.
- Whether `UpdatedText` is the full file for every engine (hypothesis in section 3, item 7).
- `Does.Contain` on content already present in the input beyond the examples given; a wider category that needs
  per-test review.
- DTO members of `DuplicateMethodGroup` and `DuplicateBlockGroup` were not read; the corresponding "assert instead"
  entries are approximate.
- Section 5 rows marked HYPOTHESIS were not traced into the engines; which engines return `DocumentNotFound` versus
  null/empty `UpdatedText` was inferred from tests and comments, not from engine source.
- No tests were run, so no test was confirmed to pass or fail; "false-pass" means the assertion would not detect the
  described failure.
- The shell dogfood hook blocked two earlier PowerShell commands that only mentioned `Get-Content` together with a
  `.cs` literal while processing JSON and scratchpad data (hook false positive, not rephrased).