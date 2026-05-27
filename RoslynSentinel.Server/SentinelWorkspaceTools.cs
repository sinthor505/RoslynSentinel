using System.Collections.Generic;
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
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (project == null) throw new InvalidOperationException($"Project '{projectName}' not found.");
            return project.Documents.Select(d => d.FilePath ?? d.Name).ToList();
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListFiles unexpected exception for project '{ProjectName}'", projectName);
            throw new InvalidOperationException($"ListFiles for project '{projectName}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
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
        try
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
                warnings.Add(new HealthIssue("W5001", "MSBuild not found. If the workspace loaded successfully, this can be ignored.", null, "Install Visual Studio, Build Tools, or .NET SDK for full MSBuild support."));
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnose unexpected exception");
            throw new InvalidOperationException($"Diagnose failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
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
    [Description("Validates a dictionary of proposed file changes in-memory and returns compiler diagnostics. Use this for a 'dry run' before applying changes.")]
    public async Task<DiagnosticReport> ValidateProposedChanges(Dictionary<string, string> changes)
    {
        try
        {
            return await _validationEngine.ValidateChangesAsync(changes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ValidateProposedChanges unexpected exception");
            throw new InvalidOperationException($"ValidateProposedChanges failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Validates a previously staged set of changes (from a refactoring tool) in-memory and returns compiler diagnostics. Use this for a 'dry run' before applying staged changes.")]
    public async Task<DiagnosticReport> ValidateStagedChanges(string changeId)
    {
        var changes = _workspaceManager.GetStagedChanges(changeId);
        return await _validationEngine.ValidateChangesAsync(changes);
    }

    [McpServerTool]
    [Description("Applies a Unified Diff to a file and writes the result to disk. Returns ApplyChangesResult with SucceededFiles, WorkspaceInSync, and WorkspaceVersion. To preview the result without writing, use validate_proposed_diff instead.")]
    public async Task<PersistentWorkspaceManager.ApplyChangesResult> ApplyProposedDiff(string filePath, string unifiedDiff)
    {
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var document = solution.Projects.SelectMany(p => p.Documents)
                .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
            if (document == null) throw new InvalidOperationException("File not found.");
            var oldText = await document.GetTextAsync();
            var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff).ToString();
            return await _workspaceManager.ApplyProposedChangesAsync(
                new Dictionary<string, string> { [filePath] = newContent });
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyProposedDiff unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"ApplyProposedDiff for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
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

    [McpServerTool]
    [Description("Gets all compiler errors and warnings for a specific project.")]
    public async Task<DiagnosticSummary> GetProjectDiagnostics(string projectName)
        => await _diagnosticEngine.GetProjectDiagnosticsAsync(projectName);

    [McpServerTool]
    [Description("""
        Gets compiler errors and warnings across the entire loaded solution.
        Results are capped at maxDetails entries in the detail list (default 50) to keep
        output manageable — the Errors/Warnings counts always reflect the full totals.
        Errors are always sorted before warnings so the cap never hides an error.
        File paths are relative to the solution root for compact output.
        For complete diagnostics on a specific project, use get_project_diagnostics instead.
        Blazor source-generator false positives (CS0234/CS0246/CS0103) are automatically
        suppressed in Razor-component projects.
        """)]
    public async Task<DiagnosticSummary> GetSolutionDiagnostics(int maxDetails = 50)
        => await _diagnosticEngine.GetSolutionDiagnosticsAsync(maxDetails);

    [McpServerTool]
    [Description("Moves all files under a specific folder from a source project to a new target project, preserving folder structure.")]
    public async Task<string> SplitProjectByFolder(string sourceProjectName, string folderName, string targetProjectName)
        => await _solutionManagementEngine.SplitProjectByFolderAsync(sourceProjectName, folderName, targetProjectName);
}
