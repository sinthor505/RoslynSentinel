using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Basic;

/// <summary>
/// Plain DI-constructed implementation backing WorkspaceHealthMiscTools. Method bodies moved
/// verbatim from WorkspaceTools (Decision 7 step 2).
/// </summary>
public class WorkspaceHealthMiscImpl
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;
    private readonly BuildEngine _buildEngine;
    private readonly ILogger _logger;

    public WorkspaceHealthMiscImpl(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _config = new SentinelConfiguration();
        _buildEngine = new BuildEngine(workspaceManager, new DiagnosticEngine(workspaceManager));
        _logger = NullLogger<WorkspaceHealthMiscImpl>.Instance;
    }

    public WorkspaceHealthMiscImpl(IWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
        _buildEngine = new BuildEngine(workspaceManager, new DiagnosticEngine(workspaceManager));
        _logger = NullLogger<WorkspaceHealthMiscImpl>.Instance;
    }

    public WorkspaceHealthMiscImpl(IWorkspaceManager workspaceManager, SentinelConfiguration config,
    BuildEngine buildEngine)
    {
        _workspaceManager = workspaceManager;
        _config = config;
        _buildEngine = buildEngine;
        _logger = NullLogger<WorkspaceHealthMiscImpl>.Instance;
    }

    public WorkspaceHealthMiscImpl(IWorkspaceManager workspaceManager, SentinelConfiguration config,
        BuildEngine buildEngine, ILogger logger)
    {
        _workspaceManager = workspaceManager;
        _config = config;
        _buildEngine = buildEngine;
        _logger = logger;
    }

    private string UpdateFeaturesInternal(List<KeyValuePair<string, bool>> updates)
    {
        _config.BatchUpdateFeatureStatus(updates);
        return $"Updated {updates.Count} features.";
    }

    public async Task<SentinelCallToolResult<object>> Features(ToolCallReason reason, FeaturesAction action,
        List<string>? names = null, List<KeyValuePair<string, bool>>? enabled = null,
        int delaySeconds = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }

            return action switch
            {
                FeaturesAction.list => new SentinelCallToolResult<object> { IsSuccess = true, SuccessData = _config.GetFeatureStatuses() },
                FeaturesAction.get => new SentinelCallToolResult<object> { IsSuccess = true, SuccessData = _config.GetFeatureStatuses(names) },
                FeaturesAction.update => new SentinelCallToolResult<object> { IsSuccess = true, SuccessData = UpdateFeaturesInternal(enabled ?? []) },
                _ => new SentinelCallToolResult<object>
                {
                    IsSuccess = false,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown action '{action}'. Valid values: list, get, update.")
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Features ({Action}) failed", action);
            return new SentinelCallToolResult<object>
            {
                IsSuccess = false,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Features")
            };
        }
    }

    /// <summary>
    /// Returns a targeted workspace health report based on actual solution state.
    /// </summary>
    public Task<WorkspaceHealthReport> GetWorkspaceHealthAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Solution? currentSolution;
        try
        {
            currentSolution = _workspaceManager.CurrentSolution;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new WorkspaceHealthReport(IsOperational: false, HasLoadedSolution: false, LoadedSolutionPath: null, ProjectCount: 0, DocumentCount: 0, LoadErrors: [$"Workspace exception: {ex.Message}"], Summary: $"Workspace is NOT operational: {ex.Message}"));
        }

        var loadErrors = _workspaceManager.GetWorkspaceLoadErrors();
        if (currentSolution == null)
        {
            var msbuildNote = _workspaceManager.GetHealthComponents().MsBuildFound ? "" : " No MSBuild installation was detected - LoadSolution may fail; install Visual Studio, Build Tools, or the .NET SDK.";
            return Task.FromResult(new WorkspaceHealthReport(IsOperational: true, HasLoadedSolution: false, LoadedSolutionPath: null, ProjectCount: 0, DocumentCount: 0, LoadErrors: loadErrors, Summary: "Workspace is operational. No solution is currently loaded. " + "Call LoadSolution to load a .sln or .csproj file." + msbuildNote));
        }

        var projectCount = currentSolution.ProjectIds.Count;
        var documentCount = currentSolution.Projects.SelectMany(p => p.Documents).Count();
        var solutionPath = currentSolution.FilePath ?? _workspaceManager.SolutionPath;
        var status = _workspaceManager.GetWorkspaceStatus();
        return Task.FromResult(new WorkspaceHealthReport(IsOperational: true, HasLoadedSolution: true, LoadedSolutionPath: solutionPath, ProjectCount: projectCount, DocumentCount: documentCount, LoadErrors: loadErrors, Summary: $"Workspace operational. {projectCount} project(s) loaded, " + $"{documentCount} document(s). " + (loadErrors.Count > 0 ? $"{loadErrors.Count} load warning(s) recorded (non-fatal)." : "No load errors.") + (status.RequiresReload ? $" {status.StaleDocumentCount} file(s) changed on disk since the last load - call LoadSolution to refresh." : ""), StaleDocumentCount: status.StaleDocumentCount, RequiresReload: status.RequiresReload, SampleStaleFiles: status.SampleStaleFiles));
    }

    public async Task<SentinelCallToolResult<WorkspaceHealthReport, ResultError>> GetWorkspaceHealth(ToolCallReason reason, BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("GetWorkspaceHealth called");
        }

        try
        {
            var result = await GetWorkspaceHealthAsync(cancellationToken: cancellationToken);

            if (verify != BuildVerifyLevel.noBuild)
            {
                var buildResult = verify == BuildVerifyLevel.fullBuild
                    ? await _buildEngine.RunFullBuildAsync(cancellationToken)
                    : await _buildEngine.RunQuickBuildAsync(ToolScope.solution, null, 50, cancellationToken);
                if (buildResult.TryGetData(out var data))
                {
                    result = result with { BuildVerification = data };
                }
            }

            return new SentinelCallToolResult<WorkspaceHealthReport, ResultError>
            {
                IsSuccess = true,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetWorkspaceHealth failed");
            return new SentinelCallToolResult<WorkspaceHealthReport, ResultError>
            {
                IsSuccess = false,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetWorkspaceHealth")
            };
        }
    }
}
