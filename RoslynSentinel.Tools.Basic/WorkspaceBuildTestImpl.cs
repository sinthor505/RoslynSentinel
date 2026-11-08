using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
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

                // The engine takes an already-resolved path (see PathCaseLookupRegressionTests); resolve the
                // wire form (solution-root-relative '/' or absolute) here, and reuse it for the quickBuild
                // file scope below so both agree on the file.
                scopeName = _workspaceManager.ResolveFromWire(scopeName).Absolute;
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
                var buildRun = verify is BuildVerifyLevel.fullBuild or BuildVerifyLevel.fullBuildThrowaway
                    ? await _buildEngine.RunFullBuildAsync(cancellationToken, level: verify)
                    : await _buildEngine.RunQuickBuildAsync(scope, scopeName, maxDetails, cancellationToken);
                if (buildRun.TryGetData(out var rawBuild))
                {
                    // Project like Build does (breakdown, root-cause order); every error is inline and the result marshaller pages a long list.
                    buildVerification = BuildResultProjector.Project(
                        rawBuild, new BuildProjection(maxDetails, IncludeOutput: false, IncludeWarnings: false), await GetTransitiveDependenciesAsync(cancellationToken));
                }
            }

            if (!summarize)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = result.Data with { BuildVerification = buildVerification },
                    Findings = WithForeignWriterFindings(result.Findings, summary)
                };
            }

            var relevant = result.Data.Details.Where(d => d.Severity is "Error" or "Warning").ToList();
            var groups = relevant.GroupBySeverity(topN);
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = new DiagnosticsSummaryResult(TotalIssues: relevant.Count, Errors: summary.Errors, Warnings: summary.Warnings, TopIssues: groups, BuildVerification: buildVerification),
                Findings = WithForeignWriterFindings(result.Findings, summary)
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
    // summary: for a solution-wide RunTestTool they were ~17 KB of dotnet output (40 lines x 10 projects)
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
        // A throwaway build's detail is only the "output went to ..." note; any problem text comes first (see BuildEngine).
        var clean = build.Outcome == BuildOutcome.Succeeded
            && (build.Detail is null || build.Detail.StartsWith(BuildEngine.ThrowawayDetailPrefix, StringComparison.Ordinal));
        return clean ? build with { StdoutTail = null, StderrTail = null } : build;
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s";

    // Short inline headline. The generic offload filter relays StatusMessage into its pointer
    // envelope, so even a response too large to inline (hundreds of failures) still carries the
    // totals without a GetLargeResultTool round trip.
    public static string SummarizeTestRun(TestRunResult run)
    {
        var headline =
            $"{(run.RunSucceeded ? "Tests passed" : "Tests FAILED")}: {run.TotalCount} tests, {run.PassedCount} passed, " +
            $"{run.FailedCount} failed, {run.SkippedCount} skipped in {FormatDuration(run.Duration)} " +
            $"across {run.ProjectSummaries?.Count ?? 1} project(s).";

        // A project can exit non-zero (build or adapter error) without any failed test; name it so the
        // caller does not have to open the large result to find out which project broke.
        if (!run.RunSucceeded && run.FailedCount == 0 && run.ProjectSummaries is { } projects)
        {
            headline += " No test failed, but " + string.Join(", ", projects.Where(p => !p.RunSucceeded).Select(p => p.ProjectName)) +
                " exited non-zero (build or adapter error); see StdoutTail.";
        }

        // A failed run ends with one concrete re-run of its first failing test, so the caller does not
        // have to open the large result to pick one. The row carries no project name, so only the filter is passed.
        var firstFailed = run.Results.FirstOrDefault(r => r.Outcome == TestOutcome.Failed);
        if (!run.RunSucceeded && run.FailedCount > 0 && firstFailed is not null)
        {
            headline += " " + ToolCallHint.Next("RunTest", "to re-run only the first failing test.", ("filter", $"FullyQualifiedName~{firstFailed.TestName}"));
        }

        return headline;
    }

    private static string SummarizeBuild(BuildResult build) =>
        $"Build {build.Outcome}: {build.ErrorCount} error(s), {build.WarningCount} warning(s) in {FormatDuration(build.Duration)}.";

    // Transitive project-dependency names per project, so the projector can tell root-cause projects from
    // projects that only failed because an upstream project did. Null (every project with errors then counts
    // as a root cause) when the solution cannot be read.
    private async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>?> GetTransitiveDependenciesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            var graph = solution.GetProjectDependencyGraph();
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var project in solution.Projects)
            {
                if (!map.TryGetValue(project.Name, out var dependencyNames))
                {
                    dependencyNames = new HashSet<string>(StringComparer.Ordinal);
                    map[project.Name] = dependencyNames;
                }

                foreach (var dependencyId in graph.GetProjectsThatThisProjectTransitivelyDependsOn(project.Id))
                {
                    var dependencyName = solution.GetProject(dependencyId)?.Name;
                    if (dependencyName is not null)
                    {
                        dependencyNames.Add(dependencyName);
                    }
                }
            }

            return map.ToDictionary(entry => entry.Key, entry => (IReadOnlySet<string>)entry.Value, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Build: could not read the project dependency graph; treating every project with errors as a root cause");
            return null;
        }
    }

    // Groups a failed row by its test class: "Namespace.Class.Method(args)" -> "Namespace.Class".
    private static string TestClassOf(TestCaseResult row)
    {
        var name = row.TestName;
        var paren = name.IndexOf('(');
        if (paren >= 0)
        {
            name = name[..paren];
        }

        name = name.Trim();
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[..dot] : name;
    }

    // Appends Info findings for error files whose latest edit attribution ledger entry was written by another agent.
    // Returns existing unchanged (same reference) when there are none, so single-agent output stays byte-identical.
    private IReadOnlyList<Finding> WithForeignWriterFindings(IReadOnlyList<Finding> existing, IEnumerable<DiagnosticInfo> diagnostics)
    {
        var errorFiles = diagnostics.Where(d => d.Severity == "Error").Select(d => d.FilePath.ToString());
        var attribution = EditAttributionFindings.ForCurrentCaller(_workspaceManager, errorFiles);
        return attribution.Count == 0 ? existing : existing.Concat(attribution).ToList();
    }

    // GetDiagnostics form: solution scope caps Details and relativizes its paths, so it carries the uncapped absolute list instead.
    private IReadOnlyList<Finding> WithForeignWriterFindings(IReadOnlyList<Finding> existing, DiagnosticSummary summary)
    {
        if (summary.ErrorFilePaths is null)
        {
            return WithForeignWriterFindings(existing, summary.Details);
        }

        var attribution = EditAttributionFindings.ForCurrentCaller(_workspaceManager, summary.ErrorFilePaths);
        return attribution.Count == 0 ? existing : existing.Concat(attribution).ToList();
    }

    public async Task<SentinelCallToolResult<object>> Build(ToolCallReason reason, BuildVerifyLevel level = BuildVerifyLevel.fullBuildThrowaway,
        ToolScope scope = ToolScope.solution, string? scopeName = null, int maxDetails = 20,
        bool useScratchDir = false, bool includeOutput = false, bool includeWarnings = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rateLimitError = _workspaceManager.CheckRateLimit("Build", 10);
            if (rateLimitError is not null)
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.BuildFailed, rateLimitError) };
            }

            var isFullBuild = level is BuildVerifyLevel.fullBuild or BuildVerifyLevel.fullBuildThrowaway;

            // file scope (quick and full builds): the engine takes an already-resolved path, so resolve the wire form
            // (solution-root-relative '/' or absolute) here. Project scope is a name; full builds map file scope to its containing project.
            if (scope == ToolScope.file && !string.IsNullOrEmpty(scopeName))
            {
                scopeName = _workspaceManager.ResolveFromWire(scopeName).Absolute;
            }

            // useScratchDir is obsolete: true with level=fullBuild means the throwaway level.
            var fullBuildLevel = level == BuildVerifyLevel.fullBuild && useScratchDir ? BuildVerifyLevel.fullBuildThrowaway : level;
            var result = isFullBuild
                ? await _buildEngine.RunFullBuildAsync(cancellationToken, maxDetails, level: fullBuildLevel, scope: scope, scopeName: scopeName)
                : await _buildEngine.RunQuickBuildAsync(scope, scopeName, maxDetails, cancellationToken);

            if (!result.TryGetData(out var buildResult))
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.BuildFailed, result.Error?.Message ?? "Build failed unexpectedly."), Findings = result.Findings };
            }

            var transitiveDependencies = await GetTransitiveDependenciesAsync(cancellationToken);
            var projected = BuildResultProjector.Project(
                buildResult, new BuildProjection(maxDetails, includeOutput, includeWarnings), transitiveDependencies);

            // includeOutput=true asks for the tails even from a clean build, so only strip them otherwise.
            var buildToolResult = new SentinelCallToolResult<object> { IsError = false, SuccessData = includeOutput ? projected : WithoutTailsWhenClean(projected), WorkspaceVersion = _workspaceManager.WorkspaceVersion };
            var buildSummary = SummarizeBuild(buildResult);
            if (buildResult.Outcome == BuildOutcome.Failed)
            {
                // Surface external drift: a failed build may be caused by a file changed on disk under us.
                var driftHint = DriftMessages.BuildHint(_workspaceManager.GetExternalFileChanges());
                var failureMessage = (driftHint is not null ? buildSummary + " " + driftHint : buildSummary) + " " + ToolCallHint.Next("Build", "after fixing the errors in this result (root-cause project first).", ("level", "quickBuild"));
                // The build ran and failed: report isError:true like RunTestTool does for an incomplete run, but keep the
                // BuildResult payload (inline or offloaded) so the caller still gets the error details.
                return buildToolResult with { IsError = true, ErrorData = new ResultError(ToolErrorCode.BuildFailed, failureMessage), Findings = WithForeignWriterFindings(result.Findings, buildResult.Errors), StatusMessage = failureMessage };
            }

            return buildToolResult with { Findings = result.Findings, StatusMessage = buildSummary };
        }
        catch (ToolNotFoundException ex)
        {
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Build") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Build ({Level}) failed", level);
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.Exception, $"Build failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and dotnet is on PATH. Data: {ex.Message}") };
        }
    }

    public async Task<SentinelCallToolResult<object>> RunTest(ToolCallReason reason, ToolScope scope = ToolScope.solution,
        string? scopeName = null, string? filter = null, TestResultsFilter resultsType = TestResultsFilter.failed,
        int maxDetails = 50, int timeoutSeconds = 600, bool summary = false, bool useScratchDir = false, bool includeOutput = false,
        Dictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rateLimitError = _workspaceManager.CheckRateLimit("RunTest", 10);
            if (rateLimitError is not null)
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.TestRunFailed, rateLimitError) };
            }

            var result = await _testRunEngine.RunAsync(scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, environment: environment, cancellationToken: cancellationToken);

            if (!result.TryGetData(out var testRunResult))
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.TestRunFailed, result.Error?.Message ?? ("Test run failed unexpectedly. " + ToolCallHint.Next("Build", "to rule out compile errors, then retry the test run.", ("level", "quickBuild")))), Findings = result.Findings };
            }

            var projected = TestRunResultProjector.Project(testRunResult, resultsType, includeOutput: includeOutput);
            if (summary)
            {
                projected = projected with { OmittedResultCount = 0 };
            }

            if (!testRunResult.RunCompleted)
            {
                var incompleteMessage = (testRunResult.Detail ?? "Test run did not complete.") + " " + ToolCallHint.Next("Build", "to rule out compile errors, then retry the test run.", ("level", "quickBuild"));
                return new SentinelCallToolResult<object>() { IsError = true, SuccessData = projected, ErrorData = new ResultError(ToolErrorCode.TestRunFailed, incompleteMessage), StatusMessage = incompleteMessage, WorkspaceVersion = _workspaceManager.WorkspaceVersion, Findings = result.Findings };
            }

            var failedRows = testRunResult.Results.Where(r => r.Outcome == TestOutcome.Failed).ToList();
            var toolResult = new SentinelCallToolResult<object> { IsError = false, SuccessData = WithoutTailsWhenClean(projected), TotalRecords = testRunResult.FailedCount, WorkspaceVersion = _workspaceManager.WorkspaceVersion, StatusMessage = SummarizeTestRun(testRunResult), ListSummary = SummarizeListResult.Build(failedRows, TestClassOf) };
            var finalResult = toolResult with { Findings = result.Findings, StatusMessage = SummarizeTestRun(testRunResult) };

            // The unprojected rows (full messages, stack traces, Output) are stored by the marshaller under
            // successData.fullResults, not inline; the response's largeResult.resultId + that path reach them via
            // GetLargeResult. Only the rows are detached: the counts are already inline.
            if (testRunResult.FailedCount > 0 && !summary)
            {
                finalResult.Detach("fullResults", testRunResult.Results.ToList());
            }

            return finalResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunTest failed");
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.Exception, $"RunTest failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and dotnet is on PATH. Data: {ex.Message}") };
        }
    }
}
