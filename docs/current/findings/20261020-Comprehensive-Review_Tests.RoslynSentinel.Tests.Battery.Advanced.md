# Finding: RoslynSentinel.Tests.Battery.Advanced has about 75-80 weak-assertion tests, concentrated in BatteryTwentyFiveTests.cs

**Status:** OPEN 2026-10-20. Audit complete (read-only); strengthening not yet scheduled, no fix decided.

All paths below are relative to `RoslynSentinel.Tests.Battery.Advanced/` unless stated. Counts are
regex-based estimates. No tests were run; engine behaviour is inferred from source reads.

## Context

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

## What is broken

### Verdict

Mostly clean except `BatteryTwentyFiveTests.cs` ("Gap Coverage" smoke tests, 57 tests, about 45 can
false-pass). Outside it there are about 30 isolated weak tests in 14 files, plus a few tests that can never
fail. The common root cause is that unknown-file and no-op tests assert `UpdatedText Is.Null` or
`Not.Empty` instead of `Outcome`: only 17 places in 6 files (Fourteen, Nine, Seventeen, Seven, Ten,
Twelve) assert an `Outcome`, `ErrorCode` or `IsError` in the whole project.

### 1. Project totals (estimates)

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

### 2. Pattern categories

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

### 3. Genuine false-passes (flagged first; these cannot fail on a real regression)

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

### 4. Offenders table (grouped by file)

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

### 5. Guarded or clean (not counted as weak)

- Clean (strong): `BatteryTwentySevenTests.cs` (45 asserts, all content-based), `BatteryThirtyNineTests.cs`
  (`Kind`/`Risk`, counts, `CyclePath`), `BatteryTenTests.cs` apart from line 224-226 (`ValidateChanges`
  checks `IsError` plus `Diagnostics`; security tests check `IssueType`).
- Guarded: BatteryFive:56, 119; BatteryNine:65, 103, 198, 272; BatteryThirteen:45, 64, 176; BatterySix:234;
  BatteryEight:129, 323 (`Not.Null.And.Not.Empty` followed by `Does.Contain`); Eleven:138, 168, 202; Fourteen:193;
  Fifteen:124; Seven:90, 238, 242, 269, 294; TwentySix:76, 94, 131 (filtered by `Pattern`), 418, 454, 734, 765,
  778; ThirtyEight:115 (found-case lookup is the contract), 302; ThirtyFive:140, 144; TwentyFive:214, 425 and
  the List tests followed by `Is.Empty`.

## Root cause

Not traced to a single source. Hypotheses (unverified):
- `BatteryTwentyFiveTests.cs` was written as a coverage smoke file ("does the engine not crash"), so
  the Not.Null assertion was the intended bar. This is consistent with test names such as `*_ReturnsNonNull`
  and the "stub" comment at line 335, but the authoring intent was not confirmed from history.
- Most engine results (`DocumentEditResult`) are never null, so `Is.Not.Null` on them carries no information;
  the richer `Outcome` member is simply not used by most tests (17 uses in the project).

## NotFound-style outcomes that let failures slip through

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

## Why it matters

The project is the battery that is meant to show each Advanced engine does something useful. With `Not.Null`
on a never-null record, a stubbed or silently failing engine stays green, so a regression in an engine (or a
broken workspace/file resolver that turns everything into NotFound) is invisible. Weaker models that rely on
these results get no signal, and the environment (tests) fails to communicate which outcome is expected.

## Recommendation

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

## Out of scope

- Other test projects (audited separately in the multi-project review).
- Strong tests and guarded null checks (listed in section 5).

## Not checked

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