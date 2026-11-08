# Plan — Add a real `Build` tool + `verify` parameter on Diagnose/health tools

## Title
Add a `Build` MCP tool that runs a real MSBuild/`dotnet build`, and a `verify` tri-state
parameter (`noBuild` / `quickBuild` / `fullBuild`) on `GetDiagnostics` and `GetWorkspaceHealth` so
agents can validate their own edits in one call instead of trusting in-memory Roslyn diagnostics
alone.

## Background
`GetDiagnostics` (`RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs:506-593`) and
`ValidationEngine.ValidateChangesAsync` (`RoslynSentinel.Common\ValidationEngine.cs:86-161`) both
resolve diagnostics via `Project.GetCompilationAsync().GetDiagnostics()` — Roslyn's in-memory
semantic/syntax model. This catches the compiler errors Roslyn itself can see, but not:
- Missing/locked files, resource/content-copy failures
- NuGet restore failures
- Post-build events, custom MSBuild targets/`Exec` tasks
- Multi-targeting matrix failures (a `Compilation` is per-TFM; a real build walks all of them)

`PersistentWorkspaceManager` (`RoslynSentinel.Common\PersistentWorkspaceManager.cs`) loads the
solution via `MSBuildWorkspace` for **design-time analysis only** — never invokes an actual build.
No code path anywhere in the repo calls `dotnet build`, `Compilation.Emit`, or the full
`Microsoft.Build` package (confirmed by repo-wide grep). `Samples\ContosoOrders\SCENARIOS.md:300-312`
already flags this exact gap for graders: verify with a real `dotnet build`, not just
`GetDiagnostics`' summary count.

This plan closes that gap with a two-tier design, confirmed with the user:
- **Tier 1 (quick):** in-process `Compilation.GetDiagnostics()` — what `GetDiagnostics` already
  does. Fast, no subprocess, catches ordinary compile errors.
- **Tier 2 (full):** shell out to `dotnet build`, following `GitTools.RunGitAsync`'s existing
  subprocess pattern (`RoslynSentinel.Server.Basic\GitTools.cs:176-203`). Slower, catches
  everything MSBuild catches.

The user specified the verify parameter should be a tri-state: `noBuild` (default, no behavior
change) / `quickBuild` (Tier 1) / `fullBuild` (Tier 2), reusing the exact same underlying routine
the standalone `Build` tool uses — one code path, not two.

## Assumptions
- Editing `RoslynSentinel.Basic`/`RoslynSentinel.Common`/`RoslynSentinel.Server.Basic` source
  directly (Read/Edit/Bash), not via the MCP tools operating on their own source.
- `dotnet` is on PATH in the environment the server runs in (same assumption `git` already makes
  in `GitTools`).
- No new NuGet package needed — subprocess `dotnet build`, not an in-process `Microsoft.Build`
  `BuildManager`. `Microsoft.Build` (full engine package) is not referenced anywhere today; adding
  it would introduce MSBuildLocator-timing risk against the existing `MSBuildWorkspace` registration
  in `PersistentWorkspaceManager.cs:98-104`. Subprocess avoids that entirely.
- Line numbers cited below are current as of this plan's writing and will drift — re-locate with
  Grep before editing.
- Build (0 errors) and test after each task; diff failing tests against
  `docs/known-failing-tests.txt` rather than eyeballing the full pre-existing baseline. Commit each
  task separately.
- Both `RoslynSentinel.Server.Basic` and `RoslynSentinel.Server.Advanced` register
  `SentinelWorkspaceTools` (`ServiceRegistrationExtensionsBasic.cs`, `ServiceRegistrationExtensionsAdvanced.cs`
  ~line 165) — adding the new tool/parameter there means both server flavors pick it up for free.

## Known operational caveat
`docs/PROPOSED_TOOLS.md:376-380` notes `dotnet build` can fail on the final exe-copy step with
`MSB3027` if the target executable is locked (e.g. by a running instance of the same process, or
VS Code holding it). This mainly bites when the *user's* solution being built is RoslynSentinel
itself — surface this as a specific, recognizable error rather than a generic exception when the
build tool detects it (exit code + `MSB3027` in stderr → tailored `Detail` message suggesting the
process holding the lock be closed).

## Approach
Tasks 1–3 build the shared engine and tool; Task 4 wires the tri-state parameter into
`GetDiagnostics`/`GetWorkspaceHealth`, reusing Task 1's engine. Do them in order — 4 depends on 1.

### Task 1 — `BuildEngine` (new file, `RoslynSentinel.Common\BuildEngine.cs`)
Follow the existing two-layer convention (engine returns `EngineResultWrapper<T>`; tool layer
translates to `ToolResult<object>` — see `DiagnosticEngine` for the pattern).

```csharp
public enum BuildVerifyLevel { noBuild, quickBuild, fullBuild }

public record BuildResult(
    bool BuildSucceeded,
    BuildVerifyLevel Level,
    int ExitCode,          // -1 for quickBuild (no process ran)
    int ErrorCount,
    int WarningCount,
    List<DiagnosticInfo> Errors,      // capped, mirrors GetDiagnostics maxDetails convention
    List<DiagnosticInfo> Warnings,    // capped
    string? StdoutTail,     // last N lines, fullBuild only
    string? StderrTail,
    TimeSpan Duration,
    string? Detail = null   // e.g. MSB3027 lock hint
);
```

- `RunQuickBuildAsync(string? projectOrScopeName, ToolScope scope, CancellationToken)`: delegates
  to the existing `DiagnosticEngine.GetSolutionDiagnosticsAsync`/`GetProjectDiagnosticsAsync`/
  `GetFileDiagnosticsAsync` (don't reimplement — call the same engine `GetDiagnostics` already
  calls) and reshapes into `BuildResult` with `ExitCode = -1`.
- `RunFullBuildAsync(CancellationToken)`: resolves the target path via
  `_workspaceManager.SolutionPath ?? _workspaceManager.CurrentSolution?.FilePath` (no new state
  needed per the research — `PersistentWorkspaceManager` already exposes this), then subprocess:
  ```csharp
  FileName = "dotnet", ArgumentList = ["build", solutionPath, "--nologo", "-v", "quiet"]
  ```
  matching `GitTools.RunGitAsync`'s exact `ProcessStartInfo`/`BeginOutputReadLine`/
  `WaitForExitAsync(cancellationToken)` shape. Parse stdout/stderr for MSBuild's
  `<path>(line,col): error CSxxxx: message` / `warning CSxxxx:` lines (regex) into
  `List<DiagnosticInfo>` reusing the existing `DiagnosticInfo` record — don't invent a parallel
  shape. Detect `MSB3027`/`MSB3021` in stderr and set `Detail` with the file-lock hint from the
  caveat above.
- Both methods should consult `_workspaceManager.CheckRateLimit("Build", <limit>)` before running
  — a real build is expensive, matching how other tools already guard cost
  (`DocumentationTools.cs:171` for precedent). `fullBuild` does not need the mutating-tool circuit
  breaker (`CheckBreaker`) since it doesn't mutate the workspace, only rate limiting.

### Task 2 — `Build` MCP tool (`SentinelWorkspaceTools.cs`, alongside `GetDiagnostics`)
```csharp
[McpServerTool(Name = "Build")]
[Produces(DataTag.Report)]
[Description("Compiles the loaded solution and reports errors/warnings. level=quickBuild uses in-memory Roslyn diagnostics (fast, matches GetDiagnostics). level=fullBuild shells out to `dotnet build` (slower, catches MSBuild-only failures: NuGet restore, resource copy, post-build events — things quickBuild can't see). Returns BuildSucceeded, ExitCode, ErrorCount/WarningCount, capped Errors/Warnings lists, Duration.")]
public async Task<ToolResult<object>> Build(
    BuildVerifyLevel level = BuildVerifyLevel.fullBuild,
    [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
    CancellationToken cancellationToken = default)
```
Body follows the standard try/catch + `_logger.LogError(ex, "{ToolName} failed", ...)` +
`ToolErrorCode.Exception` shape every other tool in this file uses. Stamp `WorkspaceVersion` on
the returned `ToolResult` (per `ToolResult.cs:98`, matching the existing convention that mutating/
verification calls stamp it so callers can detect staleness). Add `BuildFailed` to
`ToolErrorCode` (`ToolResult.cs:6-12`) for the case where the build tool itself errors (dotnet not
found, no solution loaded) — distinct from `Success=true, Data.BuildSucceeded=false` (the build
ran and reported real compile errors, which is a normal/expected outcome, not a tool failure).

### Task 3 — Tests
New test file (or extend an existing workspace-tools test file) covering: quickBuild on a solution
with a known compile error returns `BuildSucceeded=false` with matching `Errors`; fullBuild against
a real fixture solution round-trips exit code 0 on a clean build; `noBuild`-equivalent (i.e. Build
tool itself, not the parameter) behavior is unaffected by call order/repeated invocation; rate-limit
rejection surfaces a clean `ToolResult` error rather than throwing.

### Task 4 — `verify` parameter on `GetDiagnostics` and `GetWorkspaceHealth`
Add `BuildVerifyLevel verify = BuildVerifyLevel.noBuild` as a new optional parameter (after existing
params, before `cancellationToken`, per the codebase's cancellation-token-last convention) to both:
- `GetDiagnostics` (`SentinelWorkspaceTools.cs:509-516`)
- `GetWorkspaceHealth` (`SentinelWorkspaceTools.cs:1556-1558`) — and its core
  `GetWorkspaceHealthAsync` (line 1485), or thread it through only at the tool-method layer if the
  async core stays health-only; prefer doing the build call in the tool method itself so
  `GetWorkspaceHealthAsync`'s existing pure "read state" contract (used elsewhere?) isn't muddied.

When `verify != noBuild`, call `_buildEngine.RunQuickBuildAsync(...)` or `RunFullBuildAsync(...)`
(Task 1) and attach the result as a new optional field on the response — mirroring the existing
`ApplyChangesResult.ValidationResult: DiagnosticReport?` optional-field convention
(`PersistentWorkspaceManager.cs:813`) rather than changing either tool's primary return shape:
- `GetDiagnostics`: add `BuildResult? BuildVerification` to whichever result record is actually
  returned for that call (`DiagnosticSummary` for the raw path, `DiagnosticsSummaryResult` for
  `summarize=true` — both need the field, or wrap both in a shared outer envelope; pick whichever
  is the smaller diff once you're looking at the current `DiagnosticSummary` definition).
- `GetWorkspaceHealth`: add `BuildResult? BuildVerification = null` to `WorkspaceHealthReport`
  (`RoslynSentinel.Common\WorkspaceHealthReport.cs:7-17`) as a trailing optional positional param,
  matching how `StaleDocumentCount`/`RequiresReload`/`SampleStaleFiles` were already added as
  trailing optionals to that same record.

Update both tools' `[Description(...)]` to mention the new parameter tersely (this codebase is
deliberately token-cost-conscious about description length — see
`docs/spec-tool-description-compression-v1.md` — so append one short clause, don't restate the
whole Build tool's behavior).

### Task 5 — Docs
Add a short entry to `docs/TOOL_DOCUMENTATION.md` (or wherever current tool docs actually live —
confirm it's still maintained/current before writing into it, since some `docs/*.md` files are
stale from an earlier tool generation per the research). Remove/update
`Samples\ContosoOrders\SCENARIOS.md:300-312`'s note once the real fix exists, since it currently
describes a gap this plan closes.

## Open questions to resolve before/while implementing
- Exact current field layout of `DiagnosticSummary` (referenced in `GetDiagnostics` but not
  inspected in this research pass) — check it before deciding where `BuildVerification` attaches
  on that path.
- Whether `fullBuild`'s subprocess should target the whole solution or allow a `scopeName`
  (single project) like `GetDiagnostics` does — `dotnet build <csproj>` is supported and faster
  than a full-solution build; consider exposing the same `scope`/`scopeName` pair on `Build` for
  consistency, defaulting to solution-wide.
- Timeout policy for `fullBuild` — a hung build should not hang the tool call indefinitely; decide
  a default timeout (e.g. 120s) distinct from `cancellationToken` being externally cancelled.
