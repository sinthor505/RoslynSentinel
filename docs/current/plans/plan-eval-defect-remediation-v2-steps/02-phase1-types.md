# Step 1.1 — Reshape `BuildResult`, add `EngineErrorCode`/`EngineResultWrapper` additions

## Prior state

Baseline test counts recorded in step 0. No production code changed yet.

## Context

`Build`'s `quickBuild` mode currently fabricates a success verdict: `BuildEngine.RunQuickBuildAsync`
hardcodes `ExitCode: -1` and derives `BuildSucceeded` from `errorCount == 0` with no check that
anything was actually compiled — a build of zero projects reports as a clean pass. This step lays
the type groundwork; the actual behavioral fix is step 1.2.

**Files this step touches:**
- `RoslynSentinel.Common/BuildResult.cs`
- `RoslynSentinel.Common/EngineResultWrapper.cs`

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
unless the build actually spawned a process (only `RunFullBuildAsync`'s path populates it —
that's step 1.2's job, not this one). `ProjectsCompiled` is a new load-bearing field: the
substrate stating what it actually looked at.

The type name `BuildResult` stays the same. Three pass-through consumers hold `BuildResult?`
fields with no field access on them: `DiagnosticSummary.BuildVerification`,
`DiagnosticsSummaryResult.BuildVerification`, `WorkspaceHealthReport.BuildVerification`. These
need **no changes** — confirm this yourself with a search (`SearchSolutionText` or
`FindReferences`) for `BuildVerification` before moving on, rather than assuming it's still true.

This change will break compilation everywhere `BuildResult` is constructed or its old fields are
read (`BuildEngine.cs`, `SentinelWorkspaceTools.cs`'s `Build` tool, `BatteryTwentyTests.cs`). That
is expected — those call sites are fixed in steps 1.2 and 1.3, not this one. It is fine (and
expected) for the solution to **not** build cleanly at the end of this step.

### B. `EngineErrorCode` addition

Add `EngineErrorCode.BuildNotRun` to the existing enum (it currently has 3 values).

### C. `EngineResultWrapper<T>` addition

Add one small additive static helper. Confirm first that no `.Failure(error, payload:)` factory
already exists — only a 3-arg constructor `(EngineOutcome, T?, EngineError?)` exists today:

```csharp
public static EngineResultWrapper<T> Failure(EngineOutcome outcome, EngineError error) => new(outcome, default, error);
```

Purely additive — no existing call site changes. Do **not** add a `Findings` property here; that
belongs to a later phase (step 3.2) and is out of scope for this step.

## Gate

No test/build gate for this step in isolation — the solution is expected to have compile errors
after this step, since `BuildEngine.cs` and its callers haven't been updated yet. Proceed directly
to [03-phase1-engine-fix.md](03-phase1-engine-fix.md), which fixes those call sites and restores
a clean build.
