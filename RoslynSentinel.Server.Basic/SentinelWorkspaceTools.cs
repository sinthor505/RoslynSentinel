using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;
/// <summary>Structural outline entry returned by get_file_outline.</summary>
public record OutlineItem(string Kind, string Name, string? Container, int StartLine, int EndLine);
/// <summary>Single text-search hit returned by search_solution_text.</summary>
public record TextSearchMatch(FilePath filePath, int Line, int Column, string Preview, string? EnclosingMember = null);
/// <summary>
/// A file attached to the solution via a .sln Solution Folder (ProjectSection(SolutionItems)),
/// returned by ListSolutionItems(kind: solutionItems). SolutionFolder is the enclosing folder's
/// display name (e.g. "Solution Items").
/// </summary>
public record SolutionItemFile(FilePath FilePath, string SolutionFolder);
[McpServerToolType]
public class SentinelWorkspaceTools
{
    // Added by AddConstructorParameter
    private readonly BuildEngine _buildEngine;
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
    public SentinelWorkspaceTools(PersistentWorkspaceManager workspaceManager, ValidationEngine validationEngine, DiffEngine diffEngine, DiagnosticEngine diagnosticEngine, SolutionManagementEngine solutionManagementEngine, StructuralRefinementEngine structuralRefinementEngine, DependencyEngine dependencyEngine, ProjectConsistencyEngine projectConsistencyEngine, SentinelConfiguration config, ILogger<SentinelWorkspaceTools> logger, BuildEngine buildEngine)
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
    }

    [McpServerTool(Name = "Features")]
    [Produces(DataTag.Report)]
    [Description("Queries or updates feature flags. list → all; get → by names; update → batch-update via enabled as [{Key: featureName, Value: bool}] pairs.")]
    public object Features(FeaturesAction action, List<string>? names = null, List<KeyValuePair<string, bool>>? enabled = null)
    {
        try
        {
            return action switch
            {
                FeaturesAction.list => (object)_config.GetFeatureStatuses(),
                FeaturesAction.get => _config.GetFeatureStatuses(names),
                FeaturesAction.update => (object)UpdateFeaturesInternal(enabled ?? []),
                _ => new
                {
                    Success = false,
                    Error = $"Unknown action '{action}'. Valid values: list, get, update."}
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Features ({Action}) failed", action);
            return new
            {
                Success = false,
                Error = $"Features failed unexpectedly ({ex.GetType().Name}): {ex.Message}"};
        }
    }

    private string UpdateFeaturesInternal(List<KeyValuePair<string, bool>> updates, // RequestContext<CallToolRequestParams> requestParams = null,
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
    public async Task<ToolResult<object>> ListSolutionItems([ExternalInputRequired(DataTag.Scope)] SolutionItemsKind kind, [Consumes(DataTag.ProjectName)] string? projectName = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            if (kind == SolutionItemsKind.projects)
            {
                var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = solution.Projects.Select(p => (object)new { p.Name, p.FilePath }).ToList()
                };
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
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = items,
                    TotalRecords = items.Count
                };
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
                    var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
                    var files = project.Documents.Select(d => d.FilePath ?? d.Name).Where(p => !p.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) && !p.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)).ToList<object>();
                    return new ToolResult<object>()
                    {
                        Success = true,
                        Data = files
                    };
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
                    Data = $"Solution loaded: {solutionPath}{BuildPostLoadHint(solutionRoot)}"};
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
    private static readonly (string Dir, string DocType)[] ProjectDocSubdirs = [("plans", "plan"), ("handoffs", "handoff"), ("completed", "completed_work"), ("documentation", "documentation"), ];
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
        foreach (var(dir, docType)in ProjectDocSubdirs)
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
        return _workspaceManager.GetExternalDrift();
    }

    [McpServerTool(Name = "ClearExternalDrift")]
    [Produces(DataTag.ResultOnly)]
    [Description("Clears the external-drift list after the AI has read the latest disk changes. No parameters.")]
    public string ClearExternalDrift(// RequestContext<CallToolRequestParams> requestParams = null,
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

    [McpServerTool(Name = "ApplyDiff")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set. changesetFormat=files → changes dict filePath→newContent (filepath not used). changesetFormat=diff → filepath and unifiedDiff are BOTH REQUIRED (filepath names the single file the diff applies to; omitting it is a common mistake and fails immediately). For changesetFormat=diff, hunk line numbers are treated as a starting guess: if a hunk's declared position doesn't match, this searches nearby lines and re-anchors automatically, so modest line-number drift from an earlier edit to the same file is tolerated. Returns ApplyChangesResult with UndoChangeId on successful apply. The full pre-edit file content is NOT included by default (it's already captured for undo via UndoLastApply/GetOperationDetail) — pass returnDiff=true to get a unified-diff-style preview of what changed instead.")]
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
                    var result = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff pre-apply validate failed: {result.ValidationResult.Diagnostics.ToJson()}")
                        };
                    await WriteBlobForApplyAsync("apply_diff", result);
                    // PreImages (full pre-edit file content) is dropped from the default response -
                    // it's already captured in the undo blob written above (GetOperationDetail/
                    // UndoLastApply can retrieve it) and was the single largest contributor to
                    // ApplyDiff responses exceeding the calling harness's token limit on large files.
                    var strippedResult = result with { PreImages = null };
                    object responseData = returnDiff
                        ? new { result = strippedResult, diff = SentinelRefactoringTools.BuildDiffFromPreImages(changes, result.PreImages) }
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
                        var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
                                Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff diff validate failed: {result.ValidationResult.Diagnostics.ToJson()}")
                            };
                        await WriteBlobForApplyAsync("apply_diff", result);
                        var strippedDiffResult = result with { PreImages = null };
                        object diffResponseData = returnDiff
                            ? new { result = strippedDiffResult, diff = SentinelRefactoringTools.BuildDiffFromPreImages(diffChanges, result.PreImages) }
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
                Data = await _workspaceManager.RetryFailedChangesAsync(specificFiles, retryCount)
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
    private async Task WriteBlobForApplyAsync(string toolName, PersistentWorkspaceManager.ApplyChangesResult result, string? blobChangeId = null, // RequestContext<CallToolRequestParams> requestParams = null,
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
        var blobName = await OperationBlobWriter.WriteAsync(toolName, changeId, items, _workspaceManager.GetSolutionRoot());
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
                Data = new PersistentWorkspaceManager.AppliedChangeSummary(changeId, [filePath], $"Deleted unused symbol in {Path.GetFileName(filePath)}.", false)
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
            var result = await _solutionManagementEngine.CreateProjectAsync(projectName, projectType);
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
            var result = await _solutionManagementEngine.SplitProjectByFolderAsync(sourceProjectName, folderName, targetProjectName);
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
            var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            const int thresholdBytes = 8 * 1024;
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (methodBytes > thresholdBytes && !string.IsNullOrEmpty(solutionRoot))
            {
                var fullResult = new MethodSourceResult { Signature = signature, Source = methodSource, Attributes = attributes };
                var stored = await ScanResultHelper.StoreScanResultAsync(fullResult, solutionRoot, ScanWrapperType.MethodSource);
                return new ToolResult<object>
                {
                    Success = true,
                    LargeResult = new LargeResultInfo(resultType: "MethodSource", writtenToFile: stored.offloaded, filePath: stored.filePath, scanId: stored.scanId!, sizeBytes: methodBytes, totalRecords: 1, message: $"Result is {methodBytes} bytes (threshold: {thresholdBytes}). " + $"Use get_scan_result(scanId: \"{stored.scanId}\") to page through results."),
                    Data = new
                    {
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
    [Description("Returns the raw text of a file in the loaded solution, verbatim (no reformatting). Pass startLine/endLine (1-based, inclusive) to read a slice instead of the whole file — useful once GetFileOutline or a search result gives you a line range. Whole-file reads past the size threshold are written to .roslynsentinel/scans and returned as a scanId (see GetMethodSource) instead of inline text.")]
    public async Task<ToolResult<object>> ReadFile([Consumes(DataTag.SourceFilepath, required: true)] string filepath, [Description("1-based, inclusive. Omit to start from the first line.")] int? startLine = null, [Description("1-based, inclusive. Omit to read through the last line.")] int? endLine = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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
            const int thresholdBytes = 8 * 1024;
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (textBytes > thresholdBytes && !string.IsNullOrEmpty(solutionRoot))
            {
                var fullResult = new FileSourceResult { FilePath = (string)filePath, StartLine = 1, EndLine = totalLines, TotalLines = totalLines, Source = fullText };
                var stored = await ScanResultHelper.StoreScanResultAsync(fullResult, solutionRoot, ScanWrapperType.FileSource);
                return new ToolResult<object>
                {
                    Success = true,
                    LargeResult = new LargeResultInfo(resultType: "FileSource", writtenToFile: stored.offloaded, filePath: stored.filePath, scanId: stored.scanId!, sizeBytes: textBytes, totalRecords: 1, message: $"Result is {textBytes} bytes (threshold: {thresholdBytes}). " + $"Use get_scan_result(scanId: \"{stored.scanId}\") to page through results, or retry ReadFile with startLine/endLine for just the slice you need."),
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
            var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
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

            return new ToolResult<object>()
            {
                Success = true,
                Data = items,
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

    /// <summary>Regex metacharacters that suggest the caller meant to pass isRegex=true.</summary>
    private static readonly Regex LikelyRegexPattern = new(@"[\^\$\.\*\+\?\(\)\[\]\{\}\|\\]", RegexOptions.Compiled);
    [McpServerTool(Name = "SearchSolutionText")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches all source files in the loaded solution for a text pattern or regex. Only searches documents that are part of a loaded project's compilation (e.g. .cs files) — files attached via the .sln's Solution Folders and other non-project files are never included, no matter the pattern; use ListSolutionItems(kind: solutionItems) to see those, and ProjectDoc to read plan/handoff/documentation files directly. Returns file path, 1-based line and column, a preview, and enclosingMember (the name of the method/property/constructor/field/etc. containing the match, or null if the match isn't inside any member) per match. isRegex=true treats pattern as a regular expression (default false, literal substring match); if pattern contains regex metacharacters (e.g. ^ $ . * + ? ( ) [ ] { } | \\) but isRegex is false, the result includes a Warning suggesting isRegex=true. fileGlob restricts to matching file paths. maxResults caps total matches (default 200).")]
    public async Task<ToolResult<object>> SearchSolutionText([ToolOption(ToolOptionTag.Pattern, required: true)] string pattern, [ToolOption(ToolOptionTag.IsRegex)] bool isRegex = false, [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null, [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync(cancellationToken);
            var results = new List<TextSearchMatch>();
            Regex? regex = null;
            if (isRegex)
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, matchTimeout: TimeSpan.FromSeconds(5));
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

                    var text = await document.GetTextAsync(cancellationToken);
                    var sourceText = text.ToString();
                    var lines = sourceText.Split('\n');
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
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
            return new ToolResult<object>()
            {
                Success = true,
                Data = results,
                Warning = warning,
                WorkspaceVersion = _workspaceManager.WorkspaceVersion
            };
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
    [Description("Returns a filtered slice of an operation result blob by changeId. filter accepts prefix synonyms: fail/err → failures, warn/skip → skipped, ok/pass/info/success → succeeded, roll/revert/undo → rolledback, manual/manual_review/needs_manual_review → NeedsManualReview (bridge compiler-error skips), file:<path> to filter by path, or omit for all items. Unrecognised prefixes return an error. maxItems caps the returned slice. TotalItems reflects the filtered count; HasMorePages is true when more items remain.")]
    public async Task<ToolResult<object>> GetOperationDetail([Consumes(DataTag.ChangeId, required: true)] string changeId, [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null, [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50, // RequestContext<CallToolRequestParams> requestParams = null,
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

            var reverted = new List<string>();
            var failed = new List<string>();
            foreach (var item in revertable)
            {
                // Security: only revert files under the solution root to prevent path traversal.
                if (solutionRoot != null && !item.FilePath.StartsWith(solutionRoot, StringComparison.OrdinalIgnoreCase))
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
            return new ToolResult<object>()
            {
                Success = true,
                Data = $"Reverted {reverted.Count} files. Files: {string.Join(", ", reverted)}{failedPart}"};
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

    // ── Phase 3 — Circuit breaker tools ────────────────────────────────────
    [McpServerTool(Name = "ResetBreaker")]
    [Produces(DataTag.ResultOnly)]
    [Description("Resets the circuit breaker and all failure counters, re-enabling mutating tools. Only call after investigating and addressing the root cause of the failures that tripped the breaker.")]
    public ToolResult<object> ResetBreaker(// RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        _workspaceManager.ResetBreaker();
        return new ToolResult<object>()
        {
            Success = true,
            Data = "Circuit breaker reset. Failure counters cleared. Mutating tools re-enabled."
        };
    }

    [McpServerTool(Name = "GetBreakerStatus")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns the current circuit breaker state: severity (ok/caution/halt), trip-condition counters, and thresholds. Use to assess failure health before running large batch operations.")]
    public ToolResult<object> GetBreakerStatus(// RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        return new ToolResult<object>()
        {
            Success = true,
            Data = _workspaceManager.GetBreakerStatus()
        };
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