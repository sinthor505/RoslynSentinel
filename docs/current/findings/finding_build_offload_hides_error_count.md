# Finding: an offloaded Build result keeps nothing inline, not even ErrorCount

**Status:** RESOLVED 2026-10-09 by `docs/current/plans/plan_blocking_solution_load_and_build_result_shape.md`
Part B (commits 7b01137, 9924978, 4da918c, a8c6726, c7af88b). `Build` now projects its result
(`BuildResultProjector`): a green build returns counts and projects only, a failed build returns a
per-project/per-file breakdown with the root-cause project first, `maxDetails` (default 20) errors and
`FullDiagnosticsResultId` for the rest, so the verdict stays inline and the payload stays under the 15 KB
offload threshold. Original analysis kept below.

## Context
Reported in two separate session tool-experience journals on 2026-10-01
(`.claude/journal/`, sessions `cc715aa0` and `93fd2d31`):

- 17:45 `~ Build: fullBuild result is always written to a large-result file, so I needed
  GetLargeResult just to read ErrorCount.`
- 17:59 `~ Build: the offloaded result's file was quicker to summarize with ConvertFrom-Json
  than by paging GetLargeResult; a maxDetails-capped inline call worked fine on the retry.`

Session `cc715aa0`'s call log has 2 `GetLargeResult` calls after its single `Build`.

## What is broken
A `Build` whose serialized result exceeds 15 KB gets back only a `largeResult` pointer:
`isSuccess: true`, no `successData`, and no `statusMessage`. The answer to "did it build?"
(`Outcome`, `ErrorCount`, `WarningCount`) is in the offloaded file and not in the response.
The caller has to page `GetLargeResult` or read the file with a shell command. The second
journal entry shows the shell route is already being used, which is a fallback the
environment pushed the agent into.

## Root cause
Traced to source:
- `WorkspaceBuildTestImpl.Build` (`RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs:139`)
  passes the whole `BuildResult` to `SentinelCallToolResult<object>.ForPossiblyLargeDataAsync`.
  It passes no `statusMessage`, `totalRecords` or `listSummary`.
- `ForPossiblyLargeDataAsync` (`RoslynSentinel.Common/SentinelCallToolResult.cs:266`)
  offloads when the payload is over `LargeResultHelper.OffloadThresholdBytes` = 15 KB
  (`LargeResultHelper.cs:14`). When it offloads, only `statusMessage`, `totalRecords`,
  `listSummary` and the `LargeResultInfo` pointer stay inline, so for Build nothing useful
  survives.
- `BuildResult` (`RoslynSentinel.Common/BuildResult.cs:5`) mixes small verdict fields
  (`Outcome`, `ErrorCount`, `WarningCount`, `DiagnosticsComplete`, `Duration`, `ExitCode`) with
  bulky ones: up to `maxDetails` (default 50) `Errors` and `Warnings`, plus `ProjectsCompiled`
  and `StdoutTail`/`StderrTail`.

Hypothesis, not measured: a solution-wide full build of this repo hits 15 KB mostly through
the 50 detailed warnings and the list of about 30 compiled projects, so in practice a full
build is offloaded every time. That matches the "always" in the first journal entry.

## Why it matters
Build is the most common verification call, and its first question is a yes/no. Hiding that
verdict behind a second tool call costs every session a round trip. A weak model may also
read `isSuccess: true` on the call itself as "the build succeeded" without opening the
offloaded file. That is a silent false positive, the worst outcome for a verification tool.

## Recommendation
1. Decided direction, details open: when Build offloads, keep a verdict inline. Either pass a
   `statusMessage` such as `"Build Failed: 3 errors, 41 warnings (details offloaded)"`, or
   return a slim inline object (`Outcome`, `ErrorCount`, `WarningCount`, `ErrorSummary`, the
   first few errors) and offload only the detail lists. The slim object is better because
   `ErrorSummary` (grouped by code) is usually enough to act on.
2. Consider generalizing this. `ForPossiblyLargeDataAsync` could accept an optional "inline
   summary" projection, so every caller (25 call sites, including `ListAll`, `GetCodeInventory`
   and `ScanBreakingChanges`) can keep its headline numbers inline. Each caller still has to
   decide what its headline is.
3. Optional: never count warnings toward the offload when `ErrorCount > 0`. When a build
   fails, the errors are what the caller needs.

## Out of scope
`RunTest` does not use `ForPossiblyLargeDataAsync`. Its offload behaviour was not reviewed.
