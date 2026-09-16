# Session halted (external file drift) mid-way through `plan_contexterrorbuilder_orienting_guidance.md` Step 4, blocking `ReplaceSnippet` on `SentinelWorkspaceTools.cs`

**Status:** RESOLVED (false-positive drift, latch cleared 2026-09-15). See "Resolution" below.
Originally hit while implementing Step 4 (labeling the compiler-diagnostic block in
`SentinelWorkspaceTools.cs` with a `[COMPILER ERROR]` prefix). Steps 1-3 of the plan were already
applied and compiling cleanly (0 errors) before this halt; Step 4 was the first edit blocked.

## What happened

A `ReplaceSnippet(action: apply)` call targeting
`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` (adding `"[COMPILER ERROR] "` immediately
before the `CompilerErrorLookupHelper.DescribeAsync(...)` call in the `ReplaceSnippet` apply path,
around line 727) failed with:

```json
{"success":false,"error":{"errorCode":"SessionHalted",
"message":"ReplaceSnippet apply for 'C:\\Users\\Administrator\\source\\repos\\RoslynSentinel\\RoslynSentinel.Server.Basic\\SentinelWorkspaceTools.cs' failed: Session halted: external file drift was detected on a tracked file. This session cannot safely continue. Stop and report to the user/operator.",
"detail":"Session halted: external file drift was detected on a tracked file. This session cannot safely continue. Stop and report to the user/operator."}}
```

Follow-up (read-only) diagnostics:

- `IsSessionHalted()` -> `true`.
- `ListExternalDiskChanges()` -> returned 34 files, including `SentinelWorkspaceTools.cs` itself
  (the exact file the blocked edit targeted) and 33 others: `ToolParams.cs`,
  `SentinelRefactoringTools.cs`, `SentinelWholeFileWriteTools.cs`, `SentinelSymbolTools.cs`,
  `WorkspaceReadNavigationImpl.cs`, `StackOverflowEngine.cs`, `SentinelScanTools.cs`,
  `SentinelCodemodTools.cs`, `SentinelQualityTools.cs`, `SentinelModernizationTools.cs`,
  `SentinelDocumentationTools.cs`, `SentinelAsyncifyToolsTests.cs`, `RegressionTests.cs`,
  `SentinelAsyncifyTools.cs`, `ModelAgentRunner.cs`, `MetricsEngine.cs`, `GetScanResultTests.cs`,
  `BatteryFourTests.cs`, `BatteryTwentySixTests.cs`, `BatteryTwentySevenTests.cs`,
  `BatteryThirtyThreeTests.cs`, `AsyncOptimizationEngine.cs`, `SentinelAccuracyTests.cs`,
  `SecurityEngine.cs`, `PersistentWorkspaceManager.cs`, `FiveStarToolTests.cs`,
  `DiscoveryEngine.cs`, `BatteryThirtyNineTests.cs`, `BatterySixTests.cs`, `AnalysisEngine.cs`,
  `MigrationScanResultTests.cs`, `AntiPatternEngine.cs`, `ToolResult.cs`.

## Resolution (2026-09-15)

Confirmed as a **false-positive drift detection**, not a real concurrent external edit. Direct
investigation by the operator found zero overlap between the 34-file `ListExternalDiskChanges`
drift list and the actual `Git(operation: status)` output, which showed only
`RoslynSentinel.Common/ContextHelper.cs` as modified plus this session's own untracked new files
(`ContextErrorBuilder.cs`, several `docs/current/` additions). None of the 34 "drifted" files were
reported as modified by git at all - if another process had genuinely written to them, git would
show it. Most likely trigger: file timestamps changing during an earlier MCP server restart this
session (a restart alone can update mtimes without changing content), which the drift detector
apparently treats as equivalent to a real content change.

`AcknowledgeExternalFileChanges` was called and reported "Cleared 34 tracked external file
change(s) and the session-wide fatal drift latch." `IsSessionHalted()` re-checked immediately
afterward and confirmed `false`. The task resumed from exactly where it left off (Step 4 of
`plan_contexterrorbuilder_orienting_guidance.md`), with no further drift halts.

This does not fully resolve the three open root-cause questions below (the detector still doesn't
distinguish "mtime-only touch" from "real content drift", and the halt message still doesn't
self-report the affected file count) - those remain valid follow-up items, now filed as
open TODO entries rather than blocking anything.

## Why this was (almost certainly) not self-inflicted this session

This session never used a non-MCP write tool on any `.cs` file. Every mutating call up to this
point (enum add, `ContextErrorBuilder.cs` scaffold + `Member`/`ReplaceSnippet` population,
`ChangeAccessibility`, `ApplyDiff` on `ContextHelper.cs`) went through the required MCP tools per
`CLAUDE.md`'s dog-fooding mandate.

The 34-file drift list matched, near-exactly, the `git status` snapshot captured at the *start* of
this conversation (before any tool call in this session) - but investigation showed that snapshot
itself only ever listed `ContextHelper.cs` as modified, so the drift list did not actually
correspond to real working-tree changes at all. The concurrent-session theory floated at the time
of the original halt is now considered unlikely as the primary cause; a timestamp-driven
false-positive is the better-supported explanation given the zero-overlap finding above.

## Impact on the in-flight task (as of the original halt)

`plan_contexterrorbuilder_orienting_guidance.md` Steps 1-3 were fully applied and compiling (0
errors, confirmed via each `ReplaceSnippet`/`ApplyDiff` call's own compile-delta validation along
the way):

- Step 1 (`DiagnoseNoMatch` + supporting helpers: `SplitSnippetIntoTrimmedContentLines`,
  `LineSimilarityScore`, `GatherNoMatchEvidence`, `FormatNoMatchDiagnosis`,
  `FormatAllMatchedDiagnosis`, `FormatPartialMatchDiagnosis`, `FormatZeroMatchedDiagnosis`,
  `AppendUnmatchedLines`) added to `RoslynSentinel.Common/ContextHelper.cs`.
- Step 2 (`ContextErrorBuilder` + `SnippetMatchOutcome`) added as new file
  `RoslynSentinel.Common/ContextErrorBuilder.cs`; `DescribeAmbiguousCandidates` changed from
  `private` to `internal` in `ContextHelper.cs` to allow the call.
- Step 3 (reworded "not found" wording) is live via `ContextErrorBuilder.BuildNoMatch`;
  `FindSnippetPositionWithLength` and `FindExactSnippetPosition` both now throw via
  `ContextErrorBuilder.Build(...)` instead of hand-rolled strings; `ThrowIfMultiLine` reworked to
  build `InvalidDisambiguator` wording via `ContextErrorBuilder` (signature changed to accept
  `SourceText` - both call sites updated atomically via one `ApplyDiff`).

**Not yet applied at halt time:** Step 4 (`[COMPILER ERROR]` label on the 4 compiler-diagnostic
blocks in `SentinelWorkspaceTools.cs` - `ReplaceSnippet` ~line 727, `ReplaceSnippetBatch` ~line 966,
`ApplyDiff` ~lines 1211 and 1319; the 4th site at line 966 was found via `SearchSolutionText` during
this task and is the same message shape as the plan's 3 cited sites, so it was included in scope).
No edit to `SentinelWorkspaceTools.cs` had been written at halt time - the failed `ReplaceSnippet`
call above left the file completely unchanged (consistent with the compile-delta-validation
chokepoint's documented behavior: a failed apply never partially lands). Step 4 resumed and
completed after the latch was cleared - see the plan doc / session report for final status.

Step 5 (wrong-node-kind sweep) was not started as of the halt; it was already the lowest-priority,
optional item per the plan.

## Suggested follow-up (root cause still open - moved to TODO.md)

The three open items originally listed here (halt message not naming drift file count; detector
not distinguishing mtime-only touch from real content drift; recovery tool names not surfaced in
the halt message) remain valid and are tracked in `docs/current/TODO.md` rather than repeated here.

## Related

- `docs/current/blockers/blocking_error_session_halt_from_out_of_band_rm_mid_spike.md` - prior
  occurrence of the same exception type, different (self-inflicted) trigger; same open
  root-cause questions.
- `docs/current/plans/plan_contexterrorbuilder_orienting_guidance.md` - the plan being implemented
  when this halt was hit.
- `project_concurrent_sessions` (memory) - repo may have multiple Claude/VS sessions editing
  simultaneously; considered and downgraded as the likely explanation once git status showed zero
  overlap with the drift list.
