# Step 1.1 — Reshape `BuildResult`, fix `RunQuickBuildAsync`, adapt `RunFullBuildAsync`, sweep call sites

**This step implements only this file** (see "Files this step touches" below). Do not read,
open, or act on any other plan step file (e.g. via `ProjectDoc`) — another process runs each step
as its own isolated task.

## Prior state

Baseline test counts recorded in step 0. No production code changed yet.

## Context

`Build`'s `quickBuild` mode currently fabricates a success verdict: `BuildEngine.RunQuickBuildAsync`
hardcodes `ExitCode: -1` and derives `BuildSucceeded` from `errorCount == 0` with no check that
anything was actually compiled — a build of zero projects reports as a clean pass. This step does
the full fix in one pass: reshape `BuildResult`'s type, then fix the engine logic and every call
site that reads the old shape, so the solution builds clean by the end of this step.

**Files this step touches:**
- `RoslynSentinel.Common/BuildResult.cs`
- `RoslynSentinel.Common/EngineResultWrapper.cs`
- `RoslynSentinel.Basic/BuildEngine.cs` (`RunQuickBuildAsync` lines 18-77, `RunFullBuildAsync`
  lines 83-202 — line numbers approximate, re-locate by method name)
- `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` (`Build` tool, ~line 1093)
- `RoslynSentinel.Tests.Battery/BatteryTwentyTests.cs` (only as needed to keep it compiling —
  full test updates are a later step's job)
- Any other file the sweep in part D below finds

## Task

### A. Reshape `BuildResult`

Replace the boolean verdict with a 3-state outcome and make `ExitCode` honest:

```csharp
public enum BuildOutcome { Succeeded, Failed, NotRun }

public record BuildResult(
    BuildOutcome Outcome,
    BuildVerifyLevel Level,
    List<string> ProjectsCompiled,
    bool DiagnosticsComplete,
    int ErrorCount,
    int WarningCount,
    List<DiagnosticInfo> Errors,
    List<DiagnosticInfo> Warnings,
    List<DiagnosticGroupSummary> ErrorSummary,
    List<DiagnosticGroupSummary> WarningSummary,
    string? StdoutTail,
    string? StderrTail,
    TimeSpan Duration,
    int? ExitCode = null,
    string? Detail = null);
```

Remove `BuildSucceeded` entirely — do not redefine it or keep it as an alias. A caller that
reads the old field must get a compile error, not stale semantics. `ExitCode` must be `null`
unless the build actually spawned a process (only `RunFullBuildAsync`'s path populates it).
`ProjectsCompiled` is a new load-bearing field: the substrate stating what it actually looked at.

The type name `BuildResult` stays the same. Three pass-through consumers hold `BuildResult?`
fields with no field access on them: `DiagnosticSummary.BuildVerification`,
`DiagnosticsSummaryResult.BuildVerification`, `WorkspaceHealthReport.BuildVerification`. These
need **no changes** — confirm this yourself with a search (`SearchSolutionText` or
`FindReferences`) for `BuildVerification` before moving on, rather than assuming it's still true.

This change will break compilation everywhere `BuildResult` is constructed or its old fields are
read (`BuildEngine.cs`, `SentinelWorkspaceTools.cs`'s `Build` tool, `BatteryTwentyTests.cs`) —
parts B/C/D of this step fix all of those in the same pass, so the solution builds clean by the
end of this step.

### B. `EngineErrorCode` and `EngineResultWrapper<T>` additions

Add `EngineErrorCode.BuildNotRun` to the existing enum (it currently has 3 values).

Add one small additive static helper. Confirm first that no `.Failure(error, payload:)` factory
already exists — only a 3-arg constructor `(EngineOutcome, T?, EngineError?)` exists today:

```csharp
public static EngineResultWrapper<T> Failure(EngineOutcome outcome, EngineError error) => new(outcome, default, error);
```

Purely additive — no existing call site changes. Do **not** add a `Findings` property here; that
belongs to a later phase and is out of scope for this step.

### C. Fix `RunQuickBuildAsync`, adapt `RunFullBuildAsync`

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
Findings channel (not part of this step) is the proper long-term home for this.

`RunFullBuildAsync` is **not part of the defect** — it genuinely spawns `dotnet build` and reads a
real `process.ExitCode`. Leave its logic alone. Only adapt its result construction to the new
field names:
- `Outcome` derived from `process.ExitCode == 0` (`Succeeded`/`Failed`)
- Real non-null `ExitCode`
- `DiagnosticsComplete: true`
- Populate `ProjectsCompiled` from whatever scope it already resolved

### D. Call-site sweep

Search the whole solution (`SearchSolutionText` or `FindReferences`) for `BuildResult`
construction and any `.BuildSucceeded`/`.ExitCode` reads. Confirm nothing outside
`BuildEngine.cs`, `SentinelWorkspaceTools.cs`'s `Build` tool, and the three known pass-through
holders (`DiagnosticSummary`, `DiagnosticsSummaryResult`, `WorkspaceHealthReport`) touches the old
shape. Fix anything else the sweep turns up, including whatever minimal changes
`BatteryTwentyTests.cs` needs just to compile against the new shape — rewriting its assertions to
match the new behavior in full is a later step's job; here, only do enough to keep it building.

## Gate

Full solution build clean (via the `Build` MCP tool, fullBuild scope). Test failures are expected
at this point since a later step hasn't fully updated the tests yet — a clean **build** is the
bar for this step, not green tests.

Report the build result above and stop — do not proceed to any other step.
