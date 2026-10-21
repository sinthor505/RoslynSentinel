# Finding: RoslynSentinel.Tests.Asyncify is mostly clean, but 4 tests assert almost nothing and 4 more can pass when the operation did nothing

**Status:** OPEN 2026-10-20. Audit only; no fix decided and nothing changed in the test project.

## Context
Read-only weak-assertion audit of `RoslynSentinel.Tests.Asyncify` (6 files, all read in full) on 2026-10-20.
Goal: find tests whose only meaningful assertion is `Is.Null` / `Is.Not.Null`, `Is.Not.Empty`,
`GreaterThan(0)`, `Assert.Pass` or "does not throw" on a rich DTO (engine result, tool result,
`DocumentEditResult`, `BatchResultSummary`, `AsyncMigrationProgressReport`, ...), which can false-pass.
Method: MCP `ReadFile` / `Search` / `GetLargeResult` on every `.cs` file, plus DTO inspection of
`BatchResultSummary` (`RoslynSentinel.Common/BatchTypes.cs:66`), `AsyncMigrationProgressReport`
(`RoslynSentinel.Common/AsyncMigrationProgressReport.cs:14`) and `DocumentEditResult`
(`RoslynSentinel.Common/DocumentEditResult.cs:22`). No test was run. All paths below are relative to
`RoslynSentinel.Tests.Asyncify/` unless stated.

## What is broken

### 1. Totals
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

### 2. Possible false-passes (highest priority)
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

### 3. Pattern categories (counts)
| Pattern | Count | Verdict | Examples |
| --- | --- | --- | --- |
| Guard `Is.Not.Null` on `SuccessData` / `ErrorData` / list, followed by member asserts | ~47 | fine | `SentinelAsyncifyToolsTests.cs:154`, `MigrationScanResultTests.cs:460` |
| `ErrorData Is.Not.Null` before `ErrorCode == ...` | 9 (inside the ~47) | fine | `SentinelAsyncifyToolsTests.cs:154,235,274,449,463`; `MigrationScanResultTests.cs:460,475,497,726` |
| Vacuous `Is.Not.Null` on a locally built `Wrap<T>()` envelope (never null) | 10 | dead line, strong asserts follow | `MigrationScanResultTests.cs:234,282,311,337,458,527,564,689,792,844` |
| Contract nulls (`LargeResult` null when inline, `TopCandidates` null without topN, `ErrorData` null on success, router `hint == null`) | ~9 | fine | `MigrationScanResultTests.cs:285,313,566,806`; `OperationOutcomeRoutingTests.cs:190` |
| Tautological thresholds | 6 | weak | `SentinelAsyncifyToolsTests.cs:194,203,259,369`; `MigrationScanResultTests.cs:537,611` |
| Negative-only or unchecked-index asserts | 3 | false-pass risk | `FlagMigrationCandidateTests.cs:219`; `ConvertToAsyncBridgeTests.cs:257`; `OperationOutcomeRoutingTests.cs:253` |
| Echo / tautology (asserts what the test assigned) | 2 tests | false-pass risk | `OperationOutcomeRoutingTests.cs:81-103`, `:144-171` |

### 4. Offenders table
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

### 5. Overall verdict
Mostly clean. Strong: `AsyncBatchEngineFlagOutcomeTests` (every test pins message text, counts, disk state or
the logged warning), the byte-exact tests (`ConvertToAsyncBridge_ByteExact`, `T21_UpliftCallers_ByteExact`,
`FlagCandidatesInProject_ByteExact`), the `Throws` tests, and `FindMigrationCandidates_*`. Weak: the
progress-report tests (T6, T13, T14), T11, and the operation-outcome directive and routing tests.

### 6. NotFound-style outcomes that let failures slip through
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

## Root cause
Not traced beyond the test code. The pattern is test-authoring convenience (status-only or "no exception" checks, `>= 0`
placeholders, negative-only text checks), not a product defect. Section 6 items 1, 3 and 4 are the only ones that point
at engine or tool design rather than test strength.

## Why it matters
A weak test lets the tool surface regress silently: the progress report, dry-run batch summaries, directive wording and
flag-attribute emission can break while these tests stay green. The shared `InvalidOperationException` contract also
means the engine cannot tell a caller (or a test) which precondition failed without parsing the message.

## Recommendation
Open items; none decided.
1. Fix false-passes 1-5 first (small, local test edits).
2. Strengthen T6, T11, T13, T14 with the `AsyncMigrationProgressReport` and `BatchResultSummary` members in the table.
3. Tighten T3, T10, T16, T21 (count and content instead of `>=` / `.All` on possibly empty lists).
4. Owner decision: distinct error code or exception type for NotFound-style engine failures (section 6, item 1), and
   whether a batch with `Succeeded == 0 && Failed > 0` should flag `IsError` (item 3).

## Out of scope
- Other test projects.
- Did not run any test, so current pass/fail state is unconfirmed.
- Engine behaviour behind suggested values (T11 dry-run `Succeeded == 1`, T10 `Count == 4`) is taken from the test inputs
  and comments, not verified.
- Member lists of `MigrationScanSummary`, `MigrationCandidateFinding` and `FlagCandidateTarget` were not inspected beyond
  what the tests use.
- Line numbers marked `~` and those for T13, T14, T16 are approximate (from read ranges, not search hits).
- `ConvertToAsyncBridgeTests.cs` lines 657-681 (the lambda test) were not read separately; it mirrors the delegate test
  above it.

## Not checked
- Test execution results and the known-failing baseline.
- Engine source for `AsyncOptimizationEngine`, `AsyncBatchEngine` and `AsyncifyTools` (only DTO shapes and tool signatures
  were inspected).
- Whether `TestSolutionBuilder` or `PersistentWorkspaceManager` behaviour could itself mask failures.