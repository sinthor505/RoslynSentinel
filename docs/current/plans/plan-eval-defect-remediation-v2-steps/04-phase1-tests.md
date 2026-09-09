# Step 1.3 — Phase 1 test updates and new tests

## Prior state

Steps 1.1 and 1.2 already landed (verify by reading the files, not just trusting this note):

- `BuildResult` has `BuildOutcome { Succeeded, Failed, NotRun }`, `ProjectsCompiled`, nullable
  `ExitCode`, no `BuildSucceeded`.
- `EngineResultWrapper<T>` has `EngineErrorCode.BuildNotRun` and a `Failure(EngineOutcome, EngineError)` helper.
- `BuildEngine.RunQuickBuildAsync` now gates on `projectsCompiled.Count == 0` and returns
  `EngineOutcome.InvalidInput` with `EngineErrorCode.BuildNotRun` in that case; otherwise derives
  `Outcome` from `ErrorCount`.
- `BuildEngine.RunFullBuildAsync` still spawns a real process and now populates the new field
  names (`Outcome`, real `ExitCode`, `DiagnosticsComplete: true`, `ProjectsCompiled`).
- The solution builds clean.

If the live code doesn't match this description, trust the live code and adjust.

## Context

**File this step touches:** `RoslynSentinel.Tests.Battery/BatteryTwentyTests.cs` (~line 602-660+),
plus a new `RoslynSentinel.Tests.Battery/BuildEngineTests.cs`.

## Task

### A. Update existing tests in `BatteryTwentyTests.cs`

1. `Build_QuickBuild_CleanSource_ReturnsSuccess` — this test **currently asserts `ExitCode == -1`
   as documented-correct behavior**. This directly contradicts the fix. Rewrite it to assert:
   - `Outcome == BuildOutcome.Succeeded`
   - `ExitCode == null`
   - `ProjectsCompiled` is non-empty
2. The other two tests in that block (names containing `...SourceWithCompileError...` and
   `...RepeatedDiagnosticAcrossFiles...`) currently cast to `BuildResult` and read
   `.BuildSucceeded`. Update those reads to `.Outcome`.
3. `GetDiagnostics_VerifyQuickBuild_AttachesBuildVerification` (around line 652) — read its body
   first. It likely only needs a mechanical field-name update since it only touches
   `BuildVerification` as a pass-through value, not a field it inspects deeply.

### B. New tests

Add these (in `BatteryTwentyTests.cs` unless a test more naturally belongs in the new engine-level
file below):

1. Zero-project scope → `ToolResult` non-success, error message references `ListAll`.
2. Clean solution, quick build → `Outcome == Succeeded`, `ProjectsCompiled` non-empty,
   `ExitCode == null`.
3. Solution with a genuine compile error → `Outcome == Failed`, `ErrorCount > 0`,
   `ProjectsCompiled` still non-empty (it compiled, just had errors — must not collapse into
   `NotRun`).
4. Full build smoke test — confirm `ExitCode` is still real and non-null, `Outcome` derives
   correctly. This is a regression guard confirming `RunFullBuildAsync` wasn't disturbed by this
   phase's changes.
5. New engine-level test in a new file `RoslynSentinel.Tests.Battery/BuildEngineTests.cs`
   (match `BatteryTwentyTests.cs`'s existing conventions for setup/fixtures), calling
   `RunQuickBuildAsync` directly (below the MCP bridge) with a manufactured zero-project scope,
   asserting `EngineOutcome.InvalidInput` and `EngineErrorCode.BuildNotRun` at the engine layer.

## Gate — Phase 1 gate

Run `RoslynSentinel.Tests.Battery` and `RoslynSentinel.Tests.Basic` via the `RunTest` MCP tool.
Both must be green. Full solution build clean via the `Build` MCP tool. Zero regressions versus
the step-0 baseline (same or better pass counts, no new unrelated failures).

Once this gate passes, Phase 1 is complete. Proceed to
[05-phase2-repro.md](05-phase2-repro.md).
