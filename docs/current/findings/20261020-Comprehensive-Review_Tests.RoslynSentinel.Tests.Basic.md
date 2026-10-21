# Finding: RoslynSentinel.Tests.Basic - mostly clean, with three false-passes and ~20 tests worth strengthening

**Status:** OPEN 2026-10-20. Strengthening is proposed, not started; the throw-vs-NotFound question is undecided.

## Context
Part of the 2026-10-20 comprehensive review of weak `Is.Null` / `Is.Not.Null` style assertions. A read-only explore-sonnet agent scanned every assertion-like line with `Search` (text mode), grouped them by enclosing method, then read the suspect methods. "Weak-only" means a test's only assertions are `Is.Null`, `Is.Not.Null`, `Is.Empty`, `Is.Not.Empty`, `Count>0` or `Assert.Pass`. Tests with no assertion or other weak shapes (e.g. `Is.True`-only) are listed separately. No tests were run. Line numbers come from search hits; `isServerBinaryStale` flipped mid-run but the reads were unaffected.

## What is broken

Totals:
- ~655 test methods (601 `[Test]` + 207 `[TestCase]` attributes, many methods carry several, + 7 `[TestCaseSource]`).
- 38 weak-only tests (~6%), plus 1 more whose only other assertions are `Is.True` on a computed flag (`SemanticReplaceEngineTests.cs:1157`). 156 other tests contain a null/empty assertion but also assert real values.
- 241 `Is.Null` / `Is.Not.Null` asserts by per-method count (153 `Is.Null`, 87 `Is.Not.Null`); a whole-file pass counted 243 (2 lines not reconciled). 192 (80%) sit in tests that also assert something real; 49 (20%) are in weak-only tests. Of the 87 `Is.Not.Null`, 73 are guarded and 14 are not.
- `Assert.Pass`: 1 real (`UnitTest1.cs:13`); 3 other matches are inside fixture-source strings in `TestCategoryTaggingEngineTests.cs`. `Count>0` as key assertion: 6. `DoesNotThrow`: 0 real. 3 tests with no `Assert` line (`AttributeTextEditBuilderRenderTests.cs`) are fine - the `AssertCommentsRejected` helper checks type, exact message and ErrorCode.

### Genuine false-passes (flagged first)
1. **`MoveTypeToFileTests.cs:184-189`** `MoveTypeToFile_ReturnsNonEmptyContent_ForBothFiles`: the only assertions are inside `foreach (var (path, content) in changes)`. An empty dictionary passes with zero assertions; a file that was not split passes too. Fix: assert `changes.Count == 2`, the new and source keys, and that content contains `class WidgetB` and `class WidgetA`.
2. **`MemberRefactoringTests.cs:2464-2470`** `BUG_60_RemoveMember_ChecksUsagesBeforeRemoving`: the only real check is inside `if (!Message.Contains("error") && !Message.Contains("Error"))`. The engine returns `Outcome = CannotRemove` with `"// ERROR: Cannot remove member ... usages"` (`MemberRefactoringEngine.cs:3337-3339`). The test passes whether or not removal was refused, and also if the member was removed and the message merely mentions `GetName`. Fix: assert `Outcome == CannotRemove` and `Message` contains `usages`.
3. **`MemberRefactoringTests.cs:2493`** `BUG_60_RemoveMember_ErrorsWhenMemberIsUsed`: asserts `Message` contains "ERROR" or "usages" but not `Outcome == CannotRemove`.
4. Weaker "work done not checked" risks: `WriteChokepointGuardrailTests.cs:102` (only `outcome.Error` is null; siblings also assert text, `LineChanges`, `ErrorCode`) and `ExtractLocalVariableTests.cs:259` (only `UpdatedText` null; already commented as flaky).

### Pattern categories
| Pattern | Count | Examples |
| --- | --- | --- |
| Edit result: only `UpdatedText` null/empty or non-empty, no `Outcome`/`Message`/`ErrorCode` | 7 | `MemberRefactoringTests.cs:817`, `856`, `2407`, `2421`, `259` (ExtractLocalVariable) |
| Engine DTO or symbol only checked non-null / non-empty | ~10 | `SemanticReplaceEngineTests.cs:51-52`, `153-157`, `1157-1160` |
| Real null-return contract (`TryGet...`, `Check` returns no violation, `StoreChanged...` returns null) | ~20 | `CollateralChangeGuardTests.cs:26-165`, `BasicRefactoringTests.cs:121`, `ChangedContentOffloadTests.cs:71` |
| `reason` / `Message` text only asserted non-null/non-empty | 13 | `ReferenceRoleClassifierTests.cs:142-304` (11 tests), `ResultStatusTests.cs:558`, `564` |
| Error-is-null only, no check that the work happened | 2 | `WriteChokepointGuardrailTests.cs:102`, `SemanticReplaceEngineTests.cs:1157` |
| Placeholder | 1 | `UnitTest1.cs:13` |

### Offenders table
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

## NotFound-style outcomes that let failures slip through
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

## Root cause
Not traced beyond the file:line references above. The common mechanism: edit results carry failure in `Outcome`/`Message` beside a nullable `UpdatedText`, and several tests assert only the nullable field or sit behind conditionals.

## Why it matters
Most of the 655 tests assert exact text, status enums, ErrorCodes and spans, and `ResultStatusTests`, `SemanticReplaceEngineTests` and `CollateralChangeGuardTests` are strong. The three false-passes can stay green while the operation is refused or does nothing.

## Recommendation
1. Fix the three false-passes (rows above: `MoveTypeToFileTests.cs:184-189`, `MemberRefactoringTests.cs:2464-2470`, `:2493`) and the two "work done" gaps.
2. Strengthen the `MemberRefactoringTests.cs` outcome/message cluster, `ReferenceRoleClassifierTests.cs` reason checks, `SemanticReplaceEngineTests.cs` declaration/site tests, `DependencyEngineFixtureTests.cs`, `DiagnosticEngineTests.cs:46`; delete `UnitTest1.cs`.
3. Open decision: whether the NotFound/CannotRemove outcomes and the `// ERROR:` message prefix should become typed exceptions; decide before rewriting rows 1, 2, 5 so they are not rewritten twice.

## Out of scope
Other weak shapes (`Does.Contain`-only, strong-but-wrong asserts); helper-only assertions (only tests flagged weak-only or conditional were traced; 652 methods with direct Assert lines were scanned); not inspected beyond the scan: `BasicRefactoringTests`, `InlineVariableTests`, `ExtractConstantTests`, `DiffEngine*`, `RoslynFormattingHelper*`, `*NormalizeCharacterization*` tests, `MoveBindCheckTests`, `NamedArgumentsEngineTests`; `MoveTypeToFileTests`/`MoveMemberPreservesUntouchedTextTests` fixture/helper bodies.
