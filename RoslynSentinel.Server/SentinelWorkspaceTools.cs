using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

[McpServerToolType]
public class SentinelWorkspaceTools
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly DiffEngine _diffEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly SolutionManagementEngine _solutionManagementEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<SentinelWorkspaceTools> _logger;

    public SentinelWorkspaceTools(
        PersistentWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        DiffEngine diffEngine,
        DiagnosticEngine diagnosticEngine,
        SolutionManagementEngine solutionManagementEngine,
        StructuralRefinementEngine structuralRefinementEngine,
        DependencyEngine dependencyEngine,
        SentinelConfiguration config,
        ILogger<SentinelWorkspaceTools> logger)
    {
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _diffEngine = diffEngine;
        _diagnosticEngine = diagnosticEngine;
        _solutionManagementEngine = solutionManagementEngine;
        _structuralRefinementEngine = structuralRefinementEngine;
        _dependencyEngine = dependencyEngine;
        _config = config;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Lists all available analysis/refactoring features and their current enabled status.")]
    public List<KeyValuePair<string, bool>> ListFeatures() => _config.GetFeatureStatuses();

    [McpServerTool]
    [Description("Batch updates the enabled status of one or more features.")]
    public string UpdateFeatures(List<KeyValuePair<string, bool>> updates)
    {
        _config.BatchUpdateFeatureStatus(updates);
        return $"Updated {updates.Count} features.";
    }

    [McpServerTool]
    [Description("Gets the enabled status of specific features by name.")]
    public List<KeyValuePair<string, bool>> GetFeatureStatus(List<string> featureNames)
        => _config.GetFeatureStatuses(featureNames);

    [McpServerTool]
    [Description("Lists all projects in the current solution.")]
    public async Task<List<object>> ListProjects()
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        return solution.Projects.Select(p => (object)new { p.Name, p.FilePath }).ToList();
    }

    [McpServerTool]
    [Description("Lists all files within a specific project.")]
    public async Task<List<string>> ListFiles(string projectName)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (project == null) throw new Exception($"Project '{projectName}' not found.");
        return project.Documents.Select(d => d.FilePath ?? d.Name).ToList();
    }

    [McpServerTool]
    [Description("Lists all project and NuGet dependencies for a specific project.")]
    public async Task<DependencyEngine.ProjectDependencyReport> ListDependencies(string projectName)
    {
        return await _dependencyEngine.GetProjectDependenciesAsync(projectName);
    }

    [McpServerTool]
    [Description("Loads a .NET solution into memory for persistent analysis.")]
    public async Task<string> LoadSolution(string solutionPath)
    {
        try
        {
            await _workspaceManager.LoadSolutionAsync(solutionPath);
            return $"Solution loaded successfully: {solutionPath}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load solution.");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("Checks the health of the Roslyn MCP server environment and workspace status.")]
    public async Task<HealthReport> Diagnose(string? solutionPath = null, bool verbose = false)
    {
        if (!string.IsNullOrEmpty(solutionPath))
        {
            await _workspaceManager.LoadSolutionAsync(solutionPath);
        }

        var components = _workspaceManager.GetHealthComponents();
        var workspace = _workspaceManager.GetWorkspaceStatus();
        var errors = new List<HealthIssue>();
        var warnings = new List<HealthIssue>();

        if (!components.MsBuildFound)
        {
            errors.Add(new HealthIssue("5001", "MSBuild not found. Install Visual Studio, Build Tools, or .NET SDK."));
        }

        if (workspace.SolutionLoaded && workspace.ProjectCount == 0)
        {
            var loadErrors = _workspaceManager.GetWorkspaceLoadErrors();
            foreach (var error in loadErrors)
            {
                errors.Add(new HealthIssue("5005", "Workspace load error", error, "Check project file for syntax errors, missing NuGet packages, or SDK version mismatches."));
            }
            
            if (loadErrors.Count == 0)
            {
                warnings.Add(new HealthIssue("4001", "Solution loaded but no projects found. Check for unsupported project types."));
            }
        }

        return new HealthReport(
            Healthy: errors.Count == 0,
            Components: components,
            Workspace: workspace,
            Capabilities: new List<string> { "diagnose", "load_solution", "refactor", "intelligence" },
            Errors: errors,
            Warnings: warnings
        );
    }

    [McpServerTool]
    [Description("Returns a list of files that have been modified externally (on disk) since the AI last synced.")]
    public List<string> GetExternalChanges() => _workspaceManager.GetExternalDrift();

    [McpServerTool]
    [Description("Clears the external drift list, indicating the AI has acknowledged and read the latest disk changes.")]
    public void AcknowledgeSync() => _workspaceManager.ClearDrift();

    [McpServerTool]
    [Description("Validates a proposed Unified Diff in-memory and returns compiler diagnostics.")]
    public async Task<DiagnosticReport> ValidateProposedDiff(string filePath, string unifiedDiff) 
        => await _validationEngine.ValidateDiffAsync(filePath, unifiedDiff);

    [McpServerTool]
    [Description("Applies a Unified Diff to a file and returns the resulting full content.")]
    public async Task<string> ApplyProposedDiff(string filePath, string unifiedDiff)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");
        var oldText = await document.GetTextAsync();
        return _diffEngine.ApplyDiff(oldText, unifiedDiff).ToString();
    }

    [McpServerTool]
    [Description("Commits a dictionary of file paths and their new contents to disk. Supports automatic retry for transient file locks.")]
    public async Task<PersistentWorkspaceManager.ApplyChangesResult> ApplyProposedChanges(Dictionary<string, string> changes, int retryCount = 3)
    {
        return await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount);
    }

    [McpServerTool]
    [Description("Retries committing previously failed file changes using server-side cached content. Token-efficient: no need to re-send file contents.")]
    public async Task<PersistentWorkspaceManager.ApplyChangesResult> RetryFailedChanges(List<string>? specificFiles = null, int retryCount = 3)
    {
        return await _workspaceManager.RetryFailedChangesAsync(specificFiles, retryCount);
    }

    [McpServerTool]
    [Description("Commits a previously staged set of changes (from a refactoring tool) to disk.")]
    public async Task<PersistentWorkspaceManager.ApplyChangesResult> ApplyStagedChanges(string changeId, int retryCount = 3)
    {
        return await _workspaceManager.ApplyStagedChangesAsync(changeId, retryCount);
    }

    [McpServerTool]
    [Description("Returns the full file content of a staged change set for inspection.")]
    public Dictionary<string, string> GetStagedChanges(string changeId)
    {
        return _workspaceManager.GetStagedChanges(changeId);
    }

    [McpServerTool]
    [Description("Manually removes a staged change set from the server-side buffer without applying it.")]
    public string DiscardStagedChanges(string changeId)
    {
        var success = _workspaceManager.DiscardStagedChanges(changeId);
        return success ? $"Staged change '{changeId}' discarded." : $"Staged change '{changeId}' not found.";
    }

    [McpServerTool]
    [Description("Gets all compiler errors and warnings for a specific file.")]
    public async Task<DiagnosticSummary> GetFileDiagnostics(string filePath) => await _diagnosticEngine.GetFileDiagnosticsAsync(filePath);

    [McpServerTool]
    [Description("Safely deletes a symbol (method, property, class) only if it has zero usages in the entire codebase.")]
    public async Task<string> SafeDelete(string filePath, int line, int column) 
        => await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePath, line, column);

    [McpServerTool]
    [Description("Synchronizes the filename to match the primary type declared within it.")]
    public async Task<string> SyncTypeAndFilename(string filePath) 
        => await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePath);

    [McpServerTool]
    [Description("Creates a new project and adds it to the current solution.")]
    public async Task<string> CreateProject(string projectName, string projectType = "console") 
        => await _solutionManagementEngine.CreateProjectAsync(projectName, projectType);
}
