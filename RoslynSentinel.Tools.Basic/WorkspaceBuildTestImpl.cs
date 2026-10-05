using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Plain DI-constructed implementation backing WorkspaceBuildTestTools. Method bodies moved
/// verbatim from WorkspaceTools (Decision 7 step 2).
/// </summary>
public class WorkspaceBuildTestImpl
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly BuildEngine _buildEngine;
    private readonly TestRunEngine _testRunEngine;
    private readonly ILogger _logger;

    public WorkspaceBuildTestImpl(IWorkspaceManager workspaceManager, DiagnosticEngine diagnosticEngine,
        BuildEngine buildEngine, TestRunEngine testRunEngine, ILogger logger)
    {
        _workspaceManager = workspaceManager;
        _diagnosticEngine = diagnosticEngine;
        _buildEngine = buildEngine;
        _testRunEngine = testRunEngine;
        _logger = logger;
    }

    public async Task<SentinelCallToolResult<object>> GetDiagnostics(ToolCallReason reason, ToolScope scope = ToolScope.solution,
        string? scopeName = null, bool summarize = false, int maxDetails = 50, int topN = 20,
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild, CancellationToken cancellationToken = default)
    {
        try
        {
            EngineResultWrapper<DiagnosticSummary> result;
            DiagnosticSummary summary;
            if (scope == ToolScope.file)
            {
                if (string.IsNullOrEmpty(scopeName))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (filePath) is required when scope=file.")
                    };
                }

                result = await _diagnosticEngine.GetFileDiagnosticsAsync(scopeName, cancellationToken: cancellationToken);
                summary = result.Data;
            }
            else if (scope == ToolScope.project)
            {
                if (string.IsNullOrEmpty(scopeName))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (projectName) is required when scope=project.")
                    };
                }

                result = await _diagnosticEngine.GetProjectDiagnosticsAsync(scopeName, cancellationToken: cancellationToken);
                summary = result.Data;
            }
            else if (scope == ToolScope.solution)
            {
                result = await _diagnosticEngine.GetSolutionDiagnosticsAsync(maxDetails, cancellationToken: cancellationToken);
                summary = result.Data;
            }
            else
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception, $"Unhandled scope '{scope}'.")
                };
            }

            BuildResult? buildVerification = null;
            if (verify != BuildVerifyLevel.noBuild)
            {
                var buildRun = verify == BuildVerifyLevel.fullBuild
                    ? await _buildEngine.RunFullBuildAsync(cancellationToken)
                    : await _buildEngine.RunQuickBuildAsync(scope, scopeName, maxDetails, cancellationToken);
                buildRun.TryGetData(out buildVerification);
            }

            if (!summarize)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = result.Data with { BuildVerification = buildVerification },
                    Findings = result.Findings
                };
            }

            var relevant = result.Data.Details.Where(d => d.Severity is "Error" or "Warning").ToList();
            var groups = relevant.GroupBySeverity(topN);
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = new DiagnosticsSummaryResult(TotalIssues: relevant.Count, Errors: summary.Errors, Warnings: summary.Warnings, TopIssues: groups, BuildVerification: buildVerification),
                Findings = result.Findings
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDiagnostics ({Scope}) failed", scope);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetDiagnostics")
            };
        }
    }

    // Raw stdout/stderr tails are diagnostic material for a run that went wrong. The engines return
    // them for every run on purpose - other consumers of the engine result may want them, and the
    // engine should not need to change for that - so the decision to drop them is made here, at the
    // tool boundary, where response size is what matters. On a clean run they only restate the
    // summary: for a solution-wide RunTest they were ~17 KB of dotnet output (40 lines x 10 projects)
    // and alone pushed the response over LargeResultHelper.OffloadThresholdBytes, forcing the
    // caller to page a saved file just to read the totals.
    private static TestRunResult WithoutTailsWhenClean(TestRunResult run)
    {
        var clean = run.RunSucceeded
            && run.Detail is null
            && (run.ProjectSummaries?.All(p => p.RunSucceeded) ?? true);
        return clean ? run with { StdoutTail = null, StderrTail = null } : run;
    }

    // Same rationale as the TestRunResult overload above: a successful build with no detail has
    // nothing in its tails that the error/warning counts do not already say.
    private static BuildResult WithoutTailsWhenClean(BuildResult build)
    {
        var clean = build.Outcome == BuildOutcome.Succeeded && build.Detail is null;
        return clean ? build with { StdoutTail = null, StderrTail = null } : build;
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s";

    // Short inline headline. The generic offload filter relays StatusMessage into its pointer
    // envelope, so even a response too large to inline (hundreds of failures) still carries the
    // totals without a GetLargeResult round trip.
    private static string SummarizeTestRun(TestRunResult run) =>
        $"{(run.RunSucceeded ? "Tests passed" : "Tests FAILED")}: {run.TotalCount} tests, {run.PassedCount} passed, " +
        $"{run.FailedCount} failed, {run.SkippedCount} skipped in {FormatDuration(run.Duration)} " +
        $"across {run.ProjectSummaries?.Count ?? 1} project(s).";

    private static string SummarizeBuild(BuildResult build) =>
        $"Build {build.Outcome}: {build.ErrorCount} error(s), {build.WarningCount} warning(s) in {FormatDuration(build.Duration)}.";

    public async Task<SentinelCallToolResult<object>> Build(ToolCallReason reason, BuildVerifyLevel level = BuildVerifyLevel.fullBuild,
        ToolScope scope = ToolScope.solution, string? scopeName = null, int maxDetails = 50,
        bool useScratchDir = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var rateLimitError = _workspaceManager.CheckRateLimit("Build", 10);
            if (rateLimitError is not null)
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.BuildFailed, rateLimitError) };
            }

            var result = level == BuildVerifyLevel.fullBuild
                ? await _buildEngine.RunFullBuildAsync(cancellationToken, maxDetails, useScratchDir)
                : await _buildEngine.RunQuickBuildAsync(scope, scopeName, maxDetails, cancellationToken);

            if (!result.TryGetData(out var buildResult))
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.BuildFailed, result.Error?.Message ?? "Build failed unexpectedly."), Findings = result.Findings };
            }

            var buildToolResult = await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                WithoutTailsWhenClean(buildResult), _workspaceManager.GetSolutionRoot(), "BuildResult", ResultWrapperType.Raw,
                workspaceVersion: _workspaceManager.WorkspaceVersion, cancellationToken: cancellationToken);
            return buildToolResult with { Findings = result.Findings, StatusMessage = SummarizeBuild(buildResult) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Build ({Level}) failed", level);
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.Exception, $"Build failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and dotnet is on PATH. Data: {ex.Message}") };
        }
    }

    public async Task<SentinelCallToolResult<object>> RunTest(ToolCallReason reason, ToolScope scope = ToolScope.solution,
        string? scopeName = null, string? filter = null, TestResultsFilter resultsType = TestResultsFilter.failed,
        int maxDetails = 50, int timeoutSeconds = 600, bool summary = false, bool useScratchDir = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rateLimitError = _workspaceManager.CheckRateLimit("RunTest", 10);
            if (rateLimitError is not null)
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.TestRunFailed, rateLimitError) };
            }

            var result = await _testRunEngine.RunAsync(scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, useScratchDir, cancellationToken);

            if (!result.TryGetData(out var testRunResult))
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.TestRunFailed, result.Error?.Message ?? "Test run failed unexpectedly."), Findings = result.Findings };
            }

            if (!testRunResult.RunCompleted)
            {
                return new SentinelCallToolResult<object>() { IsError = true, SuccessData = testRunResult, ErrorData = new ResultError(ToolErrorCode.TestRunFailed, testRunResult.Detail ?? "Test run did not complete."), WorkspaceVersion = _workspaceManager.WorkspaceVersion, Findings = result.Findings };
            }

            return new SentinelCallToolResult<object>() { IsError = false, SuccessData = WithoutTailsWhenClean(testRunResult), StatusMessage = SummarizeTestRun(testRunResult), WorkspaceVersion = _workspaceManager.WorkspaceVersion, Findings = result.Findings };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunTest failed");
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.Exception, $"RunTest failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and dotnet is on PATH. Data: {ex.Message}") };
        }
    }
}
