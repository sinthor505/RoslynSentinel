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

/// <summary>
/// A file attached to the solution via a .sln Solution Folder (ProjectSection(SolutionItems)),
/// returned by ListSolutionItems(kind: solutionItems). SolutionFolder is the enclosing folder's
/// display name (e.g. "Solution Items").
/// </summary>
public record SolutionItemFile(FilePath FilePath, string SolutionFolder);

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
    [Description("Queries or updates feature flags. list → all; get → by names; update → batch-update via enabled as [{Key: featureName, Value: bool}] pairs.")]
    public object Features(
        FeaturesAction action,
        List<string>? names = null,
        List<KeyValuePair<string, bool>>? enabled = null)
    {
        try
        {
            return action switch
            {
                FeaturesAction.list => (object)_config.GetFeatureStatuses(),
                FeaturesAction.get => _config.GetFeatureStatuses(names),
                FeaturesAction.update => (object)UpdateFeaturesInternal(enabled ?? []),
                _ => new { Success = false, Error = $"Unknown action '{action}'. Valid values: list, get, update." }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Features ({Action}) failed", action);
            return new { Success = false, Error = $"Features failed unexpectedly ({ex.GetType().Name}): {ex.Message}" };
        }
    }

    private string UpdateFeaturesInternal(List<KeyValuePair<string, bool>> updates,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        _config.BatchUpdateFeatureStatus(updates);
        return $"Updated {updates.Count} features.";
    }

    [McpServerTool(Name = "ListSolutionItems")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.ProjectList)]
    [Produces(DataTag.DependencyList)]
    [Description("Lists projects, files, dependencies, or solution-folder items. files and dependencies require projectName. solutionItems (no projectName needed) returns files attached via the .sln's Solution Folders — e.g. plan/handoff docs referenced there for discoverability in an IDE. These are never part of any project's compiled Documents, so SearchSolutionText and kind=files will never find them; read their content with ProjectDoc.")]
    public async Task<ToolResult<object>> ListSolutionItems(
        [ExternalInputRequired(DataTag.Scope)] SolutionItemsKind kind,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (kind == SolutionItemsKind.projects)
            {
                var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
                return new ToolResult<object>() { Success = true, Data = solution.Projects.Select(p => (object)new { p.Name, p.FilePath }).ToList() };
            }
            if (kind == SolutionItemsKind.solutionItems)
            {
                var solutionRoot = _workspaceManager.GetSolutionRoot();
                if (solutionRoot is null)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.SolutionNotLoaded, "No solution loaded. Call LoadSolution first.") };
                }

                var items = _workspaceManager.GetSolutionFolderItems()
                    .Select(i => new SolutionItemFile(
                        new FilePath(Path.GetFullPath(Path.Combine(solutionRoot, i.RelativePath)), solutionRoot),
                        i.SolutionFolder))
                    .ToList();

                return new ToolResult<object>() { Success = true, Data = items, TotalRecords = items.Count };
            }
            if (kind == SolutionItemsKind.files)
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "projectName is required when kind=files.") };
                }
                try
                {
                    var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"List files for project '{projectName}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
                }
            }
            if (kind == SolutionItemsKind.dependencies)
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "projectName is required when kind=dependencies.") };
                }
                var result = await _dependencyEngine.GetProjectDependenciesAsync(projectName, cancellationToken);
                return new ToolResult<object>() { Success = true, Data = result };
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unknown kind '{kind}'.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List ({Kind}) failed", kind);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"List failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "ListWorkspaceSolutions")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.SolutionList)]
    [Description("Lists all *.sln and *.slnx files under a directory. Returns absolute paths for use with LoadSolution. Pass your workspace root as workspacePath.")]
    public ToolResult<List<SolutionFileInfo>> ListWorkspaceSolutions(
        string workspacePath,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(workspacePath))
        {
            return new ToolResult<List<SolutionFileInfo>>
            {
                Success = false,
                Error = new ResultError("InvalidArgument", $"Directory not found: '{workspacePath}'")
            };
        }

        try
        {
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListWorkspaceSolutions failed for '{WorkspacePath}'", workspacePath);
            return new ToolResult<List<SolutionFileInfo>>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"ListWorkspaceSolutions failed unexpectedly ({ex.GetType().Name}) while scanning '{workspacePath}'. Details: {ex.Message}")
            };
        }
    }

    public sealed record SolutionFileInfo(string Path, string Format);

    // current directory, --base-repo-dir (if set), or the server's install directory.
    [McpServerTool(Name = "LoadSolution")]
    [Produces(DataTag.ResultOnly)]
    [Description("Loads a .NET solution file into memory for persistent analysis. Must be called before any operation that returns ErrorCode=\"SolutionNotLoaded\". Accepts absolute paths. For relative paths, pass baseRepoDir (the directory containing solutionPath) or rely on the server to resolve it")]
    public async Task<ToolResult<object>> LoadSolution(
        [Consumes(DataTag.SolutionFilepath, required: true)] string solutionPath,
        [ToolOption(ToolOptionTag.RepoDirectory)][Description("Optional base directory used to resolve a relative solutionPath (e.g. the repo root). Overrides the server's configured base-repo-dir for this call.")] string? baseRepoDir = null,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _workspaceManager.LoadSolutionAsync(solutionPath, baseRepoDir, cancellationToken: cancellationToken);

            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (solutionRoot != null)
            {
                return new ToolResult<object>() { Success = true, Data = $"Solution loaded: {solutionPath}{BuildPostLoadHint(solutionRoot)}" };
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"LoadSolution failed: Workspace root is null after loading '{solutionPath}'.") };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadSolution failed for '{SolutionPath}'", solutionPath);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"LoadSolution failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    // Subdirectories ProjectDoc reads/writes under docs/, paired with the docType value that
    // maps to each — see DocumentationTools.ProjectDoc.
    private static readonly (string Dir, string DocType)[] ProjectDocSubdirs =
    [
        ("plans", "plan"),
        ("handoffs", "handoff"),
        ("completed", "completed_work"),
        ("documentation", "documentation"),
    ];

    // Surfaces docs/ and Solution-Folder content right after a solution loads, so an agent
    // doesn't have to burn a round of (fruitless) SearchSolutionText calls to discover a plan,
    // handoff, or other doc file the solution already has waiting for it.
    private string BuildPostLoadHint(string solutionRoot)
    {
        var parts = new List<string>();

        var solutionItems = _workspaceManager.GetSolutionFolderItems();
        if (solutionItems.Count > 0)
        {
            parts.Add($"{solutionItems.Count} file(s) attached via Solution Folders in the .sln (not visible to SearchSolutionText — list them with ListSolutionItems(kind: solutionItems)).");
        }

        var docsRoot = Path.Combine(solutionRoot, "docs");
        foreach (var (dir, docType) in ProjectDocSubdirs)
        {
            var fullDir = Path.Combine(docsRoot, dir);
            if (!Directory.Exists(fullDir))
            {
                continue;
            }

            var count = Directory.GetFiles(fullDir).Length;
            if (count > 0)
            {
                parts.Add($"docs/{dir}/ has {count} file(s) — read with ProjectDoc(action: read, docType: {docType}, name: \"<filename>\").");
            }
        }

        return parts.Count > 0 ? " " + string.Join(" ", parts) : "";
    }

    [McpServerTool(Name = "ListExternalDiskChanges")]
    [Produces(DataTag.FileList)]
    [Description("Returns files modified on disk since the AI last synced. No parameters.")]
    public List<string> ListExternalDiskChanges(
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        return _workspaceManager.GetExternalDrift();
    }

    [McpServerTool(Name = "ClearExternalDrift")]
    [Produces(DataTag.ResultOnly)]
    [Description("Clears the external-drift list after the AI has read the latest disk changes. No parameters.")]
    public string ClearExternalDrift(
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        var count = _workspaceManager.GetExternalDrift().Count;
        _workspaceManager.ClearDrift();
        return $"Cleared {count} tracked external change(s).";
    }

    private static string PreviewFileContent(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length <= 20)
        {
            return content;
        }

        var head = lines.Take(10);
        var tail = lines.TakeLast(10);
        return string.Join("\n", head) + "\n// ... (truncated)\n" + string.Join("\n", tail);
    }

    [McpServerTool(Name = "ProposedChange")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set. changesetFormat files → changes dict filePath→newContent; diff → filepath + unifiedDiff. Returns ApplyChangesResult with UndoChangeId on successful apply.")]
    public async Task<ToolResult<object>> ProposedChange(
        [ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat,
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        [ExternalInputRequired(DataTag.OperationId)] Dictionary<FilePath, string>?
        changes = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);

            if (changesetFormat == ChangesetFormat.files)
            {
                if (changes == null)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "changes is required when changesetFormat=files.") };
                }
                if (action == ProposedChangeAction.apply)
                {
                    var result = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange pre-apply validate failed: {result.ValidationResult.Diagnostics.ToJson()}") };
                    await WriteBlobForApplyAsync("proposed_change", result);
                    return new ToolResult<object>() { Success = true, Data = result };
                }
                if (action == ProposedChangeAction.validate)
                {
                    try
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(changes);
                        return validationResult.Success
                            ? new ToolResult<object>() { Success = true, Data = validationResult }
                            : new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange validate failed: {validationResult.Diagnostics}") };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ProposedChange validate unexpected exception");
                        return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange validate failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
                    }
                }
            }
            else if (changesetFormat == ChangesetFormat.diff)
            {
                if (!filePath.Validated || string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath and unifiedDiff are required when changesetFormat=diff.") };
                }
                if (action == ProposedChangeAction.apply)
                {
                    try
                    {
                        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

                        var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges, validateChanges: validateOnApply);
                        if (!result.Success && result.ValidationResult != null)
                            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange diff validate failed: {result.ValidationResult.Diagnostics.ToJson()}") };
                        await WriteBlobForApplyAsync("proposed_change", result);
                        return new ToolResult<object>() { Success = true, Data = result };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ProposedChange diff apply unexpected exception for '{FilePath}'", filePath);
                        return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange diff apply for '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
                    }
                }
                if (action == ProposedChangeAction.validate)
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePath.Absolute, unifiedDiff);
                    return validationResult.Success
                        ? new ToolResult<object>() { Success = true, Data = validationResult }
                        : new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange diff validate failed: {validationResult}") };
                }
            }
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unhandled changesetFormat '{changesetFormat}' / action '{action}'.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProposedChange ({ChangesetFormat}/{Action}) failed", changesetFormat, action);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ProposedChange failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries failed file writes using server-cached content — no need to re-send file contents. specificFiles limits to a subset. retryCount defaults to 3.")]
    public async Task<ToolResult<object>> RetryFailedChanges(
        [Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return new ToolResult<object>() { Success = true, Data = await _workspaceManager.RetryFailedChangesAsync(specificFiles, retryCount) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetryFailedChanges failed");
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"RetryFailedChanges failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    /// <summary>
    /// Writes a forensic blob for a completed apply so undo_last_apply can revert it.
    /// Uses pre-images from ApplyChangesResult.PreImages (populated by ApplyProposedChangesAsync).
    /// blobChangeId: if provided, uses this id for the blob filename; if null, mints a fresh id.
    /// Logs a warning but does not throw on blob write failure — apply already succeeded.
    /// </summary>
    private async Task WriteBlobForApplyAsync(
        string toolName,
        PersistentWorkspaceManager.ApplyChangesResult result,
        string? blobChangeId = null,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
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
                Outcome = ItemRecordOutcome.Succeeded,
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
    [Description("Gets compiler diagnostics. file → scopeName=filePath; project → scopeName=projectName; solution → scopeName ignored. summarize=true groups by diagnostic ID and returns counts. maxDetails caps raw list (default 50). topN caps groups (default 20).")]
    public async Task<ToolResult<object>> GetDiagnostics(
        [Consumes(DataTag.ProjectName, required: true)][Consumes(DataTag.SourceFilepath, required: false)] ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        bool summarize = false,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        [ToolOptionAttribute(ToolOptionTag.TopN)] int topN = 20,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            EngineResultWrapper<DiagnosticSummary> result;
            DiagnosticSummary summary;
            if (scope == ToolScope.file)
            {
                if (string.IsNullOrEmpty(scopeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (filePath) is required when scope=file.") };
                }
                result = await _diagnosticEngine.GetFileDiagnosticsAsync(scopeName);
                summary = result.Data;
            }
            else if (scope == ToolScope.project)
            {
                if (string.IsNullOrEmpty(scopeName))
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (projectName) is required when scope=project.") };
                }
                result = await _diagnosticEngine.GetProjectDiagnosticsAsync(scopeName);
                summary = result.Data;
            }
            else if (scope == ToolScope.solution)
            {
                result = await _diagnosticEngine.GetSolutionDiagnosticsAsync(maxDetails);
                summary = result.Data;
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Unhandled scope '{scope}'.") };
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
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"GetDiagnostics failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "SafeDeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages in the entire codebase. Requires line and column (1-based) to identify the symbol at the declaration site.")]
    public async Task<ToolResult<object>> SafeDeleteUnusedSymbol(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.StartLine, required: true)] int line,
        [Consumes(DataTag.Offset, required: true)] int column,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
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
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SafeDeleteUnusedSymbol failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "CreateProject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Creates a new project and adds it to the current solution. projectType defaults to console.")]
    public async Task<ToolResult<object>> CreateProject(
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName,
        [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console",
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _solutionManagementEngine.CreateProjectAsync(projectName, projectType);
            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateProject failed for '{ProjectName}'", projectName);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"CreateProject failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "SplitProjectByFolder")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves all files under a specific folder from a source project to a new target project, preserving folder structure.")]
    public async Task<ToolResult<object>> SplitProjectByFolder(
        [Consumes(DataTag.ProjectName, required: true)] string sourceProjectName,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _solutionManagementEngine.SplitProjectByFolderAsync(sourceProjectName, folderName, targetProjectName);
            return new ToolResult<object>() { Success = true, Data = result };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SplitProjectByFolder failed for '{SourceProjectName}'", sourceProjectName);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SplitProjectByFolder failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    // ── Phase 1 — Low-level fallback tools ──────────────────────────────────

    [McpServerTool(Name = "GetMethodSource")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the full source text of a named method plus a structured list of its attributes. Case-sensitive match with case-insensitive fallback. Returns the first match for overloaded names.")]
    public async Task<ToolResult<object>> GetMethodSource(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.MethodName, required: true)] string methodName,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

            var methodSource = method.ToFullString();
            var methodBytes = System.Text.Encoding.UTF8.GetByteCount(methodSource);
            var attributes = ExtractAttributes(method);
            var signature = BuildSignature(method);

            _logger.LogInformation("GetMethodSource: {SizeBytes} bytes for '{MethodName}'", methodBytes, methodName);

            const int thresholdBytes = 8 * 1024;
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (methodBytes > thresholdBytes && !string.IsNullOrEmpty(solutionRoot))
            {
                var scanId = Guid.NewGuid().ToString("N");
                var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "scans");
                Directory.CreateDirectory(dir);
                var ts = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                var fp = System.IO.Path.Combine(dir, $"scan_{ts}_{scanId}.json");
                await File.WriteAllTextAsync(fp, methodSource, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
                        message: $"Result is {methodBytes} bytes (threshold: {thresholdBytes}). " +
                                 $"Use get_scan_result(scanId: \"{scanId}\") to page through results."),
                    Data = new { signature, attributes },
                };
            }

            return new ToolResult<object>()
            {
                Success = true,
                Data = new MethodSourceResult { Signature = signature, Source = methodSource, Attributes = attributes },
            };

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMethodSource failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>() { Success = false, Error = new ResultError("GetMethodSourceFailed", $"GetMethodSource failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "GetFileOutline")]
    [Produces(DataTag.Report)]
    [Description("Returns a structural outline of a file — namespaces, classes, interfaces, methods, and properties with 1-based line ranges. Member bodies are not included.")]
    public async Task<ToolResult<object>> GetFileOutline(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            return new ToolResult<object>() { Success = false, Error = new ResultError("GetFileOutlineFailed", $"GetFileOutline failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    /// <summary>Regex metacharacters that suggest the caller meant to pass isRegex=true.</summary>
    private static readonly Regex LikelyRegexPattern = new(@"[\^\$\.\*\+\?\(\)\[\]\{\}\|\\]", RegexOptions.Compiled);

    [McpServerTool(Name = "SearchSolutionText")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches all source files in the loaded solution for a text pattern or regex. Only searches documents that are part of a loaded project's compilation (e.g. .cs files) — files attached via the .sln's Solution Folders and other non-project files are never included, no matter the pattern; use ListSolutionItems(kind: solutionItems) to see those, and ProjectDoc to read plan/handoff/documentation files directly. Returns file path, 1-based line and column, and a preview per match. isRegex=true treats pattern as a regular expression (default false, literal substring match); if pattern contains regex metacharacters (e.g. ^ $ . * + ? ( ) [ ] { } | \\) but isRegex is false, the result includes a Warning suggesting isRegex=true. fileGlob restricts to matching file paths. maxResults caps total matches (default 200).")]
    public async Task<ToolResult<object>> SearchSolutionText(
        [ToolOption(ToolOptionTag.Pattern, required: true)] string pattern,
        [ToolOption(ToolOptionTag.IsRegex)] bool isRegex = false,
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

            var warnings = new List<string>();
            if (!isRegex && LikelyRegexPattern.IsMatch(pattern))
            {
                warnings.Add($"Pattern '{pattern}' contains regex metacharacters but isRegex is false, so it was matched as a literal substring. If you intended a regex, retry with isRegex=true.");
            }
            if (results.Count == 0)
            {
                warnings.Add("No matches. SearchSolutionText only searches documents that are part of a loaded project's compilation (e.g. .cs files) — it does not see files attached via the .sln's Solution Folders, docs/ files, or other non-project files. Use ListSolutionItems(kind: solutionItems) to list files attached via Solution Folders, or ProjectDoc to read plan/handoff/documentation files directly.");
            }
            string? warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;

            return new ToolResult<object>() { Success = true, Data = results, Warning = warning };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchSolutionText failed for '{Pattern}'", pattern);
            return new ToolResult<object>() { Success = false, Error = new ResultError("SearchSolutionTextFailed", $"SearchSolutionText failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    // Globs without a path separator (e.g. "*.cs", "OrderService.cs") are matched against the
    // bare filename so callers can filter by name without knowing the file's directory. Globs
    // with a separator (e.g. "**/OrderService.cs", "ContosoOrders.Core/*.cs") are matched against
    // the path relative to the solution root instead — matching them against Path.GetFileName()
    // would strip the very directory segment the glob is testing for, so a glob like "**/*.cs"
    // could never match anything.
    private static bool GlobMatchesFileName([Consumes(DataTag.SourceFilepath, required: true)] FilePath filePath, string glob)
    {
        var normalizedGlob = glob.Replace('\\', '/');
        var candidate = normalizedGlob.Contains('/')
            ? filePath.Relative.Replace('\\', '/')
            : Path.GetFileName(filePath.Absolute);

        var regexPattern = "^" + GlobToRegex(normalizedGlob) + "$";
        return Regex.IsMatch(candidate, regexPattern, RegexOptions.IgnoreCase);
    }

    // Translates glob syntax to a regex fragment: "**/" matches any depth (including none),
    // a lone "**" matches anything, and a single "*"/"?" stay within one path segment so they
    // don't accidentally cross a "/" boundary.
    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < glob.Length)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    sb.Append("(?:.*/)?");
                    i += 3;
                }
                else
                {
                    sb.Append(".*");
                    i += 2;
                }
            }
            else if (glob[i] == '*')
            {
                sb.Append("[^/]*");
                i++;
            }
            else if (glob[i] == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(glob[i].ToString()));
                i++;
            }
        }
        return sb.ToString();
    }

    // ── Phase 2 — Blob persistence query + undo tools ───────────────────────

    [McpServerTool(Name = "GetOperationDetail")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns a filtered slice of an operation result blob by changeId. filter accepts prefix synonyms: fail/err → failures, warn/skip → skipped, ok/pass/info/success → succeeded, roll/revert/undo → rolledback, manual/manual_review/needs_manual_review → NeedsManualReview (bridge compiler-error skips), file:<path> to filter by path, or omit for all items. Unrecognised prefixes return an error. maxItems caps the returned slice. TotalItems reflects the filtered count; HasMorePages is true when more items remain.")]
    public async Task<ToolResult<object>> GetOperationDetail(
        [Consumes(DataTag.ChangeId, required: true)] string changeId,
        [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            var blobPath = OperationBlobWriter.FindBlobPath(changeId, solutionRoot);

            if (blobPath == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"No operation blob found for changeId '{changeId}'. Verify the changeId, or check that a solution is loaded.")
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
                    var outcome = ResolveOutcomeFilter(filter);
                    if (outcome is null)
                    {
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(
                                ToolErrorCode.InvalidArgument,
                                $"Unknown filter \"{filter}\". Accepted prefixes: fail/err → failures, warn/skip → skipped, ok/pass/info/success → succeeded, roll/revert/undo → rolledback. Use file:<path> to filter by path, or omit for all items.")
                        };
                    }
                    filtered = allItems.Where(r => r.Outcome == outcome.Value);
                }
            }

            var filteredList = filtered.ToList();
            var slice = filteredList.Take(maxItems).ToList();

            return new ToolResult<object>()
            {
                Success = true,
                HasMorePages = filteredList.Count > maxItems,
                Data = new OperationDetailResult
                {
                    ChangeId = changeId,
                    BlobName = Path.GetFileName(blobPath),
                    TotalItems = filteredList.Count,
                    ReturnedItems = slice.Count,
                    Filter = filter,
                    Items = slice,
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOperationDetail failed for '{ChangeId}'", changeId);
            return new ToolResult<object>() { Success = false, Error = new ResultError("GetOperationDetailFailed", $"GetOperationDetail failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    // Maps a human-readable filter string to an ItemRecordOutcome via prefix matching.
    // Returns null when the prefix is unrecognised so the caller can return a helpful error.
    private static ItemRecordOutcome? ResolveOutcomeFilter(string filter)
    {
        string f = filter.ToLowerInvariant();
        if (f.StartsWith("fail") || f.StartsWith("err")) return ItemRecordOutcome.Failed;
        if (f.StartsWith("skip") || f.StartsWith("warn")) return ItemRecordOutcome.Skipped;
        if (f.StartsWith("ok") || f.StartsWith("pass")
         || f.StartsWith("info") || f.StartsWith("success")
         || f.StartsWith("succeed")) return ItemRecordOutcome.Succeeded;
        if (f.StartsWith("roll") || f.StartsWith("revert")
         || f.StartsWith("undo")) return ItemRecordOutcome.RolledBack;
        if (f.StartsWith("manual") || f.StartsWith("needs_manual"))
            return ItemRecordOutcome.NeedsManualReview;
        return null;
    }

    private static string BuildSignature(MethodDeclarationSyntax method)
    {
        var modifiers = method.Modifiers.ToString();
        var returnType = method.ReturnType.ToString();
        var name = method.Identifier.Text;
        var typeParams = method.TypeParameterList?.ToString() ?? "";
        var parameters = method.ParameterList.ToString();
        return string.IsNullOrEmpty(modifiers)
            ? $"{returnType} {name}{typeParams}{parameters}"
            : $"{modifiers} {returnType} {name}{typeParams}{parameters}";
    }

    private static List<MethodAttributeInfo> ExtractAttributes(MethodDeclarationSyntax method) =>
        method.AttributeLists
              .SelectMany(al => al.Attributes)
              .Select(a => new MethodAttributeInfo
              {
                  Name = a.Name.ToString(),
                  Arguments = a.ArgumentList?.Arguments.ToString() ?? "",
              })
              .ToList();

    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previously applied batch to their pre-apply state using the forensic blob written at apply time. Covers all apply operations: proposed_change, refactoring-tool writes, and batch-first tools.")]
    public async Task<ToolResult<object>> UndoLastApply(
        [Consumes(DataTag.OperationId, required: true)] string changeId,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
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
                               .Where(r => r.Outcome == ItemRecordOutcome.Succeeded && r.BeforeSource != null)
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
            return new ToolResult<object>() { Success = false, Error = new ResultError("UndoLastApplyFailed", $"UndoLastApply failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    // ── Phase 3 — Circuit breaker tools ────────────────────────────────────

    [McpServerTool(Name = "ResetBreaker")]
    [Produces(DataTag.ResultOnly)]
    [Description("Resets the circuit breaker and all failure counters, re-enabling mutating tools. Only call after investigating and addressing the root cause of the failures that tripped the breaker.")]
    public ToolResult<object> ResetBreaker(
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        _workspaceManager.ResetBreaker();
        return new ToolResult<object>() { Success = true, Data = "Circuit breaker reset. Failure counters cleared. Mutating tools re-enabled." };
    }

    [McpServerTool(Name = "GetBreakerStatus")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns the current circuit breaker state: severity (ok/caution/halt), trip-condition counters, and thresholds. Use to assess failure health before running large batch operations.")]
    public ToolResult<object> GetBreakerStatus(
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        return new ToolResult<object>() { Success = true, Data = _workspaceManager.GetBreakerStatus() };
    }

    // ── 8. GetWorkspaceHealthAsync ─────────────────────────────────────────────────
    // Reads actual workspace/solution state directly rather than inferring health from
    // environment probes (e.g. MSBuild path existence), which can false-negative a fully
    // operational workspace with a loaded solution.

    /// <summary>
    /// Returns a targeted workspace health report based on actual solution state — the sole
    /// health-check tool now that the older, less reliable <c>Diagnose</c> tool has been removed.
    /// </summary>
    public Task<WorkspaceHealthReport> GetWorkspaceHealthAsync(CancellationToken cancellationToken = default)
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
            // No solution is loaded — but the workspace itself is operational. Surface an
            // MSBuild-missing note here (and only here): once a solution has loaded
            // successfully, MSBuildFound is moot and flagging it would just reintroduce the
            // false-negative behavior this tool replaced Diagnose to fix.
            var msbuildNote = _workspaceManager.GetHealthComponents().MsBuildFound
                ? ""
                : " No MSBuild installation was detected — LoadSolution may fail; install Visual Studio, Build Tools, or the .NET SDK.";
            return Task.FromResult(new WorkspaceHealthReport(
                IsOperational: true,
                HasLoadedSolution: false,
                LoadedSolutionPath: null,
                ProjectCount: 0,
                DocumentCount: 0,
                LoadErrors: loadErrors,
                Summary: "Workspace is operational. No solution is currently loaded. " +
                         "Call load_solution to load a .sln or .csproj file." + msbuildNote));
        }

        var projectCount = currentSolution.ProjectIds.Count;
        var documentCount = currentSolution.Projects.SelectMany(p => p.Documents).Count();
        var solutionPath = currentSolution.FilePath ?? _workspaceManager.SolutionPath;
        var status = _workspaceManager.GetWorkspaceStatus();

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
                         : "No load errors.") +
                     (status.RequiresReload
                         ? $" {status.StaleDocumentCount} file(s) changed on disk since the last load — call LoadSolution to refresh."
                         : ""),
            StaleDocumentCount: status.StaleDocumentCount,
            RequiresReload: status.RequiresReload,
            SampleStaleFiles: status.SampleStaleFiles));
    }

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────

    [McpServerTool(Name = "GetWorkspaceHealth")]
    [Produces(DataTag.ResultOnly)]
    [Description("Targeted workspace health check — reads actual workspace/solution state directly rather than environment probes. Returns IsOperational, HasLoadedSolution, LoadedSolutionPath, ProjectCount, DocumentCount, LoadErrors, Summary, StaleDocumentCount, RequiresReload, SampleStaleFiles. IsOperational=true + HasLoadedSolution=false means no solution loaded yet — not an error. RequiresReload=true means files changed on disk since the last LoadSolution call.")]
    public async Task<ToolResult<object>> GetWorkspaceHealth(
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
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
                Error = new ResultError(ToolErrorCode.Exception, $"GetWorkspaceHealth failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "ListProjectFrameworkTargets")]
    [Produces(DataTag.Report)]
    [Description("Returns each project's TargetFramework value. No parameters.")]
    public async Task<ToolResult<object>> ListProjectFrameworkTargets(
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _projectConsistencyEngine.GetProjectFrameworkSummaryAsync(cancellationToken);
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
                Error = new ResultError(ToolErrorCode.Exception, $"GetProjectFrameworkSummary failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }
}

/// <summary>Return payload for <c>GetMethodSource</c>.</summary>
public record MethodSourceResult
{
    /// <summary>Condensed method declaration: modifiers, return type, name, and parameter list — no body.</summary>
    public string Signature { get; init; } = "";
    /// <summary>Attributes declared on the method, in declaration order.</summary>
    public List<MethodAttributeInfo> Attributes { get; init; } = new();
    /// <summary>Complete source text of the method including attributes and body.</summary>
    public string Source { get; init; } = "";
}

/// <summary>One attribute applied to a method.</summary>
public record MethodAttributeInfo
{
    /// <summary>Attribute name as written in source, e.g. "MigrationCandidate" or "Obsolete".</summary>
    public string Name { get; init; } = "";
    /// <summary>Argument list contents (no outer parentheses), e.g. "\"AsyncBridgeCandidate\", Score = 80". Empty string when no arguments.</summary>
    public string Arguments { get; init; } = "";
}
