# Reference: server tool telemetry (collection, files, rollup)

**Status:** CURRENT 2026-11-05. Verified against `RoslynSentinel.Common/ToolTelemetrySnapshot.cs` and `ToolTelemetryFlusher.cs`.

## Purpose
Per-tool usage counters for each server instance, so tool changes can be judged on real use: call volume,
error rate and codes, latency, and whether the hints we emit are followed. Consult when reading the numbers
or touching the telemetry files.

## How it works
- **Counters per tool** (only tools called at least once; zero values are omitted on the wire, read them as 0):
  `calls`, `successes`, `errors`, `errorsByCode`, `totalDurationMs`, `maxDurationMs`, `firstCallUtc`,
  `lastCallUtc`, `guidanceIssued`, `guidanceFollowed`, `guidanceFollowedSucceeded`, `guidanceIgnored`.
- **Guidance semantics**, keyed on the ORIGINATING tool (the one whose hints suggested a next tool):
  issued = a next tool was suggested; followed = the very next matching call happened; followedSucceeded =
  that call succeeded; ignored = the suggestion expired unfollowed (after 3 further calls or 2 minutes).
  Approximate: hints built but discarded over-count issued, and concurrent subagents share one call stream.
- **Live view:** `McpServerStatus` returns the same data as the `toolTelemetry` field (instance id, start time,
  build sha, tools).
- **File:** `server-telemetry_<instanceId>.json` in the instance working folder, only for explicit instances
  (those started with `--working-dir`). Shape: `schemaVersion` (1), `writtenUtc`, then the `toolTelemetry` object.
  Flushed every 60 s when dirty and on shutdown. It is written IN PLACE, so a reader can see a partial file
  and must tolerate it.
- **Retention:** `roslynsentinel-mcp-launch.ps1` copies a stale instance's file to `<root>\telemetry\` before
  sweeping its folder (a newer copy is never overwritten). Root: `ROSLYNSENTINEL_INSTANCES_ROOT`, default
  `%USERPROFILE%\RoslynSentinelInstances`; live files are in `<root>\instances\<id>\`.

## Usage
```
pwsh scripts\Get-TelemetryRollup.ps1                 # saved copies only
pwsh scripts\Get-TelemetryRollup.ps1 -IncludeLive    # plus running instances
pwsh scripts\Test-TelemetryRollup.ps1                # synthetic self-test (PS 5.1 and 7)
```
Other parameters: `-InstancesRoot`, `-OutDir` (default `<root>\telemetry`), `-Date`, `-Quiet`. Writes
`rollup_<yyyy-MM-dd>.json` (camelCase: `sourceFiles`, `skippedFiles`, `instances`, `dateRange`, `buildShas`,
`tools[]` with error%, avg/max ms, merged `errorsByCode`, `topErrorCodes` (5), guidance sums, `followPercent`,
`succeedPercent`, plus `totals`) and prints a table by calls. Runs are de-duplicated on
(`instanceId`, `serverStartedUtc`), latest `writtenUtc` wins. Unreadable, partial or unknown-schema files are
skipped and listed; exit 1 only when files existed and none were readable. Source files are never modified.

## Gotchas
- `rollup_*.json` never matches the input pattern `server-telemetry_*.json`, so the output can sit in the
  telemetry folder safely.
- Counters restart with each server start; compare runs by `buildSha`, not by file.
- Legacy (non-explicit) launches write no file; only `McpServerStatus` shows their counters.
