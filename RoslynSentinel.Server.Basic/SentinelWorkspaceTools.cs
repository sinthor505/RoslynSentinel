using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;
/// <summary>Structural outline entry returned by get_file_outline.</summary>
public record OutlineItem(string Kind, string Name, string? Container, int StartLine, int EndLine);
/// <summary>Single solution-wide symbol entry returned by ListAll — an OutlineItem plus the file it was found in.</summary>
public record SolutionSymbolEntry(FilePath FilePath, string Kind, string Name, string? Container, int StartLine, int EndLine);
/// <summary>Return payload for <c>GetFileOutline</c>.</summary>
public record FileOutlineResult
{
    /// <summary>Scope/truncation metadata for the file. See <see cref="ReadEnvelope"/>.</summary>
    public ReadEnvelope Envelope { get; init; } = null!;
    /// <summary>The parsed structural outline.</summary>
    public List<OutlineItem> Symbols { get; init; } = new();
}
/// <summary>Single text-search hit returned by search_solution_text.</summary>
public record TextSearchMatch(FilePath filePath, int Line, int Column, string Preview, string? EnclosingMember = null);
/// <summary>
/// A file attached to the solution via a .sln Solution Folder (ProjectSection(SolutionItems)),
/// returned by ListSolutionItems(kind: solutionItems). SolutionFolder is the enclosing folder's
/// display name (e.g. "Solution Items").
/// </summary>
public record SolutionItemFile(FilePath FilePath, string SolutionFolder);
/// <summary>A project entry returned by ListSolutionItems(kind: projects).</summary>
public record ProjectInfoEntry(string Name, string? FilePath);
/// <summary>One project's aggregated files and dependencies, as returned within ListSolutionItems(kind: all).</summary>
public record ProjectFilesAndDependencies(string ProjectName, List<string> Files, ProjectDependencyReport Dependencies);
/// <summary>Combined payload for ListSolutionItems(kind: all): everything the other kinds return in one call, deduplicated by file where applicable.</summary>
public record SolutionItemsAllResult(List<ProjectInfoEntry> Projects, List<SolutionItemFile> SolutionItems, List<ProjectFilesAndDependencies> ProjectDetails);
[McpServerToolType]
public class SentinelWorkspaceTools
{
    // Added by AddConstructorParameter
    private readonly SymbolNavigationEngine _symbolNavigationEngine;    // Added by AddConstructorParameter
    private readonly BuildEngine _buildEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly DiffEngine _diffEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly SolutionManagementEngine _solutionManagementEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<SentinelWorkspaceTools> _logger;
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };

    public SentinelWorkspaceTools(IWorkspaceManager workspaceManager, ValidationEngine validationEngine, DiffEngine diffEngine, DiagnosticEngine diagnosticEngine, SolutionManagementEngine solutionManagementEngine, StructuralRefinementEngine structuralRefinementEngine, DependencyEngine dependencyEngine, ProjectConsistencyEngine projectConsistencyEngine, SentinelConfiguration config, ILogger<SentinelWorkspaceTools> logger, BuildEngine buildEngine, SymbolNavigationEngine symbolNavigationEngine)
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
        _buildEngine = buildEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
    }

    [McpServerTool(Name = "Features")]
    [Produces(DataTag.Report)]
    [Description("Queries or updates feature flags. list → all; get → by names; update → batch-update via enabled as [{Key: featureName, Value: bool}] pairs. delaySeconds (test-only) waits before acting, to exercise MCP task polling/cancellation.")]
    public async Task<ToolResult<object>> Features(FeaturesAction action, List<string>? names = null, List<KeyValuePair<string, bool>>? enabled = null, int delaySeconds = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }

            return action switch
            {
                FeaturesAction.list => new ToolResult<object> { Success = true, Data = _config.GetFeatureStatuses() },
                FeaturesAction.get => new ToolResult<object> { Success = true, Data = _config.GetFeatureStatuses(names) },
                FeaturesAction.update => new ToolResult<object> { Success = true, Data = UpdateFeaturesInternal(enabled ?? []) },
                _ => new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown action '{action}'. Valid values: list, get, update.")
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Features ({Action}) failed", action);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Features")
            };
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
    [Description("Lists projects, files, dependencies, or solution-folder items. files and dependencies require projectName. solutionItems (no projectName needed) returns files attached via the .sln's Solution Folders — e.g. plan/handoff docs referenced there for discoverability in an IDE. These are never part of any project's compiled Documents, so SearchSolutionText and kind=files will never find them; read their content with ProjectDoc. all (no projectName needed/used) returns everything in one call: every project, every solution-folder item, and every project's files and dependencies — use this when you want a complete, guaranteed-non-empty view of the solution instead of guessing which project or kind to ask for.")]
    public async Task<ToolResult<object>> ListSolutionItems([ExternalInputRequired(DataTag.Scope)] SolutionItemsKind kind, [Consumes(DataTag.ProjectName)] string? projectName = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            if (kind == SolutionItemsKind.projects)
            {
                var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                var projectInfos = solution.Projects.Select(p => new ProjectInfoEntry(p.Name, p.FilePath)).ToList();
                return await ToolResult<object>.ForPossiblyLargeDataAsync(
                    projectInfos,
                    _workspaceManager.GetSolutionRoot(),
                    typeof(ProjectInfoEntry).Name,
                    ResultWrapperType.ProjectInfoList,
                    totalRecords: projectInfos.Count,
                    cancellationToken: cancellationToken);
            }

            if (kind == SolutionItemsKind.solutionItems)
            {
                var solutionRoot = _workspaceManager.GetSolutionRoot();
                if (solutionRoot is null)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.SolutionNotLoaded, "No solution loaded. Call LoadSolution first.")
                    };
                }

                var items = _workspaceManager.GetSolutionFolderItems().Select(i => new SolutionItemFile(new FilePath(Path.GetFullPath(Path.Combine(solutionRoot, i.RelativePath)), solutionRoot), i.SolutionFolder)).ToList();
                return await ToolResult<object>.ForPossiblyLargeDataAsync(
                    items,
                    solutionRoot,
                    typeof(SolutionItemFile).Name,
                    ResultWrapperType.SolutionItemFileList,
                    totalRecords: items.Count,
                    cancellationToken: cancellationToken);
            }

            if (kind == SolutionItemsKind.files)
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "projectName is required when kind=files.")
                    };
                }

                try
                {
                    var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                    var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
                    if (project == null)
                    {
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception, $"Project '{projectName}' not found.")
                        };
                    }

                    var sep = Path.DirectorySeparatorChar;
                    var files = project.Documents.Select(d => d.FilePath ?? d.Name).Where(p => !p.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) && !p.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)).ToList();
                    return await ToolResult<object>.ForPossiblyLargeDataAsync(
                        files,
                        _workspaceManager.GetSolutionRoot(),
                        "ProjectFile",
                        ResultWrapperType.ProjectFileList,
                        totalRecords: files.Count,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "List files unexpected exception for project '{ProjectName}'", projectName);
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"List files for project '{projectName}'")
                    };
                }
            }

            if (kind == SolutionItemsKind.dependencies)
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "projectName is required when kind=dependencies.")
                    };
                }

                var result = await _dependencyEngine.GetProjectDependenciesAsync(projectName, cancellationToken);
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = result
                };
            }

            if (kind == SolutionItemsKind.all)
            {
                var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                var solutionRoot = _workspaceManager.GetSolutionRoot();

                var projectInfos = solution.Projects.Select(p => new ProjectInfoEntry(p.Name, p.FilePath)).ToList();

                var solutionItems = new List<SolutionItemFile>();
                if (solutionRoot is not null)
                {
                    solutionItems = _workspaceManager.GetSolutionFolderItems()
                        .Select(i => new SolutionItemFile(new FilePath(Path.GetFullPath(Path.Combine(solutionRoot, i.RelativePath)), solutionRoot), i.SolutionFolder))
                        .ToList();
                }

                var sep = Path.DirectorySeparatorChar;
                var projectDetails = new List<ProjectFilesAndDependencies>();
                // Files are deduped by path within each project's own list (a document can be
                // linked into a project more than once); dependencies are inherently per-project,
                // so they're kept as one report per project rather than merged.
                foreach (var project in solution.Projects)
                {
                    var filesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var document in project.Documents)
                    {
                        var path = document.FilePath ?? document.Name;
                        if (path.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) ||
                            path.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        filesByPath[path] = path;
                    }

                    var dependencies = await _dependencyEngine.GetProjectDependenciesAsync(project.Name, cancellationToken);
                    projectDetails.Add(new ProjectFilesAndDependencies(project.Name, filesByPath.Values.ToList(), dependencies));
                }

                var combined = new SolutionItemsAllResult(projectInfos, solutionItems, projectDetails);
                var totalRecords = projectInfos.Count + solutionItems.Count + projectDetails.Sum(p => p.Files.Count);
                return await ToolResult<object>.ForPossiblyLargeDataAsync(
                    combined,
                    solutionRoot,
                    typeof(SolutionItemsAllResult).Name,
                    ResultWrapperType.SolutionItemsAllResult,
                    totalRecords: totalRecords,
                    cancellationToken: cancellationToken);
            }

            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"Unknown kind '{kind}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List ({Kind}) failed", kind);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "List")
            };
        }
    }

    [McpServerTool(Name = "ListWorkspaceSolutions")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.SolutionList)]
    [Description("Lists all *.sln and *.slnx files under a directory. Returns absolute paths for use with LoadSolution. Pass your workspace root as workspacePath.")]
    public ToolResult<List<SolutionFileInfo>> ListWorkspaceSolutions(string workspacePath, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        workspacePath = FilePath.NormalizeWirePath(workspacePath);
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
            var files = Directory.EnumerateFiles(workspacePath, "*.sln", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(workspacePath, "*.slnx", SearchOption.AllDirectories)).OrderBy(p => p).Select(p => new SolutionFileInfo(Path: p, Format: Path.GetExtension(p).TrimStart('.').ToLowerInvariant())).ToList();
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
    [Description("Loads a .NET solution file into memory for persistent analysis. Must be called before any operation that returns ErrorCode=\"SolutionNotLoaded\". Accepts absolute paths. For relative paths, omit baseRepoDir and let the server resolve it against its configured base directory — only pass baseRepoDir if you have independently confirmed that exact directory exists on this host; a fabricated/guessed baseRepoDir is rejected with an error rather than silently ignored.")]
    public async Task<ToolResult<object>> LoadSolution([Consumes(DataTag.SolutionFilepath, required: true)] string solutionPath, [ToolOption(ToolOptionTag.RepoDirectory)][Description("Optional base directory used to resolve a relative solutionPath (e.g. the repo root). Overrides the server's configured base-repo-dir for this call. Must exist on this host — omit this entirely rather than guessing a value.")] string? baseRepoDir = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            await _workspaceManager.LoadSolutionAsync(solutionPath, baseRepoDir, cancellationToken: cancellationToken);
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (solutionRoot != null)
            {
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = $"Solution loaded: {solutionPath}{BuildPostLoadHint(solutionRoot)}"
                };
            }
            else
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"LoadSolution failed: Workspace root is null after loading '{solutionPath}'.")
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadSolution failed for '{SolutionPath}'", solutionPath);
            // Not routed through ToolErrorMapper: its SolutionNotLoaded branch would
            // say "call LoadSolution first" from inside LoadSolution's own catch block, which is
            // circular and useless here — the exception (e.g. ToolNotFoundException for a bad path)
            // already says what actually went wrong.
            var codeAndMessage = ex is ToolException toolEx
                ? (toolEx.ErrorCode, toolEx.Message)
                : (ToolErrorCode.Exception, $"failed unexpectedly ({ex.GetType().Name}): {ex.Message}");
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(codeAndMessage.Item1, $"LoadSolution '{solutionPath}' {codeAndMessage.Item2}")
            };
        }
    }

    // Subdirectories ProjectDoc reads/writes under docs/, paired with the docType value that
    // maps to each — see DocumentationTools.ProjectDoc.
    private static readonly (string Dir, string DocType)[] ProjectDocSubdirs = [("plans", "plan"), ("handoffs", "handoff"), ("completed", "completed_work"), ("documentation", "documentation"),];
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
    public List<string> ListExternalDiskChanges(// RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return _workspaceManager.GetExternalFileChanges();
    }

    [McpServerTool(Name = "AcknowledgeExternalFileChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Clears the external-change list after the AI has read the latest file changes. No parameters.")]
    public string AcknowledgeExternalFileChanges(// RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var count = _workspaceManager.GetExternalFileChanges().Count;
        _workspaceManager.ClearExternalFileChanges();
        return $"Cleared {count} tracked external file change(s).";
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

    /// <summary>
    /// Fraction of <paramref name="oldContent"/>'s line count that <paramref name="newContent"/>
    /// would remove. Only shrinkage counts — a large *increase* (codegen, genuine expansion) is
    /// not the "submitted a fragment as if it were the whole file" failure mode this guards
    /// against, so it's exempt. Returns 0 for a new file (no oldContent to shrink from) or a
    /// same-size-or-larger replacement.
    /// </summary>
    private static double PercentLinesRemoved(string? oldContent, string newContent)
    {
        if (string.IsNullOrEmpty(oldContent))
        {
            return 0;
        }

        int oldLines = oldContent.Split('\n').Length;
        int newLines = newContent.Split('\n').Length;
        if (newLines >= oldLines || oldLines == 0)
        {
            return 0;
        }

        return (oldLines - newLines) / (double)oldLines;
    }

    /// <summary>
    /// A files-format apply where any file would lose more than this fraction of its line count
    /// is rejected (see <see cref="ToolErrorCode.ConfirmationRequired"/>) rather than applied —
    /// this is the signature of a caller submitting only a changed fragment as if it were the
    /// entire file, rather than an intentional whole-file rewrite. Only shrinkage is checked (see
    /// <see cref="PercentLinesRemoved"/>); a large increase is exempt.
    /// </summary>
    private const double LargeShrinkRejectionThreshold = 0.5;

    [McpServerTool(Name = "ApplyDiff")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set. changesetFormat=files → changes dict filePath→newContent (filepath not used). changesetFormat=diff → filepath and unifiedDiff are BOTH REQUIRED (filepath names the single file the diff applies to; omitting it is a common mistake and fails immediately). For changesetFormat=diff, hunk line numbers are treated as a starting guess: if a hunk's declared position doesn't match, this searches nearby lines and re-anchors automatically, so modest line-number drift from an earlier edit to the same file is tolerated. Returns ApplyChangesResult with UndoChangeId on successful apply. The full pre-edit file content is NOT included by default (it's already captured for undo via UndoLastApply/GetOperationDetail) — pass returnDiff=true to get a unified-diff-style preview of what changed instead. IMPORTANT: for changesetFormat=files with action=apply, any file whose content would shrink by more than 50% is rejected with errorCode=ConfirmationRequired — this is a strong signal you submitted only a changed fragment as if it were the whole file, rather than a genuine whole-file rewrite. If that happens, re-submit the complete, unabridged file content in a fresh ApplyDiff call (or switch to changesetFormat=diff for a partial edit) — do not retry with a different action.")]
    public async Task<ToolResult<object>> ApplyDiff([ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat, [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action, [ExternalInputRequired(DataTag.OperationId)] Dictionary<FilePath, string>? changes = null, [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null, [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null, [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3, [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);
            if (changesetFormat == ChangesetFormat.files)
            {
                if (changes == null)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "changes is required when changesetFormat=files.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    string? oversizedFile = null;
                    double oversizedPercent = 0;
                    foreach (var (changedPath, newContent) in changes)
                    {
                        var oldContent = await FileIoHelper.ReadAllTextIfExistsAsync(changedPath, cancellationToken);
                        var percentRemoved = PercentLinesRemoved(oldContent, newContent);
                        if (percentRemoved > LargeShrinkRejectionThreshold)
                        {
                            oversizedFile = changedPath;
                            oversizedPercent = percentRemoved;
                            break;
                        }
                    }

                    if (oversizedFile != null)
                    {
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.ConfirmationRequired,
                                $"File '{oversizedFile}' would shrink by {oversizedPercent:P0}, exceeding the {LargeShrinkRejectionThreshold:P0} threshold for a files-format apply. " +
                                "This usually means only a changed fragment was submitted instead of the complete file content. If this is a genuine whole-file rewrite, re-submit ApplyDiff with the complete file content included in 'changes'. For a partial edit, use changesetFormat=diff instead.")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception,
                                "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors — change not applied. Fix the issue(s) below and retry:\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await WriteBlobForApplyAsync("apply_diff", result);
                    // PreImages (full pre-edit file content) is dropped from the default response -
                    // it's already captured in the undo blob written above (GetOperationDetail/
                    // UndoLastApply can retrieve it) and was the single largest contributor to
                    // ApplyDiff responses exceeding the calling harness's token limit on large files.
                    var strippedResult = result with { PreImages = null };
                    object responseData = returnDiff
                        ? new
                        {
                            result = strippedResult,
                            diff = SentinelRefactoringTools.BuildDiffFromPreImages(changes, result.PreImages)
                        }
                        : strippedResult;
                    return new ToolResult<object>()
                    {
                        Success = true,
                        Data = responseData
                    };
                }

                if (action == ProposedChangeAction.validate)
                {
                    try
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(changes);
                        return validationResult.Success ? new ToolResult<object>()
                        {
                            Success = true,
                            Data = validationResult
                        }

                        : new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff validate failed: {validationResult.Diagnostics}")
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff validate unexpected exception");
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff validate")
                        };
                    }
                }
            }
            else if (changesetFormat == ChangesetFormat.diff)
            {
                if (!filePath.Validated && string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: both 'filepath' and 'unifiedDiff' are required when changesetFormat=diff.")
                    };
                }

                if (!filePath.Validated)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'filepath' is required when changesetFormat=diff (it names the single file the unifiedDiff applies to). Only changesetFormat=files takes multiple files via 'changes'.")
                    };
                }

                if (string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'unifiedDiff' is required when changesetFormat=diff.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    try
                    {
                        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath.Absolute || d.FilePath == filePath.Absolute);
                        if (document == null)
                        {
                            return new ToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                            };
                        }

                        var oldText = await document.GetTextAsync();
                        var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff).ToString();
                        var targetPath = document.FilePath ?? filePath;
                        var diffChanges = new Dictionary<FilePath, string>
                        {
                            [targetPath] = newContent
                        };
                        var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges, validateChanges: validateOnApply);
                        if (!result.Success && result.ValidationResult != null)
                            return new ToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors — change not applied. Fix the issue(s) below and retry:\n" +
                                    await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                            };
                        await WriteBlobForApplyAsync("apply_diff", result);
                        var strippedDiffResult = result with { PreImages = null };
                        object diffResponseData = returnDiff
                            ? new
                            {
                                result = strippedDiffResult,
                                diff = SentinelRefactoringTools.BuildDiffFromPreImages(diffChanges, result.PreImages)
                            }
                            : strippedDiffResult;
                        return new ToolResult<object>()
                        {
                            Success = true,
                            Data = diffResponseData
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff diff apply unexpected exception for '{FilePath}'", filePath);
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyDiff diff apply for '{filePath}'")
                        };
                    }
                }

                if (action == ProposedChangeAction.validate)
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePath.Absolute, unifiedDiff);
                    return validationResult.Success ? new ToolResult<object>()
                    {
                        Success = true,
                        Data = validationResult
                    }

                    : new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff diff validate failed: {validationResult}")
                    };
                }
            }

            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"Unhandled changesetFormat '{changesetFormat}' / action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyDiff ({ChangesetFormat}/{Action}) failed", changesetFormat, action);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff")
            };
        }
    }

    // The confirmationCode paramater was causing hallucinations and invalid tool calls. Reverted back to the original ApplyDiff tool but keeping this here (block-commented, since it depends
    // on ProposedChangeAction.confirmationCode, which is also commented out in ToolEnums.cs) in case we want to reintroduce ApplyDiff with a confirmationCode in the future.
    /*
    //[McpServerTool(Name = "ApplyDiffWithConfirmationCode")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set. changesetFormat=files → changes dict filePath→newContent (filepath not used). changesetFormat=diff → filepath and unifiedDiff are BOTH REQUIRED (filepath names the single file the diff applies to; omitting it is a common mistake and fails immediately). For changesetFormat=diff, hunk line numbers are treated as a starting guess: if a hunk's declared position doesn't match, this searches nearby lines and re-anchors automatically, so modest line-number drift from an earlier edit to the same file is tolerated. Returns ApplyChangesResult with UndoChangeId on successful apply. The full pre-edit file content is NOT included by default (it's already captured for undo via UndoLastApply/GetOperationDetail) — pass returnDiff=true to get a unified-diff-style preview of what changed instead. IMPORTANT: for changesetFormat=files with action=apply, any file whose content would shrink by more than 50% is rejected with errorCode=ConfirmationRequired — this is a strong signal you submitted only a changed fragment as if it were the whole file, rather than a genuine whole-file rewrite. If the rewrite is really intended, call ApplyDiff again with action=confirmationCode and confirmationCode set to the code from the rejection — do not resend changes/filepath/unifiedDiff on that call, the original changeset is already cached server-side.")]
    public async Task<ToolResult<object>> ApplyDiffWithConfirmationCode([ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat, [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action, [ExternalInputRequired(DataTag.OperationId)] Dictionary<FilePath, string>? changes = null, [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null, [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null, [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3, [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, [ToolOption(ToolOptionTag.ConfirmationCode)][Description("Required when action=confirmationCode. The code returned by a prior apply call that was rejected for exceeding the whole-file-rewrite size threshold. Replays that exact cached changeset — do not also pass changes/filepath/unifiedDiff.")] string? confirmationCode = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            if (action == ProposedChangeAction.confirmationCode)
            {
                if (string.IsNullOrEmpty(confirmationCode))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "confirmationCode is required when action=confirmationCode.")
                    };
                }

                var pending = _workspaceManager.TakePendingChangeset(confirmationCode);
                if (pending == null)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, $"confirmationCode '{confirmationCode}' is unrecognized or has expired (codes are single-use and expire after 10 minutes). Resubmit the original ApplyDiff(changesetFormat: files, action: apply, ...) call to get a fresh code.")
                    };
                }

                var confirmedResult = await _workspaceManager.ApplyProposedChangesAsync(pending.Value.Changes, pending.Value.RetryCount, validateChanges: pending.Value.ValidateOnApply);
                if (!confirmedResult.Success && confirmedResult.ValidationResult != null)
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff pre-apply validate failed: {confirmedResult.ValidationResult.Diagnostics.ToJson()}")
                    };
                await WriteBlobForApplyAsync("apply_diff", confirmedResult);
                var strippedConfirmedResult = confirmedResult with { PreImages = null };
                object confirmedResponseData = returnDiff
                    ? new
                    {
                        result = strippedConfirmedResult,
                        diff = SentinelRefactoringTools.BuildDiffFromPreImages(pending.Value.Changes, confirmedResult.PreImages)
                    }
                    : strippedConfirmedResult;
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = confirmedResponseData
                };
            }

            FilePath filePath = _workspaceManager.SetFilePath(filepath);
            if (changesetFormat == ChangesetFormat.files)
            {
                if (changes == null)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "changes is required when changesetFormat=files.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    string? oversizedFile = null;
                    double oversizedPercent = 0;
                    foreach (var (changedPath, newContent) in changes)
                    {
                        var oldContent = await FileIoHelper.ReadAllTextIfExistsAsync(changedPath, cancellationToken);
                        var percentRemoved = PercentLinesRemoved(oldContent, newContent);
                        if (percentRemoved > LargeShrinkRejectionThreshold)
                        {
                            oversizedFile = changedPath;
                            oversizedPercent = percentRemoved;
                            break;
                        }
                    }

                    if (oversizedFile != null)
                    {
                        var code = _workspaceManager.CachePendingChangeset(changes, retryCount, validateOnApply);
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.ConfirmationRequired,
                                $"File '{oversizedFile}' would shrink by {oversizedPercent:P0}, exceeding the {LargeShrinkRejectionThreshold:P0} threshold for a files-format apply. " +
                                "This usually means only a changed fragment was submitted instead of the complete file content — use changesetFormat=diff for a partial edit instead. " +
                                $"If a whole-file rewrite to this size is genuinely intended, call ApplyDiff again with action=confirmationCode and confirmationCode=\"{code}\" to apply the exact changeset just submitted (no need to resend changes). This code expires in 10 minutes.")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception,
                                "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors — change not applied. Fix the issue(s) below and retry:\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await WriteBlobForApplyAsync("apply_diff", result);
                    // PreImages (full pre-edit file content) is dropped from the default response -
                    // it's already captured in the undo blob written above (GetOperationDetail/
                    // UndoLastApply can retrieve it) and was the single largest contributor to
                    // ApplyDiff responses exceeding the calling harness's token limit on large files.
                    var strippedResult = result with { PreImages = null };
                    object responseData = returnDiff
                        ? new
                        {
                            result = strippedResult,
                            diff = SentinelRefactoringTools.BuildDiffFromPreImages(changes, result.PreImages)
                        }
                        : strippedResult;
                    return new ToolResult<object>()
                    {
                        Success = true,
                        Data = responseData
                    };
                }

                if (action == ProposedChangeAction.validate)
                {
                    try
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(changes);
                        return validationResult.Success ? new ToolResult<object>()
                        {
                            Success = true,
                            Data = validationResult
                        }

                        : new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff validate failed: {validationResult.Diagnostics}")
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff validate unexpected exception");
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff validate")
                        };
                    }
                }
            }
            else if (changesetFormat == ChangesetFormat.diff)
            {
                if (!filePath.Validated && string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: both 'filepath' and 'unifiedDiff' are required when changesetFormat=diff.")
                    };
                }

                if (!filePath.Validated)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'filepath' is required when changesetFormat=diff (it names the single file the unifiedDiff applies to). Only changesetFormat=files takes multiple files via 'changes'.")
                    };
                }

                if (string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: 'unifiedDiff' is required when changesetFormat=diff.")
                    };
                }

                if (action == ProposedChangeAction.apply)
                {
                    try
                    {
                        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath.Absolute || d.FilePath == filePath.Absolute);
                        if (document == null)
                        {
                            return new ToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                            };
                        }

                        var oldText = await document.GetTextAsync();
                        var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff).ToString();
                        var targetPath = document.FilePath ?? filePath;
                        var diffChanges = new Dictionary<FilePath, string>
                        {
                            [targetPath] = newContent
                        };
                        var result = await _workspaceManager.ApplyProposedChangesAsync(diffChanges, validateChanges: validateOnApply);
                        if (!result.Success && result.ValidationResult != null)
                            return new ToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    "ApplyDiff: the diff was valid and matched the target file, but the resulting code introduces new compiler errors — change not applied. Fix the issue(s) below and retry:\n" +
                                    await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                            };
                        await WriteBlobForApplyAsync("apply_diff", result);
                        var strippedDiffResult = result with { PreImages = null };
                        object diffResponseData = returnDiff
                            ? new
                            {
                                result = strippedDiffResult,
                                diff = SentinelRefactoringTools.BuildDiffFromPreImages(diffChanges, result.PreImages)
                            }
                            : strippedDiffResult;
                        return new ToolResult<object>()
                        {
                            Success = true,
                            Data = diffResponseData
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ApplyDiff diff apply unexpected exception for '{FilePath}'", filePath);
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyDiff diff apply for '{filePath}'")
                        };
                    }
                }

                if (action == ProposedChangeAction.validate)
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePath.Absolute, unifiedDiff);
                    return validationResult.Success ? new ToolResult<object>()
                    {
                        Success = true,
                        Data = validationResult
                    }

                    : new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff diff validate failed: {validationResult}")
                    };
                }
            }

            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"Unhandled changesetFormat '{changesetFormat}' / action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyDiff ({ChangesetFormat}/{Action}) failed", changesetFormat, action);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ApplyDiff")
            };
        }
    }
    */

    [McpServerTool(Name = "CreateFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Creates a new file with the given content. Fails if the file already exists — use ApplyDiff (changesetFormat=files, action=apply) to overwrite an existing file. Routes through the same write-path chokepoint as every other mutating tool (drift-checked, undo-tracked via UndoLastApply). Parent directories are created automatically if missing.")]
    public async Task<ToolResult<object>> CreateFile([Consumes(DataTag.SourceFilepath, required: true)] string filepath, [Description("Full content of the new file.")] string content, [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true, CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (File.Exists(filePath))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"CreateFile: '{filePath}' already exists. Use ApplyDiff to overwrite an existing file.")
                };
            }

            var directory = Path.GetDirectoryName((string)filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var changes = new Dictionary<FilePath, string> { [filePath] = content };
            var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: validateOnApply, cancellationToken: cancellationToken);
            if (!result.Success && result.ValidationResult != null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"CreateFile pre-apply validate failed: {result.ValidationResult.Diagnostics.ToJson()}")
                };
            }

            if (!result.Success)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"CreateFile failed to write '{filePath}': {result.Summary}")
                };
            }

            await WriteBlobForApplyAsync("create_file", result);
            var strippedResult = result with { PreImages = null };
            return new ToolResult<object>()
            {
                Success = true,
                Data = strippedResult
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateFile failed for '{FilePath}'", filePath);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"CreateFile for '{filePath}'")
            };
        }
    }

    [McpServerTool(Name = "DeleteFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Deletes a file from disk. Fails if the file does not exist. Routes through the same write-path chokepoint as every other mutating tool: refused if the file was modified externally since the last sync (see ListExternalDiskChanges/ClearExternalDrift), and undoable via UndoLastApply (the pre-delete content is captured). If the file is a tracked Roslyn Document, it's removed from the in-memory solution as part of the same operation.")]
    public async Task<ToolResult<object>> DeleteFile([Consumes(DataTag.SourceFilepath, required: true)] string filepath, CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (!File.Exists(filePath))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"DeleteFile: '{filePath}' does not exist.")
                };
            }

            var result = await _workspaceManager.ApplyProposedChangesAsync(
                changes: [],
                cancellationToken: cancellationToken,
                deletePaths: [filePath]);
            if (!result.Success)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"DeleteFile failed to delete '{filePath}': {result.Summary}")
                };
            }

            await WriteBlobForApplyAsync("delete_file", result);
            var strippedResult = result with { PreImages = null };
            return new ToolResult<object>()
            {
                Success = true,
                Data = strippedResult
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteFile failed for '{FilePath}'", filePath);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"DeleteFile for '{filePath}'")
            };
        }
    }

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries failed file writes using server-cached content — no need to re-send file contents. specificFiles limits to a subset. retryCount defaults to 3.")]
    public async Task<ToolResult<object>> RetryFailedChanges([Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null, [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            return new ToolResult<object>()
            {
                Success = true,
                Data = await _workspaceManager.RetryFailedChangesAsync(specificFiles, retryCount, cancellationToken)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RetryFailedChanges failed");
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "RetryFailedChanges")
            };
        }
    }

    /// <summary>
    /// Writes a forensic blob for a completed apply so undo_last_apply can revert it.
    /// Uses pre-images from ApplyChangesResult.PreImages (populated by ApplyProposedChangesAsync).
    /// blobChangeId: if provided, uses this id for the blob filename; if null, mints a fresh id.
    /// Logs a warning but does not throw on blob write failure — apply already succeeded.
    /// </summary>
    private async Task WriteBlobForApplyAsync(string toolName, ApplyChangesResult result, string? blobChangeId = null, // RequestContext<CallToolRequestParams> requestParams = null,
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
        var blobName = await OperationBlobWriter.WriteAsync(toolName, changeId, items, _workspaceManager.GetSolutionRoot(), cancellationToken);
        // OperationBlobWriter returns a diagnostic string (not an exception) on failure.
        if (blobName.StartsWith('('))
        {
            _logger.LogWarning("Blob write failed for {ToolName}/{ChangeId}: {Reason}. " + "undo_last_apply will not be available for this apply.", toolName, changeId, blobName);
        }
        else if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Forensic blob written: {BlobName} (changeId={ChangeId})", blobName, changeId);
        }
    }

    [McpServerTool(Name = "GetDiagnostics")]
    [Produces(DataTag.Report)]
    [Description("Gets compiler diagnostics. file → scopeName=filePath; project → scopeName=projectName; solution → scopeName ignored. summarize=true groups by diagnostic ID and returns counts. maxDetails caps raw list (default 50). topN caps groups (default 20). verify=quickBuild/fullBuild additionally runs a build check (see Build tool) and attaches it as BuildVerification.")]
    public async Task<ToolResult<object>> GetDiagnostics([Consumes(DataTag.ProjectName, required: true)][Consumes(DataTag.SourceFilepath, required: false)] ToolScope scope = ToolScope.solution, string? scopeName = null, bool summarize = false, [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50, [ToolOptionAttribute(ToolOptionTag.TopN)] int topN = 20, BuildVerifyLevel verify = BuildVerifyLevel.noBuild, // RequestContext<CallToolRequestParams> requestParams = null,
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
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (filePath) is required when scope=file.")
                    };
                }

                result = await _diagnosticEngine.GetFileDiagnosticsAsync(scopeName);
                summary = result.Data;
            }
            else if (scope == ToolScope.project)
            {
                if (string.IsNullOrEmpty(scopeName))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "scopeName (projectName) is required when scope=project.")
                    };
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
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"Unhandled scope '{scope}'.")
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
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = result.Data with { BuildVerification = buildVerification }
                };
            }

            var relevant = result.Data.Details.Where(d => d.Severity is "Error" or "Warning").ToList();
            var groups = relevant.GroupBy(d => d.Id).Select(g =>
            {
                var first = g.First();
                var locations = g.Select(d => $"{d.FilePath}:{d.StartLine}").Distinct().Take(10).ToList();
                return new DiagnosticGroupSummary(DiagnosticId: g.Key, Severity: first.Severity, MessageTemplate: first.Message, Count: g.Count(), Locations: locations);
            }).OrderByDescending(g => g.Count).Take(topN).ToList();
            return new ToolResult<object>()
            {
                Success = true,
                Data = new DiagnosticsSummaryResult(TotalIssues: relevant.Count, Errors: summary.Errors, Warnings: summary.Warnings, TopIssues: groups, BuildVerification: buildVerification)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDiagnostics ({Scope}) failed", scope);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetDiagnostics")
            };
        }
    }

    [McpServerTool(Name = "Build")]
    [Produces(DataTag.Report)]
    [Description("Compiles the loaded solution and reports errors/warnings. level=quickBuild uses in-memory Roslyn diagnostics (fast, same check GetDiagnostics does). level=fullBuild shells out to `dotnet build` (slower, catches MSBuild-only failures — NuGet restore, resource copy, post-build events — that quickBuild can't see). Returns BuildSucceeded, ExitCode, ErrorCount/WarningCount, capped Errors/Warnings lists, Duration.")]
    public async Task<ToolResult<object>> Build(
        BuildVerifyLevel level = BuildVerifyLevel.fullBuild,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rateLimitError = _workspaceManager.CheckRateLimit("Build", 10);
            if (rateLimitError is not null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.BuildFailed, rateLimitError) };
            }

            var result = level == BuildVerifyLevel.fullBuild
                ? await _buildEngine.RunFullBuildAsync(cancellationToken)
                : await _buildEngine.RunQuickBuildAsync(scope, scopeName, maxDetails, cancellationToken);

            if (!result.TryGetData(out var buildResult))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.BuildFailed, result.Error?.Message ?? "Build failed unexpectedly.") };
            }

            return new ToolResult<object>() { Success = true, Data = buildResult, WorkspaceVersion = _workspaceManager.WorkspaceVersion };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Build ({Level}) failed", level);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Build failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and dotnet is on PATH. Details: {ex.Message}") };
        }
    }

    [McpServerTool(Name = "SafeDeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages in the entire codebase. Preferred path: handle-based resolution using projectName and docCommentId (from LocateSymbol/FindReferences — the most reliable and accurate symbol resolution). Fallback paths: symbolName with contextSnippet/lineBefore/lineAfter (snippet-based resolution — symbolName alone if there's only one declaration with that name), or line/column (1-based, both required) at the declaration site. Distinction from RemoveMember: this tool refuses if ANY usage is found; RemoveMember checks for callers/implementations but allows skipPrecheck. Returns changeId.")]
    public async Task<ToolResult<object>> SafeDeleteUnusedSymbol([Consumes(DataTag.SourceFilepath, required: true)] string filepath, [Description("Project name containing the symbol. Required for handle-based resolution.")] string projectName = "", [Description("Documentation comment ID of the symbol. Required for handle-based resolution.")] string docCommentId = "", [Consumes(DataTag.SymbolName, required: false)] string? symbolName = null, [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null, [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null, [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null, [Consumes(DataTag.StartLine, required: false)] int line = 0, [Consumes(DataTag.Offset, required: false)] int column = 0, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        async Task<ToolResult<object>> ApplyAndRespondAsync(DocumentEditResult result)
        {
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"SafeDeleteUnusedSymbol: no change produced for '{filePath}' ({result.Outcome}). {result.Message}")
                };
            }

            var changes = new Dictionary<FilePath, string>
            {
                [filePath] = result.UpdatedText
            };
            var apply = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount: 3, validateChanges: true, cancellationToken: cancellationToken);
            if (!apply.Success)
            {
                var reason = apply.ValidationResult is not null ? $"introduces new compiler errors — change not applied. Fix diagnostics and retry: {apply.ValidationResult.Diagnostics.ToJson()}" : $"failed to write to disk: {apply.Summary}";
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"SafeDeleteUnusedSymbol {reason}")
                };
            }

            var changeId = Guid.NewGuid().ToString("n")[..8];
            await WriteBlobForApplyAsync("safe_delete_unused_symbol", apply, changeId, cancellationToken);
            return new ToolResult<object>()
            {
                Success = true,
                Data = new AppliedChangeSummary(changeId, [filePath], $"Deleted unused symbol in {Path.GetFileName(filePath)}.", false)
            };
        }

        try
        {
            // Primary path: handle-based resolution (docCommentId + projectName, from LocateSymbol/
            // FindReferences). sessionId is intentionally not exposed on this tool's surface — it is
            // never obtainable through any tool an agent can call, so requiring it would make this
            // path permanently unsatisfiable; ResolveFromWireAsync already treats an absent sessionId
            // as "not stale" (nothing to compare against), so omitting it here is correct, not a
            // workaround.
            if (!string.IsNullOrEmpty(docCommentId) && !string.IsNullOrEmpty(projectName))
            {
                SymbolResolution resolution = await _workspaceManager.ResolveFromWireAsync(string.Empty, projectName, docCommentId, cancellationToken);
                if (!resolution.Resolved)
                {
                    return new ToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, resolution.Error!.Message)
                    };
                }

                var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePath, resolution.Symbol!, cancellationToken);
                return await ApplyAndRespondAsync(result);
            }

            // Fallback: symbolName + contextSnippet-based resolution. Added because the tool's own
            // description previously promised this path while the implementation silently ignored
            // contextSnippet/lineBefore/lineAfter entirely, leaving callers with no way to identify a
            // target by anything other than a raw line/column pair (see the next branch) — and no
            // tool exposes a column, only a line, making that pair effectively unobtainable too.
            if (!string.IsNullOrEmpty(symbolName))
            {
                var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                return await ApplyAndRespondAsync(result);
            }

            // Fallback: line/column-based resolution (legacy path) — requires a precise column, which
            // no other tool surfaces; prefer symbolName+contextSnippet above when possible.
            if (line > 0 && column > 0)
            {
                var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePath, line, column, cancellationToken);
                return await ApplyAndRespondAsync(result);
            }

            // No valid parameters provided
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "SafeDeleteUnusedSymbol requires one of: (projectName, docCommentId) for handle-based resolution, (symbolName, optionally with contextSnippet/lineBefore/lineAfter) for name-based resolution, or (line, column) for legacy line/column-based resolution.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeDeleteUnusedSymbol failed for '{FilePath}' at {Line}:{Column} or handle {ProjectName}/{DocCommentId}", filePath, line, column, projectName, docCommentId);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SafeDeleteUnusedSymbol")
            };
        }
    }

    [McpServerTool(Name = "CreateProject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Creates a new project and adds it to the current solution. projectType defaults to console.")]
    public async Task<ToolResult<object>> CreateProject([ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName, [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console", // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _solutionManagementEngine.CreateProjectAsync(projectName, projectType, cancellationToken);
            return new ToolResult<object>()
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateProject failed for '{ProjectName}'", projectName);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "CreateProject")
            };
        }
    }

    [McpServerTool(Name = "SplitProjectByFolder")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves all files under a specific folder from a source project to a new target project, preserving folder structure.")]
    public async Task<ToolResult<object>> SplitProjectByFolder([Consumes(DataTag.ProjectName, required: true)] string sourceProjectName, [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName, [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _solutionManagementEngine.SplitProjectByFolderAsync(sourceProjectName, folderName, targetProjectName, cancellationToken);
            return new ToolResult<object>()
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SplitProjectByFolder failed for '{SourceProjectName}'", sourceProjectName);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SplitProjectByFolder")
            };
        }
    }

    // ── Phase 1 — Low-level fallback tools ──────────────────────────────────
    [McpServerTool(Name = "GetMethodSource")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the full source text of a named method or constructor, plus a structured list of its attributes. For a constructor, pass the containing class's name (e.g. methodName: \"OrderService\" for `public OrderService(...)`). Case-sensitive match with case-insensitive fallback. Returns the first match for overloaded names.")]
    public async Task<ToolResult<object>> GetMethodSource([Consumes(DataTag.SourceFilepath, required: true)] string filepath, [Consumes(DataTag.MethodName, required: true)] string methodName, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var normalizedPath = Path.GetFullPath(filePath);
            var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault() ?? solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (document == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("FileNotFound", $"File not found in solution: {normalizedPath} (existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).")
                };
            }

            var root = await document.GetSyntaxRootAsync();
            if (root == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("SyntaxRootNotFound", "Syntax root not found.")
                };
            }

            // Constructors are ConstructorDeclarationSyntax, not MethodDeclarationSyntax, but callers
            // naturally pass the class name for "give me the source of its constructor" — resolve
            // both node kinds under the shared BaseMethodDeclarationSyntax base.
            var method = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault(m => GetMethodOrCtorName(m).Equals(methodName, StringComparison.Ordinal)) ?? root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault(m => GetMethodOrCtorName(m).Equals(methodName, StringComparison.OrdinalIgnoreCase));
            if (method == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("MethodNotFound", $"Method or constructor '{methodName}' not found in '{filePath}'.")
                };
            }

            var methodSource = method.ToFullString();
            var methodBytes = System.Text.Encoding.UTF8.GetByteCount(methodSource);
            var attributes = ExtractAttributes(method);
            var signature = BuildSignature(method);
            _logger.LogInformation("GetMethodSource: {SizeBytes} bytes for '{MethodName}'", methodBytes, methodName);
            const int thresholdBytes = LargeResultHelper.OffloadThresholdBytes;
            var solutionRoot = _workspaceManager.GetSolutionRoot();

            var fileText = await document.GetTextAsync(cancellationToken);
            var fileLineCount = fileText.Lines.Count;
            var fileByteCount = System.Text.Encoding.UTF8.GetByteCount(fileText.ToString());
            var methodSpan = method.GetLocation().GetLineSpan();
            var envelope = ReadEnvelopeBuilder.Build(
                fileLineCount, fileByteCount,
                returnedFromLine: methodSpan.StartLinePosition.Line + 1,
                returnedToLine: methodSpan.EndLinePosition.Line + 1);

            if (methodBytes > thresholdBytes && !string.IsNullOrEmpty(solutionRoot))
            {
                var fullResult = new MethodSourceResult { Envelope = envelope, Signature = signature, Source = methodSource, Attributes = attributes };
                var stored = await LargeResultHelper.StoreLargeResultAsync(fullResult, solutionRoot, ResultWrapperType.MethodSource, cancellationToken);
                return new ToolResult<object>
                {
                    Success = true,
                    LargeResult = new LargeResultInfo(resultType: "MethodSource", writtenToFile: stored.offloaded, filePath: stored.filePath, resultId: stored.resultId!, sizeBytes: methodBytes, totalRecords: 1, message: $"Result is {methodBytes} bytes (threshold: {thresholdBytes}). " + $"Use get_large_result(resultId: \"{stored.resultId}\") to page through results."),
                    Data = new
                    {
                        envelope,
                        signature,
                        attributes
                    },
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            return new ToolResult<object>()
            {
                Success = true,
                Data = new MethodSourceResult
                {
                    Envelope = envelope,
                    Signature = signature,
                    Source = methodSource,
                    Attributes = attributes
                },
                WorkspaceVersion = _workspaceManager.WorkspaceVersion,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMethodSource failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetMethodSource")
            };
        }
    }

    [McpServerTool(Name = "ReadFile")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the raw text of a file in the loaded solution, verbatim (no reformatting). Pass startLine/endLine (1-based, inclusive) to read a slice instead of the whole file — useful once GetFileOutline or a search result gives you a line range. Whole-file reads past the size threshold are written to .roslynsentinel/largeresults and returned as a resultId (see GetMethodSource) instead of inline text.")]
    public async Task<ToolResult<object>> ReadFile([Consumes(DataTag.SourceFilepath, required: true)] string filepath, [Description("1-based, inclusive. Omit to start from the first line.")] int? startLine = null, [Description("1-based, inclusive. Omit to read through the last line.")] int? endLine = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var normalizedPath = Path.GetFullPath(filePath);
            var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault() ?? solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (document == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("FileNotFound", $"File not found in solution: {normalizedPath} (existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).")
                };
            }

            var sourceText = await document.GetTextAsync(cancellationToken);
            var totalLines = sourceText.Lines.Count;
            if (startLine.HasValue || endLine.HasValue)
            {
                int from = Math.Max(1, startLine ?? 1);
                int to = Math.Min(totalLines, endLine ?? totalLines);
                if (from > totalLines || from > to)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, $"ReadFile: requested range {from}-{to} is out of bounds for a {totalLines}-line file.")
                    };
                }

                var start = sourceText.Lines[from - 1].Start;
                var end = sourceText.Lines[to - 1].EndIncludingLineBreak;
                var slice = sourceText.ToString(TextSpan.FromBounds(start, end));
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = new
                    {
                        filePath = (string)filePath,
                        startLine = from,
                        endLine = to,
                        totalLines,
                        source = slice
                    },
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            var fullText = sourceText.ToString();
            var textBytes = System.Text.Encoding.UTF8.GetByteCount(fullText);
            const int thresholdBytes = LargeResultHelper.OffloadThresholdBytes;
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (textBytes > thresholdBytes && !string.IsNullOrEmpty(solutionRoot))
            {
                var fullResult = new FileSourceResult { FilePath = (string)filePath, StartLine = 1, EndLine = totalLines, TotalLines = totalLines, Source = fullText };
                var stored = await LargeResultHelper.StoreLargeResultAsync(fullResult, solutionRoot, ResultWrapperType.FileSource, cancellationToken);

                try
                {
                    var fileOutline = await GetFileOutline(filepath, cancellationToken);
                    if (fileOutline.LargeResult is null)
                    {
                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new
                            {
                                largeResult = new LargeResultInfo(resultType: "FileSource", writtenToFile: stored.offloaded, filePath: stored.filePath, resultId: stored.resultId!, sizeBytes: textBytes, totalRecords: 1, message: $"The file content exceeds the threshold, only the file outline is shown here. The full result is {totalLines} lines, {textBytes} bytes (threshold: {thresholdBytes}). " + $"Use get_large_result(resultId: \"{stored.resultId}\") to page through results, or retry ReadFile with startLine/endLine for just the slice you need."),
                                fileOutline.Data
                            },
                            WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GetFileOutline failed for '{FilePath}'", filePath);
                }

                return new ToolResult<object>
                {
                    Success = true,
                    LargeResult = new LargeResultInfo(resultType: "FileSource", writtenToFile: stored.offloaded, filePath: stored.filePath, resultId: stored.resultId!, sizeBytes: textBytes, totalRecords: 1, message: $"Result is {totalLines} lines, {textBytes} bytes (threshold: {thresholdBytes}). " + $"Use get_large_result(resultId: \"{stored.resultId}\") to page through results, or retry ReadFile with startLine/endLine for just the slice you need, or use GetFileOutline to get the constructors, methods, helpers, members, enums, fields, properties, etc of a file without reading the entire file."),
                    Data = new
                    {
                        totalLines
                    },
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            return new ToolResult<object>()
            {
                Success = true,
                Data = new
                {
                    filePath = (string)filePath,
                    startLine = 1,
                    endLine = totalLines,
                    totalLines,
                    source = fullText
                },
                WorkspaceVersion = _workspaceManager.WorkspaceVersion,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReadFile failed for '{FilePath}'", filePath);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReadFile")
            };
        }
    }

    [McpServerTool(Name = "GetFileOutline")]
    [Produces(DataTag.Report)]
    [Description("Returns a structural outline of a file — namespaces, classes, structs, records, interfaces, enums (and their members), methods, properties, constructors, and fields, with 1-based line ranges. Member bodies are not included.")]
    public async Task<ToolResult<object>> GetFileOutline([Consumes(DataTag.SourceFilepath, required: true)] string filepath, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var normalizedPath = Path.GetFullPath(filePath);
            var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault() ?? solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (document == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("FileNotFound", $"File not found in solution: {normalizedPath} (existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).")
                };
            }

            var root = await document.GetSyntaxRootAsync();
            if (root == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("SyntaxRootNotFound", "Syntax root not found.")
                };
            }

            var items = ExtractOutlineItems(root);

            var fileText = await document.GetTextAsync(cancellationToken);
            var fileLineCount = fileText.Lines.Count;
            var fileByteCount = System.Text.Encoding.UTF8.GetByteCount(fileText.ToString());
            var envelope = ReadEnvelopeBuilder.Build(fileLineCount, fileByteCount, returnedFromLine: 1, returnedToLine: fileLineCount);

            return new ToolResult<object>()
            {
                Success = true,
                Data = new FileOutlineResult { Envelope = envelope, Symbols = items },
                WorkspaceVersion = _workspaceManager.WorkspaceVersion
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFileOutline failed for '{FilePath}'", filePath);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetFileOutline")
            };
        }
    }

    /// <summary>Walks a document's syntax tree and extracts the same outline entries GetFileOutline returns for one file — shared with ListAll, which runs this across every document in the solution.</summary>
    private static List<OutlineItem> ExtractOutlineItems(SyntaxNode root)
    {
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
                    container = (cls.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (cls.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case InterfaceDeclarationSyntax iface:
                    kind = "interface";
                    name = iface.Identifier.Text;
                    container = (iface.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (iface.Parent as TypeDeclarationSyntax)?.Identifier.Text;
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
                // Struct/record/enum, and enum members, constructors, and fields were never
                // covered here — a file containing only these (e.g. a pure enum file) produced
                // an outline with nothing but a "namespace" entry, silently implying the file had
                // no commentable/editable members at all. Confirmed live: an agent asked to add
                // summary comments to every member skipped OrderStatus.cs's enum entirely because
                // GetFileOutline gave no indication OrderStatus existed, then SummaryComment also
                // failed once the agent tried it anyway (separate gap, see GetMemberName).
                case StructDeclarationSyntax @struct:
                    kind = "struct";
                    name = @struct.Identifier.Text;
                    container = (@struct.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (@struct.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case RecordDeclarationSyntax record:
                    kind = "record";
                    name = record.Identifier.Text;
                    container = (record.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (record.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case EnumDeclarationSyntax @enum:
                    kind = "enum";
                    name = @enum.Identifier.Text;
                    container = (@enum.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString() ?? (@enum.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case EnumMemberDeclarationSyntax enumMember:
                    kind = "enum member";
                    name = enumMember.Identifier.Text;
                    container = (enumMember.Parent as EnumDeclarationSyntax)?.Identifier.Text;
                    break;
                case ConstructorDeclarationSyntax ctor:
                    kind = "constructor";
                    name = ctor.Identifier.Text;
                    container = (ctor.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
                case FieldDeclarationSyntax field:
                    kind = "field";
                    name = field.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
                    container = (field.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
            }

            if (kind == null || name == null)
            {
                continue;
            }

            var span = node.GetLocation().GetLineSpan();
            items.Add(new OutlineItem(Kind: kind, Name: name, Container: container, StartLine: span.StartLinePosition.Line + 1, EndLine: span.EndLinePosition.Line + 1));
        }

        return items;
    }

    [McpServerTool(Name = "ListAll")]
    [Produces(DataTag.Report)]
    [Description("Lists every namespace/class/interface/struct/record/enum/enum member/constructor/field/method/property declared anywhere in the loaded solution, one row per symbol with its file, kind, name, container, and line range — the solution-wide equivalent of GetFileOutline. Call this FIRST when you don't already know the exact name of the type/method/field you need — it is cheaper and more reliable than guessing plausible-sounding names and searching for each one individually with SearchSolutionText. kind filters to one symbol kind (default: all — every kind). Optional projectName restricts to one project. Can return a lot of rows on a large solution; narrow with kind and/or projectName first.")]
    public async Task<ToolResult<object>> ListAll(
        [Description(ToolParams.ListAllKindValues)][ExternalInputRequired(DataTag.SymbolKind, required: false)] ListAllKind kind = ListAllKind.all,
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var projects = solution.Projects.AsEnumerable();
            if (!string.IsNullOrEmpty(projectName))
            {
                projects = projects.Where(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            }

            var solutionRoot = _workspaceManager.GetSolutionRoot();
            var kindFilter = kind switch
            {
                ListAllKind.all => null,
                ListAllKind.enumMember => "enum member",
                _ => kind.ToString()
            };
            var entries = new List<SolutionSymbolEntry>();
            foreach (var project in projects)
            {
                foreach (var document in project.Documents)
                {
                    if (string.IsNullOrEmpty(document.FilePath))
                    {
                        continue;
                    }

                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null)
                    {
                        continue;
                    }

                    var filePath = new FilePath(document.FilePath, solutionRoot);
                    foreach (var item in ExtractOutlineItems(root))
                    {
                        if (kindFilter != null && item.Kind != kindFilter)
                        {
                            continue;
                        }

                        entries.Add(new SolutionSymbolEntry(filePath, item.Kind, item.Name, item.Container, item.StartLine, item.EndLine));
                    }
                }
            }

            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                entries,
                solutionRoot,
                typeof(SolutionSymbolEntry).Name,
                ResultWrapperType.SolutionSymbolEntryList,
                totalRecords: entries.Count,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ListAll failed (kind={Kind}, projectName={ProjectName})", kind, projectName);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ListAll")
            };
        }
    }

    /// <summary>Regex metacharacters that suggest the caller meant to pass isRegex=true.</summary>
    private static readonly Regex LikelyRegexPattern = new(@"[\^\$\.\*\+\?\(\)\[\]\{\}\|\\]", RegexOptions.Compiled);
    [McpServerTool(Name = "SearchSolutionText")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches all source files in the loaded solution for a text pattern or regex. Only searches documents that are part of a loaded project's source code (e.g. .cs files). Use ListSolutionItems(kind: solutionItems) to see files attached via the .sln's Solution Folders and other non-project files, use ProjectDoc to read plan/handoff/documentation files directly, and use GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file. Returns file path, 1-based line and column, a preview, and enclosingMember (the name of the method/property/constructor/field/etc. containing the match, or null if the match isn't inside any member) per match. fileGlob restricts to matching file paths. maxResults caps total matches (default 200).")]
    public async Task<ToolResult<object>> SearchSolutionText([ToolOption(ToolOptionTag.Pattern, required: true)] string pattern, [ToolOption(ToolOptionTag.SearchMode)] TextSearchMode searchMode = TextSearchMode.literal, [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null, [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var results = new List<TextSearchMatch>();
            var warnings = new List<string>();
            Regex? regex = null;
            TextSearchMode actualSearchMode = searchMode;

            if (searchMode == TextSearchMode.regex)
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeout: TimeSpan.FromSeconds(5));
            }
            else if (searchMode == TextSearchMode.literal && LikelyRegexPattern.IsMatch(pattern))
            {
                warnings.Add($"Pattern '{pattern}' contains regex metacharacters ({LikelyRegexPattern}) but searchMode is literal - searched for the literal substring as requested. Pass searchMode: regex if you meant to search as a regex.");
            }

            var options1 = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount };

            await Parallel.ForEachAsync(solution.Projects, options1, async (project, ct1) =>
            {
                var options2 = new ParallelOptions { CancellationToken = ct1, MaxDegreeOfParallelism = Environment.ProcessorCount };

                await Parallel.ForEachAsync(project.Documents, options2, async (document, ct2) =>
                {
                    if (results.Count >= maxResults)
                    {
                        return;
                    }

                    var docPath = new FilePath(document.FilePath ?? "", _workspaceManager.GetSolutionRoot());
                    if (!string.IsNullOrEmpty(fileGlob) && !GlobMatchesFileName(docPath, fileGlob))
                    {
                        return;
                    }

                    var text = await document.GetTextAsync(ct2);
                    var sourceText = text.ToString();
                    var lines = sourceText.Split('\n');
                    var root = await document.GetSyntaxRootAsync(ct2);
                    for (int i = 0; i < lines.Length && results.Count < maxResults; i++)
                    {
                        var line = lines[i];
                        int col = -1;
                        if (actualSearchMode == TextSearchMode.regex && regex != null)
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

                            string? enclosingMember = null;
                            if (root != null && i < text.Lines.Count)
                            {
                                var lineStart = text.Lines[i].Start;
                                var lineLength = text.Lines[i].End - lineStart;
                                var position = lineStart + Math.Clamp(col, 0, Math.Max(0, lineLength));
                                enclosingMember = GetEnclosingMemberName(root, position);
                            }

                            results.Add(new TextSearchMatch(docPath.Absolute, i + 1, col + 1, preview, enclosingMember));
                        }
                    }
                });
            });

            if (results.Count == 0)
            {
                // warnings.Add("No matches. SearchSolutionText only searches documents that are part of a loaded project's compilation (e.g. .cs files) — it does not see files attached via the .sln's Solution Folders, docs/ files, or other non-project files. Use ListSolutionItems(kind: solutionItems) to list files attached via Solution Folders, or ProjectDoc to read plan/handoff/documentation files directly.");

                if (actualSearchMode == TextSearchMode.literal)
                {
                    warnings.Add($"No matches were found for the literal substring '{pattern}'. Try adjusting the search pattern or using the regex search mode. Use ProjectDoc to read plan/handoff/documentation files directly or use GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file.");
                }
                else if (actualSearchMode == TextSearchMode.regex)
                {
                    warnings.Add($"No matches were found for the regex pattern '{pattern}'. Try adjusting the search pattern or using the literal search mode. Use ProjectDoc to read plan/handoff/documentation files directly or use GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file.");
                }
            }
            else if (results.Count >= maxResults)
            {
                warnings.Add($"{results.Count} matches found — returning first ({maxResults}) matches — Narrow fileGlob/pattern or increase maxResults to see more.");
            }

            string? warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;
            var searchResult = await ToolResult<object>.ForPossiblyLargeDataAsync(
                results,
                _workspaceManager.GetSolutionRoot(),
                typeof(TextSearchMatch).Name,
                ResultWrapperType.TextSearchMatchList,
                totalRecords: results.Count,
                workspaceVersion: _workspaceManager.WorkspaceVersion,
                cancellationToken: cancellationToken);
            return searchResult with { Warning = warning };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchSolutionText failed for '{Pattern}'", pattern);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SearchSolutionText")
            };
        }
    }

    /// <summary>
    /// Walks up from <paramref name = "position"/> to the nearest named member declaration
    /// (method, property, constructor, field/event, indexer, or operator) and returns its name.
    /// Returns null if the position isn't inside any member — e.g. a using directive, a
    /// namespace-level comment, or a type declaration's own header.
    /// </summary>
    private static string? GetEnclosingMemberName(SyntaxNode root, int position)
    {
        if (position < root.FullSpan.Start || position > root.FullSpan.End)
        {
            return null;
        }

        var token = root.FindToken(position);
        foreach (var node in token.Parent?.AncestorsAndSelf() ?? [])
        {
            switch (node)
            {
                case MethodDeclarationSyntax method:
                    return method.Identifier.Text;
                case ConstructorDeclarationSyntax ctor:
                    return ctor.Identifier.Text;
                case PropertyDeclarationSyntax prop:
                    return prop.Identifier.Text;
                case IndexerDeclarationSyntax:
                    return "this[]";
                case OperatorDeclarationSyntax op:
                    return $"operator {op.OperatorToken.Text}";
                case EventDeclarationSyntax evt:
                    return evt.Identifier.Text;
                case FieldDeclarationSyntax field:
                    return string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text));
                case EventFieldDeclarationSyntax eventField:
                    return string.Join(", ", eventField.Declaration.Variables.Select(v => v.Identifier.Text));
                case BaseTypeDeclarationSyntax:
                    // Reached a type declaration without finding a member first — e.g. the match
                    // was on the class header itself, not inside any member body.
                    return null;
            }
        }

        return null;
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
        var candidate = normalizedGlob.Contains('/') ? filePath.Relative.Replace('\\', '/') : Path.GetFileName(filePath.Absolute);
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
    [Description("Returns a filtered slice of an operation result blob by changeId. filter accepts prefix synonyms: fail/err → failures, warn/skip → skipped, ok/pass/info/success → succeeded, roll/revert/undo → rolledback, manual/manual_review/needs_manual_review → NeedsManualReview (bridge compiler-error skips), file:<path> to filter by path, or omit for all items. Unrecognised prefixes return an error. offset skips that many filtered items before taking maxItems; pass NextOffset from the previous response to page through the rest. TotalItems reflects the filtered count; HasMorePages is true when more items remain past this page.")]
    public async Task<ToolResult<object>> GetOperationDetail([Consumes(DataTag.ChangeId, required: true)] string changeId, [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null, [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50, [ToolOptionAttribute(ToolOptionTag.Offset)] int offset = 0, // RequestContext<CallToolRequestParams> requestParams = null,
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

            var json = await File.ReadAllTextAsync(blobPath, cancellationToken);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var allItems = doc.GetProperty("items").EnumerateArray().Select(e => JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!).ToList();
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
                            Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown filter \"{filter}\". Accepted prefixes: fail/err → failures, warn/skip → skipped, ok/pass/info/success → succeeded, roll/revert/undo → rolledback. Use file:<path> to filter by path, or omit for all items.")
                        };
                    }

                    filtered = allItems.Where(r => r.Outcome == outcome.Value);
                }
            }

            var filteredList = filtered.ToList();
            var safeOffset = Math.Max(0, offset);
            var slice = filteredList.Skip(safeOffset).Take(maxItems).ToList();
            var nextOffset = safeOffset + slice.Count;
            var hasMore = nextOffset < filteredList.Count;
            return new ToolResult<object>()
            {
                Success = true,
                HasMorePages = hasMore,
                Data = new OperationDetailResult
                {
                    ChangeId = changeId,
                    BlobName = Path.GetFileName(blobPath),
                    TotalItems = filteredList.Count,
                    ReturnedItems = slice.Count,
                    Offset = safeOffset,
                    NextOffset = hasMore ? nextOffset : null,
                    Filter = filter,
                    Items = slice,
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOperationDetail failed for '{ChangeId}'", changeId);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetOperationDetail")
            };
        }
    }

    // Maps a human-readable filter string to an ItemRecordOutcome via prefix matching.
    // Returns null when the prefix is unrecognised so the caller can return a helpful error.
    private static ItemRecordOutcome? ResolveOutcomeFilter(string filter)
    {
        string f = filter.ToLowerInvariant();
        if (f.StartsWith("fail") || f.StartsWith("err"))
            return ItemRecordOutcome.Failed;
        if (f.StartsWith("skip") || f.StartsWith("warn"))
            return ItemRecordOutcome.Skipped;
        if (f.StartsWith("ok") || f.StartsWith("pass") || f.StartsWith("info") || f.StartsWith("success") || f.StartsWith("succeed"))
            return ItemRecordOutcome.Succeeded;
        if (f.StartsWith("roll") || f.StartsWith("revert") || f.StartsWith("undo"))
            return ItemRecordOutcome.RolledBack;
        if (f.StartsWith("manual") || f.StartsWith("needs_manual"))
            return ItemRecordOutcome.NeedsManualReview;
        return null;
    }

    private static string GetMethodOrCtorName(BaseMethodDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        _ => ""
    };
    private static string BuildSignature(BaseMethodDeclarationSyntax method)
    {
        var modifiers = method.Modifiers.ToString();
        var name = GetMethodOrCtorName(method);
        var parameters = method.ParameterList.ToString();
        if (method is MethodDeclarationSyntax m)
        {
            var returnType = m.ReturnType.ToString();
            var typeParams = m.TypeParameterList?.ToString() ?? "";
            return string.IsNullOrEmpty(modifiers) ? $"{returnType} {name}{typeParams}{parameters}" : $"{modifiers} {returnType} {name}{typeParams}{parameters}";
        }

        return string.IsNullOrEmpty(modifiers) ? $"{name}{parameters}" : $"{modifiers} {name}{parameters}";
    }

    private static List<MethodAttributeInfo> ExtractAttributes(BaseMethodDeclarationSyntax method) => method.AttributeLists.SelectMany(al => al.Attributes).Select(a => new MethodAttributeInfo { Name = a.Name.ToString(), Arguments = a.ArgumentList?.Arguments.ToString() ?? "", }).ToList();
    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previously applied batch to their pre-apply state using the forensic blob written at apply time. Covers all apply operations: apply_diff, refactoring-tool writes, and batch-first tools.")]
    public async Task<ToolResult<object>> UndoLastApply([Consumes(DataTag.OperationId, required: true)] string changeId, // RequestContext<CallToolRequestParams> requestParams = null,
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
                    Error = new ResultError("NoOperationBlobFound", $"No operation blob found for changeId '{changeId}'. Ensure the apply completed successfully and a solution is loaded.")
                };
            }

            var json = await File.ReadAllTextAsync(blobPath);
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var revertable = doc.GetProperty("items").EnumerateArray().Select(e => JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!).Where(r => r.Outcome == ItemRecordOutcome.Succeeded && r.BeforeSource != null).ToList();
            if (revertable.Count == 0)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError("NoReversibleItems", $"No reversible items in blob for changeId '{changeId}'. Ensure the apply completed successfully and a solution is loaded.")
                };
            }

            var failed = new List<string>();
            var revertChanges = new Dictionary<FilePath, string>();
            foreach (var item in revertable)
            {
                // Security: only revert files under the solution root to prevent path traversal.
                if (solutionRoot != null && !item.FilePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add($"{item.FilePath}: outside solution root, skipped");
                    continue;
                }

                revertChanges[item.FilePath] = item.BeforeSource!;
            }

            var reverted = new List<string>();
            if (revertChanges.Count > 0)
            {
                // Route through the shared chokepoint (ApplyProposedChangesAsync) rather than
                // writing directly, so an undo gets the same rollback-on-partial-failure and
                // FileSystemWatcher loop suppression as a forward apply — a revert that bypassed
                // this previously looked like an external edit to the watcher.
                var revertResult = await _workspaceManager.ApplyProposedChangesAsync(
                    revertChanges, rollbackOnPartialFailure: true, cancellationToken: cancellationToken);
                reverted.AddRange(revertResult.SucceededFiles);
                foreach (var (path, error) in revertResult.FailedFiles)
                {
                    failed.Add($"{path}: {error}");
                }
            }

            var failedPart = failed.Count > 0 ? $" Failures: {string.Join("; ", failed)}" : "";
            return new ToolResult<object>()
            {
                Success = true,
                Data = $"Reverted {reverted.Count} files. Files: {string.Join(", ", reverted)}{failedPart}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UndoLastApply failed for '{ChangeId}'", changeId);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "UndoLastApply")
            };
        }
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
        _ = cancellationToken;
        // Use CurrentSolution (sync, no throw) rather than GetCurrentSolutionAsync
        // to distinguish "no solution loaded" from "workspace error"
        Solution? currentSolution;
        try
        {
            currentSolution = _workspaceManager.CurrentSolution;
        }
        catch (Exception ex)
        {
            // Workspace itself threw — genuinely non-operational
            return Task.FromResult(new WorkspaceHealthReport(IsOperational: false, HasLoadedSolution: false, LoadedSolutionPath: null, ProjectCount: 0, DocumentCount: 0, LoadErrors: [$"Workspace exception: {ex.Message}"], Summary: $"Workspace is NOT operational: {ex.Message}"));
        }

        var loadErrors = _workspaceManager.GetWorkspaceLoadErrors();
        if (currentSolution == null)
        {
            // No solution is loaded — but the workspace itself is operational. Surface an
            // MSBuild-missing note here (and only here): once a solution has loaded
            // successfully, MSBuildFound is moot and flagging it would just reintroduce the
            // false-negative behavior this tool replaced Diagnose to fix.
            var msbuildNote = _workspaceManager.GetHealthComponents().MsBuildFound ? "" : " No MSBuild installation was detected — LoadSolution may fail; install Visual Studio, Build Tools, or the .NET SDK.";
            return Task.FromResult(new WorkspaceHealthReport(IsOperational: true, HasLoadedSolution: false, LoadedSolutionPath: null, ProjectCount: 0, DocumentCount: 0, LoadErrors: loadErrors, Summary: "Workspace is operational. No solution is currently loaded. " + "Call load_solution to load a .sln or .csproj file." + msbuildNote));
        }

        var projectCount = currentSolution.ProjectIds.Count;
        var documentCount = currentSolution.Projects.SelectMany(p => p.Documents).Count();
        var solutionPath = currentSolution.FilePath ?? _workspaceManager.SolutionPath;
        var status = _workspaceManager.GetWorkspaceStatus();
        return Task.FromResult(new WorkspaceHealthReport(IsOperational: true, HasLoadedSolution: true, LoadedSolutionPath: solutionPath, ProjectCount: projectCount, DocumentCount: documentCount, LoadErrors: loadErrors, Summary: $"Workspace operational. {projectCount} project(s) loaded, " + $"{documentCount} document(s). " + (loadErrors.Count > 0 ? $"{loadErrors.Count} load warning(s) recorded (non-fatal)." : "No load errors.") + (status.RequiresReload ? $" {status.StaleDocumentCount} file(s) changed on disk since the last load — call LoadSolution to refresh." : ""), StaleDocumentCount: status.StaleDocumentCount, RequiresReload: status.RequiresReload, SampleStaleFiles: status.SampleStaleFiles));
    }

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────
    [McpServerTool(Name = "GetWorkspaceHealth")]
    [Produces(DataTag.ResultOnly)]
    [Description("Targeted workspace health check — reads actual workspace/solution state directly rather than environment probes. Returns IsOperational, HasLoadedSolution, LoadedSolutionPath, ProjectCount, DocumentCount, LoadErrors, Summary, StaleDocumentCount, RequiresReload, SampleStaleFiles. IsOperational=true + HasLoadedSolution=false means no solution loaded yet — not an error. RequiresReload=true means files changed on disk since the last LoadSolution call. verify=quickBuild/fullBuild additionally runs a build check and attaches it as BuildVerification.")]
    public async Task<ToolResult<object>> GetWorkspaceHealth(// RequestContext<CallToolRequestParams> requestParams = null,
    BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
    CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("GetWorkspaceHealth called");
        }

        try
        {
            var result = await GetWorkspaceHealthAsync();

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
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetWorkspaceHealth")
            };
        }
    }

    [McpServerTool(Name = "ListProjectFrameworkTargets")]
    [Produces(DataTag.Report)]
    [Description("Returns each project's TargetFramework value. No parameters.")]
    public async Task<ToolResult<object>> ListProjectFrameworkTargets(// RequestContext<CallToolRequestParams> requestParams = null,
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
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetProjectFrameworkSummary")
            };
        }
    }

    // ── get_large_result ────────────────────────────────────────────────────────

    [McpServerTool(Name = "GetLargeResult")]
    [Produces(DataTag.Report)]
    [Description("""
        Pages through a large result written to disk when output result payload exceeded the inline size threshold. Supply either resultId (resolves to .roslynsentinel/largeresults/largeresult_*_{resultId}.json) or filePath (must match the largeresult_*.json pattern). Returns ToolResult<object> with TotalRecords and HasMore.
        """)]
    public async Task<ToolResult<object>> GetLargeResult(
        [Consumes(DataTag.ResultId)] string? resultId = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = _workspaceManager.SetFilePath(filepath);
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        string? resolvedPath = null;

        if (!string.IsNullOrEmpty(resultId) && !string.IsNullOrEmpty(solutionRoot))
        {
            var dir = System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "largeresults");
            if (Directory.Exists(dir))
            {
                resolvedPath = Directory
                    .EnumerateFiles(dir, $"largeresult_*_{resultId}.json")
                    .FirstOrDefault();
            }
        }
        else if (!string.IsNullOrEmpty(filePath))
        {
            // Validate: path must be inside the largeresults directory and match the largeresult_*.json pattern.
            var fileName = System.IO.Path.GetFileName(filePath);
            if (!string.IsNullOrEmpty(solutionRoot))
            {
                var resultsDir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(solutionRoot, ".roslynsentinel", "largeresults"));
                var candidate = System.IO.Path.GetFullPath(filePath);
                if (candidate.StartsWith(resultsDir, StringComparison.OrdinalIgnoreCase)
                    && fileName.StartsWith("scan_", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(candidate))
                {
                    resolvedPath = candidate;
                }
            }
        }

        if (resolvedPath == null)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("Exception",
                                           "Result file not found. Supply a valid resultId or filePath pointing to a scan_*.json file in the scans directory.")
            };
        }

        ResultWrapper all;
        try
        {
            var json = await File.ReadAllTextAsync(resolvedPath, cancellationToken);
            all = JsonSerializer.Deserialize<ResultWrapper>(
                      json,
                      _jsonOptions)
                  ?? new ResultWrapper();

            if (all.Data == null)
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError("Exception", "Result file has no Data payload — it may be corrupt.")
                };
            }

            ToolResult<object> result;

            switch (all.Type)
            {
                case ResultWrapperType.MigrationCandidateFindingList:
                    {
                        var findings = JsonSerializer.Deserialize<List<MigrationCandidateFinding>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            // limit/offset were previously accepted but never applied — the full
                            // on-disk list was returned regardless of the requested page.
                            Data = findings.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }

                case ResultWrapperType.ApiSurfaceEntryList:
                    {
                        var entries = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = entries.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.CodeInventoryReport:
                    {
                        var entries = JsonSerializer.Deserialize<List<ApiSurfaceEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = entries.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.MethodSource:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // GetMethodSource returns inline when the result is small enough not to offload.
                        var methodSource = JsonSerializer.Deserialize<MethodSourceResult>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = methodSource
                        };
                        break;
                    }
                case ResultWrapperType.MigrationScanSummary:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // returned inline when the summary is small enough not to offload.
                        var migrationScanSummary = JsonSerializer.Deserialize<MigrationScanSummary>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = migrationScanSummary
                        };
                        break;
                    }
                case ResultWrapperType.FileSource:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // ReadFile returns inline when the result is small enough not to offload.
                        var fileSource = JsonSerializer.Deserialize<FileSourceResult>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = fileSource
                        };
                        break;
                    }
                case ResultWrapperType.MemberChangedContent:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // returned inline when the changed content is small enough not to offload.
                        var memberChangedContent = JsonSerializer.Deserialize<MemberChangedContentResult>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = memberChangedContent
                        };
                        break;
                    }
                case ResultWrapperType.BreakingChangeList:
                    {
                        var changes = JsonSerializer.Deserialize<List<BreakingChange>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = changes.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.TextSearchMatchList:
                    {
                        var matches = JsonSerializer.Deserialize<List<TextSearchMatch>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = matches.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.ProjectFileList:
                    {
                        var files = JsonSerializer.Deserialize<List<string>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = files.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.ProjectInfoList:
                    {
                        var projects = JsonSerializer.Deserialize<List<ProjectInfoEntry>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = projects.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.SolutionItemFileList:
                    {
                        var solutionItems = JsonSerializer.Deserialize<List<SolutionItemFile>>(all.Data.ToString(), _jsonOptions)
                            ?? [];
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = solutionItems.Skip(offset).Take(limit).ToList()
                        };
                        break;
                    }
                case ResultWrapperType.SolutionItemsAllResult:
                    {
                        // Single object, not a list - limit/offset don't apply, matching the shape
                        // ListSolutionItems(kind: all) returns inline when small enough not to offload.
                        var solutionItemsAll = JsonSerializer.Deserialize<SolutionItemsAllResult>(all.Data.ToString(), _jsonOptions);
                        result = new ToolResult<object>
                        {
                            Success = true,
                            Data = solutionItemsAll
                        };
                        break;
                    }
                default:
                    {
                        return new ToolResult<object>
                        {
                            Success = false,
                            Error = new ResultError("Exception",
                                          "Unknown scan result type.")
                        };
                    }
            }
            ;

            // MethodSource/FileSource/MigrationScanSummary/MemberChangedContent wrap a single object, not a list - the array-shaped
            // TotalRecords/HasMorePages computation below doesn't apply (and AsArray() on a
            // single-object payload's first property, e.g. a string Signature, throws rather than
            // returning null, since it's the wrong node kind rather than a missing one).
            int totalRecords;
            bool hasMorePages;
            if (all.Type is ResultWrapperType.MethodSource or ResultWrapperType.FileSource or ResultWrapperType.MigrationScanSummary or ResultWrapperType.MemberChangedContent or ResultWrapperType.SolutionItemsAllResult)
            {
                totalRecords = 1;
                hasMorePages = false;
            }
            else
            {
                var dataArray = all.Data as JsonArray
                    ?? all.Data?.AsObject().FirstOrDefault().Value?.AsArray()
                    ?? [];
                totalRecords = dataArray.Count;
                hasMorePages = (offset + limit) < totalRecords;
            }

            return new ToolResult<object>
            {
                Success = true,
                // Unwrap: `result` is itself a ToolResult<object> built above per ResultWrapperType.
                // Returning it as-is here double-wraps the payload (Data.Data instead of Data),
                // which doesn't match every other tool's flat ToolResult<object> shape.
                Data = result.Data,
                TotalRecords = totalRecords,
                HasMorePages = hasMorePages,
            };
        }
        catch (Exception ex)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("Exception",
                              "Failed to read scan file.", ex.Message)
            };
        }
    }
}

/// <summary>Return payload for <c>GetMethodSource</c>.</summary>
public record MethodSourceResult
{
    /// <summary>Scope/truncation metadata for the containing file. See <see cref="ReadEnvelope"/>.</summary>
    public ReadEnvelope Envelope { get; init; } = null!;
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