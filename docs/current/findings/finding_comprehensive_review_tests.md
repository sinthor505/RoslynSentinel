# Finding: comprehensive review of test-project assertions (audit of 2026-10-20)

**Status:** OPEN 2026-10-20. Nine per-project assertion audits merged into one doc; no fix decided, the owner triages which tests to strengthen.

**Merged from:** 20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Advanced, 20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Asyncify, 20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Basic, 20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Battery.Advanced, 20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Battery.Basic, 20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests, 20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Server, 20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Tools.Advanced, 20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Tools.Basic

One section per test project; each keeps the original audit text under its own heading.

---

## RoslynSentinel.Tests.Advanced

**RoslynSentinel.Tests.Advanced has ~110 weak-only tests, including genuine false-passes**

**Original status:** OPEN 2026-10-20. Audit complete; no fix decided, owner to triage the false-pass list first.

### Context

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

### What is broken

#### 1. Project totals

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

#### 2. Pattern categories (weak-only tests, approximate counts)

| Category | Count | Examples |
| --- | --- | --- |
| Detector returns `Is.Not.Empty` and nothing else (each source has a single trigger, so low risk) | ~48 | `SentinelAccuracyTests.cs:55`, `BugHuntSevenTests.cs:87`, `BugHuntTests.cs:112` |
| `UpdatedText` is `Is.Null` or `Is.Null.Or.Empty` with no Outcome/Message check | ~17 | `BugFixTests.cs:3552`, `StubImplementationTests.cs:145`, `ApiGenerationEngineTests.cs:127` |
| `Is.Not.Null` on a never-null record or list (tautology) | ~39 | `BugFixTests.cs:2858`, `BugFixTests.cs:1356`, `BugFixTests.cs:3568` |
| Null IS the contract (ignore) | ~7 | `BugFixTests.cs:3424`, `BugFixTests.cs:3448`, `BugFixTests.cs:3485`, `CodeGenerationTests.cs:403`, `DependencyInjectionTests.cs:67`, `DependencyInjectionTests.cs:83` |

The first three categories account for about 105 of the 110; the totals overlap slightly, so they sum to ~111.

#### 3. Genuine false-passes (read these first)

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

#### 4. Offenders table

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

#### 5. NotFound-style outcomes that let failures slip through

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

### Root cause

Not traced to a single source defect; this is a test-suite pattern. Two environment observations:
- Engines return a rich `DocumentEditResult` (with `Outcome`, `IsError`, `Code`) for failures, but older regression
  tests were written against earlier APIs that returned strings or null, and kept string/null-shaped assertions
  (comments such as "Engine returns file content (not empty)" and "MoveFileToNamespaceFolderAsync became
  PreviewMoveFileToNamespaceFolderAsync, which returns a DocumentEditResult" at `BugFixTests.cs:2413-2416`).
  The assertions were never upgraded to `Outcome`.
- The `*NullReturnRegression` fixtures document a tool-layer exception conversion but test only the engine side.
  Whether the tool layer still converts these to exceptions was not checked.

### Why it matters

These tests are labelled regression guards for specific bug numbers (BUG-45..78, Bug 3/4/9x). Several would stay green
if the guarded bug returned or the engine returned an Error outcome, so the guard gives false assurance. The
`Tool_`-named duplicates add test count without covering the tool. A weak model or reviewer reading the suite would
infer coverage of the tool-layer exception behaviour that does not exist.

### Recommendation

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

### Out of scope

- Contract-null tests where null or non-null is the contract (`BugFixTests.cs:3424,3448,3485`, `CodeGenerationTests.cs:403`,
  `DependencyInjectionTests.cs:67,83`, `SolutionWideFunctionalTests.cs:66`, `GenericContainerNameTests.cs:395`,
  `BugFixTests.cs:2346`).
- The 24 success-contract `Code`/`Message` `Is.Null` asserts in `AdvancedResultStatusTests.cs`.
- Characterization/ByteExact tests (`*CharacterizationTests.cs`), which assert full-text equality, and
  `MoveTypeToOuterScopeFormatCharacterizationTests.cs`, which asserts `Outcome` before text. Its
  `Assert.Pass("LF case: ...")` at `:242` is a deliberate skip inside a TestCaseSource and was left alone.
- Other test projects.

### Not checked

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

---

## RoslynSentinel.Tests.Asyncify

**RoslynSentinel.Tests.Asyncify is mostly clean, but 4 tests assert almost nothing and 4 more can pass when the operation did nothing**

**Original status:** OPEN 2026-10-20. Audit only; no fix decided and nothing changed in the test project.

### Context
Read-only weak-assertion audit of `RoslynSentinel.Tests.Asyncify` (6 files, all read in full) on 2026-10-20.
Goal: find tests whose only meaningful assertion is `Is.Null` / `Is.Not.Null`, `Is.Not.Empty`,
`GreaterThan(0)`, `Assert.Pass` or "does not throw" on a rich DTO (engine result, tool result,
`DocumentEditResult`, `BatchResultSummary`, `AsyncMigrationProgressReport`, ...), which can false-pass.
Method: MCP `ReadFile` / `Search` / `GetLargeResult` on every `.cs` file, plus DTO inspection of
`BatchResultSummary` (`RoslynSentinel.Common/BatchTypes.cs:66`), `AsyncMigrationProgressReport`
(`RoslynSentinel.Common/AsyncMigrationProgressReport.cs:14`) and `DocumentEditResult`
(`RoslynSentinel.Common/DocumentEditResult.cs:22`). No test was run. All paths below are relative to
`RoslynSentinel.Tests.Asyncify/` unless stated.

### What is broken

#### 1. Totals
- Tests: 104 `[Test]` methods, 113 cases (with `[TestCase]` rows).
  - `ConvertToAsyncBridgeTests.cs`: 26 methods / 30 cases
  - `SentinelAsyncifyToolsTests.cs` (class `AsyncifyToolsTests`): 21 / 23
  - `MigrationScanResultTests.cs`: 20 / 20
  - `FlagMigrationCandidateTests.cs`: 17 / 19
  - `OperationOutcomeRoutingTests.cs`: 11 / 12
  - `AsyncBatchEngineFlagOutcomeTests.cs`: 9 / 9
- Weak-only tests: 4 clear (T6, T11, T13, T14) plus 4 with a real false-pass hole (section 2). Counting
  tautological or vacuous tests as well, about 11 of 104.
- `Is.Null` / `Is.Not.Null` asserts overall: 73 (counting `Is.Not.Null.And.Not.Empty`):
  `FlagMigrationCandidateTests` 2, `MigrationScanResultTests` 42, `OperationOutcomeRoutingTests` 5,
  `SentinelAsyncifyToolsTests` 24, others 0.
- Guarded by a follow-up assert on the same value: about 69 of 73 (94%). The 4 that are not:
  `MigrationScanResultTests.cs:610`, `MigrationScanResultTests.cs:632`, `SentinelAsyncifyToolsTests.cs:258`,
  `OperationOutcomeRoutingTests.cs:169`.
- Not counted in the 73: `GreaterThanOrEqualTo(0)` x2, and `GreaterThan(0)` at `FlagMigrationCandidateTests.cs:372`
  and `MigrationScanResultTests.cs:344` (both are secondary asserts after strong ones).

#### 2. Possible false-passes (highest priority)
1. `OperationOutcomeRoutingTests.cs:253` (`Directive_DoesNotClaimCompletion_WhenOutcomeIsPartialOrNoProgress`, 2 cases).
   The assert is `Does.Not.Contain("successfully").IgnoreCase.Or.Contain("complete").IgnoreCase`. It is an OR:
   it passes whenever the text lacks "successfully", and also for any text containing "complete" (e.g.
   "could not be completed"). The test also passes its own `BuildTestDirective(...)` string into
   `OperationSummary.FromCounts` and reads it back (`:237-262`), so it never exercises production directive
   logic. Fix direction: `.And`, and a directive produced by real engine code.
2. `ConvertToAsyncBridgeTests.cs:253-258` (`ConvertToAsyncBridge_AsyncMethodInsertedAfterOriginal`).
   `bridgeIdx` and `asyncBodyIdx` are `IndexOf` results used unchecked in `Is.LessThan(asyncBodyIdx)`. If the
   conversion does nothing, `"GetAwaiter"` is absent (-1) while `"DataHelper.Search"` is present, and
   `-1 < n` passes. Fix direction: assert both indexes are >= 0.
3. `FlagMigrationCandidateTests.cs:219` (`FlagMigrationCandidate_ZeroScore_OmitsScoreArg`). The only content
   assert is `Does.Not.Contain("Score")`. The input source contains no "Score" and the attribute class is
   injected into a separate file, so an unchanged target file, or a flag that added no attribute, passes.
   Needs a positive `Does.Contain("[MigrationCandidate(\"AsyncBridge\"")`.
4. `OperationOutcomeRoutingTests.cs:144-171` (`Router_NoAsyncEquivalent_ReturnsNullHint_ItemStillInActionable`).
   After the real `hint == null` check (`:157`), it builds an `ItemFailure` from the null `hint` and asserts
   `SuggestedTool == null` (`:169`) and `Outcome == Failed`, values it just assigned. It never tests that the
   item is in `Actionable`, despite the name.
5. `OperationOutcomeRoutingTests.cs:81-103` (`AlreadySatisfied_AbsentFromActionable_AndOutcomeIsCompletedWithNoOps`).
   `alreadySatisfiedItem` (`:83`) is built and never used; `FromCounts` is given
   `Array.Empty<ItemFailure>()` and `Actionable Is.Empty` (`:102`) is asserted. The `Outcome` and count asserts do
   test `FromCounts`; the "absent from Actionable" claim does not.
6. `SentinelAsyncifyToolsTests.cs:369` (T11) accepts null or empty `Failures`; `:259` (T6) and
   `MigrationScanResultTests.cs:611` (T13) assert `TotalAsyncMethods >= 0`, which any outcome satisfies.

#### 3. Pattern categories (counts)
| Pattern | Count | Verdict | Examples |
| --- | --- | --- | --- |
| Guard `Is.Not.Null` on `SuccessData` / `ErrorData` / list, followed by member asserts | ~47 | fine | `SentinelAsyncifyToolsTests.cs:154`, `MigrationScanResultTests.cs:460` |
| `ErrorData Is.Not.Null` before `ErrorCode == ...` | 9 (inside the ~47) | fine | `SentinelAsyncifyToolsTests.cs:154,235,274,449,463`; `MigrationScanResultTests.cs:460,475,497,726` |
| Vacuous `Is.Not.Null` on a locally built `Wrap<T>()` envelope (never null) | 10 | dead line, strong asserts follow | `MigrationScanResultTests.cs:234,282,311,337,458,527,564,689,792,844` |
| Contract nulls (`LargeResult` null when inline, `TopCandidates` null without topN, `ErrorData` null on success, router `hint == null`) | ~9 | fine | `MigrationScanResultTests.cs:285,313,566,806`; `OperationOutcomeRoutingTests.cs:190` |
| Tautological thresholds | 6 | weak | `SentinelAsyncifyToolsTests.cs:194,203,259,369`; `MigrationScanResultTests.cs:537,611` |
| Negative-only or unchecked-index asserts | 3 | false-pass risk | `FlagMigrationCandidateTests.cs:219`; `ConvertToAsyncBridgeTests.cs:257`; `OperationOutcomeRoutingTests.cs:253` |
| Echo / tautology (asserts what the test assigned) | 2 tests | false-pass risk | `OperationOutcomeRoutingTests.cs:81-103`, `:144-171` |

#### 4. Offenders table
| file:line | test | DTO | what to assert | effort |
| --- | --- | --- | --- | --- |
| `SentinelAsyncifyToolsTests.cs:243-260` | `T6_GetAsyncMigrationProgress_WithAsyncMethods_ReturnsReport` | `AsyncMigrationProgressReport` | `TotalAsyncMethods == 2`, `WithCancellationToken == 1`, `WithoutCancellationToken == 1`, `CancellationTokenPct == 50` (source has `DoWorkAsync()` and `GetValueAsync(ct)`) | trivial |
| `MigrationScanResultTests.cs:595-612` | `T13_GetAsyncMigrationProgress_AfterLoadSolution_NoException` | `AsyncMigrationProgressReport` | same source and values as T6; replace `TotalAsyncMethods >= 0` (`:611`) | trivial |
| `MigrationScanResultTests.cs:619-633` | `T14_GetAsyncMigrationProgress_ProjectNameScoped_NoException` | `AsyncMigrationProgressReport` | only `IsError` false, `ErrorData` null, `SuccessData` not null; assert `TotalAsyncMethods == 1`, `WithoutCancellationToken == 1`; add a nonexistent `projectName` case so scoping is tested | trivial |
| `SentinelAsyncifyToolsTests.cs:351-370` | `T11_AddCancellationToken_DryRun_ReturnsWithoutError` | `BatchResultSummary` | `Attempted == 1`, `Succeeded == 1`, `Failed == 0`, `Skipped == 0` (a skipped target currently passes via `Failures Is.Empty.Or.Null`) | trivial |
| `MigrationScanResultTests.cs:527-545` | `T10_Summarize_TopN_MinScore_PopulatesTopCandidates` | `MigrationScanSummary.TopCandidates` | `Count == 4`, scores equal `[100, 90, 80, 70]`; `Count <= 5` (`:537`), `.All(...)` and the sort check all pass on an empty list | trivial |
| `MigrationScanResultTests.cs:689-706` | `T16_ScanAsyncMigrationCandidates_AllReasonTokensStructured` | `MigrationCandidateFinding` | `Count == 3` and that findings A and B were token-checked; the `foreach` passes on an empty list or blank `Reason` | trivial |
| `MigrationScanResultTests.cs:844-856` | `T21_Paged_MinScore_AllRecordsAboveThreshold_TotalRecordsFiltered` | `List<MigrationCandidateFinding>` | `SuccessData.Count == 3`, names `MethodD`, `MethodE`, `MethodF`; `.All(Score >= 85)` is vacuous on an empty page | trivial |
| `SentinelAsyncifyToolsTests.cs:184-204` | `T3_ScanAsyncMigrationCandidates_Summarize_ReturnsMigrationScanSummary` | `MigrationScanSummary` | `TotalCandidates == 1`, `ByClass.Count == 1`, `ByClass[0].ClassName == "RegionForm"` instead of `>= 1` (`:194`, `:203`) | trivial |
| `ConvertToAsyncBridgeTests.cs:196-214` | `ConvertToAsyncBridge_ExpressionBodiedMethod_AsyncOverloadGetsBlockBody` | `DocumentEditResult.UpdatedText` | name and comment promise a block body; only `GetTripsAsync` and `DataHelper.Search` (already in the input) are checked; assert the async overload has `{` | moderate |
| `ConvertToAsyncBridgeTests.cs:151-174` | `ConvertToAsyncBridge_VoidMethod_BridgeUsesExpressionStatement` | `DocumentEditResult.UpdatedText` | comment says "no return keyword" but it is never asserted; the `Task` check is `Contain \| Contain \| Contain("async Task")`, very loose | moderate |
| `FlagMigrationCandidateTests.cs:105-125` | `FlagMigrationCandidate_AttributeClassAbsent_NamespaceMatchesTargetFile` | `DocumentEditResult.Changes` | only `Does.Not.Contain("namespace Avaal.Service")`; add `Does.Contain("class MigrationCandidateAttribute")` | trivial |
| `FlagMigrationCandidateTests.cs:~205-220` | `FlagMigrationCandidate_ZeroScore_OmitsScoreArg` | `DocumentEditResult.Changes` | see false-pass 3 | trivial |
| `ConvertToAsyncBridgeTests.cs:237-259` | `ConvertToAsyncBridge_AsyncMethodInsertedAfterOriginal` | `DocumentEditResult.UpdatedText` | see false-pass 2 | trivial |
| `OperationOutcomeRoutingTests.cs:237-262` | `Directive_DoesNotClaimCompletion_...` | `OperationSummary.Directive` | see false-pass 1 | moderate |
| `OperationOutcomeRoutingTests.cs:81-103`, `:144-171` | `AlreadySatisfied_AbsentFromActionable_...`, `Router_NoAsyncEquivalent_...` | `OperationSummary`, `ToolHint`, `ItemFailure` | see false-passes 4 and 5 | moderate |

The 13 rows above cover every finding; no per-file overflow summary is needed.
`ConvertToAsyncBridgeTests` asserts only `UpdatedText`. Failures throw, so none of its `Does.Contain` asserts
needs an `Outcome` check; `Outcome == EditOutcome.Modified` would be optional hardening only.

#### 5. Overall verdict
Mostly clean. Strong: `AsyncBatchEngineFlagOutcomeTests` (every test pins message text, counts, disk state or
the logged warning), the byte-exact tests (`ConvertToAsyncBridge_ByteExact`, `T21_UpliftCallers_ByteExact`,
`FlagCandidatesInProject_ByteExact`), the `Throws` tests, and `FindMigrationCandidates_*`. Weak: the
progress-report tests (T6, T13, T14), T11, and the operation-outcome directive and routing tests.

#### 6. NotFound-style outcomes that let failures slip through
Candidates for the owner to judge whether the engine should throw (or the test should assert more). Items marked
"hypothesis" were not traced into engine source.

1. Same exception type for different causes (hypothesis on impact). `ConvertToAsyncBridgeTests.cs:376-528`
   (9 `*_Throws` tests) and `FlagMigrationCandidateTests.cs:~378-398` assert only
   `Assert.ThrowsAsync<InvalidOperationException>`. `ConvertToAsyncBridge_FileNotFound_Throws` (`:520`) and
   `..._MethodNotFound_Throws` (`:506`) show that file-not-found and method-not-found throw the same type as the
   precondition failures (already async, abstract, ref/out, event handler, overload exists). The second argument
   to `ThrowsAsync` is only a failure message, not a message check. If a fixture path were wrong, every `*_Throws`
   test would pass for the wrong reason (file not found). A distinct exception type or an `ErrorCode`, plus a
   message assert, would close this.
2. `Wrap<T>` swallows type mismatches. `MigrationScanResultTests.cs:170` sets `SuccessData = raw.SuccessData as T`.
   A wrong-typed payload becomes null, so `Assert.That(result.SuccessData, Is.Null, ...)` at `:345` (T4: "null when
   LargeResult is set") passes even if the engine returned a populated payload of another type. The other `as`
   casts (`SentinelAsyncifyToolsTests.cs:170,192`) are followed by `Is.Not.Null`, so they fail safely.
3. Batch tools return a success envelope when every item failed. `SentinelAsyncifyToolsTests.cs:396-420` (T13) shows
   `!result.IsError` true while `Failed == 1` (missing `ContextSnippet` is a failed item, not an error). Any caller
   or test that checks only `IsError` (T8/T10/T12/T14 check `Summary.Attempted` too) would miss it. Owner decision:
   keep failed-items-in-success-envelope, or set `IsError` / `Severity` when `Succeeded == 0 && Failed > 0`.
4. Skipped or no-match looks like success. `SentinelAsyncifyToolsTests.cs:351-370` (T11): `Failures Is.Empty.Or.Null`
   means a dry run that skipped the target (no match, no methods) passes. The target is `new BatchTarget { FilePath = ... }`
   with no `MethodNames`; what the engine does with that is a hypothesis. `AsyncBatchEngineFlagOutcomeTests` shows the
   engine reports skips as `Skipped[].Reason` text, which is why those tests pin the reason prefix.
5. Historical NotFound false-pass, now fixed. The comment block above `MigrationScanResultTests.cs:458` (T7) records that
   the original test never set `scope=file`, so it always hit the "filePath ignored" path and never exercised the
   `matched no documents` `ArgumentException` -> `InvalidArgument` mapping. It now asserts
   `ErrorData.ErrorCode == MigrationErrorCode.InvalidArgument`. In `scope=solution` a `filePath` that matches nothing is
   silently ignored by design; the owner may want a warning there.
6. Empty list as the "nothing found" result. `FlagMigrationCandidateTests.cs:~365-376`
   (`FindMigrationCandidates_EmptySolution_ReturnsEmptyList`) and `SentinelAsyncifyToolsTests.cs:210-223` (T4) assert an
   empty list / zero count. An empty list is also what a broken attribute detector would return, so these rely on the
   companion positive tests (`FindMigrationCandidates_ReturnsFlaggedMethods`, T2) to stay meaningful.
7. Unchecked `IndexOf` -1 and null `UpdatedText` (see false-pass 2). `ConvertToAsyncBridgeTests.cs:253-258` treats
   "not found" (-1) as a valid index. Hypothesis: `Assert.That(result.UpdatedText, Does.Not.Contain(...))` on a null
   `UpdatedText` passes in NUnit (a string constraint on null fails, so its negation succeeds), as in
   `FlagMigrationCandidateTests.cs:~326-333` (`ConvertToAsyncBridge_StripesMigrationCandidateAttribute`), which is
   saved only because later `Does.Contain` asserts on the same value fail. Not run to confirm.
8. `FirstOrDefault` on `Dictionary<FilePathWrapper,string>.Keys` (`FlagMigrationCandidateTests.cs:86,111`) yields a
   default key with null `Absolute` on a miss. This is guarded by `Is.Not.Null.And.Not.Empty` at `:88,113`, so it fails
   safely; listed for completeness.

### Root cause
Not traced beyond the test code. The pattern is test-authoring convenience (status-only or "no exception" checks, `>= 0`
placeholders, negative-only text checks), not a product defect. Section 6 items 1, 3 and 4 are the only ones that point
at engine or tool design rather than test strength.

### Why it matters
A weak test lets the tool surface regress silently: the progress report, dry-run batch summaries, directive wording and
flag-attribute emission can break while these tests stay green. The shared `InvalidOperationException` contract also
means the engine cannot tell a caller (or a test) which precondition failed without parsing the message.

### Recommendation
Open items; none decided.
1. Fix false-passes 1-5 first (small, local test edits).
2. Strengthen T6, T11, T13, T14 with the `AsyncMigrationProgressReport` and `BatchResultSummary` members in the table.
3. Tighten T3, T10, T16, T21 (count and content instead of `>=` / `.All` on possibly empty lists).
4. Owner decision: distinct error code or exception type for NotFound-style engine failures (section 6, item 1), and
   whether a batch with `Succeeded == 0 && Failed > 0` should flag `IsError` (item 3).

### Out of scope
- Other test projects.
- Did not run any test, so current pass/fail state is unconfirmed.
- Engine behaviour behind suggested values (T11 dry-run `Succeeded == 1`, T10 `Count == 4`) is taken from the test inputs
  and comments, not verified.
- Member lists of `MigrationScanSummary`, `MigrationCandidateFinding` and `FlagCandidateTarget` were not inspected beyond
  what the tests use.
- Line numbers marked `~` and those for T13, T14, T16 are approximate (from read ranges, not search hits).
- `ConvertToAsyncBridgeTests.cs` lines 657-681 (the lambda test) were not read separately; it mirrors the delegate test
  above it.

### Not checked
- Test execution results and the known-failing baseline.
- Engine source for `AsyncOptimizationEngine`, `AsyncBatchEngine` and `AsyncifyTools` (only DTO shapes and tool signatures
  were inspected).
- Whether `TestSolutionBuilder` or `PersistentWorkspaceManager` behaviour could itself mask failures.

---

## RoslynSentinel.Tests.Basic

**RoslynSentinel.Tests.Basic - mostly clean, with three false-passes and ~20 tests worth strengthening**

**Original status:** OPEN 2026-10-20. Strengthening is proposed, not started; the throw-vs-NotFound question is undecided.

### Context
Part of the 2026-10-20 comprehensive review of weak `Is.Null` / `Is.Not.Null` style assertions. A read-only explore-sonnet agent scanned every assertion-like line with `Search` (text mode), grouped them by enclosing method, then read the suspect methods. "Weak-only" means a test's only assertions are `Is.Null`, `Is.Not.Null`, `Is.Empty`, `Is.Not.Empty`, `Count>0` or `Assert.Pass`. Tests with no assertion or other weak shapes (e.g. `Is.True`-only) are listed separately. No tests were run. Line numbers come from search hits; `isServerBinaryStale` flipped mid-run but the reads were unaffected.

### What is broken

Totals:
- ~655 test methods (601 `[Test]` + 207 `[TestCase]` attributes, many methods carry several, + 7 `[TestCaseSource]`).
- 38 weak-only tests (~6%), plus 1 more whose only other assertions are `Is.True` on a computed flag (`SemanticReplaceEngineTests.cs:1157`). 156 other tests contain a null/empty assertion but also assert real values.
- 241 `Is.Null` / `Is.Not.Null` asserts by per-method count (153 `Is.Null`, 87 `Is.Not.Null`); a whole-file pass counted 243 (2 lines not reconciled). 192 (80%) sit in tests that also assert something real; 49 (20%) are in weak-only tests. Of the 87 `Is.Not.Null`, 73 are guarded and 14 are not.
- `Assert.Pass`: 1 real (`UnitTest1.cs:13`); 3 other matches are inside fixture-source strings in `TestCategoryTaggingEngineTests.cs`. `Count>0` as key assertion: 6. `DoesNotThrow`: 0 real. 3 tests with no `Assert` line (`AttributeTextEditBuilderRenderTests.cs`) are fine - the `AssertCommentsRejected` helper checks type, exact message and ErrorCode.

#### Genuine false-passes (flagged first)
1. **`MoveTypeToFileTests.cs:184-189`** `MoveTypeToFile_ReturnsNonEmptyContent_ForBothFiles`: the only assertions are inside `foreach (var (path, content) in changes)`. An empty dictionary passes with zero assertions; a file that was not split passes too. Fix: assert `changes.Count == 2`, the new and source keys, and that content contains `class WidgetB` and `class WidgetA`.
2. **`MemberRefactoringTests.cs:2464-2470`** `BUG_60_RemoveMember_ChecksUsagesBeforeRemoving`: the only real check is inside `if (!Message.Contains("error") && !Message.Contains("Error"))`. The engine returns `Outcome = CannotRemove` with `"// ERROR: Cannot remove member ... usages"` (`MemberRefactoringEngine.cs:3337-3339`). The test passes whether or not removal was refused, and also if the member was removed and the message merely mentions `GetName`. Fix: assert `Outcome == CannotRemove` and `Message` contains `usages`.
3. **`MemberRefactoringTests.cs:2493`** `BUG_60_RemoveMember_ErrorsWhenMemberIsUsed`: asserts `Message` contains "ERROR" or "usages" but not `Outcome == CannotRemove`.
4. Weaker "work done not checked" risks: `WriteChokepointGuardrailTests.cs:102` (only `outcome.Error` is null; siblings also assert text, `LineChanges`, `ErrorCode`) and `ExtractLocalVariableTests.cs:259` (only `UpdatedText` null; already commented as flaky).

#### Pattern categories
| Pattern | Count | Examples |
| --- | --- | --- |
| Edit result: only `UpdatedText` null/empty or non-empty, no `Outcome`/`Message`/`ErrorCode` | 7 | `MemberRefactoringTests.cs:817`, `856`, `2407`, `2421`, `259` (ExtractLocalVariable) |
| Engine DTO or symbol only checked non-null / non-empty | ~10 | `SemanticReplaceEngineTests.cs:51-52`, `153-157`, `1157-1160` |
| Real null-return contract (`TryGet...`, `Check` returns no violation, `StoreChanged...` returns null) | ~20 | `CollateralChangeGuardTests.cs:26-165`, `BasicRefactoringTests.cs:121`, `ChangedContentOffloadTests.cs:71` |
| `reason` / `Message` text only asserted non-null/non-empty | 13 | `ReferenceRoleClassifierTests.cs:142-304` (11 tests), `ResultStatusTests.cs:558`, `564` |
| Error-is-null only, no check that the work happened | 2 | `WriteChokepointGuardrailTests.cs:102`, `SemanticReplaceEngineTests.cs:1157` |
| Placeholder | 1 | `UnitTest1.cs:13` |

#### Offenders table
Rows 1-8 return `DocumentEditResult` (members: `Outcome` (EditOutcome), `ErrorCode`, `Message`, `UpdatedText`, `Changes`, `Warnings`). All trivial unless stated.

| # | file:line | Test | What to assert | Effort |
| --- | --- | --- | --- | --- |
| 1 | `MemberRefactoringTests.cs:817` | `RemoveEnumMember_NotFound_ReturnsErrorWithoutText` | `Outcome == TargetNotFound`, `Message` names `nonexistent` | trivial |
| 2 | `MemberRefactoringTests.cs:856` | `ReplaceEnumMember_NotFound_ReturnsErrorWithoutText` | same | trivial |
| 3 | `MemberRefactoringTests.cs:2407`, `2421` | `AddMember_ResolvesAMultiParameterGenericContainerAsync` (4 cases), `AddMember_NonGenericContainerStillResolvesAsync` | `Outcome == Modified`; `UpdatedText` contains `public int Added`. Sibling at line 2530 already does this. | trivial |
| 4 | `MemberRefactoringTests.cs:2302-2303`, `2314-2315` | `ReplaceMember_SingleNonOverloadedMember_MatchingContextSnippetHasNoWarning`, `...BlankContextSnippetHasNoWarning` | `Outcome == Modified` and `UpdatedText` contains the new body (`Warnings` null is the contract, but a failed edit also yields null) | trivial |
| 5 | `MemberRefactoringTests.cs:360`, `661` | `ReplaceMember_SnippetMatchesBothOverloads_SetsStructuredCandidates`, `GetContainerMembers_UnresolvableSnippet_...` | `Candidates` count of 2 and their names; message text | trivial |
| 6 | `ExtractLocalVariableTests.cs:259` | `ExtractLocalVariable_SkipsMethodCallsWithSideEffects` | `UpdatedText` null is all it asserts; add `Outcome` (not Modified) and the `Message` reason | trivial |
| 7 | `DiagnosticEngineTests.cs:46` | `GetFileDiagnostics_OnBrokenFile_ReportsError` | `Data.Details` contains `DiagnosticInfo` with `Id == "CS0103"`, `Severity`, `StartLine` (`Data` is `DiagnosticSummary(Errors, Warnings, Details)` and throws on non-Success, so a failed run cannot pass) | trivial |
| 8 | `WriteChokepointGuardrailTests.cs:102` | `MixedFile_KeepingItsDominantStyle_IsNotRefusedAsync` | only `outcome.Error` is null; add the written text via `workspace.ReadText("A.cs")` as siblings do | trivial |
| 9 | `SemanticReplaceEngineTests.cs:51-52`, `65-66`, `954`, `970` | `ResolveBoolMemberAsync_WithAutoProperty/WithField_ReturnsSymbol`, `..._InverseAlias_...Accepted` | `symbol.Name` and `Kind` are the expected member | trivial |
| 10 | `SemanticReplaceEngineTests.cs:153-157`, `197-201`, `176-178`, `252-253` | four `DescribeDeclaration_*_ReturnsCorrectInfo` tests | `DeclarationInfo(FilePath, IdentifierSpan, InitializerValueSpan, InitializerText)`: assert `FilePath`, identifier text via span, initializer text. Parametrized test at line 211 already does this; these four are partly redundant. | trivial |
| 11 | `SemanticReplaceEngineTests.cs:1157-1160` | `CollectSitesAsync_AliasRetarget_PartialClassBothFiles_PrivateFieldRefSucceeds` | `edits` and `sites` only non-empty; assert the edit text, site `Role` list, file paths as sibling at line 920 does | moderate |
| 12 | `DependencyEngineFixtureTests.cs:39-42`, `66` | `GetProjectDependencies_WithProjectReferences_ReturnsExactReferences` | name promises exactness but asserts `Contains.Item("UpstreamLib")` and `PackageReferences` not-null; use `Is.EquivalentTo(["UpstreamLib"])` and `PackageReferences Is.Empty`; same for the `unused` list | trivial |
| 13 | `MoveTypeToFileTests.cs:71-74` | `MoveTypeToFile_WithRealWorldScenario_ShouldSucceed` | `newFilePath.Absolute Is.Not.Null`; assert it ends with `FeatureGateErrorResponse.cs` and content contains the record | trivial |
| 14 | `ReferenceRoleClassifierTests.cs:142`, `154`, `208`, `220`, `232`, `244`, `256`, `268`, `280`, `292`, `304` | 11 `Classify_*_ReturnsUnsupported*` tests | `reason Is.Not.Null.And.Not.Empty`; `role` is already strong - add `Does.Contain` of the specific reason | trivial |
| 15 | `CollateralChangeGuardTests.cs:118` | `InsertionsAtTheSamePosition_KeepTheirGivenOrder` | second check is `Is.Not.Null`; assert `Kind == ContentDiffers` | trivial |
| 16 | `UnitTest1.cs:13` | `Test1` | placeholder `Assert.Pass()`; delete or give a real assertion | trivial |
| 17 | `ResultStatusTests.cs:558`, `564` | `DocumentLookupResult_StatusAgreesWithIsError_...` | `Message` non-empty only; `Status` and `Code` already asserted - minor | trivial |

Not flagged (null is the contract and no richer status exists): `AppliedChangeSummaryTests.cs:23`; `ChangedContentOffloadTests.cs:71`, `90`; `CountResultItemsTests.cs:25`; the `TryGetEnumMemberContainerName_*` pairs in `BasicRefactoringTests.cs` / `MemberRefactoringTests.cs`; `CollateralChangeGuardTests` null returns (paired with positive tests asserting `Kind`, `Line`, `Expected`); `TestRunResultProjector` null-drop tests; and assertions in `TestRunEngineLockDetectionTests`, `TestRunEngineTrxOutputTests`, `WhitespaceRatchetTests`, `MovePositionMapTests`, `DependencyEngineFixtureTests:33`, `ResultStatusTests` (~70 Code/Message null checks after Status asserts), `SemanticReplaceEngineTests` error-null checks - all assert real values alongside.

### NotFound-style outcomes that let failures slip through
Verified against source unless marked hypothesis.
1. **Enum NotFound tests assert only the text.** `MemberRefactoringTests.cs:817` and `:856` check only `UpdatedText Is.Null.Or.Empty`; the same assertion passes for DocumentNotFound, SourceInvalid, a swallowed error or a no-op. `Outcome == TargetNotFound` and `Message` are not asserted. `:794`, `:1075`, `:1154`, `:1276` use the same pattern but also assert `Outcome`/`Message`, so they are fine.
2. **ExtractLocalVariable skip** (`ExtractLocalVariableTests.cs:259`) passes whenever `UpdatedText` is null; a document-lookup failure or internal error looks identical to the intended "skipped, has side effects" case.
3. **Removing a used member only mentions the failure.** `MemberRefactoringEngine.cs:3337-3339` returns `Outcome = CannotRemove` with `Message = "// ERROR: Cannot remove member ... usages"` and the message embeds the full file text; the `// ERROR:` comment prefix makes it read as code, so callers must string-match. `MemberRefactoringTests.cs:2464` and `:2493` lack an `Outcome` check (the first is also conditional). The same `// Cannot edit:` prefix style appears at `MemberRefactoringEngine.cs:989` (`RemoveEnumMemberAsync`). A thrown typed exception would force callers and tests to handle it explicitly - owner's call.
4. **`TryGetEnumMemberContainerNameAsync` returns null for two cases.** `BasicRefactoringTests.cs:121`, `:132` and `MemberRefactoringTests.cs:883`, `:894` assert null for both a regular member and a missing symbol; not-found vs not-an-enum-member cannot be told apart. A deliberate probe, but a missing file or symbol also yields null.
5. **MoveTypeToFile tests do not prove the file was split.** `MoveTypeToFileTests.cs:71-74`: `changes.Keys.FirstOrDefault(...)` returns `default(FilePathWrapper)` on a miss and `.Absolute` on a default struct is null, so `Is.Not.Null` detects a miss only by accident of the struct default and does not check the path. `:184-189` has a `foreach` with no other assertion.
6. **Null returns that mean "nothing happened".** `ChangedContentOffloadTests.cs:71`, `:90` (`StoreChangedContentAsync`) return null for "no solution root" and "empty changes"; a caller cannot tell skipped from failed. `TestRunEngineLockDetectionTests.cs:36` null for "no lock detected" is the intended contract. Hypothesis (not traced): non-test callers of `StoreChangedContentAsync` may share the ambiguity.
7. **Tuple helpers separate error and success** (`ResolveBoolMemberAsync`, `CollectSitesAsync`, `PlanInvertBooleanAsync` return (result, `ToolError`)); these tests assert `ErrorCode` and `Message` and are strong, except `SemanticReplaceEngineTests.cs:1157-1160`.
8. **`GetMemberSource` null `info`:** `MemberRefactoringTests.cs:294`, `:315`, `:338`, `:638`, `:676` assert `info Is.Null`. `:638` also asserts `Outcome == CannotEdit` and `ErrorCode == NotFound`. Hypothesis: `:294`, `:315`, `:338`, `:676` pair null with outcome and error code likewise (not re-read).

Not-found cases above are returned inconsistently as an outcome, a null, or `// ERROR` text; the owner can decide whether engine paths 1-4 should throw a typed tool exception.

### Root cause
Not traced beyond the file:line references above. The common mechanism: edit results carry failure in `Outcome`/`Message` beside a nullable `UpdatedText`, and several tests assert only the nullable field or sit behind conditionals.

### Why it matters
Most of the 655 tests assert exact text, status enums, ErrorCodes and spans, and `ResultStatusTests`, `SemanticReplaceEngineTests` and `CollateralChangeGuardTests` are strong. The three false-passes can stay green while the operation is refused or does nothing.

### Recommendation
1. Fix the three false-passes (rows above: `MoveTypeToFileTests.cs:184-189`, `MemberRefactoringTests.cs:2464-2470`, `:2493`) and the two "work done" gaps.
2. Strengthen the `MemberRefactoringTests.cs` outcome/message cluster, `ReferenceRoleClassifierTests.cs` reason checks, `SemanticReplaceEngineTests.cs` declaration/site tests, `DependencyEngineFixtureTests.cs`, `DiagnosticEngineTests.cs:46`; delete `UnitTest1.cs`.
3. Open decision: whether the NotFound/CannotRemove outcomes and the `// ERROR:` message prefix should become typed exceptions; decide before rewriting rows 1, 2, 5 so they are not rewritten twice.

### Out of scope
Other weak shapes (`Does.Contain`-only, strong-but-wrong asserts); helper-only assertions (only tests flagged weak-only or conditional were traced; 652 methods with direct Assert lines were scanned); not inspected beyond the scan: `BasicRefactoringTests`, `InlineVariableTests`, `ExtractConstantTests`, `DiffEngine*`, `RoslynFormattingHelper*`, `*NormalizeCharacterization*` tests, `MoveBindCheckTests`, `NamedArgumentsEngineTests`; `MoveTypeToFileTests`/`MoveMemberPreservesUntouchedTextTests` fixture/helper bodies.

---

## RoslynSentinel.Tests.Battery.Advanced

**RoslynSentinel.Tests.Battery.Advanced has about 75-80 weak-assertion tests, concentrated in BatteryTwentyFiveTests.cs**

**Original status:** OPEN 2026-10-20. Audit complete (read-only); strengthening not yet scheduled, no fix decided.

All paths below are relative to `RoslynSentinel.Tests.Battery.Advanced/` unless stated. Counts are
regex-based estimates. No tests were run; engine behaviour is inferred from source reads.

### Context

Read-only audit of every NUnit test in the project, very thorough breadth, part of a multi-project review of
weak assertions. Weak means the only meaningful assertion is `Is.Null`/`Is.Not.Null`, `Is.Not.Empty`,
`Count > 0`, `Assert.Pass` or "does not throw" on a rich DTO or result, so the test can pass while the
engine misbehaves. Ignored as not weak: a null guard immediately followed by member asserts on the same
value, and cases where null/non-null is the contract and the DTO has no richer status.

Method: `Search` (text, symbol) scoped to the project, `GetMethodSource`/`ReadFile` for each flagged test,
`Search` symbol to read the DTO members so every recommendation names a real member.

DTO facts used (verified from source during the audit):
- `DocumentEditResult` is a sealed record extending `EngineResultBase`, never null. Members: `Outcome`
  (`EditOutcome`), `UpdatedText`, `Message`, `Changes`, `ErrorCode`, `IsError`, `Status`, `Code`,
  `IsCommitted`, `ChangeId`, `Candidates`, `Warnings`, `AppliedEditIndexes`.
- `EditOutcome`: Unset, Modified, TargetNotFound, DocumentNotFound, SourceInvalid, CannotOptimize,
  CannotEdit, CannotMove, CannotRemove, CannotConvert, FeatureDisabled, Error, AlreadyInTargetState,
  NoCandidatesFound. Factories: `DocumentEditResult.DocumentNotFound/TargetNotFound/FeatureDisabled`.
- `EngineResultWrapper<T>`: `Outcome` (`EngineOutcome`: Success, Failure, DocumentNotFound,
  TargetNotFound, InvalidInput, Timeout, InternalError), `Error`, `Data`, `IsError`, `Status`, `Code`, `Message`.
- `ExtractMethodResult(IsError, ErrorMessage, BeforeSnippet, CallSiteReplacement, ExtractedMethodText,
  UpdatedSourceContent)`.
- `FluentBuilderResult(BuilderClassName, BuilderCode, UsageExample, Error)`; `IsError => Error is not null`.
- `DiRegistration(Lifetime, ServiceType, ImplementationType, filePath, Line, CallSite)`.
- `ComprehensiveHealthReport(TotalIssues, TotalIssuesByCategory, ProjectSummaries, HasMoreData,
  NextProjectOffset, StatusMessage)`.
- `DiagnosticSummary(Errors, Warnings, Details, BuildVerification)`.
- `SecurityIssueReport` has `IssueType` ("HardcodedPath", "HardcodedSecret", "PossibleSqlInjection");
  `AsyncSafetyReport` has `MethodName` and `Reason`; `AntiPatternFinding` has `Pattern` and `Snippet`.

### What is broken

#### Verdict

Mostly clean except `BatteryTwentyFiveTests.cs` ("Gap Coverage" smoke tests, 57 tests, about 45 can
false-pass). Outside it there are about 30 isolated weak tests in 14 files, plus a few tests that can never
fail. The common root cause is that unknown-file and no-op tests assert `UpdatedText Is.Null` or
`Not.Empty` instead of `Outcome`: only 17 places in 6 files (Fourteen, Nine, Seventeen, Seven, Ten,
Twelve) assert an `Outcome`, `ErrorCode` or `IsError` in the whole project.

#### 1. Project totals (estimates)

| Metric | Value |
| --- | --- |
| Test files | 24 |
| `[Test]` attributes | about 434 (regex over attribute lines) |
| Weak-only tests | about 75-80 (about 18%): about 45 in TwentyFive, about 30 elsewhere |
| Raw weak-pattern regex hits | 179 in 22 files (about 100 are real `Is.Null`/`Is.Not.Null` asserts; the rest are `Not.Empty`, `GreaterThan`, `DoesNotThrow` and method names containing "IsNull") |
| Null/empty asserts guarded by a follow-up | about 40-45% overall; about 55-60% outside TwentyFive; about 20% inside TwentyFive |
| Outcome/status asserts | 17 places in 6 files |

Per-file raw regex hits: TwentyFive 55, ThirtyTwo 21, TwentySix 18, Seven 11, ThirtyFive 9, ThirtySeven 9,
ThirtySix 8, Ten 6, Seventeen 5, Twelve 5, Eleven 4, Five 4, Nine 4, Sixteen 4, Fourteen 3, Thirteen 3,
Thirty 3, Eight 2, ThirtyEight 2, Fifteen 1, TwentyEight 1, Six 1.

Files with the most `[Test]` attributes: TwentyFive 57, TwentySix 55, Thirty 25, TwentySeven 25, Seven 22,
ThirtySeven 20, Five 19, TwentyEight 16, ThirtyEight 15, ThirtySix 15, Ten 14, ThirtyFive 14, Eight 13,
then 12 each in Eleven, Fourteen, Six, Thirteen, ThirtyNine, ThirtyTwo, Twelve (Nine, Fifteen, Sixteen and
Seventeen were in a truncated listing).

#### 2. Pattern categories

| Cat | Pattern | Count (est.) | Examples |
| --- | --- | --- | --- |
| A | `Not.Null` only on a never-null record or List (tautology) | about 38 | `BatteryTwentyFiveTests.cs:42, 67, 82, 98, 126, 136, 152, 335, 369, 542, 590, 952`; `BatterySevenTests.cs:409, 462, 471`; `BatterySixteenTests.cs:132` |
| B | Unknown-file test asserts `UpdatedText Is.Null`, no `Outcome` | about 24 | TwentyFive:51, 378, 599, 618, 637, 732, 961 (7); Twelve:56, 115, 223; Fourteen:41, 130; Seventeen:36, 66, 73; Eleven:51; Fifteen:150; Sixteen:77; Seven:418 |
| C | `Not.Empty` only on a findings list, no member checked | about 20 | TwentySix:167, 183, 297, 315, 334, 533, 1110, 1208; ThirtySix:114, 152, 164, 187, 204, 226; Sixteen:55; Seventeen:176; ThirtySeven:138 |
| D | Always-true or either-or assertions | about 8 | TwentyFive:513, 562, 822; Seven:388; Seventeen:59; TwentyEight:377; Twelve:174, 235 |
| E | `>= 0` on an `int` | 3 | Sixteen:146; Ten:225, 226 |
| F | Error `Not.Null/Not.Empty` with no `IsError`/class-name/`BuilderCode` check | 4 | ThirtyTwo:60-146 (four DoesNotThrow tests) |
| G | Conditional assertion inside `if` | 1 | ThirtyFive:169 |

Categories overlap slightly; counts are not additive to the 75-80 total.

#### 3. Genuine false-passes (flagged first; these cannot fail on a real regression)

1. `BatteryTwentyFiveTests.cs:562` `AddDependencyAsync_UnknownFile_ReturnsNonNullOrThrows`:
   `Assert.That(result != null || caughtEx != null, Is.True, ...)`. The catch is `catch (Exception)`, so
   the expression is always true. The test cannot fail.
2. `BatteryTwentyFiveTests.cs:513` `CheckPackageInconsistencyAsync_InMemorySolution_ReturnsNonNull`: same
   either-or shape; fails only if a non-`Exception` escapes (effectively never).
3. `BatteryTwentyFiveTests.cs:822` `GetSymbolInfoAsync_KnownMethodSnippet_ReturnsNonNull`:
   `(result == null || result.Name != null)`. A null result passes, so a lookup that finds nothing passes.
4. `BatterySevenTests.cs:388` `ExtractMethodAsync_ValidRange_ReturnsResult` (test starts at 387):
   `Not.Null` plus `!IsError || ErrorMessage != null`. A failing result that carries a message passes.
5. `BatterySeventeenTests.cs:59` `ConvertToNullCoalescing_IfNullAssignment_ConvertsToModernSyntax`: only
   `UpdatedText Is.Not.EqualTo("")`. Unchanged source passes.
6. `BatteryTwentyEightTests.cs:377` `ConvertWhileToFor_NoInitDeclaration_ReturnsUnchanged`:
   `UpdatedText Not.Null.And.Not.Empty`. A converted output also passes, so the "unchanged" claim is untested.
7. About 35 `Not.Null`-only tests in `BatteryTwentyFiveTests.cs` (category A lines above and in the table).
   They assert on a never-null record or List, so they pass whatever the engine returns short of throwing.
8. `BatterySixteenTests.cs:146` and `BatteryTenTests.cs:225, 226`: `>= 0` on an `int` (tautology).
9. `BatteryThirtyFiveTests.cs:169` `TraceVariableLifetime_IsInLoop_TrueInsideForEach`: the assertion is inside
   `if (decl != null)`, so it silently passes when no Declaration access is found.
10. `BatteryTwelveTests.cs:174` `ClassToRecord_UnknownFile`: `Outcome Is.Not.EqualTo(Modified)` accepts every
    failure kind, not just `DocumentNotFound`.

#### 4. Offenders table (grouped by file)

Effort: trivial = one or two assertions from the DTO members, no fixture change; moderate = needs the exact
expected count or text from running the fixture once.

| file:line | Test | DTO | Assert instead | Effort |
| --- | --- | --- | --- | --- |
| TwentyFive:42, 67, 82, 98 | LogicSimplification conversions (if-to-switch, foreach/for/while) | `DocumentEditResult` | `Outcome == EditOutcome.Modified`; `UpdatedText` contains `switch` or `for (` | trivial |
| TwentyFive:369 | `UseIndexFromEnd` | `DocumentEditResult` | `Outcome == Modified`; `UpdatedText` contains `^1` | trivial |
| TwentyFive:335 | `AddRetryPolicyAsync` (comment says stub) | `DocumentEditResult` | `Outcome` is not `Unset`; check `Message` | trivial |
| TwentyFive:542, 590, 609, 628, 651, 723, 952, 971 | AddDependency, RunMicroRefactoring, InlineParameter, ConvertMethodToIndexer, MoveTypeToOuterScope, ConvertIndexerToMethod, ConvertSwitchExpressionToStatement, UseFieldBackedProperties | `DocumentEditResult` | `Outcome == Modified` or `TargetNotFound`, plus `UpdatedText` content | moderate |
| TwentyFive:51, 378, 599, 618, 637, 732, 961 | unknown-file variants | `DocumentEditResult` | `Outcome == EditOutcome.DocumentNotFound` | trivial |
| TwentyFive:126, 136, 161, 170, 180, 223, 237, 266, 276, 290, 306, 689 | Find*/Detect* list tests | `List<AntiPatternFinding>`, `List<string>`, `List<DeadCodeReport>`, `List<ExceptionHandlingFinding>`, `List<MissingCancellationTokenFinding>` | The finding the fixture plants. `FindMutablePublicProperties`: `Pattern`/`Snippet` naming `Name`. `FindNamingViolations`: names `my_class`. | moderate |
| TwentyFive:152 | `DetectUnreachableCode` | `List<string>` | Non-empty; an entry mentions `int x` | trivial |
| TwentyFive:460 | `DetectUnusedLocalVariables` | `List<DeadCodeReport>` | An entry naming `unused` | trivial |
| TwentyFive:760, 787 | unknown-file `ControlFlowSummary` / `DataFlowSummary` | summary types | `MethodName` is empty/default and members are default | trivial |
| TwentyFive:844, 869, 892, 916 | Uninstantiated types, unused interfaces and similar | `List<string>` | Name `Bar` / `IFoo` in the positive case; `Is.Empty` in the negative case (916 has no `Is.Empty`) | trivial |
| Seven:387 | `ExtractMethodAsync_ValidRange_ReturnsResult` | `ExtractMethodResult` | `IsError == false`; `ExtractedMethodText` contains `ComputeSum`; `CallSiteReplacement` and `UpdatedSourceContent` contain the call | moderate |
| Seven:471 | `ExtractMethod_ValidLineRange_ReturnsResult` (MemberRefactoringEngine) | `ExtractMethodResult` | `IsError == false`; `ExtractedMethodText` contains `FormatLabel` | moderate |
| Seven:409 | `AddRemoveParamsAsync_MethodWithParams` | `DocumentEditResult` | `Outcome` (`Modified`) | trivial |
| Seven:462 | `SortMembers_AutoStageTrue` | `DocumentEditResult` | `Outcome == Modified`; member order in `UpdatedText` | trivial |
| Seven:418 | `AddRemoveParamsAsync_UnknownFile` | `DocumentEditResult` | `Outcome == DocumentNotFound` | trivial |
| Seven:398 | `ExtractMethodAsync_UnknownFile` | `ExtractMethodResult` | Fine already; optionally check `ErrorMessage` | trivial |
| Seventeen:59 | `ConvertToNullCoalescing_IfNullAssignment` | `DocumentEditResult` | `Outcome == Modified`; `UpdatedText` contains `??=` or `??` | trivial |
| Seventeen:36, 66, 73 | unknown-file variants | `DocumentEditResult` | `Outcome == DocumentNotFound` | trivial |
| Seventeen:176 | `FindStructuralSmellsAsync_MultipleTypes` | `List<string>` | An entry contains `MultiType` | trivial |
| TwentyEight:377 | `ConvertWhileToFor_NoInitDeclaration_ReturnsUnchanged` | `DocumentEditResult` | `UpdatedText == source` (or no `for (`); `Outcome` | trivial |
| Sixteen:132, 139, 146 | `GenerateHealthReport_MinimalSolution_ReturnsReport` | `ComprehensiveHealthReport` | `ProjectSummaries.Count == 1`; exact `TotalIssues`; `HasMoreData == false`; specific `StatusMessage` | moderate |
| Sixteen:55 | `FindDiRegistrationsAsync` | `List<DiRegistration>` | `Count == 2`; `Lifetime` Singleton/Scoped; `ServiceType` IMyService/IOrderService; `ImplementationType` | moderate |
| Sixteen:77 | `InlineField_UnknownFile` | `DocumentEditResult` | `Outcome == DocumentNotFound` | trivial |
| Ten:224-226 | `GetProjectDiagnostics_KnownProject` | `EngineResultWrapper<DiagnosticSummary>` | `Outcome == EngineOutcome.Success`; `Data.Errors == 0`; `Data.Warnings == 0` (sibling `GetFileDiagnostics` asserts `Errors Is.Zero`) | trivial |
| Thirty:449 | `UseFieldBackedPropertiesEngine_WhenFeatureDisabled` | `DocumentEditResult` | `Outcome == EditOutcome.FeatureDisabled` | trivial |
| Twelve:56, 115, 223 | unknown-file variants | `DocumentEditResult` | `Outcome == DocumentNotFound` | trivial |
| Twelve:174 | `ClassToRecord_UnknownFile` | `DocumentEditResult` | `Outcome == DocumentNotFound` (not `Is.Not.EqualTo(Modified)`) | trivial |
| Twelve:235 | `FormatDocument_ValidFile` | `DocumentEditResult` | `UpdatedText` differs from original / has formatted spacing | trivial |
| TwentySix:167, 183 | `DetectAsyncVoidMethodsAsync` | `List<AsyncSafetyReport>` | `Any(r => r.MethodName == "DoWork"/"FireAndForget")` | trivial |
| TwentySix:297, 315, 334 | `FindBlockingCallsInAsyncAsync` | `List<AsyncSafetyReport>` | `MethodName` `PollAsync`/`DoAsync`; `Reason` | trivial |
| TwentySix:533 | `DetectValueTaskMisuseAsync` | `List<AsyncSafetyReport>` | `MethodName` and `Reason` | trivial |
| TwentySix:1110 | `FindUnawaitedFireAndForgetAsync` | `List<AsyncSafetyReport>` | `MethodName == "StartAsync"`; `Reason` contains `RunAsync` (sibling at 1132 asserts `Reason`) | trivial |
| TwentySix:1208 | `FindConfigureAwaitMissingAsync` | `List<AsyncSafetyReport>` | `MethodName == "FetchAsync"` | trivial |
| ThirtySix:114 | FireAndForget project scope | `List<AsyncSafetyReport>` | `MethodName == "M"` (an empty-for-ghost counter assert already exists) | trivial |
| ThirtySix:152, 164 | `FindHardcodedPaths` file/project scope | `List<SecurityIssueReport>` | `IssueType == "HardcodedPath"` | trivial |
| ThirtySix:187, 204, 226 | SQL injection null/project/file scope | `List<SecurityIssueReport>` | `IssueType == "PossibleSqlInjection"` | trivial |
| ThirtyTwo:60-146 | DIServiceClass, APIController, AbstractClassNoProperties, EmptyClass DoesNotThrow tests | `FluentBuilderResult` | `IsError == true`; `Error` contains the class name; `BuilderCode` null or empty | trivial |
| ThirtyTwo (happy path) | `Error Is.Null.Or.Empty` | `FluentBuilderResult` | `IsError == false` (empty-string `Error` still counts as an error) | trivial |
| ThirtyTwo:239 | record builder | `FluentBuilderResult` | `BuilderCode` contains `WithName`/`WithPrice` (low severity) | trivial |
| ThirtySeven:138 | `DetectAntiPatterns_ExplicitProjectName_StillWorksForTestProject` | `List<AntiPatternFinding>` | `Any(f => f.Pattern == "AsyncVoidMethod")` | trivial |
| ThirtyFive:169 | `TraceVariableLifetime_IsInLoop_TrueInsideForEach` | lifetime report | Assert the Declaration access exists, then the loop flag, outside the `if` | trivial |
| Eleven:51; Fourteen:41, 130; Fifteen:150 | unknown-file variants | `DocumentEditResult` | `Outcome == DocumentNotFound` (Fourteen:32 already does this) | trivial |

Per-file counts for the lower-priority remainder (error-path tests that assert only a `Message` prefix such as
`"// ErrorDetails:"` or `"// ERROR:"` with no `Outcome`): Eleven, Twelve, Fifteen, Thirty, ThirtyFive, about
1-3 tests each. The TimeAbstraction tests in ThirtySeven could also assert the class name in the finding
string (minor).

#### 5. Guarded or clean (not counted as weak)

- Clean (strong): `BatteryTwentySevenTests.cs` (45 asserts, all content-based), `BatteryThirtyNineTests.cs`
  (`Kind`/`Risk`, counts, `CyclePath`), `BatteryTenTests.cs` apart from line 224-226 (`ValidateChanges`
  checks `IsError` plus `Diagnostics`; security tests check `IssueType`).
- Guarded: BatteryFive:56, 119; BatteryNine:65, 103, 198, 272; BatteryThirteen:45, 64, 176; BatterySix:234;
  BatteryEight:129, 323 (`Not.Null.And.Not.Empty` followed by `Does.Contain`); Eleven:138, 168, 202; Fourteen:193;
  Fifteen:124; Seven:90, 238, 242, 269, 294; TwentySix:76, 94, 131 (filtered by `Pattern`), 418, 454, 734, 765,
  778; ThirtyEight:115 (found-case lookup is the contract), 302; ThirtyFive:140, 144; TwentyFive:214, 425 and
  the List tests followed by `Is.Empty`.

### Root cause

Not traced to a single source. Hypotheses (unverified):
- `BatteryTwentyFiveTests.cs` was written as a coverage smoke file ("does the engine not crash"), so
  the Not.Null assertion was the intended bar. This is consistent with test names such as `*_ReturnsNonNull`
  and the "stub" comment at line 335, but the authoring intent was not confirmed from history.
- Most engine results (`DocumentEditResult`) are never null, so `Is.Not.Null` on them carries no information;
  the richer `Outcome` member is simply not used by most tests (17 uses in the project).

### NotFound-style outcomes that let failures slip through

Where a NotFound/TargetNotFound/null-style result lets a failure pass silently, so the owner can judge whether
the engine should throw instead. Items marked "verified" were read in source or test; others are hypotheses.

1. Unknown-file paths return a result, not an exception (verified from engine source reads). Edit engines
   return `DocumentEditResult { Outcome = DocumentNotFound, UpdatedText = null }`; no-target paths return
   `TargetNotFound`. Tests then assert only `UpdatedText Is.Null` (category B, about 24 tests), which a
   `TargetNotFound`, `SourceInvalid` or `Error` result also satisfies. A broken file resolver that turns every
   file into `TargetNotFound` would pass all of them. Owner question: should unknown-file be an exception
   (typed, as in the error-code taxonomy work), or is the `Outcome` value the contract? If the latter, tests
   should assert `DocumentNotFound`.
2. `AddRetryPolicyAsync` returns `Outcome = DocumentNotFound` with `Message = "// Document not found."` for
   an unknown file (verified). The message is the only human-readable signal; the test at
   `BatteryTwentyFiveTests.cs:335` (valid snippet) asserts only `Not.Null`, so a stub that always returns
   `DocumentNotFound` would pass. The in-code comment says it is a stub.
3. `BatteryTwentyFiveTests.cs:562` and `:513` accept either a result or an exception, with the catch on
   `catch (Exception)`. A NotFound result, a thrown `FileNotFoundException`, or a null all pass. Owner
   question: which of "return NotFound" or "throw" is the contract for `AddDependencyAsync` and
   `CheckPackageInconsistencyAsync` on an unknown file; today the test pins neither.
4. `BatteryTwentyFiveTests.cs:822` accepts `null` from `GetSymbolInfoAsync` (`SymbolHoverInfo?`). Null is a
   valid "not found" contract for a lookup, but the test is titled "KnownMethodSnippet", so a lookup that
   fails to find a known method passes. Hypothesis: the engine returns null for both "unknown" and
   "known but unresolved"; not confirmed.
5. `BatterySevenTests.cs:388` passes when `IsError == true` as long as `ErrorMessage != null`, so an
   `ExtractMethod` that reports "target not found" on a valid range passes (verified in test source).
6. `BatteryTwelveTests.cs:174` asserts `Outcome Is.Not.EqualTo(Modified)`; any NotFound, `CannotConvert` or
   `Error` outcome passes.
7. `BatterySeventeenTests.cs:59` and `BatteryTwentyEightTests.cs:377`: a no-op (nothing found to convert, so
   the original text is returned or `TargetNotFound` with empty text) is indistinguishable from success or
   from "unchanged" in the assertion used. The "ReturnsUnchanged" test cannot tell a deliberate no-op from a
   failed match.
8. `BatteryThirtyFiveTests.cs:169`: when no Declaration access is found (a null-style match), the `if`
   skips the assertion and the test passes (verified at the `if (decl != null)` line).
9. List-returning detectors (category C and the TwentyFive list tests): "no findings" and "target not found"
   both surface as an empty list or a non-null empty list, so `Not.Null`-only and, for negative cases,
   `Is.Empty` cannot distinguish "analysed and clean" from "project/file not found". Hypothesis: several
   engines return an empty list for an unknown project name (the ThirtySix ghost-project counter asserts rely
   on this); an unknown-scope input might better throw or return a status.
10. Error-path tests that assert only a `Message` prefix (`"// ErrorDetails:"`, `"// ERROR:"`) in Eleven,
    Twelve, Fifteen, Thirty, ThirtyFive: the prefix does not distinguish NotFound from other failures.

### Why it matters

The project is the battery that is meant to show each Advanced engine does something useful. With `Not.Null`
on a never-null record, a stubbed or silently failing engine stays green, so a regression in an engine (or a
broken workspace/file resolver that turns everything into NotFound) is invisible. Weaker models that rely on
these results get no signal, and the environment (tests) fails to communicate which outcome is expected.

### Recommendation

Ranked by leverage; nothing here is decided.
1. Fix the never-fail tests first (section 3 items 1-3, 8, 9): `BatteryTwentyFiveTests.cs:562, 513, 822`,
   `BatterySixteenTests.cs:146`, `BatteryTenTests.cs:225-226`, `BatteryThirtyFiveTests.cs:169`.
2. Replace `UpdatedText Is.Null` in the unknown-file tests with `Outcome == EditOutcome.DocumentNotFound`
   (about 24 tests, all trivial), or add a shared helper that asserts the outcome and `Message`.
3. In `BatteryTwentyFiveTests.cs` replace the `Not.Null`-only asserts with `Outcome == Modified` plus a text
   contains-check for the `DocumentEditResult` tests and a named-finding check for the list tests (table above).
4. Add member asserts (`MethodName`, `IssueType`, `Pattern`, `Lifetime`, `ServiceType`) to the `Not.Empty`-only
   list tests in TwentySix, ThirtySix, Sixteen, Seventeen and ThirtySeven.
5. Tighten Seven:387/471, Seventeen:59, TwentyEight:377, Twelve:174/235, Thirty:449, ThirtyTwo:60-146 and
   Sixteen:132 per the table.
6. Open for the owner: decide whether unknown-file and unknown-scope inputs should throw typed errors
   instead of returning NotFound-style results (see the NotFound section); the answer fixes what the tests
   should pin.

### Out of scope

- Other test projects (audited separately in the multi-project review).
- Strong tests and guarded null checks (listed in section 5).

### Not checked

- No tests were run. Expected outcomes (for example whether `ConvertForEachToForAsync` returns `Modified`
  on the fixture) are inferred from engine source, not observed.
- Full test bodies of BatteryFive, Six, Eight, Eleven, Thirteen and TwentySix were read only around the
  regex hits. BatterySix was read only partly.
- The `[Test]` count (434) comes from a regex over attribute lines; it can miss attributes on shared lines or
  count `[TestCase]` rows differently.
- The return type of `FindStructuralSmellsAsync` as called through `SolutionStructureEngine`: the indexed
  signature (`List<string>`) came from `ProjectStructureEngine`, so the match is a hypothesis.
- Percentages for "guarded by follow-up" are estimates from the audit pass, not an exhaustive count.
- Engine behaviour on NotFound paths was read from source for the TwentyFive engines only; the other engines
  in the NotFound section are inferred from the test assertions.
- Tool note: a PowerShell scratch-dump command during the audit was blocked by the dog-fooding hook (false
  positive on the literal text "Set-Content" and a `.cs` path). It was not reworded; results were paged with
  `GetLargeResult` instead.

---

## RoslynSentinel.Tests.Battery.Basic

**RoslynSentinel.Tests.Battery.Basic has no null-only tests, but 8 tests assert too little and 2 can false-pass**

**Original status:** OPEN 2026-10-20. No fix decided; this is an audit report for the test owner to triage.

### Context

Read-only audit of the test project `RoslynSentinel.Tests.Battery.Basic` on 2026-10-20, looking for weak NUnit
assertions: tests whose only meaningful assertion is `Is.Null` / `Is.Not.Null`, `Is.Not.Empty`-only,
`Count > 0`-only, `Assert.Pass`, or "does not throw" with no outcome check, on a rich DTO or result
(engine result, `ApplyChangesResult`, `MoveMemberResult`, status flags, message text, edited source text,
changed-file lists). Method: Roslyn MCP text search over the project, then every flagged test and most of
the unflagged tests were read in full. All 15 test files were reviewed. `BatteryFourTests.cs` had
uncommitted edits from another session at audit time, so its line numbers may shift.

DTO shapes inspected (from source):
- `ApplyChangesResult` (`RoslynSentinel.Common/ApplyChangesResult.cs:19`): `IsError`, `SucceededFiles`,
  `FailedFiles`, `Summary`, `WorkspaceInSync`, `WorkspaceVersion`, `PreImages`, `ValidationResult`,
  `RolledBackFiles`, `NoOpFiles`, `RefusalCode`; derived `Status`, `Code`, `Message`.
- `MoveMemberResult` (`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:20`): `Changes`,
  `SkippedCallSites`, `PendingLedgerEntries`, `PendingLedgerOperationName`, `AppliedFixups`, `Notes`,
  `AppliedEdits`, `RewrittenCallSites`. It has no status flag: failures throw (`ToolInvalidArgumentException`,
  `ToolTargetIneligibleException`), so a `Changes`-based text assertion is a sound outcome check.
- `StaticConversionResult` (`RoslynSentinel.Engines.Basic/StaticConversionEdits.cs:17`): `HandledIndexes`,
  `Changes`, `Notes`.

### What is broken

#### 1. Totals

| Measure | Value |
| --- | --- |
| Test files | 15 |
| Test methods | 171 (167 `[Test]` + 4 `[TestCase]`-only methods) |
| Test cases | 179 (12 `[TestCase]` rows) |
| Tests with a weak-only assertion | 8 (about 4.7%): 2 false-pass candidates, 4 weaker versions of the same risk, 2 low |
| `Is.Null` / `Is.Not.Null` asserts (incl. `Null.Or.Empty`, `Not.Null.And.Not.Empty`) | 40 |
| Other matches (`Is.Empty`, `Is.Not.Empty`, `GreaterThan(0)`) | 33 (total pattern hits 73) |
| Null asserts guarded by a follow-up or a contract | 40 of 40 (100%) |
| Tests where a null/non-null check is the ONLY assertion | 0 |

Breakdown of the 40 null asserts (all verified):
- About 20 are guards followed by member use on the same value, or a control with later disk/text asserts
  (for example `CrefSpikeTests.cs:115`, `CrefQualifierTests.cs:608`, `MoveMemberCollateralTripwireRouteTests.cs:159`).
- 7 are contract-null on `StateMessage()` / `CheckBreaker()` paired with `IsTripped` / `Open`
  (`OrientationBreakerTests.cs:39,85,106,111,128,138,142`).
- 5 are `Null.Or.Empty` on `PendingLedgerEntries` / `AppliedFixups` next to strong sibling asserts
  (`PreviewInstanceMoveCallSitesTests.cs:915,980,1066,1218,1219`).
- 4 are message-presence checks beside a status assert (`BatteryFourTests.cs:218,351,381,433`).
- The rest are contract or control cases (`PreviewInstanceMoveCallSitesTests.cs:1370` SuggestedFix null on a
  cref row, `UnrecoverableBreakerTests.cs:46,182`).

#### 2. Pattern categories

| Pattern | Count | Examples |
| --- | --- | --- |
| `Is.Not.Null` guard immediately used (ignore) | about 20 | `CrefSpikeTests.cs:115`, `MoveMemberCollateralTripwireRouteTests.cs:159` |
| Contract-null with a status companion (ignore) | about 12 | `OrientationBreakerTests.cs:39`, `UnrecoverableBreakerTests.cs:46` |
| Message only checked non-empty, status asserted | 3 | `BatteryFourTests.cs:218`, `:351`, `:433` |
| Success-only `!result.IsError` with no outcome check | 4 | `ScopedOperationLedgerBlockingTests.cs:71`, `:95`; `UnrecoverableBreakerTests.cs:129`; `PreviewInstanceMoveCallSitesTests.cs:767` |
| Negative-only or "does not throw" with no outcome check | 3 | `PreviewInstanceMoveCallSitesTests.cs:457-494`; `MakeStaticRewritesCallersTests.cs:88-101`; `MoveMemberPreservesUntouchedTextTests.cs:278-326` |

#### 3. Genuine false-pass candidates (read these first)

1. `PreviewInstanceMoveCallSitesTests.cs:457-494`, `MoveMemberAsync_FieldMapSatisfiesSourceFieldDependency_DoesNotRefuseAsync`.
   The `MoveMemberAsync` result is discarded. The only check is
   `catch (ToolInvalidArgumentException ex) when (ex.Message.Contains("private/internal")) { Assert.Fail(...) }`.
   The test passes whenever the call returns normally, even with an empty or wrong `Changes`.
2. `MakeStaticRewritesCallersTests.cs:88-101`, `AddStatic_DoesNotReportReceiverFieldThatIsStillRead`.
   The only assert is `result.Notes, Has.None.Contain("no longer read")`. It passes if the conversion did
   nothing at all (empty `Notes`).

Weaker versions of the same risk (would pass if the operation silently did the wrong thing):
3. `MoveMemberPreservesUntouchedTextTests.cs:278-326`: only `sourceNewText.EndsWith("\n"), Is.False`.
4. `ScopedOperationLedgerBlockingTests.cs:33-72`, `:74-96`; `UnrecoverableBreakerTests.cs:~108-130`:
   only `!result.IsError`. The in-test comments say the write "must actually reach a real file", but the
   file is never read back.

#### 4. Offenders table

`ApplyChangesResult`, `MoveMemberResult` and `StaticConversionResult` members are named in Context.

| # | file:line | Test | DTO | What to assert | Effort |
| --- | --- | --- | --- | --- | --- |
| 1 | `PreviewInstanceMoveCallSitesTests.cs:456-494` | `MoveMemberAsync_FieldMapSatisfiesSourceFieldDependency_DoesNotRefuseAsync` | `MoveMemberResult` (discarded) | Assign the result. Assert `Changes` has Source and Target keys, target text reads `_b` and has no `_a` (same shape as the passing sibling at `:496-550`). | moderate |
| 2 | `MoveMemberPreservesUntouchedTextTests.cs:278-326` | `StaticMethodToExistingClass_WithoutFinalNewline_PreservesMissing` | `MoveMemberResult` | Add `AssertOnlySpansChanged` for Source (member removed, cref retargeted), Target and Caller, as the sibling tests at `:25-160` do. | trivial |
| 3 | `MakeStaticRewritesCallersTests.cs:88-101` | `AddStatic_DoesNotReportReceiverFieldThatIsStillRead` | `StaticConversionResult` | `HandledIndexes` equals `{0}`; `Consumer.cs` text has `Helper.Normalize(x)` and still has `_helper.Length(x)`; keep the `Notes` assert as the negative. | trivial |
| 4 | `ScopedOperationLedgerBlockingTests.cs:33-72` (assert at `:71`) | `OpenLedgerEntry_AllowsWriteToItsOwnTrackedFileAsync` | `ApplyChangesResult` | `SucceededFiles` contains the file; `FailedFiles` empty; `NoOpFiles` does not contain it; disk text ends with the appended line. | trivial |
| 5 | `ScopedOperationLedgerBlockingTests.cs:74-96` (assert at `:95`) | `NoOpenLedger_WritesStillSucceedAsync` | `ApplyChangesResult` | Same as #4. | trivial |
| 6 | `UnrecoverableBreakerTests.cs:~108-130` (assert at `:129`) | `NotTripped_WritesStillSucceedAsync` | `ApplyChangesResult` | Same as #4. | trivial |
| 7 | `PreviewInstanceMoveCallSitesTests.cs:767`, `:799` (test at `:724-800`) | `MoveMemberAsync_UnresolvedCallSite_OpensLedgerThatBlocksUnrelatedFileAsync` | `ApplyChangesResult` | The setup apply (`:767`) is `!IsError` only; assert `SucceededFiles`. The "tracked file still writable" apply (`:799`) replaces `_f1.Foo` with itself, a no-op write; the ledger gate runs before no-op detection (`PersistentWorkspaceManager.cs:1592`) so a block would still fail it, so this is low value. | trivial (low) |
| 8 | `MakeStaticRewritesCallersTests.cs:174-185` | `AddStatic_NotAMethodOrAlreadyStatic_IsLeftToThePlainModifierPath` | `StaticConversionResult` | `HandledIndexes` and `Changes` are only `Is.Empty`. Add a `Notes` assert or a control showing the requests were parsed. | trivial (low) |

Thin but acceptable (at most a one-line strengthening each; not counted as weak-only):
- `BatteryFourTests.cs:218`, `:351`, `:433`: `Does.Contain` on `BlockingReason` / `Error` instead of non-empty.
- `OrientationBreakerTests.cs:106`, `:138`: assert the `CheckBreaker()` message instead of `Is.Not.Null`.
- `PreviewInstanceMoveCallSitesTests.cs:1287-1323`: only `Status == MoveOrderDependent`; could add `BlockReason`.
- `PreviewInstanceMoveCallSitesTests.cs:1560-1594`: a single `Does.Contain("KnownLimitTgtS.Foo(2)")`; the
  member's arrival in the target is not asserted.

#### 5. NotFound-style outcomes that let failures slip through

These are paths where a "not found / nothing to do / null / empty" result (or a null `Notes` / pending list)
is accepted as success or as an unremarkable outcome, so a failure can pass silently. Each is a question for
the owner: should the engine throw (as `MoveMember` does with `ToolInvalidArgumentException`) rather than
return a soft result? Items marked hypothesis were not traced into engine source.

A. Analysis engines return "unsafe + reason string" or "safe" for lookups that found nothing, instead of throwing.
- `BatteryFourTests.cs:185-197` (`AnalyzeForeach_FileNotFound...`) and `:199-222` (snippet not found): the
  engine returns `IsSafeToConvert=false` with a `BlockingReason`; the tests accept that. A caller that only
  reads one flag cannot tell "file missing" from "unsafe construct". `:218` and `:351` check only that
  `BlockingReason` is non-empty.
- `BatteryFourTests.cs:325-353`: same shape for `AnalyzeSwitchForPatternConversionAsync` (file not in workspace,
  snippet missing).
- `BatteryFourTests.cs:258-292` (`AnalyzeForeach_CollectionIsFieldNotLocal_ReturnsSafeWithNote`): the test
  comment records that "declIndex=-1" (declaration not found) returns `IsSafeToConvert=true` with a note. A
  not-found lookup is reported as SAFE. The test pins it (`:287-290` asserts `IsSafeToConvert` true and
  `CollectionVariableName == "_events"`). Highest-impact item in this section.
- `BatteryFourTests.cs:422-435` and `:437-481`: `ConvertSwitchToPatternSafeAsync` reports failure through
  `IsError` / `Error` text; the throw-only case (`:437`) is a documented analysis/conversion disagreement
  (`Analyze` says safe, `Convert` fails). The failure is only observable if the caller reads `IsError`.
  `:433` checks only a non-empty `Error`.

B. Soft refusal channel is inconsistent with the throw channel.
- The ledger refusal (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1592-1598`) returns
  `IsError: true` with `FailedFiles` and `Summary` but no `RefusalCode`, so `ApplyChangesResult.Code` falls
  back to `ToolErrorCode.Exception` (`ApplyChangesResult.cs`, `Code`). The test
  (`ScopedOperationLedgerBlockingTests.cs:98-147`, asserts at `:142-143`) must match `Summary` text
  ("scoped operation ledger"). Meanwhile the session-halt and unrecoverable-breaker paths throw
  `SessionHaltedException` (`PersistentWorkspaceManager.cs:1573-1577`; tested at `UnrecoverableBreakerTests.cs:~108-127`).
  Owner question: should the ledger refusal carry a typed `RefusalCode`, or throw like the halts?

C. Null or absent is treated as success.
- `PreviewInstanceMoveCallSitesTests.cs:915,980,1066,1218` use `PendingLedgerEntries, Is.Null.Or.Empty` and
  `:1219` uses `AppliedFixups, Is.Null.Or.Empty`. Null and empty are indistinguishable, so "ledger step never
  ran" passes as "no unresolved sites". Companion asserts (`Changes` text, `AppliedFixups` count) make these
  tests safe today; the exposure is the contract, not the tests.
- `PreviewInstanceMoveCallSitesTests.cs:~1640` (`InstanceMove_BareSameClassCall_WithSingleTargetField...`):
  `result.PendingLedgerEntries?.Any(...) ?? false, Is.False` treats a null list as "no NoCandidateIntroducible
  entry". The same test's `RewrittenCallSites` count of 1 guards it.
- `MoveMemberInstanceCrefTests.cs` tests at `:637-689`, `:691-735`, `:737-770`, `:772-812`, `:919-971` read
  `var notes = result.Notes ?? Array.Empty<string>();` and then assert `Does.Not.Contain(...)` or
  `Any(...) Is.False`. A null `Notes` (for example if note generation were skipped) passes the negative
  asserts; only the paired `Count(...) Is.EqualTo(1)` or `Is.EqualTo(...)` asserts in the other tests catch it.
  The two purely negative ones are `:637-689` (`BareCrefToMovedSibling...`) and `:919-971`
  (`NoBareCrefInMovedDoc...`), which still have full-text equality on `Target.cs` as protection.
- `MoveMemberInstanceCrefTests.cs:988-992` (`TextOrOriginal`) returns the ORIGINAL text when the key is absent
  from `Changes`. It is used by `NoCrefsAnywhere_...` (`:443-493`) to say "untouched file is identical". A
  file that should have been edited but was dropped from `Changes` would also pass that assert; the
  `Notes` equality assert in the same test is the only other guard.
- `MoveMemberPreservesUntouchedTextTests.cs:~415-418`: "caller must not be touched" is asserted by absence
  (`Changes.Keys.Any(... "Caller.cs") Is.False`). Intended for the base-type pull-up, but absence is also what
  a dropped edit looks like; the text equality asserts on Source and Base mitigate it.

D. Known limitations pinned as success (silent skip rather than refuse or warn).
- `CrefQualifierTests.cs:492-504`, `CrefSpikeTests.cs:108-161` and `:163-200`: bare crefs to inherited members
  do not bind; the engine counts them in `SkippedUnresolved`, leaves the text unedited, and the tests pin
  that as "observed". Whether the user is told is not asserted at the tool level (hypothesis: only the
  counter reports it).
- `PreviewInstanceMoveCallSitesTests.cs:1513-1555` (`KnownLimitation_InstanceMove_BareSameClassCall_IsNotRewrittenAsync`):
  the move is NOT refused; the source keeps `return Foo(2);` (does not compile, per the test's own comment at
  `:1507-1509`) and a pending ledger entry with `NoCandidateIntroducible` is returned. A compile of the result
  is not checked by the test.
- `MakeStaticRewritesCallersTests.cs:174-185`: ineligible targets (a field, an already-static method) yield an
  empty `HandledIndexes` and empty `Changes` with no `Notes` entry, so the caller is not told why a request
  was skipped (hypothesis: `Notes` stays empty; not traced into `ConvertMembersToStaticAsync`).

E. Swallowed exception path.
- `PreviewInstanceMoveCallSitesTests.cs:487-493`: the test catches `ToolInvalidArgumentException` filtered on
  message text "private/internal" and turns it into `Assert.Fail`. Any other `ToolInvalidArgumentException`
  message is rethrown (still a failure), so this is fine as a failure signal, but the test has no success
  assertion (see offender #1).

### Root cause

Not traced to a single cause. Observed patterns, each cited above:
- Tests that guard "should not refuse / should not throw" were written as negative checks without a positive
  outcome assert (offenders #1-#3).
- The "writes still succeed" tests were written as smoke tests of the gate and never read the file back
  (offenders #4-#6).
- Engines that return soft results (`IsSafeToConvert`, `IsError`, null/empty lists) force tests to choose
  between status asserts and message asserts; where the DTO has no typed code (`RefusalCode` null on the
  ledger refusal) tests fall back to text matching (section 5, B).

### Why it matters

- Offenders #1-#3 can stay green after a regression that makes the operation do nothing, so a refactor of
  the move/convert engines could ship unnoticed.
- Offenders #4-#6 guard the write chokepoint; `!IsError` would also hold for a skipped write.
- Section 5 items A and B are environment defects for weak agents: a tool result that says "safe" for a
  not-found lookup, or a refusal with no typed code, cannot be acted on mechanically.

### Verdict

The project is mostly clean. 171 test methods, 40 null asserts all guarded or contract-null, and the large
move/cref tests assert full edited-source equality and exact `Notes` arrays, which is strong. Only 8 tests
need a strengthening, 2 of them are real false-pass candidates, and all but one fix is trivial.

### Recommendation

Decided: none. Suggested, ranked by leverage (owner to decide):
1. Strengthen offenders #1 and #3 (false-pass candidates), then #2.
2. Add `SucceededFiles` and disk read-back to offenders #4-#6 (one shared helper would do).
3. Owner judgement on section 5: A (`IsSafeToConvert=true` for a not-found declaration, `BatteryFourTests.cs:258-292`)
   and B (typed `RefusalCode` or throw for the ledger refusal) have the best payoff; C-E are contract
   tightening that the current tests already mostly compensate for.

### Not checked

- `MoveMemberPreservesUntouchedTextTests.cs` lines 330-380 and 840-998 (fixture and helper code only).
- `MakeStaticRewritesCallersTests.cs` lines 1-52 (fixture helpers).
- Engine source behind the section 5 hypotheses (`ConvertMembersToStaticAsync`, the analysis engines'
  not-found paths, `MoveMemberAsync` note generation) was not read; those items rest on test code and
  in-test comments.
- No tests were built or run; pass/fail status was not confirmed.
- `Worktree/`, `bin/` and `obj/` folders were excluded.
- Other test projects were out of scope.

### Out of scope

Test style (naming, categories, fixture structure) and any non-assertion quality issue.

---

## RoslynSentinel.Tests

**RoslynSentinel.Tests assertion audit - project is mostly clean, one weak status assert and a few low-value no-throw / not-null asserts**

**Original status:** OPEN 2026-10-20. Audit complete and read-only; no fix decided - all items below are optional hardening, ranked by leverage.

### Context

Read-only audit of the `RoslynSentinel.Tests` project (shared/engine tests) for weak NUnit assertions: tests whose only meaningful assertion is `Is.Null` / `Is.Not.Null` / `Assert.IsNotNull` / `Is.Not.Empty` / `Count.GreaterThan(0)` / `Assert.Pass` / does-not-throw on a rich DTO or result, which can false-pass. Part of the 2026-10-20 comprehensive test review. Scope: `RoslynSentinel.Tests/**/*.cs` only (bin/obj and `*/Worktree/` excluded).

Method: `Search` (text mode) over the project for assertion patterns, then `ReadFile` on every test that carried a weak-pattern hit, then `Search` (symbol/text) to name the real members of the DTOs involved (`ApplyChangesResult`, `BlobWriteResult`). Totals were computed from the `Search` large-result JSON (per-method `enclosingMember`), not by hand. A shell `Select-String` attempt on `.cs` files was blocked by the dog-fooding hook and was not retried.

### What is broken

#### Project totals

- Test methods: 443 distinct (418 `[Test]` attributes plus `[TestCase]` rows on parameterized methods; 99 `[TestCase]` attributes). That is 517 executable cases across 34 test files.
- `Is.Null` / `Is.Not.Null` asserts: 63 (33 `Is.Not.Null`, 30 `Is.Null`) in 50 methods.
- Guarded by follow-up: 44 of those 50 methods (88%) also assert real values. All 33 `Is.Not.Null` asserts sit in methods that go on to assert on members or content.
- Methods with weak-only assertions: 8. Six are null-contract tests (null IS the contract) and two are does-not-throw tests. None is a rich-DTO status check.
- Every one of the 443 test methods has at least one direct `Assert.*` line; no `Assert.Pass`-only test exists.
- Other weak patterns are rare: `Is.Not.Empty` 11 occurrences and `GreaterThan(0)` 4 occurrences, all alongside stronger asserts or used as a precondition.

#### False-pass candidates (flagged first)

1. `RoslynSentinel.Tests/WorkspaceRefreshTests.cs:134` - `Refresh_LockIsReleasedBeforeMsBuildReload_GetCurrentSolutionAsyncDoesNotBlock` asserts only `result.WorkspaceInSync` is `Is.False` on `ApplyChangesResult`. `WorkspaceInSync` defaults to `false` (`RoslynSentinel.Common/ApplyChangesResult.cs:19-30`: `bool WorkspaceInSync = false`), so an apply that failed outright (`IsError == true`) would satisfy the assert. The real subject of the test is the lock-release timing assert that follows, so the impact is limited, but the status assert does not prove the apply succeeded structurally. Same record is used at `:46` and `:86` (`Is.True` there, which an error result would NOT satisfy, so those two are safe on that point; they just never look at `IsError` / `FailedFiles`).
2. `RoslynSentinel.Tests/SnippetSelectorTests.cs:443` - `Selectors_NeverThrowForCallerMistakes` runs 70 combinations (2 member modes x 5 snippets x 7 selector mistakes) through `Assert.DoesNotThrow(() => Resolve(...))` and checks no outcome. By design ("never throws") but it would pass whatever `SnippetMatchResult.Outcome` came back. The failure message names the combination, which is good.
3. No other test passes while the operation under test failed in a way that matters.

#### Pattern categories

| # | Category | Count | Verdict | Examples |
| --- | --- | --- | --- | --- |
| 1 | `Is.Not.Null` guard immediately followed by member/content asserts | about 33 | Not weak | `AppliedEditSpanTests.cs:48,66`; `HaltInfoTests.cs:73,86,97,107`; `WirePathRewriterTests.cs:275-506`; `WorkspaceRefreshTests.cs:52,92` (followed by `text == content`) |
| 2 | `Is.Null` / `Is.Not.Null` where null IS the contract, no richer status | 6 methods / 6 asserts | Not weak | `AppliedEditSpanTests.cs:19,27,35`; `DriftMessagesTests.cs:153`; `HaltInfoTests.cs:65`; `DiagnosticExtensionsTests.cs:62` |
| 3 | `Is.Null` as a negative pin beside strong asserts in the same method | about 24 | Not weak | `BuildResultProjectorTests.cs:87-89,109,110,159,193,212,213`; `WirePathRewriterTests.cs:399-466` (paired with `rewrites == 0`); `SnippetLookupTests.cs:116,157,185` |
| 4 | `Is.Not.Empty` / `GreaterThan(0)` as precondition or beside value asserts | 15 | Not weak | `SnippetErrorFormatterTests.cs:187,554,638`; `SnippetMatcherTests.cs:86` (inside `Assert.Multiple` with 6 other asserts), `:284`; `SnippetLookupTests.cs:133`; `SnippetSelectorTests.cs:402`; `WirePathRewriterTests.cs:276,373,481` |
| 5 | Does-not-throw only | 2 methods | Weak by design | `FilePathFromWireTests.cs:14-17`; `SnippetSelectorTests.cs:443` |
| 6 | Not-null on a looked-up value where a richer member exists | 4 asserts | Mildly weak | `OperationBlobWriterTests.cs:56,84` (`FindBlobPath(...)` not null); `OperationBlobWriterTests.cs:101,122` (`Diagnostic` `Not.Null.And.Not.Empty`) |
| 7 | Status flag that equals its default on failure | 1 | Weak | `WorkspaceRefreshTests.cs:134` (see false-pass list) |

#### Offenders table

There are fewer than 10 offenders, so no per-file rollup is needed. All are low severity.

| file:line | Test | DTO returned | Members / values that should be asserted | Effort |
| --- | --- | --- | --- | --- |
| `WorkspaceRefreshTests.cs:134` (also `:46`, `:86`) | `Refresh_LockIsReleasedBeforeMsBuildReload_GetCurrentSolutionAsyncDoesNotBlock`, and the two sibling `Refresh_*` tests | `ApplyChangesResult(IsError, SucceededFiles, FailedFiles, Summary, WorkspaceInSync, WorkspaceVersion, PreImages, ValidationResult, RolledBackFiles, NoOpFiles, RefusalCode)` | `IsError == false`; `FailedFiles` is empty; `SucceededFiles` contains the written path; `RefusalCode` is null. At `:134` this closes the gap where an error result also has `WorkspaceInSync == false`. | trivial |
| `SnippetSelectorTests.cs:443` | `Selectors_NeverThrowForCallerMistakes` | `SnippetMatchResult` (from the local `Resolve` helper) | `Outcome` is a defined, expected failure/valid value (for example not `Matched` for the mistaken selectors) rather than only "did not throw" | moderate |
| `FilePathFromWireTests.cs:14-17` | `FromWire_NullSolutionRoot_RelativePath_DoesNotThrow` | `FilePathWrapper` | `Absolute == "Test.cs"`. The sibling test at `:20` (`..._PreservesCallerPath`) already asserts exactly this, so this one is redundant rather than harmful. | trivial |
| `OperationBlobWriterTests.cs:56`, `:84` | `WriteAsync_SlashedToolName_StillProducesAFindableBlobAsync`, `WriteAsync_OtherInvalidFileNameChars_AreAlsoSanitizedAsync` | `BlobWriteResult(Written, FileName, Diagnostic, Required)` plus the `FindBlobPath` string | `result.FileName` non-null and contains the changeId (or `File.Exists` on the found path) instead of only "path is not null". `Written == true` is already asserted. | trivial |
| `OperationBlobWriterTests.cs:101`, `:122` | `WriteAsync_NoSolutionRoot_IsNotAnIntegrityFailureAsync`, `WriteAsync_SolutionRootPresentButUnwritable_IsAnIntegrityFailureAsync` | `BlobWriteResult` | Specific `Diagnostic` text rather than `Is.Not.Null.And.Not.Empty`. `Written`, `Required` and `IsIntegrityFailure` are already asserted, so the distinction that matters is covered. | trivial |
| `AppliedEditSpanTests.cs:19,27,35`; `HaltInfoTests.cs:65`; `DriftMessagesTests.cs:153`; `DiagnosticExtensionsTests.cs:62` | null-contract tests | `IReadOnlyList<AppliedEditSpan>?`, `HaltInfo?`, `string?`, `DiagnosticInfo.SourceLine` | Nothing needed. Null is the contract and the matching non-null cases are tested elsewhere in the same file. Listed for completeness. | none |

### Root cause

Not a systemic defect. The project tests pure helpers, formatters and message builders, where results are plain values (enums, strings, lists) that the tests compare exactly. The two weak spots come from (a) a test whose real subject is something else (lock timing, `WorkspaceRefreshTests.cs:134`) and (b) a "never throws" contract test that has no natural outcome value to compare (`SnippetSelectorTests.cs:443`). The `OperationBlobWriterTests.cs` not-null asserts follow from `FindBlobPath` returning `string?` as a lookup.

### NotFound-style outcomes that let failures slip through

Searched the project for `NotFound`, `TargetNotFound`, `NoMatch`, `DocumentLookupStatus`, `UpdatedText` and `ToolNotFoundException` (109 hits, 17 files). Result: no test in this project accepts a NotFound / null / no-match outcome as success where a real failure could pass silently. Where the outcome IS NotFound the tests assert it strongly (status enum plus exception type plus error code), for example:

- `ContextHelperTests.cs:29-30,174-175,349-350,612-613`: `result.Outcome == SnippetMatchOutcome.NoMatch` and `ex` is `ToolNotFoundException`.
- `DocumentLookupTests.cs:112-116,126,139`: `Status == DocumentLookupStatus.NotFound`, closest real paths named, `ToResultError().ErrorCode == ToolErrorCode.NotFound`, `GetDocumentOrThrow` throws `ToolNotFoundException`.
- `SnippetErrorFormatterTests.cs` `Build_EveryFailureOutcome_ReturnsItsExceptionTypeAndErrorCode`: pins each of 8 failure outcomes to an exception type and `ToolErrorCode`.
- `LoadSolutionPathSanitizationTests.cs:33,50,67,117`: `ThrowsAsync<ToolNotFoundException>` with message content asserted.

Engine paths that return null / "nothing" by design (silent no-op) and are pinned only as "returns null". The owner can judge whether any should throw or carry a status instead. These are observations of current contract, not defects; whether to change them is open:

1. `WirePathRewriter.RewriteJsonText` returns null (with `rewrites == 0`) for invalid JSON, a JSON array, an empty/null root and "nothing to rewrite" (`WirePathRewriterTests.cs:399,410,421,432,455,466`). Invalid JSON and non-object JSON are indistinguishable from "nothing to rewrite" at the call site. Hypothesis: harmless because the caller falls back to the original text, but not verified in this audit.
2. `AppliedEditSpan.FromEdits` returns null for null input, empty dictionary and a file with no changes (`AppliedEditSpanTests.cs:19,27,35`), so "no edits applied" and "nothing to report" look the same.
3. `OperationBlobWriter.FindBlobPath` returns null when no blob exists; the historical defect (a swallowed `DirectoryNotFoundException` turned into an unchecked return string, so issued changeIds could never be undone) is pinned by `OperationBlobWriterTests.cs:49-57`, and `BlobWriteResult.Required` / `IsIntegrityFailure` now separate "not owed" from "owed but absent" (`OperationBlobWriterTests.cs:101,122`).
4. `HaltInfo.From` returns null when nothing is tripped (`HaltInfoTests.cs:65`) and `DriftMessages.BuildHint` returns null for an empty collection (`DriftMessagesTests.cs:153`); both are optional-value contracts with a non-null counterpart tested.
5. `ApplyChangesResult.WorkspaceInSync` defaults to `false`, so "not in sync" and "apply failed" share a value (the false-pass at `WorkspaceRefreshTests.cs:134`). Not a NotFound outcome, but the same class of problem: a default/empty value that reads as a valid result.

### Why it matters

The suite is a reliable regression net for the helpers it covers. The one real gap (`WorkspaceRefreshTests.cs:134`) could let a broken apply path go unnoticed in a test whose purpose is something else. The does-not-throw tests are intentionally weak but add little signal. The null-contract tests are correct as written.

### Recommendation

Open - none of these is decided; ranked by leverage:

1. Add `IsError == false` (and `FailedFiles` empty) to the three `ApplyProposedChangesAsync` asserts in `WorkspaceRefreshTests.cs` (`:46`, `:86`, `:134`). Highest value, trivial.
2. Optionally assert an outcome in `SnippetSelectorTests.cs:443` (moderate) and drop or merge the redundant `FilePathFromWireTests.cs:14-17` (trivial).
3. Optionally strengthen `OperationBlobWriterTests.cs:56,84,101,122` with `FileName` / `Diagnostic` content (trivial).
4. Owner decision, not a test change: whether the null-by-design engine paths in the previous section should return a status or throw.

Overall verdict: the project is mostly clean.

### Out of scope

- Whether each strong assert is itself correct, and flaky or order-dependent tests.
- Tests that assert through a helper method: every test method showed a direct `Assert.*` line, so helper bodies (the shared `Resolve` / `Build` helpers in `SnippetSelectorTests` and `SnippetErrorFormatterTests`) were not traced beyond their use sites.
- Other test projects.
- Whether the engine callers of the null-returning APIs handle null correctly (hypothesis only; callers were not read).

### Not checked

- Helper bodies listed above and `Fakes/InMemoryWorkspace.cs` / `TestSolutionFixture.cs` internals.
- Runtime behaviour: no tests were run; this is a static read of assertions only.
- Callers of `WirePathRewriter.RewriteJsonText`, `AppliedEditSpan.FromEdits`, `OperationBlobWriter.FindBlobPath` in production code.

---

## RoslynSentinel.Tests.Server

**RoslynSentinel.Tests.Server has ~35 tests with weak, vacuous or dead assertions (false-pass risk)**

**Original status:** OPEN 2026-10-20. No fix decided; this is an audit for owner triage of which tests to strengthen.

### Context

Read-only audit of the NUnit project `RoslynSentinel.Tests.Server` (all `*.cs` under `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Server\`, excluding bin/obj and any `*/Worktree/` clone), part of the 2026-10-20 comprehensive test review. Goal: find tests whose only meaningful assertion is `Is.Null`/`Is.Not.Null` (also `Assert.IsNotNull`, `Is.Not.Empty`-only, `Count.GreaterThan(0)`-only, `Assert.Pass`, "does not throw" with no outcome check) or another weak/negative-only check on a rich result, so they can pass when the operation failed.

Method: text search for the null/empty/flag patterns, then every candidate test and most files were read in full via the RoslynSentinel MCP tools. DTO members were checked in `RoslynSentinel.Common/SentinelCallToolResult.cs` and `RoslynSentinel.Common/SourceTransformResult.cs`. No tests were run; everything below comes from reading source.

DTOs involved:
- Tool-level tests (`CodeTransformToolsOutcomeTests`): `SentinelCallToolResult<object>` -- members `IsError`, `Status` (`ResultStatus`), `Code`, `Message`, `SuccessData`, `ErrorData` (`ResultError.ErrorCode`/`Message`), `Findings`.
- `SourceTransformResult`: `UpdatedSource`, `WroteToFile`, `WorkspaceUpdated`, `filePath`.
- Wire-level tests: the MCP SDK `CallToolResult` (`IsError`, `Content`, `StructuredContent`) plus parsed JSON text.

### What is broken

#### Verdict

The project is mostly clean. It is infrastructure and wire-level tests (filters, schema, registry, startup parsing), and most are well asserted. No engine-DTO test relies on a null-only check. About 35 of ~239 test methods (~15%) have weak-only assertions: 29 live plus 6 dead. The problem is not the `Is.Null` pattern (only ~7 of 51 null asserts are weak) but negative-only text checks and nested `if`/`foreach` assertions that run zero times.

#### 1. Totals

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

#### 2. Pattern categories

| Pattern | Count | Examples |
| --- | --- | --- |
| Negative-only text check (`!IsPathResolutionError`, `Does.Not.Contain(...)`) with no `IsError` or content assert | 14 tests | `RelativePathInputAuditTests.cs:129-300` (11 tests), `McpToolsetControlTests.cs:482`, `TestCategoryToolSchemaTests.cs:133` |
| Assertions inside nested `if`/`foreach` that run zero times when the shape differs | 3 tests | `WirePathRelativizeFilterTests.cs:117,197,232` |
| Only `result.IsError` asserted, no content/outcome | ~8 tests | `CodeTransformToolsOutcomeTests.cs:172,182`; `OrientationBreakerFilterTests.cs:96`; `McpToolsetControlTests.cs:495,612` |
| `Is.TypeOf<SourceTransformResult>` with no member check | 2 tests | `CodeTransformToolsOutcomeTests.cs:161,264` |
| Dead tests (`Assert.Ignore` first line, `[Explicit]`) | 6 tests | `McpServerStatusStructuredContentTests.cs:61-66,78-83`; `StructuredContentDataTagTests.cs:99-103,128-132,164-168,181-185` |
| Redundant DI null asserts | 2 tests | `TestServiceProviderBuilderTests.cs:7-25,28-44` |

#### 3. Genuine false-passes (flagged first)

1. `RelativePathInputAuditTests.cs`, all 11 `*_AcceptsRelativePath` tests. The only assert is `Assert.That(!IsPathResolutionError(text), Is.True)` (helper at the file's `IsPathResolutionError`, matching "NotFound", "FileNotFound", "InvalidArgument", "InvalidPath", "not found"). Any other failure passes: solution not loaded, exception, build failure, unknown tool. `result` is captured but never asserted. Active mode is only `{"Workspace"}` (`RelativePathInputAuditTests.cs:30`).
   - HYPOTHESIS (not verified): `BasicModeToToolClasses["Workspace"]` (`RoslynSentinel.Server.Basic/ToolClassRegistry.cs:93`) lists WorkspaceTools, WorkspaceReadNavigationTools, DocumentationTools, SymbolNavigationTools, SymbolRelationshipTools, GitTools, GitRemoteTools -- not DeclarationTools, ParameterEditTools or RefactoringStructuralTools (which declares `Member`, `RoslynSentinel.Tools.Basic/RefactoringStructuralTools.cs:108`). So `Declaration_View` (:175), `ParameterEdit_View` (:192) and `Member_View` (:242) probably call tools that are not registered in that mode. Each then either passes vacuously or throws, depending on how the SDK client surfaces an unknown tool; that behaviour was not checked.
2. `WirePathRelativizeFilterTests.cs:197` `LargeResultAndSolutionPath_StayAbsolute`: after `IsError` is not true, everything is inside `if (offloaded) { if (largeResult) { if (filePath) ... } }`. If the response is not offloaded the test asserts nothing else.
3. `WirePathRelativizeFilterTests.cs:117` `Search_SymbolMode_ReturnsRelativeFilePaths_AndSolutionRoot`: the `filePath` relative-and-forward-slash loop sits inside `if (successData is JsonArray)` / `if (item is JsonObject && ContainsKey("filePath"))`, so it runs zero times for a non-array, empty array or items without `filePath`. The `solutionRoot` assert is real.
4. `WirePathRelativizeFilterTests.cs:232` `EnvVarOff_ReturnsAbsolutePaths_AndNoSolutionRoot`: same nested-if shape for the absolute-path loop. The `solutionRoot` absence assert is real.
5. `CodeTransformToolsOutcomeTests.cs:260-261` `GenerateConstructor_ClassWithFields_StillSucceedsWithTheGeneratedSummary`: the only content assert is `Assert.That(result.SuccessData as string, Does.Not.Contain("already in the requested state"))`. A null or non-string `SuccessData` yields null from `as string`, and `Does.Not.Contain` on a null actual passes (NUnit's substring constraint returns false for null, which the `Not` inverts -- from knowledge of NUnit behaviour, not run here). The name promises a generated summary that is never checked.
6. `McpToolsetControlTests.cs:482` `EnabledTool_IsCallable_WithoutRelisting`: only `Does.Not.Contain("Unable to resolve")` and `Does.Not.Contain("No service for type")`. Any other failure (including `IsError` true for another reason) passes.
7. `TestCategoryToolSchemaTests.cs:133` `Call_WithExactEnumName_PassesValidation`: `isError` is discarded and the only assert is `Does.Not.Contain("not a valid value for parameter 'framework'")`. An unrelated failure or empty text passes.
8. `SchemaLeanProfileTests.cs:220` `Lean_CallPassingHiddenReturnDiff_IsNotRejectedAsUnknown`: negative-only (`Does.Not.Contain("Unknown parameter")`). The in-test comment says no solution is loaded and any other outcome is acceptable, so this is by design; it would still pass on empty text.

#### 4. Offenders table

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

#### 5. NotFound-style outcomes that let failures slip through

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

### Root cause

Not traced to a single cause. Observed contributors, each evidenced above: (a) the audit and echo-style tests were written to check "not a path error" and never tightened to a positive outcome; (b) several wire tests parse JSON defensively with `if`/`as` so a changed response shape silently skips the assert; (c) status-only asserts were kept after the engine/tool outcome split (`CodeTransformToolsOutcomeTests`) because the "StaysASuccess" tests only needed to prove the outcome gate did not turn a no-op into an error; (d) the structured-content tests were parked with `Assert.Ignore` rather than removed. The RelativePathInputAudit wrong-mode concern is a hypothesis only.

### Why it matters

These tests can stay green while the behaviour they name is broken: path-relativizing filters, tool-set enable/disable, and the code-transform outcome mapping. The tool surface is aimed at weak models that depend on clear failures, so success-shaped "not found" outcomes (section 5) are the ones most likely to hide a wrong call from both the test suite and the agent.

### Recommendation

Not decided; ranked by leverage:
1. Fix the false-passes in section 3 (all trivial except RelativePathInputAudit, which is moderate and also needs a check of which tools are registered in `Workspace` mode).
2. Strengthen the status-only `CodeTransformToolsOutcomeTests` and the two `McpToolsetControl` error-only tests (section 4).
3. Delete or ticket the 6 dead structured-content tests.
4. Owner decision on section 5 items 1-3: whether the code-transform engines should throw a typed exception for method-not-found / ineligible targets instead of returning success-shaped or `UpdatedText`-carrying results.

### Out of scope

- Null asserts classified as guarded or contract (44 of 51) -- not weak.
- `ArchitectureDocFreshnessTests` `Assert.Ignore` calls (:98, :112) -- deliberate regenerate/update-mode skips, documented in the test.
- `NoActiveToolsStartupTests.SmokeResolve_DebugModeOnly...` `Assert.Ignore` (:194) -- DEBUG-only check, documented.
- Other projects.

### Not checked

- Setup sections of `WirePathRelativizeFilterTests`, `McpServerStatusStructuredContentTests` and `StructuredContentDataTagTests` fixtures.
- The rest of `MergedToolDescriptionSyncTests` (clause cases from about line 120 onward).
- `ArchitectureDocGenerator.cs` and `TestServiceProviderBuilder.cs` (helpers, not tests).
- The ~239 test count is from attribute-line counts, not a run; `TestCaseSource` expansions are not counted.
- How the MCP SDK client reports a call to an unregistered tool (needed to settle the RelativePathInputAudit wrong-mode hypothesis).
- Engine source for every claim in section 5; those come from test names and comments only.
- NUnit null-actual behaviour of `Does.Not.Contain` (false-pass 5) was not run.

---

## RoslynSentinel.Tests.Tools.Advanced

**RoslynSentinel.Tests.Tools.Advanced - about half the tests assert only "a result exists"**

**Original status:** OPEN 2026-10-20. Strengthening is proposed, not started; slice order and the throw-vs-NotFound question are undecided.

### Context
Part of the 2026-10-20 comprehensive review of weak `Is.Null` / `Is.Not.Null` style assertions across the test projects. A read-only explore-sonnet agent read every file in `RoslynSentinel.Tests.Tools.Advanced` and inspected the returned DTOs. Counts are regex-based estimates (+/- 10%). No tests were run; claims about return shapes come from reading source. `BatteryTwentyFourTests.cs` carries another session's uncommitted edits, so its line numbers may drift. Line numbers are approximate (~L).

### What is broken

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

#### Genuine false-passes (pass when the operation failed or did nothing)
1. **No assertion at all** (`BatteryTwentyFourTests.cs`): `UpdateXmlDocsFromSignature_ValidMethod_ReturnsString` (~L1790), `ConvertExpressionBody_ToBlockBody_ReturnsString` (~L1800), `ExtractConstant_WithLiteralSnippet_ReturnsString` (~L1810).
2. **Asserts text already in the input** (`AdvancedToolsTests.cs`): `SyncInterfaceToImplementation_AddsMissingMethod` (~L208-212) checks `void DoB()` / `void DoA()`, both already in the source class; `SyncInterfaceToImplementation_AddsMissingProperty` (~L246-252) checks `Name` and `get;`, also already present. Both pass with the interface unchanged.
3. **Tool returns a human error string that satisfies `Is.Not.Null.And.Not.Empty`** (`BatteryNineteenTests.cs`): `GenerateHttpClient_ValidController_ReturnsCode` (L111), `GenerateDefaultConfigJson_ValidProject_ReturnsJson` (~L198; the POCO has no config keys so the tool probably returns "no configuration keys found"), `InterpolateStringSafe_WithFormatCall_ReturnsCode` (~L310), `GenerateClassesFromJson_*` (L92, L101; `Is.Not.Null` on `object`).
4. **Engine returns `Outcome=Modified` with `UpdatedText` even when nothing changed.** Verified for `CodeStyleEngine.UseIndexFromEndAsync` (`Engines.Advanced/CodeStyleEngine.cs:385-428`); other engines are unverified (hypothesis). ~20 `X_ValidFile_ReturnsSource` tests in `BatteryTwentyOneTests.cs` assert only `UpdatedText Is.Not.Null.And.Not.Empty`; several use `RichSource` with no matching pattern (UseIndexFromEnd, UseTimeProvider, SimplifyMemberAccess, CleanupImplicitSpans, UpgradePatternMatching). `BatteryTwentyThreeTests.cs` throw-guard tests (~L143 on) have the same shape: AddGuardClauses, AddBenchmarkStub, AddConfigureAwaitFalse, RemoveConfigureAwaitFalse, ConvertLockToSemaphoreSlim, ConvertToAsyncEnumerable, AddCancellationTokenToMethod, MakeMethodThreadSafe.
5. **Inverted "NonExistentFile" tests** named `_Throws` / `_ReturnsNull` / `_ReturnsNotNull` that assert `Is.Not.Null` on a never-null `DocumentEditResult`: `BatteryNineteenTests.cs` `ImplementInterfaceSafe_NonExistentFile_ReturnsNull` (~L270); `BatteryTwentyOneTests.cs` `OptimizeIndependentAwaits_NonExistentFile_ReturnsNull`, `UpgradeToPrimaryConstructor_NonExistentFile_ReturnsNull`; six `BatteryTwentyThreeTests.cs` tests (AddGuardClauses, AddBenchmarkStub, ConvertLockToSemaphoreSlim, ConvertToAsyncEnumerable, AddCancellationTokenToMethod, MakeMethodThreadSafe). ~20 siblings in `BatteryTwentyOneTests.cs` (L~145-575) use only `Assert.DoesNotThrowAsync`; the engine returns `Outcome=DocumentNotFound`, Message `"// Document not found."`, and nothing checks it. `BatteryNineteenTests.cs` `*_NonExistentFile_Throws` tests (GenerateConstructor L139, AddValidationToPoco, ConvertPropertySafe) assert `UpdatedText Is.Null.Or.Empty` with no Outcome check.
6. **Tool-level `_AutoStageTrue_ReturnsNotNull` / `_ReturnsString` / `_ReturnsDictionary`** tests in `BatteryTwentyFourTests.cs` assert only `result Is.Not.Null` on a `SentinelCallToolResult` or `DocumentEditResult`; a refused or failed call is still non-null. Examples: ExtractSuperclass (L167), SafeDeleteSymbol (L177), ChangeSignature (L188), MoveTypeToFile (L208), ModifyEnum (L304), ~40 more (table below).
7. **`BlobIntegrityInvariantTests.cs:73-85` `AssertChangeIdIsRedeemable`** returns early when no `ChangeId` was issued, so all 5 tests (WrapRange, MoveType, ExtractMembers, SyncInterface, Inline) pass when the tool failed outright. The XML doc says this is deliberate; rated low and judgment-dependent.
8. **Conditional assertion:** `SyncTypeAndFilename_DryRun_NeverDeletesOriginalFileAsync` (`BatteryTwentyFourTests.cs` ~L1190) checks `DryRun` only inside `if (!result.IsError)`. The comment says intentional; the file-exists check is unconditional.
9. **Status-only checks** in `BatteryTwentyTwoTests.cs`: ~40 tests assert `Is.Not.Null` on lists or tool results, several with the real assertion commented out (`//Assert.That(result.Count > 0);`). `ComprehensiveToolTests.cs` `ClassToRecord_ShouldReturnString` (L201) asserts only `result Is.Not.Null`.

#### Pattern categories
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

#### Offenders table
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

### NotFound-style outcomes that let failures slip through
1. **Engines return a not-found outcome and tests prove only "no exception".** `CodeStyleEngine.cs:385-428` returns `Outcome=DocumentNotFound`, Message `"// Document not found."` for a missing file. ~20 `*_NonExistentFile_ReturnsNullOrEmpty` tests in `BatteryTwentyOneTests.cs` (L~145-575) discard the result inside `Assert.DoesNotThrowAsync`, so they pass whether the engine reports not-found or silently does nothing.
2. **Inverted tests:** names say `ReturnsNull` / `Throws` but assert non-null on a never-null `DocumentEditResult` (list in false-pass 5). The names imply the contract is "throw" or "null"; the engine returns a not-found outcome, so names and engine disagree.
3. **Null `UpdatedText` accepted as the not-found signal:** `BatteryNineteenTests.cs` L139, AddValidationToPoco, ConvertPropertySafe (`Is.Null.Or.Empty`); `BatteryThirtyOneTests.cs` `ConvertMethodToIndexer_UnknownFile_ReturnsEmpty` (`Is.Null`). A real failure, empty result or no-op also satisfies these. Contrast: `GenerateAsyncOverload_NonExistentFile_Throws` uses `ThrowsAsync<ToolNotFoundException>`, so the Generation tool can already throw; the engine-vs-tool contract looks inconsistent (hypothesis from names and assertions only).
4. **Tool-level not-found returned as a human string** (`Tools.Advanced/GenerationTools.cs:78-240`: "file not found", "controller class ... not found") that satisfies `Is.Not.Null.And.Not.Empty` (`BatteryNineteenTests.cs` L111, L120, ~L310, ~L198 on). Whether these should return `ResultError` with `ErrorCode.NotFound` is open.
5. **`DecoratorResult?` null when the interface is not found** (`GenerateDecoratorClass_NonExistentInterface_Throws` asserts `Is.Null`). Acceptable as a contract, but the test name says `Throws` and null cannot distinguish "not found" from "generation failed".
6. **Not-found is the only refusal signal:** `BatteryTwentyFourTests.cs` `RenameSymbol_NonExistentSymbol` (L248, `ErrorData Is.Not.Null` only) and the ConvertToNullCoalescing/Switch/Pattern `NonExistentFile_Throws` variants (L1872, 1891, 1910). Strong templates already exist: `BatteryTwentyOneTests.cs` `InvertBooleanLogic_MissingFile` / `_UnknownBoolName` (`ErrorCode==NotFound`) and `BatteryTwentyThreeTests.cs` `ConvertLockToSemaphoreSlim_NonExistentFile_ReportsNotFoundCode`.
7. **`AssertChangeIdIsRedeemable` early return** (`BlobIntegrityInvariantTests.cs:73-85`): no `ChangeId` means all 5 tests pass vacuously.
8. **"Does not flag" tests pass when analysis found nothing** (`AsyncThreadingToolsTests.cs` L52, 60, 97, 116, 135, 165, 202; `AdvancedToolsTests.cs` ~L82): a missing file or no-op analysis also yields `Is.Empty`.
9. **Engines returning `Outcome=Modified` regardless of change** (verified only for `UseIndexFromEndAsync`): a "no match" is never reported as `AlreadyInTargetState` / `NoChange`, so the `X_ValidFile_ReturnsSource` tests cannot detect it. Candidate for the engine owner.

### Root cause
Not traced beyond the above. The common mechanism is that engines and tools return a never-null DTO whose failure signal lives in `Outcome` / `Message` / nullable `UpdatedText`, and the tests were written to the "does not throw, result exists" level. Only `CodeStyleEngine.UseIndexFromEndAsync`, the Generation tools and a few engines were read to confirm return shapes.

### Why it matters
~52% of this project's tests cannot detect a regression in the operation they name. Several would stay green if the tool refused every call or the engine did nothing. Weak models rely on these tools; a silent regression here is invisible until a model-eval fails.

### Recommendation
Ranked by leverage (slices run one at a time, build and baseline check after each):
1. Fix the false-passes listed first (zero-assert tests, text-already-in-input, vacuous early returns, inverted-name tests).
2. Valid-file smoke tests: assert `Outcome` plus the transformed text; trace each engine's real outcome first (expected values here were inferred from source, not run).
3. NonExistentFile tests: assert `Outcome==DocumentNotFound` and message, or `ThrowsAsync` if the engines move to throwing. Decide that question first to avoid rewriting these twice.
4. Detector tests: assert the planted finding by name/line; pair every "does not flag" test with a positive case.
5. Decision open: whether engines should throw typed exceptions for caller-precondition failures (see the throw-vs-NotFound discussion in the session) and whether `Outcome=Modified` should be returned on a no-op.

### Out of scope
Helper and fixture files; other test projects; running the tests to confirm which weak tests currently pass.

---

## RoslynSentinel.Tests.Tools.Basic

**RoslynSentinel.Tests.Tools.Basic has about 65 weak-assertion tests, 6 of them genuine false-passes**

**Original status:** OPEN 2026-10-20. Audit complete; no fix decided, strengthening is the owner's call.

### Context

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

### What is broken

#### 1. Genuine false-passes (the assertion passes even when the operation failed)

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

#### 2. Pattern categories

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

#### 3. Offenders table

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

### NotFound-style outcomes that let failures slip through

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

### Root cause

Two causes, both in the test layer plus one design inconsistency:

- Legacy "battery" tests were written as smoke tests ("returns a result without throwing") when the engines still
  threw; the contract later changed to return result DTOs, and the assertions were not strengthened.
- The augment engine has no single not-found contract (items 1-2 above), so a test author has no one code or field to
  assert, and `Is.Not.Null` was the only thing common to all paths. Not traced further: git history of when the
  assertions were last touched.

### Why it matters

A refactor that makes an engine or tool fail (or ignore `contextSnippet`) keeps these tests green, so the suite gives
false confidence for exactly the tools agents use to edit code. The inconsistent not-found contract also means agents
receive `ErrorCode == Exception` for conditions that are really `NotFound`, which weakens the structured-error design.

### Recommendation

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

### Out of scope

- Other test projects; the engines' own behaviour beyond what the weak tests touch.
- Tests that assert only `!IsError` / `Is.True` (outside the brief's patterns).

### Not checked

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
