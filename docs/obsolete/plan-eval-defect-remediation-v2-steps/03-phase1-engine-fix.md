# Step 1.2 — Fix `RunQuickBuildAsync`, adapt `RunFullBuildAsync`, sweep call sites

## Prior state

Step 1.1 already landed (verify by reading the files, not just trusting this note):

- `RoslynSentinel.Common/BuildResult.cs` now has `BuildOutcome { Succeeded, Failed, NotRun }`,
  a `ProjectsCompiled` field, nullable `ExitCode`, and no `BuildSucceeded` field.
- `RoslynSentinel.Common/EngineResultWrapper.cs` has `EngineErrorCode.BuildNotRun` and a new
  static `Failure(EngineOutcome, EngineError)` helper.
- The solution currently does **not** build cleanly — `BuildEngine.cs` and its callers still
  reference the old shape. Fixing that is this step's job.

If the live code doesn't match this description, trust the live code and adjust — this note may
be stale.

## Context

`BuildEngine.RunQuickBuildAsync` hardcodes `ExitCode: -1` and derives success from
`errorCount == 0` with no check that anything was compiled — a zero-project build reports as a
clean pass. This is the actual defect this phase fixes.

**Files this step touches:**
- `RoslynSentinel.Basic/BuildEngine.cs` (`RunQuickBuildAsync` lines 18-77, `RunFullBuildAsync`
  lines 83-202 — line numbers approximate, re-locate by method name)
- `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` (`Build` tool, ~line 1093)
- Any other file the sweep in part C below finds

## Task

### A. Fix `RunQuickBuildAsync`

Add a gate on an empty compilation set as the **first check** after resolving scope, before any
error/warning tallying:

```csharp
if (projectsCompiled.Count == 0)
{
    return EngineResultWrapper<BuildResult>.Failure(
        EngineOutcome.InvalidInput,
        new EngineError(
            $"Quick build compiled zero projects. Scope '{scope}' with scopeName '{scopeName}' resolved to nothing — no compile verdict is available.",
            code: EngineErrorCode.BuildNotRun));
}
```

Use `EngineOutcome.InvalidInput` (not `Failure`/`InternalError`) — this is a caller-input problem
(the requested scope doesn't resolve), not an engine crash.

Otherwise derive `Outcome` from `ErrorCount`: `0` → `Succeeded`, `>0` → `Failed`. The `NotRun`
case never reaches a constructed `BuildResult` — it short-circuits via the wrapper-level failure
above instead.

For the hint text: hand-construct it directly in the `Build` tool method (`SentinelWorkspaceTools.cs`)
rather than routing through `FailureRouter`. `FailureRouter` is scoped specifically to Asyncify
transform failures and its `ToolGraph` is only reflection-scanned against `SentinelAsyncifyTools`
— `Build` lives in `SentinelWorkspaceTools`, outside that scan, so forcing this through it would
mean expanding its scanned-type list for one well-understood failure mode. For this step, fold
the guidance into the error message text itself (mention `ListAll(kind: "all")` explicitly)
rather than building a full `ToolHint` object. Leave a one-line comment noting that a future
step (Findings channel — not part of this split) is the proper long-term home for this.

### B. Adapt `RunFullBuildAsync`

This method is **not part of the defect** — it genuinely spawns `dotnet build` and reads a real
`process.ExitCode`. Leave its logic alone. Only adapt its result construction to the new field
names:
- `Outcome` derived from `process.ExitCode == 0` (`Succeeded`/`Failed`)
- Real non-null `ExitCode`
- `DiagnosticsComplete: true`
- Populate `ProjectsCompiled` from whatever scope it already resolved

### C. Call-site sweep

Search the whole solution (`SearchSolutionText` or `FindReferences`) for `BuildResult`
construction and any `.BuildSucceeded`/`.ExitCode` reads. Confirm nothing outside
`BuildEngine.cs`, `SentinelWorkspaceTools.cs`'s `Build` tool, and the three known pass-through
holders (`DiagnosticSummary`, `DiagnosticsSummaryResult`, `WorkspaceHealthReport`) touches the old
shape. Fix anything else the sweep turns up.

## Gate

Full solution build clean (via the `Build` MCP tool, fullBuild scope). Test failures are expected
at this point since step 1.3 hasn't updated the tests yet — a clean **build** is the bar for this
step, not green tests. Proceed to [04-phase1-tests.md](04-phase1-tests.md).
