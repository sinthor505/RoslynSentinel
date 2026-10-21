# Finding: RoslynSentinel.Tests assertion audit - project is mostly clean, one weak status assert and a few low-value no-throw / not-null asserts

**Status:** OPEN 2026-10-20. Audit complete and read-only; no fix decided - all items below are optional hardening, ranked by leverage.

## Context

Read-only audit of the `RoslynSentinel.Tests` project (shared/engine tests) for weak NUnit assertions: tests whose only meaningful assertion is `Is.Null` / `Is.Not.Null` / `Assert.IsNotNull` / `Is.Not.Empty` / `Count.GreaterThan(0)` / `Assert.Pass` / does-not-throw on a rich DTO or result, which can false-pass. Part of the 2026-10-20 comprehensive test review. Scope: `RoslynSentinel.Tests/**/*.cs` only (bin/obj and `*/Worktree/` excluded).

Method: `Search` (text mode) over the project for assertion patterns, then `ReadFile` on every test that carried a weak-pattern hit, then `Search` (symbol/text) to name the real members of the DTOs involved (`ApplyChangesResult`, `BlobWriteResult`). Totals were computed from the `Search` large-result JSON (per-method `enclosingMember`), not by hand. A shell `Select-String` attempt on `.cs` files was blocked by the dog-fooding hook and was not retried.

## What is broken

### Project totals

- Test methods: 443 distinct (418 `[Test]` attributes plus `[TestCase]` rows on parameterized methods; 99 `[TestCase]` attributes). That is 517 executable cases across 34 test files.
- `Is.Null` / `Is.Not.Null` asserts: 63 (33 `Is.Not.Null`, 30 `Is.Null`) in 50 methods.
- Guarded by follow-up: 44 of those 50 methods (88%) also assert real values. All 33 `Is.Not.Null` asserts sit in methods that go on to assert on members or content.
- Methods with weak-only assertions: 8. Six are null-contract tests (null IS the contract) and two are does-not-throw tests. None is a rich-DTO status check.
- Every one of the 443 test methods has at least one direct `Assert.*` line; no `Assert.Pass`-only test exists.
- Other weak patterns are rare: `Is.Not.Empty` 11 occurrences and `GreaterThan(0)` 4 occurrences, all alongside stronger asserts or used as a precondition.

### False-pass candidates (flagged first)

1. `RoslynSentinel.Tests/WorkspaceRefreshTests.cs:134` - `Refresh_LockIsReleasedBeforeMsBuildReload_GetCurrentSolutionAsyncDoesNotBlock` asserts only `result.WorkspaceInSync` is `Is.False` on `ApplyChangesResult`. `WorkspaceInSync` defaults to `false` (`RoslynSentinel.Common/ApplyChangesResult.cs:19-30`: `bool WorkspaceInSync = false`), so an apply that failed outright (`IsError == true`) would satisfy the assert. The real subject of the test is the lock-release timing assert that follows, so the impact is limited, but the status assert does not prove the apply succeeded structurally. Same record is used at `:46` and `:86` (`Is.True` there, which an error result would NOT satisfy, so those two are safe on that point; they just never look at `IsError` / `FailedFiles`).
2. `RoslynSentinel.Tests/SnippetSelectorTests.cs:443` - `Selectors_NeverThrowForCallerMistakes` runs 70 combinations (2 member modes x 5 snippets x 7 selector mistakes) through `Assert.DoesNotThrow(() => Resolve(...))` and checks no outcome. By design ("never throws") but it would pass whatever `SnippetMatchResult.Outcome` came back. The failure message names the combination, which is good.
3. No other test passes while the operation under test failed in a way that matters.

### Pattern categories

| # | Category | Count | Verdict | Examples |
| --- | --- | --- | --- | --- |
| 1 | `Is.Not.Null` guard immediately followed by member/content asserts | about 33 | Not weak | `AppliedEditSpanTests.cs:48,66`; `HaltInfoTests.cs:73,86,97,107`; `WirePathRewriterTests.cs:275-506`; `WorkspaceRefreshTests.cs:52,92` (followed by `text == content`) |
| 2 | `Is.Null` / `Is.Not.Null` where null IS the contract, no richer status | 6 methods / 6 asserts | Not weak | `AppliedEditSpanTests.cs:19,27,35`; `DriftMessagesTests.cs:153`; `HaltInfoTests.cs:65`; `DiagnosticExtensionsTests.cs:62` |
| 3 | `Is.Null` as a negative pin beside strong asserts in the same method | about 24 | Not weak | `BuildResultProjectorTests.cs:87-89,109,110,159,193,212,213`; `WirePathRewriterTests.cs:399-466` (paired with `rewrites == 0`); `SnippetLookupTests.cs:116,157,185` |
| 4 | `Is.Not.Empty` / `GreaterThan(0)` as precondition or beside value asserts | 15 | Not weak | `SnippetErrorFormatterTests.cs:187,554,638`; `SnippetMatcherTests.cs:86` (inside `Assert.Multiple` with 6 other asserts), `:284`; `SnippetLookupTests.cs:133`; `SnippetSelectorTests.cs:402`; `WirePathRewriterTests.cs:276,373,481` |
| 5 | Does-not-throw only | 2 methods | Weak by design | `FilePathFromWireTests.cs:14-17`; `SnippetSelectorTests.cs:443` |
| 6 | Not-null on a looked-up value where a richer member exists | 4 asserts | Mildly weak | `OperationBlobWriterTests.cs:56,84` (`FindBlobPath(...)` not null); `OperationBlobWriterTests.cs:101,122` (`Diagnostic` `Not.Null.And.Not.Empty`) |
| 7 | Status flag that equals its default on failure | 1 | Weak | `WorkspaceRefreshTests.cs:134` (see false-pass list) |

### Offenders table

There are fewer than 10 offenders, so no per-file rollup is needed. All are low severity.

| file:line | Test | DTO returned | Members / values that should be asserted | Effort |
| --- | --- | --- | --- | --- |
| `WorkspaceRefreshTests.cs:134` (also `:46`, `:86`) | `Refresh_LockIsReleasedBeforeMsBuildReload_GetCurrentSolutionAsyncDoesNotBlock`, and the two sibling `Refresh_*` tests | `ApplyChangesResult(IsError, SucceededFiles, FailedFiles, Summary, WorkspaceInSync, WorkspaceVersion, PreImages, ValidationResult, RolledBackFiles, NoOpFiles, RefusalCode)` | `IsError == false`; `FailedFiles` is empty; `SucceededFiles` contains the written path; `RefusalCode` is null. At `:134` this closes the gap where an error result also has `WorkspaceInSync == false`. | trivial |
| `SnippetSelectorTests.cs:443` | `Selectors_NeverThrowForCallerMistakes` | `SnippetMatchResult` (from the local `Resolve` helper) | `Outcome` is a defined, expected failure/valid value (for example not `Matched` for the mistaken selectors) rather than only "did not throw" | moderate |
| `FilePathFromWireTests.cs:14-17` | `FromWire_NullSolutionRoot_RelativePath_DoesNotThrow` | `FilePathWrapper` | `Absolute == "Test.cs"`. The sibling test at `:20` (`..._PreservesCallerPath`) already asserts exactly this, so this one is redundant rather than harmful. | trivial |
| `OperationBlobWriterTests.cs:56`, `:84` | `WriteAsync_SlashedToolName_StillProducesAFindableBlobAsync`, `WriteAsync_OtherInvalidFileNameChars_AreAlsoSanitizedAsync` | `BlobWriteResult(Written, FileName, Diagnostic, Required)` plus the `FindBlobPath` string | `result.FileName` non-null and contains the changeId (or `File.Exists` on the found path) instead of only "path is not null". `Written == true` is already asserted. | trivial |
| `OperationBlobWriterTests.cs:101`, `:122` | `WriteAsync_NoSolutionRoot_IsNotAnIntegrityFailureAsync`, `WriteAsync_SolutionRootPresentButUnwritable_IsAnIntegrityFailureAsync` | `BlobWriteResult` | Specific `Diagnostic` text rather than `Is.Not.Null.And.Not.Empty`. `Written`, `Required` and `IsIntegrityFailure` are already asserted, so the distinction that matters is covered. | trivial |
| `AppliedEditSpanTests.cs:19,27,35`; `HaltInfoTests.cs:65`; `DriftMessagesTests.cs:153`; `DiagnosticExtensionsTests.cs:62` | null-contract tests | `IReadOnlyList<AppliedEditSpan>?`, `HaltInfo?`, `string?`, `DiagnosticInfo.SourceLine` | Nothing needed. Null is the contract and the matching non-null cases are tested elsewhere in the same file. Listed for completeness. | none |

## Root cause

Not a systemic defect. The project tests pure helpers, formatters and message builders, where results are plain values (enums, strings, lists) that the tests compare exactly. The two weak spots come from (a) a test whose real subject is something else (lock timing, `WorkspaceRefreshTests.cs:134`) and (b) a "never throws" contract test that has no natural outcome value to compare (`SnippetSelectorTests.cs:443`). The `OperationBlobWriterTests.cs` not-null asserts follow from `FindBlobPath` returning `string?` as a lookup.

## NotFound-style outcomes that let failures slip through

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

## Why it matters

The suite is a reliable regression net for the helpers it covers. The one real gap (`WorkspaceRefreshTests.cs:134`) could let a broken apply path go unnoticed in a test whose purpose is something else. The does-not-throw tests are intentionally weak but add little signal. The null-contract tests are correct as written.

## Recommendation

Open - none of these is decided; ranked by leverage:

1. Add `IsError == false` (and `FailedFiles` empty) to the three `ApplyProposedChangesAsync` asserts in `WorkspaceRefreshTests.cs` (`:46`, `:86`, `:134`). Highest value, trivial.
2. Optionally assert an outcome in `SnippetSelectorTests.cs:443` (moderate) and drop or merge the redundant `FilePathFromWireTests.cs:14-17` (trivial).
3. Optionally strengthen `OperationBlobWriterTests.cs:56,84,101,122` with `FileName` / `Diagnostic` content (trivial).
4. Owner decision, not a test change: whether the null-by-design engine paths in the previous section should return a status or throw.

Overall verdict: the project is mostly clean.

## Out of scope

- Whether each strong assert is itself correct, and flaky or order-dependent tests.
- Tests that assert through a helper method: every test method showed a direct `Assert.*` line, so helper bodies (the shared `Resolve` / `Build` helpers in `SnippetSelectorTests` and `SnippetErrorFormatterTests`) were not traced beyond their use sites.
- Other test projects.
- Whether the engine callers of the null-returning APIs handle null correctly (hypothesis only; callers were not read).

## Not checked

- Helper bodies listed above and `Fakes/InMemoryWorkspace.cs` / `TestSolutionFixture.cs` internals.
- Runtime behaviour: no tests were run; this is a static read of assertions only.
- Callers of `WirePathRewriter.RewriteJsonText`, `AppliedEditSpan.FromEdits`, `OperationBlobWriter.FindBlobPath` in production code.