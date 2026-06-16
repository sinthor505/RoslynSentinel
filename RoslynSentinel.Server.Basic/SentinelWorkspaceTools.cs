using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

/// <summary>Structural outline entry returned by get_file_outline.</summary>
public record OutlineItem(string Kind, string Name, string? Container, int StartLine, int EndLine);

/// <summary>Single text-search hit returned by search_solution_text.</summary>
public record TextSearchMatch(FilePath filePath, int Line, int Column, string Preview);

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
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
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
        ProjectConsistencyEngine projectConsistencyEngine,
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
        _projectConsistencyEngine = projectConsistencyEngine;
        _config = config;
        _logger = logger;
    }

    [McpServerTool(Name = "Features")]
    [Produces(DataTag.Report)]
    [Description("""
        Queries or updates analysis/refactoring feature flags. action values: list (all features and current status; names/enabled ignored), get (enabled status of specific features by names), update (batch-update via enabled as [{ Key: featureName, Value: bool }] pairs).
        """)]
    public object Features(
        string action,
        List<string>? names = null,
        List<KeyValuePair<string, bool>>? enabled = null)
    {
        try
        {
            return action switch
            {
                "list" => (object)_config.GetFeatureStatuses(),
                "get" => _config.GetFeatureStatuses(names),
                "update" => (object)UpdateFeaturesInternal(enabled ?? []),
                _ => (object)$"Unknown action '{action}'. Valid: list, get, update."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Features ({Action}) failed", action);
            return $"Features failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private string UpdateFeaturesInternal(List<KeyValuePair<string, bool>> updates)
    {
        _config.BatchUpdateFeatureStatus(updates);
        return $"Updated {updates.Count} features.";
    }

    [McpServerTool(Name = "ListSolutionItems")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.ProjectList)]
    [Produces(DataTag.DependencyList)]
    [Description("Lists projects, files, or dependencies in the loaded solution. kind: projects (all projects), files (all source files in a project, requires projectName), dependencies (NuGet and project references for a project, requires projectName).")]
    public async Task<ToolResult<object>> ListSolutionItems(
        [ExternalInputRequired(DataTag.Scope)] string kind,
        [Consumes(DataTag.ProjectName)] string? projectName = null)
    {
        try
        {
            if (kind == "projects")
            {
                var solution = await _workspaceManager.GetBranchedSolutionAsync();
                return new ToolResult<object>() { Success = true, Data = solution.Projects.Select(p => (object)new { p.Name, p.FilePath }).ToList() };
            }
            if (kind == "files")
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "projectName is required when kind=files.") };
                }
                try
                {
                    var solution = await _workspaceManager.GetBranchedSolutionAsync();
                    var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
                    if (project == null)
                    {
                        return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Project '{projectName}' not found.") };
                    }
                    return new ToolResult<object>() { Success = true, Data = project.Documents.Select(d => d.FilePath ?? d.Name).ToList<object>() };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "List files unexpected exception for project '{ProjectName}'", projectName);
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"List files for project '{projectName}' failed: {ex.GetType().Name}: {ex.Message}") };
                }
            }
            if (kind == "dependencies")
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "projectName is required when kind=dependencies.") };
                }
                var result = await _dependencyEngine.GetProjectDependenciesAsync(projectName);
                return new ToolResult<object>() { Success = true, Data = result };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown kind '{kind}'. Valid values: projects, files, dependencies.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List ({Kind}) failed", kind);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"List failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ListWorkspaceSolutions")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.SolutionList)]
    [Description("""
    Lists all solution files (*.sln, *.slnx) under a given directory.
    Call this before load_solution when the solution path is not known.
    Pass the workspace folder path from your workspace_info context.
    Returns absolute paths suitable for passing directly to load_solution.
    If HasLoadedSolution is false in get_workspace_health, call this tool first.
    """)]
    public ToolResult<List<SolutionFileInfo>> ListWorkspaceSolutions(string workspacePath)
    {
        if (!Directory.Exists(workspacePath))
        {
            return new ToolResult<List<SolutionFileInfo>>
            {
                Success = false,
                Error = new ResultError("InvalidArgument", $"Directory not found: '{workspacePath}'")
            };
        }

        var files = Directory.EnumerateFiles(workspacePath, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(workspacePath, "*.slnx", SearchOption.AllDirectories))
            .OrderBy(p => p)
            .Select(p => new SolutionFileInfo(
                Path: p,
                Format: Path.GetExtension(p).TrimStart('.').ToLowerInvariant()))
            .ToList();

        return new ToolResult<List<SolutionFileInfo>>
        {
            Success = true,
            Data = files,
            TotalRecords = files.Count
        };
    }

    public sealed record SolutionFileInfo(string Path, string Format);

    [McpServerTool(Name = "LoadSolution")]
    [Produces(DataTag.ResultOnly)]
    [Description("Loads a .NET solution file into memory for persistent analysis. Must be called before any operation that returns ErrorCode=\"SolutionNotLoaded\".")]
    public async Task<ToolResult<object>> LoadSolution([Consumes(DataTag.SolutionFilepath, required: true)] string solutionPath)
    {
        try
        {
            await _workspaceManager.LoadSolutionAsync(solutionPath);

            if (_workspaceManager.GetSolutionRoot() != null)
            {
                return new ToolResult<object>() { Success = true, Data = $"Solution loaded: {solutionPath}" };
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"LoadSolution failed: Workspace root is null after loading '{solutionPath}'.") };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadSolution failed for '{SolutionPath}'", solutionPath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"LoadSolution failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "Diagnose")]
    [Produces(DataTag.ResultOnly)]
    [Description("Checks Roslyn MCP server environment and workspace status. solutionPath re-checks a specific path. verbose=true → extended output. Prefer get_workspace_health — this tool has a known false-negative bug where healthy workspaces are reported as unhealthy.")]
    public async Task<HealthReport> Diagnose([Consumes(DataTag.SolutionFilepath)] string? solutionPath = null, bool verbose = false)
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
            return new HealthReport(
                Healthy: false,
                Components: new HealthComponents(false, "", false, null, false, null),
                Workspace: new WorkspaceStatus(0, false, null, 0, 0),
                Capabilities: new List<string>(),
                Errors: new List<HealthIssue> { new HealthIssue("E9000", "Diagnose failed", $"{ex.GetType().Name}: {ex.Message}") },
                Warnings: new List<HealthIssue>()
            );
        }
    }

    [McpServerTool(Name = "ListExternalDiskChanges")]
    [Produces(DataTag.FileList)]
    [Description("Returns files modified on disk since the AI last synced. No parameters.")]
    public List<string> ListExternalDiskChanges() => _workspaceManager.GetExternalDrift();

    [McpServerTool(Name = "ClearExternalDrift")]
    [Produces(DataTag.ResultOnly)]
    [Description("Clears the external-drift list after the AI has read the latest disk changes. No parameters.")]
    public void ClearExternalDrift() => _workspaceManager.ClearDrift();

    [McpServerTool(Name = "ProposedChange")]
    [Produces(DataTag.ChangeId)]
    [Description("""
        Applies or validates a proposed change set. changesetFormat=files → supply changes dict (filePath → newContent). changesetFormat=diff → supply filePath + unifiedDiff. action: apply (write to disk) or validate (dry-run compiler diagnostics). validateOnApply=true (default) → runs delta compile before writing; returns validation errors without touching disk if new errors are introduced. Set false only for intentional intermediate-broken-state edits. On successful apply, returns ApplyChangesResult with UndoChangeId — pass to undo_last_apply to revert.
        """)]
    public async Task<ToolResult<object>> ProposedChange(
        [ExternalInputRequired(DataTag.ChangeseFormat)] string changesetFormat,
        [ExternalInputRequired(DataTag.Action)] string action,
        [ExternalInputRequired(DataTag.OperationId)] Dictionary<FilePath, string>?
        changes = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        [ToolOption(ToolOptionTag.ValidateOnApply)] bool validateOnApply = true)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);

            if (changesetFormat == "files")
            {
                if (changes == null)
                {
                    return new ToolResult<object>() { Success = false, Data = "changes is required when changesetFormat=files." };
                }
                if (action == "apply")
                {
                    // ── Validate-on-apply gate ────────────────────────────────────
                    // Runs a delta compile (introduced errors only) before writing.
                    // Returns the DiagnosticReport without touching disk if new errors found.
                    if (validateOnApply)
                    {
                        DiagnosticReport validation;
                        try
                        {
                            validation = await _validationEngine.ValidateChangesAsync(changes);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "ProposedChange pre-apply validate unexpected exception");
                            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange pre-apply validate failed: {ex.GetType().Name}: {ex.Message}") };
                        }

                        if (!validation.Success)
                        {
                            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange pre-apply validate failed: {validation.Diagnostics.ToJson()}") };
                        }
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount);
                    await WriteBlobForApplyAsync("proposed_change", result);
                    return new ToolResult<object>() { Success = true, Data = result };
                }
                if (action == "validate")
                {
                    try
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(changes);
                        return new ToolResult<object>() { Success = validationResult.Success, Data = validationResult, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange validate failed: {validationResult.Diagnostics}") };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ProposedChange validate unexpected exception");
                        return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange validate failed: {ex.GetType().Name}: {ex.Message}") };
                    }
                }
            }
            else if (changesetFormat == "diff")
            {
                if (!filePath.Validated || string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath and unifiedDiff are required when changesetFormat=diff.") };
                }
                if (action == "apply")
                {
                    try
                    {
                        var solution = await _workspaceManager.GetBranchedSolutionAsync();
                        var document = solution.Projects.SelectMany(p => p.Documents)
                            .FirstOrDefault(d => d.Name == filePath.Absolute || d.FilePath == filePath.Absolute);
                        if (document == null)
                        {
                            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "File not found.") };
                        }
                        var oldText = await document.GetTextAsync();
                        var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff).ToString();
                        var targetPath = document.FilePath ?? filePath;
                        var diffChanges = new Dictionary<FilePath, string> { [targetPath] = newContent };

                        // ── Validate-on-apply gate (diff path) ───────────────────
                        if (validateOnApply)
                        {
                            var validation = await _validationEngine.ValidateChangesAsync(diffChanges);
                            if (!validation.Success)
                            {
                                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange diff validate failed: {validation.Diagnostics}") };
                            }
                        }

                        var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges);
                        await WriteBlobForApplyAsync("proposed_change", result);
                        return new ToolResult<object>() { Success = true, Data = result };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ProposedChange diff apply unexpected exception for '{FilePath}'", filePath);
                        return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange diff apply for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}") };
                    }
                }
                if (action == "validate")
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePath.Absolute, unifiedDiff);
                    return new ToolResult<object>() { Success = validationResult.Success, Data = validationResult, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange diff validate failed: {validationResult}") };
                }
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown changesetFormat '{changesetFormat}' or action '{action}'. Valid formats: files, diff. Valid actions: apply, validate.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProposedChange ({ChangesetFormat}/{Action}) failed", changesetFormat, action);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries committing previously failed file changes using server-side cached content — no need to re-send file contents. specificFiles limits the retry to a subset of files. retryCount defaults to 3.")]
    public async Task<ToolResult<object>> RetryFailedChanges(
        [Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3)
    {
        try
        {
            return new ToolResult<object>() { Success = true, Data = await _workspaceManager.RetryFailedChangesAsync(specificFiles, retryCount) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetryFailedChanges failed");
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"RetryFailedChanges failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "StagedChange")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Manages a staged change set produced by a refactoring tool.
        action: apply (write to disk), get (return file contents dict), validate (dry-run compiler diagnostics), discard (remove without applying).
        changeId: the id returned by the refactoring tool that staged the changes.
        retryCount: only used for action=apply (default 3).
        validateOnApply: when true (default), runs a delta compile before writing and returns validation
        errors without touching disk if new errors are introduced. Set false only for intentional
        intermediate-broken-state edits that are part of a multi-step refactor.
        On successful apply, the same changeId can be passed to undo_last_apply to revert.
        """)]
    public async Task<ToolResult<object>> StagedChange(
        [Consumes(DataTag.Action, required: true)] string action,
        [Consumes(DataTag.ChangeId, required: true)] string changeId,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        [ToolOption(ToolOptionTag.ValidateOnApply)] bool validateOnApply = true)
    {
        try
        {
            if (action == "apply")
            {
                // ── Validate-on-apply gate ────────────────────────────────────────
                if (validateOnApply)
                {
                    var stagingChanges = _workspaceManager.GetStagedChanges(changeId);
                    DiagnosticReport validation;
                    try
                    {
                        validation = await _validationEngine.ValidateChangesAsync(stagingChanges);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StagedChange pre-apply validate unexpected exception for '{ChangeId}'", changeId);
                        return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"StagedChange pre-apply validate failed: {ex.GetType().Name}: {ex.Message}") };
                    }

                    if (!validation.Success)
                    {
                        return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"StagedChange pre-apply validate failed: {validation.Diagnostics.ToJson()}") };
                    }
                }

                var result = await _workspaceManager.ApplyStagedChangesAsync(changeId, retryCount);
                // Write blob using the existing staged changeId so undo_last_apply(changeId) resolves it.
                await WriteBlobForApplyAsync("staged_change", result, changeId);
                string resultMessage = result.SucceededFiles.Count > 0
                    ? $"Staged change '{changeId}' applied successfully with {result.SucceededFiles.Count} files changed."
                    : $"Staged change '{changeId}' apply failed: no files were changed.";
                _logger.LogInformation(resultMessage);
                return new ToolResult<object>() { Success = true, Data = resultMessage };
            }
            if (action == "get")
            {
                return new ToolResult<object>() { Success = true, Data = _workspaceManager.GetStagedChanges(changeId) };
            }
            if (action == "validate")
            {
                var stagingChanges = _workspaceManager.GetStagedChanges(changeId);
                return new ToolResult<object>() { Success = true, Data = await _validationEngine.ValidateChangesAsync(stagingChanges) };
            }
            if (action == "discard")
            {
                var success = _workspaceManager.DiscardStagedChanges(changeId);
                return success ? new ToolResult<object>() { Success = true, Data = $"Staged change '{changeId}' discarded." } : new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Staged change '{changeId}' not found.") };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown action '{action}'. Valid values: apply, get, validate, discard.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StagedChange ({Action}) failed for '{ChangeId}'", action, changeId);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"StagedChange failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    /// <summary>
    /// Writes a forensic blob for a completed apply so undo_last_apply can revert it.
    /// Uses pre-images from ApplyChangesResult.PreImages (populated by ApplyProposedChangesAsync).
    /// blobChangeId: if provided, uses this id for the blob filename (staged_change reuses its
    /// staged id); if null, mints a fresh id (proposed_change path).
    /// Logs a warning but does not throw on blob write failure — apply already succeeded.
    /// </summary>
    private async Task WriteBlobForApplyAsync(
        string toolName,
        PersistentWorkspaceManager.ApplyChangesResult result,
        string? blobChangeId = null)
    {
        if (result.SucceededFiles.Count == 0)
        {
            return;
        }

        var changeId = blobChangeId ?? Guid.NewGuid().ToString("n")[..8];

        var items = result.SucceededFiles.Select(f =>
        {
            string? before = null;
            result.PreImages?.TryGetValue(f, out before);
            return new OperationItemRecord
            {
                FilePath = f,
                Outcome = OperationOutcome.Succeeded,
                BeforeSource = before,
            };
        }).ToList();

        var blobName = await OperationBlobWriter.WriteAsync(toolName, changeId, items,
            _workspaceManager.GetSolutionRoot());

        // OperationBlobWriter returns a diagnostic string (not an exception) on failure.
        if (blobName.StartsWith('('))
        {
            _logger.LogWarning("Blob write failed for {ToolName}/{ChangeId}: {Reason}. " +
                "undo_last_apply will not be available for this apply.", toolName, changeId, blobName);
        }
        else if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Forensic blob written: {BlobName} (changeId={ChangeId})", blobName, changeId);
        }
    }

    [McpServerTool(Name = "GetDiagnostics")]
    [Produces(DataTag.Report)]
    [Description("""
        Gets compiler diagnostics for a file, project, or the entire solution.
        scope: file|project|solution.
        scopeName: filePath when scope=file, projectName when scope=project; ignored for scope=solution.
        summarize: when true, groups results by diagnostic ID and returns counts instead of raw details.
        maxDetails: caps the raw detail list (default 50); error/warning counts are always full totals. Only used when summarize=false.
        topN: max groups to return sorted by count descending (default 20). Only used when summarize=true.
        """)]
    public async Task<ToolResult<object>> GetDiagnostics(
        [Consumes(DataTag.ProjectName, required: true)][Consumes(DataTag.SourceFilepath, required: false)] string scope,
        string? scopeName = null,
        bool summarize = false,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        [ToolOptionAttribute(ToolOptionTag.TopN)] int topN = 20)
    {
        try
        {
            EngineResultWrapper<DiagnosticSummary> result;
            DiagnosticSummary summary;
            if (scope == "file")
            {
                if (string.IsNullOrEmpty(scopeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (filePath) is required when scope=file.") };
                }
                result = await _diagnosticEngine.GetFileDiagnosticsAsync(scopeName);
                summary = result.Data;
            }
            else if (scope == "project")
            {
                if (string.IsNullOrEmpty(scopeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (projectName) is required when scope=project.") };
                }
                result = await _diagnosticEngine.GetProjectDiagnosticsAsync(scopeName);
                summary = result.Data;
            }
            else if (scope == "solution")
            {
                result = await _diagnosticEngine.GetSolutionDiagnosticsAsync(maxDetails);
                summary = result.Data;
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown scope '{scope}'. Valid values: file, project, solution.") };
            }

            if (!summarize)
            {
                return new ToolResult<object>() { Success = true, Data = result.Data };
            }

            var relevant = result.Data.Details
                .Where(d => d.Severity is "Error" or "Warning")
                .ToList();

            var groups = relevant
                .GroupBy(d => d.Id)
                .Select(g =>
                {
                    var first = g.First();
                    var locations = g.Select(d => $"{d.FilePath}:{d.StartLine}").Distinct().Take(10).ToList();
                    return new DiagnosticGroupSummary(
                        DiagnosticId: g.Key,
                        Severity: first.Severity,
                        MessageTemplate: first.Message,
                        Count: g.Count(),
                        Locations: locations
                    );
                })
                .OrderByDescending(g => g.Count)
                .Take(topN)
                .ToList();

            return new ToolResult<object>()
            {
                Success = true,
                Data = new DiagnosticsSummaryResult(
                TotalIssues: relevant.Count,
                Errors: summary.Errors,
                Warnings: summary.Warnings,
                TopIssues: groups
            )
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDiagnostics ({Scope}) failed", scope);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"GetDiagnostics failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "SafeDeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages in the entire codebase. Requires line and column (1-based) to identify the symbol at the declaration site.")]
    public async Task<ToolResult<object>> SafeDeleteUnusedSymbol(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.StartLine, required: true)] int line,
        [Consumes(DataTag.Offset, required: true)] int column)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePath, line, column);
            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeDeleteUnusedSymbol failed for '{FilePath}' at {Line}:{Column}", filePath, line, column);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SafeDeleteUnusedSymbol failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "CreateProject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Creates a new project and adds it to the current solution. projectType defaults to console.")]
    public async Task<ToolResult<object>> CreateProject(
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName,
        [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console")
    {
        try
        {
            var result = await _solutionManagementEngine.CreateProjectAsync(projectName, projectType);
            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateProject failed for '{ProjectName}'", projectName);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"CreateProject failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "SplitProjectByFolder")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves all files under a specific folder from a source project to a new target project, preserving folder structure.")]
    public async Task<ToolResult<object>> SplitProjectByFolder(
        [Consumes(DataTag.ProjectName, required: true)] string sourceProjectName,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName)
    {
        try
        {
            var result = await _solutionManagementEngine.SplitProjectByFolderAsync(sourceProjectName, folderName, targetProjectName);
            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SplitProjectByFolder failed for '{SourceProjectName}'", sourceProjectName);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SplitProjectByFolder failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    // ── Phase 1 — Low-level fallback tools ──────────────────────────────────

    [McpServerTool(Name = "GetMethodSource")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the full source text of a named method. Case-sensitive match with case-insensitive fallback. Returns the first match for overloaded names.")]
    public async Task<ToolResult<object>> GetMethodSource(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.MethodName, required: true)] string methodName)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var normalizedPath = Path.GetFullPath(filePath);

            var document = solution.GetDocumentIdsWithFilePath(normalizedPath)
                                   .Select(solution.GetDocument)
                                   .FirstOrDefault()
                ?? solution.Projects
                           .SelectMany(p => p.Documents)
                           .FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) &&
                                                string.Equals(Path.GetFullPath(d.FilePath), normalizedPath,
                                                              StringComparison.OrdinalIgnoreCase));

            if (document == null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("FileNotFound", $"File not found in solution: {normalizedPath} (existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).") };
            }

            var root = await document.GetSyntaxRootAsync();
            if (root == null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("SyntaxRootNotFound", "Syntax root not found.") };
            }

            var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                             .FirstOrDefault(m => m.Identifier.Text.Equals(methodName, StringComparison.Ordinal))
                      ?? root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                             .FirstOrDefault(m => m.Identifier.Text.Equals(methodName, StringComparison.OrdinalIgnoreCase));

            if (method == null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("MethodNotFound", $"Method '{methodName}' not found in '{filePath}'.") };
            }

            var methodBytes = System.Text.Encoding.UTF8.GetByteCount(method.ToFullString());
            var methodCharLength = method.ToFullString().Length;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Method body size: {SizeBytes} bytes", methodBytes);
                _logger.LogInformation("Method character length: {Length} characters", methodCharLength);
            }

            var thresholdBytes = 8 * 1024; // 8 KB threshold for logging warnings
            if (methodBytes > thresholdBytes)
            {
                _logger.LogWarning("Method body size {SizeBytes} bytes exceeds expected limits. " +
                                   "This may indicate an issue with the summarization logic or unusually large data. " +
                                   "Consider reviewing the summary generation and applying stricter caps if necessary.",
                                   methodBytes);
            }

            var scanId = Guid.NewGuid().ToString("N");
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (!string.IsNullOrEmpty(solutionRoot))
            {
                var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "scans");
                Directory.CreateDirectory(dir);
                var ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                var fp = System.IO.Path.Combine(dir, $"scan_{ts}_{scanId}.json");
                await File.WriteAllTextAsync(fp,
                    method.ToFullString(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return new ToolResult<object>
                {
                    Success = true,
                    LargeResult = new LargeResultInfo(
                        resultType: "MethodSource",
                        writtenToFile: true,
                        filePath: fp,
                        scanId: scanId,
                        sizeBytes: methodBytes,
                        totalRecords: 1,
                        message: $"Summary exceeded {thresholdBytes} bytes ({methodBytes} bytes). " +
                                       $"Use get_scan_result(scanId: \"{scanId}\") to page through results.")
                };
            }

            return new ToolResult<object>() { Success = true, Data = method.ToFullString() };

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMethodSource failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("GetMethodSourceFailed", $"GetMethodSource failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "GetFileOutline")]
    [Produces(DataTag.Report)]
    [Description("Returns a structural outline of a file — namespaces, classes, interfaces, methods, and properties with 1-based line ranges. Member bodies are not included.")]
    public async Task<ToolResult<object>> GetFileOutline(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var normalizedPath = Path.GetFullPath(filePath);

            var document = solution.GetDocumentIdsWithFilePath(normalizedPath)
                                   .Select(solution.GetDocument)
                                   .FirstOrDefault()
                ?? solution.Projects
                           .SelectMany(p => p.Documents)
                           .FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) &&
                                                string.Equals(Path.GetFullPath(d.FilePath), normalizedPath,
                                                              StringComparison.OrdinalIgnoreCase));

            if (document == null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("FileNotFound", $"File not found in solution: {normalizedPath} (existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).") };
            }

            var root = await document.GetSyntaxRootAsync();
            if (root == null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("SyntaxRootNotFound", "Syntax root not found.") };
            }

            var items = new List<OutlineItem>();

            foreach (var node in root.DescendantNodes())
            {
                string? kind = null;
                string? name = null;
                string? container = null;

                switch (node)
                {
                    case BaseNamespaceDeclarationSyntax ns:
                        kind = "namespace";
                        name = ns.Name.ToString();
                        break;

                    case ClassDeclarationSyntax cls:
                        kind = "class";
                        name = cls.Identifier.Text;
                        container = (cls.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString()
                                 ?? (cls.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                        break;

                    case InterfaceDeclarationSyntax iface:
                        kind = "interface";
                        name = iface.Identifier.Text;
                        container = (iface.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString()
                                 ?? (iface.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                        break;

                    case MethodDeclarationSyntax method:
                        kind = "method";
                        name = method.Identifier.Text;
                        container = (method.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                        break;

                    case PropertyDeclarationSyntax prop:
                        kind = "property";
                        name = prop.Identifier.Text;
                        container = (prop.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                        break;
                }

                if (kind == null || name == null)
                {
                    continue;
                }

                var span = node.GetLocation().GetLineSpan();
                items.Add(new OutlineItem(
                    Kind: kind,
                    Name: name,
                    Container: container,
                    StartLine: span.StartLinePosition.Line + 1,
                    EndLine: span.EndLinePosition.Line + 1));
            }

            return new ToolResult<object>() { Success = true, Data = items };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileOutline failed for '{FilePath}'", filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("GetFileOutlineFailed", $"GetFileOutline failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "SearchSolutionText")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches all source files in the loaded solution for a text pattern or regex. Returns file path, 1-based line and column, and a preview per match. isRegex=true treats pattern as a regular expression. fileGlob restricts to matching file paths. maxResults caps total matches (default 200).")]
    public async Task<ToolResult<object>> SearchSolutionText(
        [ToolOption(ToolOptionTag.Pattern, required: true)] string pattern,
        [ToolOption(ToolOptionTag.IsRegex)] bool isRegex = false,
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200)
    {
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            var results = new List<TextSearchMatch>();

            Regex? regex = null;
            if (isRegex)
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase,
                                  matchTimeout: TimeSpan.FromSeconds(5));
            }

            foreach (var project in solution.Projects)
            {
                foreach (var document in project.Documents)
                {
                    if (results.Count >= maxResults)
                    {
                        break;
                    }

                    var docPath = new FilePath(document.FilePath ?? "", _workspaceManager.GetSolutionRoot());
                    if (!string.IsNullOrEmpty(fileGlob) && !GlobMatchesFileName(docPath, fileGlob))
                    {
                        continue;
                    }

                    var sourceText = (await document.GetTextAsync()).ToString();
                    var lines = sourceText.Split('\n');

                    for (int i = 0; i < lines.Length && results.Count < maxResults; i++)
                    {
                        var line = lines[i];
                        int col = -1;

                        if (isRegex && regex != null)
                        {
                            try
                            {
                                var m = regex.Match(line);
                                if (m.Success)
                                {
                                    col = m.Index;
                                }
                            }
                            catch (RegexMatchTimeoutException)
                            {
                                continue;
                            }
                        }
                        else
                        {
                            col = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                        }

                        if (col >= 0)
                        {
                            var preview = line.Trim();
                            if (preview.Length > 120)
                            {
                                preview = preview[..120] + "\u2026";
                            }

                            results.Add(new TextSearchMatch(docPath.Absolute, i + 1, col + 1, preview));
                        }
                    }
                }
            }

            return new ToolResult<object>() { Success = true, Data = results };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchSolutionText failed for '{Pattern}'", pattern);
            return new ToolResult<object>() { Success = false, Error = new ResultError("SearchSolutionTextFailed", $"SearchSolutionText failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    private static bool GlobMatchesFileName([Consumes(DataTag.SourceFilepath, required: true)] FilePath filePath, string glob)
    {
        var fileName = Path.GetFileName(filePath.Absolute);
        var regexPattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }

    // ── Phase 2 — Blob persistence query + undo tools ───────────────────────

    [McpServerTool(Name = "GetOperationDetail")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns a filtered slice of an operation result blob by changeId. filter: failures, skipped, rolledback, file:<path>, or null for all items. maxItems caps the returned slice — never dumps the full document.")]
    public async Task<ToolResult<object>> GetOperationDetail(
        [Consumes(DataTag.ChangeId, required: true)] string changeId,
        [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50)
    {
        try
        {
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            var blobPath = OperationBlobWriter.FindBlobPath(changeId, solutionRoot);

            if (blobPath == null)
            {
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = new OperationDetailResult
                    {
                        ChangeId = changeId,
                        BlobName = "",
                        TotalItems = 0,
                        ReturnedItems = 0,
                        Filter = filter,
                        Items = new List<OperationItemRecord>(),
                    }
                };
            }

            var json = await File.ReadAllTextAsync(blobPath);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var allItems = doc.GetProperty("items")
                             .EnumerateArray()
                             .Select(e => JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!)
                             .ToList();

            IEnumerable<OperationItemRecord> filtered = allItems;

            if (!string.IsNullOrEmpty(filter))
            {
                if (filter.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    var pathFilter = filter[5..];
                    filtered = allItems.Where(r => r.FilePath.Contains(pathFilter, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    filtered = filter.ToLowerInvariant() switch
                    {
                        "failures" => allItems.Where(r => r.Outcome == OperationOutcome.Failed),
                        "skipped" => allItems.Where(r => r.Outcome == OperationOutcome.Skipped),
                        "rolledback" => allItems.Where(r => r.Outcome == OperationOutcome.RolledBack),
                        _ => allItems,
                    };
                }
            }

            var slice = filtered.Take(maxItems).ToList();

            return new ToolResult<object>()
            {
                Success = true,
                Data = new OperationDetailResult
                {
                    ChangeId = changeId,
                    BlobName = Path.GetFileName(blobPath),
                    TotalItems = allItems.Count,
                    ReturnedItems = slice.Count,
                    Filter = filter,
                    Items = slice,
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOperationDetail failed for '{ChangeId}'", changeId);
            return new ToolResult<object>() { Success = false, Error = new ResultError("GetOperationDetailFailed", $"GetOperationDetail failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previously applied batch to their pre-apply state using the forensic blob written at apply time. Covers all apply operations: proposed_change, staged_change, and batch-first tools.")]
    public async Task<ToolResult<object>> UndoLastApply([Consumes(DataTag.OperationId, required: true)] string changeId)
    {
        try
        {
            if (!_workspaceManager.GetAllAppliedChanges().Any())
            {
                var statedChangeIds = _workspaceManager.GetAllStagedChanges();

                return new ToolResult<object>() { Success = false, Error = new ResultError("NoAppliedChanges", $"No applied changes found to undo. Current staged changes: {string.Join(", ", statedChangeIds.Keys)}") };
            }

            var solutionRoot = _workspaceManager.GetSolutionRoot();
            var blobPath = OperationBlobWriter.FindBlobPath(changeId, solutionRoot);

            if (blobPath == null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("NoOperationBlobFound", $"No operation blob found for changeId '{changeId}'. Ensure the apply completed successfully and a solution is loaded.") };

            }

            var json = await File.ReadAllTextAsync(blobPath);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var revertable = doc.GetProperty("items")
                               .EnumerateArray()
                               .Select(e => JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!)
                               .Where(r => r.Outcome == OperationOutcome.Succeeded && r.BeforeSource != null)
                               .ToList();

            if (revertable.Count == 0)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError("NoReversibleItems", $"No reversible items in blob for changeId '{changeId}'. Ensure the apply completed successfully and a solution is loaded.") };
            }

            var reverted = new List<string>();
            var failed = new List<string>();

            foreach (var item in revertable)
            {
                // Security: only revert files under the solution root to prevent path traversal.
                if (solutionRoot != null &&
                    !item.FilePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add($"{item.FilePath}: outside solution root, skipped");
                    continue;
                }

                try
                {
                    await File.WriteAllTextAsync(item.FilePath, item.BeforeSource!);
                    reverted.Add(item.FilePath);
                }
                catch (Exception ex)
                {
                    failed.Add($"{item.FilePath}: {ex.Message}");
                }
            }

            var failedPart = failed.Count > 0 ? $" Failures: {string.Join("; ", failed)}" : "";
            return new ToolResult<object>() { Success = true, Data = $"Reverted {reverted.Count} files. Files: {string.Join(", ", reverted)}{failedPart}" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UndoLastApply failed for '{ChangeId}'", changeId);
            return new ToolResult<object>() { Success = false, Error = new ResultError("UndoLastApplyFailed", $"UndoLastApply failed: {ex.GetType().Name}: {ex.Message}") };
        }
    }

    // ── Phase 3 — Circuit breaker tools ────────────────────────────────────

    [McpServerTool(Name = "ResetBreaker")]
    [Produces(DataTag.ResultOnly)]
    [Description("Resets the circuit breaker and all failure counters, re-enabling mutating tools. Only call after investigating and addressing the root cause of the failures that tripped the breaker.")]
    public ToolResult<object> ResetBreaker()
    {
        _workspaceManager.ResetBreaker();
        return new ToolResult<object>() { Success = true, Data = "Circuit breaker reset. Failure counters cleared. Mutating tools re-enabled." };
    }

    [McpServerTool(Name = "GetBreakerStatus")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns the current circuit breaker state: severity (ok/caution/halt), trip-condition counters, and thresholds. Use to assess failure health before running large batch operations.")]
    public ToolResult<object> GetBreakerStatus()
    {
        return new ToolResult<object>() { Success = true, Data = _workspaceManager.GetBreakerStatus() };
    }

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────
    // MS Bug: roslyn-diagnose reports healthy: false even when 86/86 projects load
    // successfully, because the health check tests MSBuild path existence rather
    // than actual workspace state. A fully operational workspace with a loaded
    // solution is falsely reported as unhealthy.
    // Fix: targeted health check that reads actual workspace state.

    /// <summary>
    /// Returns a targeted workspace health report based on actual solution state.
    /// Fixes the false-negative in the standard <c>diagnose</c> tool, which reports
    /// <c>healthy: false</c> even when all projects load successfully.
    /// </summary>
    public Task<WorkspaceHealthReport> GetWorkspaceHealthAsync(CancellationToken ct = default)
    {
        // Use CurrentSolution (sync, no throw) rather than GetBranchedSolutionAsync
        // to distinguish "no solution loaded" from "workspace error"
        Solution? currentSolution;
        try { currentSolution = _workspaceManager.CurrentSolution; }
        catch (Exception ex)
        {
            // Workspace itself threw — genuinely non-operational
            return Task.FromResult(new WorkspaceHealthReport(
                IsOperational: false,
                HasLoadedSolution: false,
                LoadedSolutionPath: null,
                ProjectCount: 0,
                DocumentCount: 0,
                LoadErrors: [$"Workspace exception: {ex.Message}"],
                Summary: $"Workspace is NOT operational: {ex.Message}"));
        }

        var loadErrors = _workspaceManager.GetWorkspaceLoadErrors();

        if (currentSolution == null)
        {
            // No solution is loaded — but the workspace itself is operational
            return Task.FromResult(new WorkspaceHealthReport(
                IsOperational: true,
                HasLoadedSolution: false,
                LoadedSolutionPath: null,
                ProjectCount: 0,
                DocumentCount: 0,
                LoadErrors: loadErrors,
                Summary: "Workspace is operational. No solution is currently loaded. " +
                         "Call load_solution to load a .sln or .csproj file."));
        }

        var projectCount = currentSolution.ProjectIds.Count;
        var documentCount = currentSolution.Projects.SelectMany(p => p.Documents).Count();
        var solutionPath = currentSolution.FilePath ?? _workspaceManager.SolutionPath;

        return Task.FromResult(new WorkspaceHealthReport(
            IsOperational: true,
            HasLoadedSolution: true,
            LoadedSolutionPath: solutionPath,
            ProjectCount: projectCount,
            DocumentCount: documentCount,
            LoadErrors: loadErrors,
            Summary: $"Workspace operational. {projectCount} project(s) loaded, " +
                     $"{documentCount} document(s). " +
                     (loadErrors.Count > 0
                         ? $"{loadErrors.Count} load warning(s) recorded (non-fatal)."
                         : "No load errors.")));
    }

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────

    [McpServerTool(Name = "GetWorkspaceHealth")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Targeted workspace health check. Returns: IsOperational, HasLoadedSolution, LoadedSolutionPath, ProjectCount, DocumentCount, LoadErrors, Summary. IsOperational=true + HasLoadedSolution=false is normal — no solution loaded yet, not an error.
        """)]
    // FIXES MS BUG: the standard diagnose tool reports healthy:false even when all projects load successfully, because it tests MSBuild path existence rather than actual workspace state. This tool reads workspace state directly.
    public async Task<ToolResult<object>> GetWorkspaceHealth()
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("GetWorkspaceHealth called");
        }
        try
        {
            var result = await GetWorkspaceHealthAsync();
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetWorkspaceHealth failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"GetWorkspaceHealth failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "ListProjectFrameworkTargets")]
    [Produces(DataTag.Report)]
    [Description("Returns each project's TargetFramework value. Use before check_project_consistency to see the full framework landscape. No parameters.")]
    public async Task<ToolResult<object>> ListProjectFrameworkTargets()
    {
        try
        {
            var result = await _projectConsistencyEngine.GetProjectFrameworkSummaryAsync();
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetProjectFrameworkSummary failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"GetProjectFrameworkSummary failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }
}
