using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Server.Basic;

// SolutionItemFile, ProjectInfoEntry, ProjectFilesAndDependencies, SolutionItemsAllResult remain
// declared in SentinelWorkspaceTools.cs (their original declaration site) to avoid a CS0101
// collision - SolutionItemsAllResult in particular already exists in
// RoslynSentinel.Common/LargeResultHelper.cs, confirming it must not be re-declared here.
public record SolutionFileInfo(string Path, string Format);

/// <summary>
/// Plain DI-constructed implementation backing WorkspaceProjectManagementTools. Method bodies are
/// moved verbatim from SentinelWorkspaceTools (Decision 7 step 2) - not yet MCP-agnostic in return
/// type, per Decision 1-Amendment's explicit scope (mechanical shim only for this pass).
/// </summary>
public class WorkspaceProjectManagementImpl
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SolutionManagementEngine _solutionManagementEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly ILogger _logger;

    private const int ListWorkspaceSolutionsMaxFilesWalked = 5000;

    public WorkspaceProjectManagementImpl(IWorkspaceManager workspaceManager, SolutionManagementEngine solutionManagementEngine,
        DependencyEngine dependencyEngine, ProjectConsistencyEngine projectConsistencyEngine,
        StructuralRefinementEngine structuralRefinementEngine, ILogger logger)
    {
        _workspaceManager = workspaceManager;
        _solutionManagementEngine = solutionManagementEngine;
        _dependencyEngine = dependencyEngine;
        _projectConsistencyEngine = projectConsistencyEngine;
        _structuralRefinementEngine = structuralRefinementEngine;
        _logger = logger;
    }

    public async Task<ToolResult<object>> ListSolutionItems(ToolCallReason reason, SolutionItemsKind kind,
        string? projectName = null, CancellationToken cancellationToken = default)
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

                var items = _workspaceManager.GetSolutionFolderItems().Select(i => new SolutionItemFile(new FilePathWrapper(Path.GetFullPath(Path.Combine(solutionRoot, i.RelativePath)), solutionRoot), i.SolutionFolder)).ToList();
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
                        .Select(i => new SolutionItemFile(new FilePathWrapper(Path.GetFullPath(Path.Combine(solutionRoot, i.RelativePath)), solutionRoot), i.SolutionFolder))
                        .ToList();
                }

                var sep = Path.DirectorySeparatorChar;
                var projectDetails = new List<ProjectFilesAndDependencies>();
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

    public ToolResult<List<SolutionFileInfo>> ListWorkspaceSolutions(ToolCallReason reason, string workspacePath, CancellationToken cancellationToken = default)
    {
        workspacePath = FilePathWrapper.NormalizeWirePath(workspacePath);
        if (!Directory.Exists(workspacePath))
        {
            return new ToolResult<List<SolutionFileInfo>>
            {
                Success = false,
                Error = new ResultError("InvalidArgument", $"Directory not found: '{workspacePath}'")
            };
        }

        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var pathRoot = Path.GetPathRoot(fullWorkspacePath);
        if (!string.IsNullOrEmpty(pathRoot) && string.Equals(
                fullWorkspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return new ToolResult<List<SolutionFileInfo>>
            {
                Success = false,
                Error = new ResultError("InvalidArgument", $"workspacePath '{workspacePath}' resolves to the drive root '{pathRoot}'. Pass a real project/repo directory instead - scanning an entire drive is not supported.")
            };
        }

        try
        {
            var files = new List<SolutionFileInfo>();
            foreach (var pattern in new[] { "*.sln", "*.slnx" })
            {
                foreach (var path in Directory.EnumerateFiles(fullWorkspacePath, pattern, SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files.Add(new SolutionFileInfo(Path: path, Format: Path.GetExtension(path).TrimStart('.').ToLowerInvariant()));
                    if (files.Count > ListWorkspaceSolutionsMaxFilesWalked)
                    {
                        return new ToolResult<List<SolutionFileInfo>>
                        {
                            Success = false,
                            Error = new ResultError("InvalidArgument", $"workspacePath '{workspacePath}' contains more than {ListWorkspaceSolutionsMaxFilesWalked} matching files - this looks like too broad a root. Pass a narrower project/repo directory instead.")
                        };
                    }
                }
            }

            files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return new ToolResult<List<SolutionFileInfo>>
            {
                Success = true,
                Data = files,
                TotalRecords = files.Count
            };
        }
        catch (OperationCanceledException)
        {
            throw;
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

    private bool IsAlreadyLoadedPath(string solutionPath, out string? currentPath)
    {
        currentPath = _workspaceManager.GetSolutionRoot() != null ? _workspaceManager.SolutionPath : null;
        if (currentPath == null || !Path.IsPathRooted(solutionPath))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(currentPath), Path.GetFullPath(solutionPath), StringComparison.OrdinalIgnoreCase);
    }


    // Added by InsertMemberBefore (expected - used for diagnostics)
    private static readonly (string Dir, string DocType)[] ProjectDocSubdirs = [("plans", "plan"), ("handoffs", "handoff"), ("completed", "completed_work"), ("documentation", "documentation"),];


    private string BuildPostLoadHint(string solutionRoot)
    {
        var parts = new List<string>();
        var solutionItems = _workspaceManager.GetSolutionFolderItems();
        if (solutionItems.Count > 0)
        {
            parts.Add($"{solutionItems.Count} file(s) attached via Solution Folders in the .sln (not visible to SearchSolutionText - list them with ListSolutionItems(kind: solutionItems)).");
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
                parts.Add($"docs/{dir}/ has {count} file(s) - read with ProjectDoc(action: read, docType: {docType}, name: \"<filename>\").");
            }
        }

        return parts.Count > 0 ? " " + string.Join(" ", parts) : "";
    }

    public async Task<ToolResult<object>> LoadSolution(ToolCallReason reason, string solutionPath, string? baseRepoDir = null,
        bool forceReload = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var wasAlreadyLoaded = IsAlreadyLoadedPath(solutionPath, out var currentPath);
            if (!forceReload && wasAlreadyLoaded)
            {
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = $"Solution '{currentPath}' is already loaded - no changes made. Pass forceReload:true to discard in-memory state and re-open it from disk."
                };
            }

            await _workspaceManager.LoadSolutionAsync(solutionPath, baseRepoDir, cancellationToken: cancellationToken);
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (solutionRoot != null)
            {
                var isReload = forceReload && wasAlreadyLoaded;
                var verb = isReload ? "reloaded" : "loaded";
                var reloadNote = isReload
                    ? " In-memory analysis state was discarded and rebuilt from what's on disk now - any edits made by tools since the last load are reflected; anything else is unaffected."
                    : "";
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = $"Solution {verb}: {solutionPath}.{reloadNote}{BuildPostLoadHint(solutionRoot)}"
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

    public async Task<ToolResult<object>> CreateProject(ToolCallReason reason, string projectName, string projectType = "console",
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

    public async Task<ToolResult<object>> SplitProjectByFolder(ToolCallReason reason, string sourceProjectName, string folderName,
        string targetProjectName, CancellationToken cancellationToken = default)
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

    public async Task<ToolResult<object>> ListProjectFrameworkTargets(ToolCallReason reason, CancellationToken cancellationToken = default)
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

    public async Task<ToolResult<object>> SafeDeleteUnusedSymbol(ToolCallReason reason, FilePathWrapper filepath,
        string projectName = "", string docCommentId = "", string? symbolName = null,
        string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null,
        int line = 0, int column = 0, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        async Task<ToolResult<object>> ApplyAndRespondAsync(DocumentEditResult result)
        {
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"SafeDeleteUnusedSymbol: no change produced for '{filePathResolved}' ({result.Outcome}). {result.Message}")
                };
            }

            var changes = new Dictionary<FilePathWrapper, string>
            {
                [filePathResolved] = result.UpdatedText
            };
            var apply = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount: 3, validateChanges: true, cancellationToken: cancellationToken);
            if (!apply.Success)
            {
                var reason = apply.ValidationResult is not null ? $"introduces new compiler errors - change not applied. Fix diagnostics and retry: {apply.ValidationResult.Diagnostics.ToJson()}" : $"failed to write to disk: {apply.Summary}";
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"SafeDeleteUnusedSymbol {reason}")
                };
            }

            var changeId = Guid.NewGuid().ToString("n")[..8];
            await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "safe_delete_unused_symbol", apply, changeId, cancellationToken);
            return new ToolResult<object>()
            {
                Success = true,
                Data = new AppliedChangeSummary(changeId, [filePathResolved], $"Deleted unused symbol in {Path.GetFileName(filePathResolved)}.", false)
            };
        }

        try
        {
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

                var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePathResolved, resolution.Symbol!, cancellationToken);
                return await ApplyAndRespondAsync(result);
            }

            if (!string.IsNullOrEmpty(symbolName))
            {
                var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                return await ApplyAndRespondAsync(result);
            }

            if (line > 0 && column > 0)
            {
                var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePathResolved, line, column, cancellationToken);
                return await ApplyAndRespondAsync(result);
            }

            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "SafeDeleteUnusedSymbol requires one of: (projectName, docCommentId) for handle-based resolution, (symbolName, optionally with contextSnippet/lineBefore/lineAfter) for name-based resolution, or (line, column) for legacy line/column-based resolution.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SafeDeleteUnusedSymbol failed for '{FilePathWrapper}' at {Line}:{Column} or handle {ProjectName}/{DocCommentId}", filePathResolved, line, column, projectName, docCommentId);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SafeDeleteUnusedSymbol")
            };
        }
    }
}
