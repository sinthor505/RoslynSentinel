# Plan: stop ranged ReadFile tipping into a large-result offload, and let reads and large-result paging explain or survive a restart that dropped the solution

**Status:** IMPLEMENTED 2026-10-08. Priority P1. Steps 1-5 landed (see Implementation notes at the end); step 6 live check is left to the parent after a server restart. Original status line: DRAFT 2026-10-08. Source: journal digest `.claude/journal/digest_20261008-1509.md` (window 2026-10-02..08), clusters "ReadFile", "GetLargeResult", and the restart-drops-the-solution entries filed under Search/LoadSolution/GetFileOutline.

## Problem

Two independent frictions on the read path, both recurring across sessions.

**A. A ranged read is offloaded behind `GetLargeResult` with no warning.** `ReadFile(startLine, endLine)` returns whatever slice was
asked for. The generic offload filter (`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`, `AddLargeResultOffloadFilter`, compares
`text.Length` of the serialized response with `LargeResultHelper.OffloadThresholdBytes` = 15 KB, `RoslynSentinel.Common/LargeResultHelper.cs:14`)
then swaps the body for a pointer. The ranged branch of `WorkspaceFileEditImpl.ReadFile` (`RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`, the
`if (startLine.HasValue || endLine.HasValue)` block, about lines 232-262) has no size awareness, so the agent discovers the limit by being offloaded and then pages
JSON-escaped windows. Evidence: `[b1dcd60b:L3]` (230 lines -> 3 GetLargeResult pages), `[a08be84f:L38]` (380 lines, 19,960 bytes), `[5b4488f3:L4]`,
`[47b2c93d:L3]` ("returned it double-escaped"), `[47b2c93d:L23]`, `[0734e848:L4]`. The full-file branch already tells the caller to retry with startLine/endLine, so the
advice is circular for a range that is itself too large.

**B. After a server restart the solution is gone and read-only calls that only need the disk fail.** The wording problem is fixed (commit `d03bc24`, `SolutionNotLoadedMessage`).
What remains, traced to source:
- `ReadFile` on a rooted path still fails with SolutionNotLoaded: `PersistentWorkspaceManager.ResolveFromWire` returns
  `FilePathWrapper(string.Empty, failureReason: NoSolutionLoaded)` whenever `CurrentSolution is null`, before looking at the path
  (`RoslynSentinel.Common/PersistentWorkspaceManager.cs` ~line 2026-2049), and `ReadFile` then calls `GetSolutionAsync`
  (`WorkspaceFileEditImpl.cs`, ReadFile). Entries: `[5b4488f3:L3]` ("a read-only file read needing a loaded solution is friction"), `[5c6305b5:L3]`, and the ReadFile SolutionNotLoaded x8 / GetFileOutline x3 / Search x17 counts in the digest.
- `GetLargeResult` returns a misleading error after a restart: the lookup only searches `<solutionRoot>/.roslynsentinel/largeresults`
  (`RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`, `GetLargeResult`, line ~929), so with no solution it falls through to
  `new ResultError("Exception", "Result file not found. Supply a valid resultId or filePath pointing to a largeresult_*.json file in the largeresults directory.")`.
  The file is on disk; the message says it is not and does not mention the restart. Entry `[23351d2a:L3]`.

## Decision

1. Make a ranged read fit: if the requested slice would serialize beyond the inline budget, return the longest prefix of whole lines that fits, set `endLine` to what was actually returned,
   `HasMoreData = true`, and put the exact continuation call in `StatusMessage`. No offload, no paging.
2. Give `ReadFile` a disk-only path for an already-rooted `filePath` when no solution is loaded (still read-only, no outline, no offload), and say so in `StatusMessage`.
3. Make the `GetLargeResult` not-found error use the shared `SolutionNotLoadedMessage` when no solution root is known.

Auto-reloading the previous solution after a restart is a separate, *needs design* decision (see Risks); this plan does not attempt it.

## Execution rules

- One compile-green slice per step; `Build` (0 errors) after each.
- Definitions before call sites. Step 1 adds the helper that step 3 reuses.
- New strings ASCII-only. Never put a raw exception message or internal path in a `ResultError`.

## Steps

### Step 1 - Shorten an oversized ranged ReadFile instead of letting it be offloaded
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceFileEditImpl.cs`
- Symbols:
  - New `private static int TrimRangeToInlineBudget(SourceText sourceText, int from, int to)` placed immediately above `ReadFile`. It returns the largest `newTo` in `[from, to]`
    such that `System.Text.Json.JsonSerializer.Serialize(sourceText.ToString(TextSpan.FromBounds(sourceText.Lines[from-1].Start, sourceText.Lines[newTo-1].EndIncludingLineBreak)), RoslynSentinel.Common.SharedJsonOptions.Default).Length <= RangedReadInlineBudgetChars`,
    never less than `from` (a single over-long line is returned as-is). `SharedJsonOptions` is `public static` (`RoslynSentinel.Common/SharedJsonOptions.cs:17`); measuring the escaped form matters because `\n`, `\"` and `\u003C` inflate the payload that the filter measures.
    Use a binary search over `newTo` (a 5000-line range must not serialize 5000 times).
  - New `private const int RangedReadInlineBudgetChars = LargeResultHelper.OffloadThresholdBytes - 2048;` (2 KB slack for the other fields and the call echo stamped on the first text block by the outermost filter).
  - `ReadFile`, ranged branch: after `from`/`to` are validated, call `to = TrimRangeToInlineBudget(sourceText, from, to)` (remember the original `to` to know whether it was shortened) before slicing. In the returned object keep the same shape (`filePath,startLine,endLine,totalLines,source`) but with the shortened `endLine`; set `HasMoreData = to < totalLines`; when shortened set
    `StatusMessage = $"Range shortened to lines {from}-{to} of {totalLines} to stay under the {LargeResultHelper.OffloadThresholdBytes}-byte inline limit. Call ReadFile again with startLine: {to + 1} for the rest."`.
- Call sites: `ReadFile` is the only caller of the new helper. `WorkspaceFileEditImpl.ReadFile` callers (unchanged signature): `WorkspaceTools.ReadFile` (WorkspaceTools.cs:576) and its tests (`RoslynSentinel.Tests.Tools.Basic/ReadFileTests.cs`, `CreateFileDeleteFileTests.cs:219`).
- Done when: `Build` reports 0 errors.

### Step 2 - Tests for the shortened range
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Tools.Basic\ReadFileTests.cs`
- Change: add, using the file's existing `Setup()` fixture pattern (`FakeWorkspaceManager`, `TestSolutionBuilder.CreateSolutionWithProject`, `GetProp` reflection helper) with a second document of about 800 lines of 60-char code-like text that includes `"`, `<` and `>`:
  - `ReadFile_RangeLargerThanInlineBudget_IsShortenedNotOffloadedAsync` - request 1-800; assert `result.LargeResult` is null, returned `endLine` < 800, `result.HasMoreData` is true, `result.StatusMessage` contains `startLine:`, and `JsonSerializer.Serialize(result.SuccessData).Length` < `LargeResultHelper.OffloadThresholdBytes`.
  - `ReadFile_RangeUnderBudget_IsReturnedUnchangedAsync` - request 1-20; assert `endLine == 20` and `StatusMessage` is null.
  - `ReadFile_ShortenedRange_ContinuationCallReturnsTheNextLinesAsync` - call again with `startLine = previous endLine + 1`; assert the first returned line follows the last returned line.
- Done when: the three named tests pass (`RunTest` scope=project `RoslynSentinel.Tests.Tools.Basic`, filter `FullyQualifiedName~ReadFileTests`).

### Step 3 - Disk-only ReadFile for a rooted path when no solution is loaded
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceFileEditImpl.cs`
- Symbols: `ReadFile`, immediately after `FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);`.
  Add: if `filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded && !string.IsNullOrWhiteSpace(filePath) && Path.IsPathRooted(filePath)`, call a new `private async Task<SentinelCallToolResult<object>> ReadFileWithoutSolutionAsync(string absolutePath, int? startLine, int? endLine, CancellationToken cancellationToken)` (declare it above `ReadFile`, same step) that:
  1. reads with `FileIoHelper.ReadAllTextIfExistsAsync(absolutePath, cancellationToken)` (already used in ReadFile); null -> `ResultError("FileNotFound", $"'{Path.GetFileName(absolutePath)}' does not exist at '{absolutePath}'. {SolutionNotLoadedMessage.Build(_workspaceManager.LoadState)}")`;
  2. builds a `SourceText`, applies the same range validation and `TrimRangeToInlineBudget` as the solution path (step 1);
  3. with no range and a whole file over the inline budget returns `InvalidArgument`: "File is N lines; it is over the inline limit and results cannot be offloaded while no solution is loaded. Pass startLine/endLine (ranges are shortened to fit) or call LoadSolution first.";
  4. otherwise returns the same object shape as the solution path plus `StatusMessage = "No solution is loaded; read directly from disk (no outline or offload available). " + <restart explanation if LoadState.IsFreshStartup>`.
  Relative paths are not handled (no root to resolve against): they keep the existing `SolutionNotLoaded` error.
- Call sites: none beyond `ReadFile`.
- Note: confirm `_workspaceManager.LoadState` is reachable from `WorkspaceFileEditImpl` (it is already used there at lines 421, 628 and 795).
- Done when: `Build` reports 0 errors.

### Step 4 - Test for the no-solution read
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Tools.Basic\ReadFileTests.cs`
- Change: add `ReadFile_NoSolutionLoadedAndRootedPath_ReadsFromDiskWithExplanationAsync` (build a `WorkspaceTools` over a `FakeWorkspaceManager` on which `SetTestSolution` is NOT called, so `CurrentSolution` is null; `FakeWorkspaceManager.ResolveFromWire` mirrors the real one, `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs:224-242`; write a temp file; call `ReadFile` with its absolute path; assert not an error, source present, `StatusMessage` contains "No solution is loaded"), and `ReadFile_NoSolutionLoadedAndRelativePath_StillReportsSolutionNotLoadedAsync` (assert `ErrorData.ErrorCode == "SolutionNotLoaded"`).
  The existing `Setup()` always loads a solution; factor the tools construction into a small local helper in the test rather than changing `Setup()`.
- Done when: both named tests pass.

### Step 5 - GetLargeResult says why it cannot find the file after a restart
- Files: `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tools.Basic\WorkspaceReadNavigationImpl.cs`, `C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Tests.Tools.Basic\GetLargeResultTests.cs`
- Symbols: `WorkspaceReadNavigationImpl.GetLargeResult` (line ~929), the `if (resolvedPath == null)` block.
  When `string.IsNullOrEmpty(solutionRoot)`, return `new ResultError(ToolErrorCode.SolutionNotLoaded, SolutionNotLoadedMessage.Build(_workspaceManager.LoadState) + " Offloaded results are stored under the solution root, so they cannot be located until a solution is loaded; the file itself is still on disk under .roslynsentinel/largeresults.")`.
  Otherwise keep the current message but change the code from the literal `"Exception"` to `ToolErrorCode.NotFound` and add the resultId that was looked up to the text. (`_workspaceManager` is `IWorkspaceManager`, field at WorkspaceReadNavigationImpl.cs:49; `ISolutionProvider.LoadState` is declared at `RoslynSentinel.Common/ISolutionProvider.cs:37` - confirm `IWorkspaceManager` inherits it; if not, take it from the type that does.)
  Test: in `GetLargeResultTests.cs` (existing tests at lines 125 and 140 call `_workspaceTools.GetLargeResult`) add `GetLargeResult_NoSolutionLoaded_ReturnsSolutionNotLoadedNotFileNotFoundAsync`. Existing test `T2_GetLargeResult_UnknownResultId_ReturnsError` must still pass (check whether it asserts the old `"Exception"` code; if it does, update that assertion in the same edit - it is asserting the defect).
- Done when: `GetLargeResult_NoSolutionLoaded_ReturnsSolutionNotLoadedNotFileNotFoundAsync` and `T2_GetLargeResult_UnknownResultId_ReturnsError` pass.

### Step 6 - Verification
- `Build` (0 errors).
- `RunTest` at solution scope; compare to the known-failure baseline (`reference_known_failing_tests`); report only new failures.
- `McpServerControl` stop (confirm guard), `LoadSolution`, then live: `ReadFile` a 400-line range of `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` and confirm it comes back shortened with a `startLine:` continuation instead of an offload pointer.

## Out of scope

- Auto-reloading the previously loaded solution after a restart, or defaulting `LoadSolution`'s `solutionPath` (see Risks).
- Making `GetFileOutline`/`Search` work without a solution (they need Roslyn documents).
- The `GetLargeResult` double-escaped text windows (`[47b2c93d:L3]`); see Risks.
- Changing `LargeResultHelper.OffloadThresholdBytes` or the generic offload filter.

## Risks and open decisions

- *Decision for the human (needs design):* the root of the restart friction is that every restart drops the solution (`[47b2c93d:L5]`, `[630f0332:L6]`, `[a08be84f:L14]`, `[6406d612:L3]`, `[5c6305b5:L3]`). `WarmupAndAutoLoadBasic` (`Server.Basic/ServiceRegistrationExtensionsBasic.cs`, line ~374) already auto-loads when the server is started with `--solution`, and `scripts/roslynsentinel-mcp-launch.ps1` forwards `ServerArgs` from the user-level `.mcp.json`. Options: (a) configuration only: put `--solution <path to RoslynSentinel.slnx>` in the `.mcp.json` args - zero code, but one fixed solution per config and a multi-worktree session would silently load the wrong tree; (b) persist the last successfully loaded path under `.roslynsentinel/` and auto-load it on a fresh start - handles worktrees but guesses on behalf of the agent and needs a stale-path rule; (c) leave as is, relying on the improved messages. Not planned here because (b) is feature-sized and (a) is the human's configuration.
- Hypothesis (not traced): `GetLargeResult` text windows are "double-escaped" because the tool returns the stored JSON as a string inside its own JSON response (`[47b2c93d:L3]`; also visible in this planner's own reads of offloaded files). The shared `SharedJsonOptions.Default` is used everywhere (`LargeResultHelper.cs:13`), so changing its encoder is a global change and was not attempted. Possible follow-up: return `FileSource` pages as raw `source` text rather than as an escaped JSON blob.
- The inline budget in step 1 is computed from the serialized slice plus 2 KB slack. If the outermost echo filter stamps more than that onto the first text block, a borderline range could still be offloaded; the step 2 test measures the serialized `SuccessData` only, so also run one live check (step 6).
- Step 3 widens `ReadFile` to read any rooted path on disk while no solution is loaded. `ReadFile` already reads files outside the solution via `FileIoHelper.ReadAllTextIfExistsAsync` when a solution is loaded, so this is not a new capability, but confirm that is the intent.

## Implementation notes (2026-10-08)

- Step 1: `WorkspaceFileEditImpl.TrimRangeToInlineBudget` plus `RangedReadInlineBudgetChars`; the ranged branch of `ReadFile` now shortens an oversized range, sets `HasMoreData` and a `StatusMessage` with the `startLine:` continuation. Implemented as planned.
- Step 3: `ReadFileWithoutSolutionAsync` plus a guard in `ReadFile` for `FailureReason == NoSolutionLoaded` with a rooted `filePath`. It reuses `TrimRangeToInlineBudget`; a whole file over the inline budget returns `InvalidArgument` since no offload is possible. Relative paths keep the `SolutionNotLoaded` error.
- Step 5: the `GetLargeResult` not-found block returns `ToolErrorCode.SolutionNotLoaded` (shared message) when no solution root is known, otherwise `ToolErrorCode.NotFound` with the resultId appended. The `"Exception"` literal is gone.
- Tests: five new tests in `RoslynSentinel.Tests.Tools.Basic/ReadFileTests.cs`, one new test in `GetLargeResultTests.cs`, and `T2_GetLargeResult_UnknownResultId_ReturnsError` now asserts the `NotFound` code, message and resultId.
- Verification: Build 0 errors; full solution RunTest 3468 total, 3442 passed, 0 failed, 26 skipped (baseline 3462/3436/0/26, so +6 new tests, no new failures). No tool added, so no `docs/generated` regeneration was needed.
- Not done here: step 6 live check (server restart, then `ReadFile` a 400-line range of `WorkspaceReadNavigationImpl.cs` and confirm a `startLine:` continuation instead of an offload pointer). The restart-auto-reload decision and the double-escaped `GetLargeResult` windows in Risks remain open and out of scope.
