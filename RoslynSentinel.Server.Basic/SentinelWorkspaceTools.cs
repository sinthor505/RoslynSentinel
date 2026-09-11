using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;
/// <summary>Structural outline entry returned by get_file_outline.</summary>
public record OutlineItem(string Kind, string Name, string? Container, int StartLine, int EndLine);
/// <summary>Single solution-wide symbol entry returned by ListAll — an OutlineItem plus the file it was found in.</summary>
public record SolutionSymbolEntry(FilePathWrapper FilePath, string Kind, string Name, string? Container, int StartLine, int EndLine);
/// <summary>Return payload for <c>GetFileOutline</c>.</summary>
public record FileOutlineResult
{
    /// <summary>Scope/truncation metadata for the file. See <see cref="ReadEnvelope"/>.</summary>
    public ReadEnvelope Envelope { get; init; } = null!;
    /// <summary>The parsed structural outline.</summary>
    public List<OutlineItem> Symbols { get; init; } = new();
}
/// <summary>Single text-search hit returned by search_solution_text.</summary>
public record TextSearchMatch(FilePathWrapper filePath, int Line, int Column, string Preview, MatchKind MatchedAs, string? EnclosingMember = null);
/// <summary>
/// Return payload for <c>SearchSolutionText</c>: the literal substring matches (always the
/// complete set) and the regex matches not already present in <see cref="LiteralResults"/> (empty
/// when the pattern has no regex metacharacters, since every regex match is then also a literal
/// match). <see cref="RegexOverlapCount"/> is how many regex matches were suppressed as duplicates
/// of a literal match — lets a caller tell "regex found nothing extra" apart from "regex found
/// nothing at all." <see cref="RegexPatternValid"/> is false when <c>pattern</c> doesn't compile
/// as a regex; <see cref="RegexResults"/> is empty and literal search is unaffected in that case.
/// </summary>
public record TextSearchResult(List<TextSearchMatch> LiteralResults, List<TextSearchMatch> RegexResults, int RegexOverlapCount, bool RegexPatternValid);
/// <summary>
/// A file attached to the solution via a .sln Solution Folder (ProjectSection(SolutionItems)),
/// returned by ListSolutionItems(kind: solutionItems). SolutionFolder is the enclosing folder's
/// display name (e.g. "Solution Items").
/// </summary>
public record SolutionItemFile(FilePathWrapper FilePath, string SolutionFolder);
/// <summary>A project entry returned by ListSolutionItems(kind: projects).</summary>
public record ProjectInfoEntry(string Name, string? FilePath);
/// <summary>One project's aggregated files and dependencies, as returned within ListSolutionItems(kind: all).</summary>
public record ProjectFilesAndDependencies(string ProjectName, List<string> Files, ProjectDependencyReport Dependencies);
/// <summary>Combined payload for ListSolutionItems(kind: all): everything the other kinds return in one call, deduplicated by file where applicable.</summary>
public record SolutionItemsAllResult(List<ProjectInfoEntry> Projects, List<SolutionItemFile> SolutionItems, List<ProjectFilesAndDependencies> ProjectDetails);
[McpServerToolType]
public class SentinelWorkspaceTools
{
    private readonly SymbolNavigationEngine _symbolNavigationEngine;    // Added by AddConstructorParameter
    private readonly BuildEngine _buildEngine;
    private readonly TestRunEngine _testRunEngine;
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
    private readonly WorkspaceReadNavigationTools _readNav;
    private readonly WriteToolAdviceHelper _writeAdvice;
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
            {
                new JsonStringEnumConverter()
            }
    };

    public SentinelWorkspaceTools(IWorkspaceManager workspaceManager, ValidationEngine validationEngine, DiffEngine diffEngine, DiagnosticEngine diagnosticEngine, SolutionManagementEngine solutionManagementEngine, StructuralRefinementEngine structuralRefinementEngine, DependencyEngine dependencyEngine, ProjectConsistencyEngine projectConsistencyEngine, SentinelConfiguration config, ILogger<SentinelWorkspaceTools> logger, BuildEngine buildEngine, SymbolNavigationEngine symbolNavigationEngine, TestRunEngine testRunEngine, WorkspaceReadNavigationTools readNav, WriteToolAdviceHelper writeAdvice)
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
        _testRunEngine = testRunEngine;
        _readNav = readNav;
        _writeAdvice = writeAdvice;
    }
    [McpServerTool(Name = "Features")]
    [Produces(DataTag.Report)]
    [Description("Queries or updates feature flags.")]
    public async Task<ToolResult<object>> Features(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("list: returns all feature flags. get: returns only the flags named in names. update: batch-updates the flags named in enabled.")]
        FeaturesAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for action=get, unused for list/update.
        [Description("Required for action=get — the feature names to look up. Not used for list/update.")]
        List<string>? names = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for action=update, unused for list/get.
        [Description("Required for action=update — [{Key: featureName, Value: bool}] pairs to apply. Not used for list/get.")]
        List<KeyValuePair<string, bool>>? enabled = null,
        [Description("Test-only: waits this many seconds before acting, to exercise MCP task polling/cancellation.")]
        int delaySeconds = 0,
        CancellationToken cancellationToken = default)
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
    [Description("Lists projects, files, dependencies, or solution-folder items in the loaded solution.")]
    public async Task<ToolResult<object>> ListSolutionItems(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("files/dependencies: requires projectName. projects/solutionItems: ignore projectName, list every project or every solution-folder item respectively — solutionItems are files attached via the .sln's Solution Folders (e.g. plan/handoff docs), never part of any project's compiled Documents, so SearchSolutionText and kind=files won't find them; read their content with ProjectDoc. all: ignores projectName and returns everything in one call (every project, every solution-folder item, and every project's files and dependencies) — use this for a complete, guaranteed-non-empty view instead of guessing which project or kind to ask for.")]
        [ExternalInputRequired(DataTag.Scope)] SolutionItemsKind kind,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for kind=files/dependencies, unused for kind=projects/solutionItems/all.
        [Description("Required for kind=files/dependencies. Not used for projects/solutionItems/all.")]
        [Consumes(DataTag.ProjectName)] string? projectName = null, // RequestContext<CallToolRequestParams> requestParams = null,
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

    /// <summary>
    /// Hard cap on how many files ListWorkspaceSolutions will walk before giving up — protects
    /// against a caller passing an overly broad root (see the drive-root guard below) that isn't
    /// caught by that check but still turns out to contain far more than any real workspace would
    /// (e.g. a node_modules-style tree, or a root one level above the intended one).
    /// </summary>
    private const int ListWorkspaceSolutionsMaxFilesWalked = 200_000;
    [McpServerTool(Name = "ListWorkspaceSolutions")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.SolutionList)]
    [Description("Lists all *.sln and *.slnx files under a directory. Returns absolute paths for use with LoadSolution.")]
    public ToolResult<List<SolutionFileInfo>> ListWorkspaceSolutions(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Your workspace root — a real project/repo directory, not a drive root or '/'.")] string workspacePath, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
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
                Error = new ResultError("InvalidArgument", $"workspacePath '{workspacePath}' resolves to the drive root '{pathRoot}'. Pass a real project/repo directory instead — scanning an entire drive is not supported.")
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
                            Error = new ResultError("InvalidArgument", $"workspacePath '{workspacePath}' contains more than {ListWorkspaceSolutionsMaxFilesWalked} matching files — this looks like too broad a root. Pass a narrower project/repo directory instead.")
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

    public sealed record SolutionFileInfo(string Path, string Format);
    // current directory, --base-repo-dir (if set), or the server's install directory.
    [McpServerTool(Name = "LoadSolution")]
    [Produces(DataTag.ResultOnly)]
    [Description("Loads a .NET solution file into memory for persistent analysis. Must be called before any operation that returns ErrorCode=\"SolutionNotLoaded\". Accepts absolute paths. For relative paths, omit baseRepoDir and let the server resolve it against its configured base directory — only pass baseRepoDir if you have independently confirmed that exact directory exists on this host; a fabricated/guessed baseRepoDir is rejected with an error rather than silently ignored. If this exact solution is already loaded, this is a no-op by default (no re-read from disk) — pass forceReload:true to discard in-memory state and re-open it from disk.")]
    public async Task<ToolResult<object>> LoadSolution(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SolutionFilepath, required: true)] string solutionPath, [ToolOption(ToolOptionTag.RepoDirectory)][Description("Optional base directory used to resolve a relative solutionPath (e.g. the repo root). Overrides the server's configured base-repo-dir for this call. Must exist on this host — omit this entirely rather than guessing a value.")] string? baseRepoDir = null, [Description("If the given solutionPath is already loaded, false (default) returns immediately without touching the workspace. true forces a full reload from disk, discarding any in-memory state (equivalent to today's unconditional LoadSolution behavior). Has no effect when a different or no solution is currently loaded — that always loads normally regardless of this flag.")] bool forceReload = false, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Compared regardless of forceReload so the success message below can correctly say
            // "reloaded" vs "loaded" — only the short-circuit-and-skip below is gated on
            // forceReload being false. A relative path or a baseRepoDir override never matches here
            // (IsAlreadyLoadedPath requires solutionPath to be rooted), always falling through to a
            // real load: ResolveSolutionPath's multi-candidate disk search (private, deliberately
            // not duplicated here) is the only reliable way to know what a relative path resolves to.
            var wasAlreadyLoaded = IsAlreadyLoadedPath(solutionPath, out var currentPath);
            if (!forceReload && wasAlreadyLoaded)
            {
                return new ToolResult<object>()
                {
                    Success = true,
                    Data = $"Solution '{currentPath}' is already loaded — no changes made. Pass forceReload:true to discard in-memory state and re-open it from disk."
                };
            }

            await _workspaceManager.LoadSolutionAsync(solutionPath, baseRepoDir, cancellationToken: cancellationToken);
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (solutionRoot != null)
            {
                // "reloaded" only when this was genuinely the same solution as before — forceReload
                // on a first load or a switch to a different solution is just an ordinary load.
                var isReload = forceReload && wasAlreadyLoaded;
                var verb = isReload ? "reloaded" : "loaded";
                var reloadNote = isReload
                    ? " In-memory analysis state was discarded and rebuilt from what's on disk now — any edits made by tools since the last load are reflected; anything else is unaffected."
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

    // True only when solutionPath is rooted AND string-matches (case-insensitively, via FilePathWrapper's
    // canonicalized separators) the currently tracked SolutionPath. A relative path never matches —
    // ResolveSolutionPath's private multi-candidate disk search is the only reliable way to know
    // what a relative path resolves to, and duplicating it here isn't worth it for a fast-path check.
    private bool IsAlreadyLoadedPath(string solutionPath, out string? currentPath)
    {
        currentPath = _workspaceManager.SolutionPath;
        if (currentPath is null || !Path.IsPathRooted(solutionPath))
        {
            return false;
        }

        var solutionRootForCompare = _workspaceManager.GetSolutionRoot();
        var requested = new FilePathWrapper(solutionPath, solutionRootForCompare);
        var current = new FilePathWrapper(currentPath, solutionRootForCompare);
        return string.Equals(requested.Absolute, current.Absolute, StringComparison.OrdinalIgnoreCase);
    }

    // Subdirectories ProjectDoc reads/writes under docs/, paired with the docType value that
    // maps to each — see SentinelDocumentationTools.ProjectDoc.
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

    // ListExternalDiskChanges/AcknowledgeExternalFileChanges moved to SentinelAdminTools.cs,
    // gated behind the "Admin" mode — see docs/current/ideas/external-drift-hard-blocker.md.
    // Not model-visible by default anymore; reconciliation is an out-of-band operator action now.

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

    // ApplyUnifiedDiff moved to SentinelWholeFileWriteTools.cs (gated off the default MCP surface,
    // alongside ApplyDiff) — see docs/current/design_applyunifieddiff_replace_snippet_v1.md.
    // ReplaceSnippet (below, on this default surface) replaces it for small, exact-text edits.

    // Raised from 20 lines / 200 chars (shared across both parameters) after run
    // 20260910-013550-398 livelocked here: the 200-char ceiling was the binding constraint —
    // a 6-line C# insertion at normal indentation exceeds it long before the 20-line bound — and
    // the only escape hatch the error named was gated off, so the model burned its last 24 turns
    // reshaping the same edit. Bounds are per-parameter now rather than a shared char cap: unlike
    // the ApplyDiff family, which anchors a small hunk inside a larger context, ReplaceSnippet
    // replaces the entire matched span, so oldContent legitimately grows with the edit.
    private const int MaxOldContentLines = 60;
    private const int MaxOldContentChars = 2000;
    private const int MaxNewContentLines = 60;
    private const int MaxNewContentChars = 2000;
    [McpServerTool(Name = "ReplaceSnippet")]
    [Produces(DataTag.ChangeId)]
    // Deliberately names no whole-file-write tool. Attribute arguments must be compile-time
    // constants, so this text can't be generated per-session from the tool registry the way the
    // over-cap error can (see WriteToolAdviceHelper) — and a hardcoded name here would be shown to
    // the model on every single call even when that tool is gated off, which is the run-398 failure
    // in its most persistent form. The error path is where the redirect is actually needed.
    [Description("Replaces one exact block of text with another in a single file, for localized edits. For a structural change, prefer the matching Roslyn tool (RenameSymbol, ChangeSignature, ExtractMethodSafe, Member, etc.) instead. For multiple small edits in the same file, call this once per edit. By default this also delta-compiles the edited project(s) plus every project that transitively references them BEFORE writing, and REJECTS the change if it introduces any new compiler error.")]
    public async Task<ToolResult<object>> ReplaceSnippet(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: writes the change. validate: checks it would apply cleanly without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [ToolOption(ToolOptionTag.OldContent, required: true)][Description(ToolParams.OldContent)] string oldContent,
        [ToolOption(ToolOptionTag.NewContent, required: true)][Description(ToolParams.NewContent)] string newContent,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);
            if (!filePathResolved.Validated)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'filepath' is required (it names the single file oldContent/newContent applies to).")
                };
            }

            if (string.IsNullOrEmpty(oldContent))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'oldContent' is required.")
                };
            }

            if (newContent == null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ReplaceSnippet: 'newContent' is required (pass an empty string for a pure deletion).")
                };
            }

            // Each bound is reported separately, with the actual value against the limit: run
            // 20260910-013550-398 shows the model repeatedly guessing wrong about which of the four
            // it had hit, because the old message named them all at once.
            var exceeded = new List<string>();
            var oldContentLineCount = oldContent.Split('\n').Length;
            var newContentLineCount = newContent.Split('\n').Length;
            if (oldContentLineCount > MaxOldContentLines)
            {
                exceeded.Add($"oldContent is {oldContentLineCount} lines (limit {MaxOldContentLines})");
            }
            if (oldContent.Length > MaxOldContentChars)
            {
                exceeded.Add($"oldContent is {oldContent.Length} chars (limit {MaxOldContentChars})");
            }
            if (newContentLineCount > MaxNewContentLines)
            {
                exceeded.Add($"newContent is {newContentLineCount} lines (limit {MaxNewContentLines})");
            }
            if (newContent.Length > MaxNewContentChars)
            {
                exceeded.Add($"newContent is {newContent.Length} chars (limit {MaxNewContentChars})");
            }

            if (exceeded.Count > 0)
            {
                // Escape-hatch advice comes from WriteToolAdviceHelper, never a hardcoded tool name:
                // the old text here named WriteFile unconditionally, and in run 398 WriteFile was
                // gated off, so the one instruction the model was given was unfollowable.
                var advice = _writeAdvice.AdviseForOversizedEdit("ReplaceSnippet");
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument,
                        $"ReplaceSnippet: {string.Join("; ", exceeded)}. " + advice.Sentence)
                };
            }

            if (action == ProposedChangeAction.apply || action == ProposedChangeAction.validate)
            {
                try
                {
                    var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
                    var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePath == filePathResolved.Absolute);
                    if (document == null)
                    {
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.InvalidArgument, "File not found.")
                        };
                    }

                    var oldText = await document.GetTextAsync();
                    // FindExactSnippetPosition (not FindSnippetPosition) is required here: it never
                    // uses ContextHelper's whitespace-collapsing fallback, so match.Length is always
                    // the real removable span. Using oldContent.Length instead of match.Length used
                    // to corrupt adjacent lines when a fallback match fired (see
                    // project_replacesnippet_silent_splice_corruption_adjacent_lines memory).
                    var match = ContextHelper.FindExactSnippetPosition(oldText, oldContent, lineBefore, lineAfter);
                    var newFileContent = oldText.ToString().Remove(match.Start, match.Length).Insert(match.Start, newContent);
                    var targetPath = document.FilePath ?? filePathResolved;
                    var snippetChanges = new Dictionary<FilePathWrapper, string>
                    {
                        [targetPath] = newFileContent
                    };

                    if (action == ProposedChangeAction.validate)
                    {
                        var validationResult = await _validationEngine.ValidateChangesAsync(snippetChanges);
                        return validationResult.Success ? new ToolResult<object>()
                        {
                            Success = true,
                            Data = validationResult
                        }
                        : new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception, $"ReplaceSnippet validate failed: {validationResult.Diagnostics.ToInfo()}")
                        };
                    }

                    var result = await _workspaceManager.ApplyProposedChangesAsync(snippetChanges, validateChanges: validateOnApply);
                    if (!result.Success && result.ValidationResult != null)
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.Exception,
                                "ReplaceSnippet: the edit matched the target file, but the resulting code introduces new compiler errors — change not applied. Fix the issue(s) below and retry:\n" +
                                await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                        };
                    await WriteBlobForApplyAsync("replace_snippet", result);
                    var strippedResult = result with { PreImages = null };
                    object responseData = returnDiff
                        ? new
                        {
                            result = strippedResult,
                            diff = SentinelRefactoringTools.BuildDiffFromPreImages(snippetChanges, result.PreImages)
                        }
                        : strippedResult;
                    return new ToolResult<object>()
                    {
                        Success = true,
                        Data = responseData
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ReplaceSnippet {Action} unexpected exception for '{FilePathWrapper}'", action, filePathResolved);
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ReplaceSnippet {action} for '{filePathResolved}'")
                    };
                }
            }

            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"Unhandled action '{action}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ReplaceSnippet ({Action}) failed", action);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReplaceSnippet")
            };
        }
    }
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: namespaceName/typeKind/typeName are required for a .cs
    // file (to seed a valid compilation unit) and ignored for every other file extension — a model
    // creating a non-.cs file can omit all three, but a model creating a .cs file must supply all
    // three or the call fails, and nothing besides the description text signals that split.
    [McpServerTool(Name = "CreateFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Creates a new file. Fails if the file already exists — this tool never overwrites or writes free-form whole-file content. Parent directories are created automatically if missing.")]
    public async Task<ToolResult<object>> CreateFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Required for .cs files, ignored otherwise. Namespace to seed the file with (e.g. 'RoslynSentinel.Tests.Battery').")] string? namespaceName = null,
        [Description("Required for .cs files, ignored otherwise. Kind of top-level type to seed the file with — this seeds a valid compilation unit plus one empty top-level type declaration (e.g. 'public class Foo\\n{\\n}'), so Member(add) can immediately populate members inside it. Use staticClass for a static utility/helper class (e.g. static test helpers, extension-method containers) — static is only valid on classes, not the other kinds. For a second top-level type in the same file, add it afterward with Member(add, containerName: null, newMemberSource: \"...\").")] NewTypeKind? typeKind = null,
        [Description("Required for .cs files, ignored otherwise. Name of the top-level type to seed the file with (e.g. 'Foo').")] string? typeName = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);
        try
        {
            if (!filePathResolved.Validated)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'filepath' is required.")
                };
            }

            if (File.Exists(filePathResolved.Absolute))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, $"CreateFile: '{filePathResolved}' already exists. CreateFile never overwrites — use Member/ReplaceSnippet to edit an existing file.")
                };
            }

            bool isCSharpFile = filePathResolved.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (isCSharpFile && string.IsNullOrWhiteSpace(namespaceName))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'namespaceName' is required for a .cs file, so the new file starts as a valid compilation unit that Member(add) can populate.")
                };
            }

            if (isCSharpFile && (typeKind == null || string.IsNullOrWhiteSpace(typeName)))
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "CreateFile: 'typeKind' and 'typeName' are both required for a .cs file, so the new file starts with an empty top-level type that Member(add) can populate members into.")
                };
            }

            string content;
            if (isCSharpFile)
            {
                string keyword = typeKind!.Value == NewTypeKind.staticClass ? "static class" : typeKind.Value.ToString().TrimStart('@');
                content = $"namespace {namespaceName};\n\npublic {keyword} {typeName}\n{{\n}}\n";
            }
            else
            {
                content = "";
            }

            var directory = Path.GetDirectoryName((string)filePathResolved.Absolute);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = content };
            var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: true, cancellationToken: cancellationToken);
            if (!result.Success && result.ValidationResult != null)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception,
                        "CreateFile: this content would introduce new compiler errors — not written to disk. Fix the issue(s) below and retry:\n" +
                        await CompilerErrorLookupHelper.DescribeAsync(result.ValidationResult, _symbolNavigationEngine, cancellationToken))
                };
            }

            if (!result.Success)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"CreateFile failed to write '{filePathResolved}': {result.Summary}")
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
            _logger.LogError(ex, "CreateFile failed for '{FilePathWrapper}'", filePathResolved);
            return new ToolResult<object>()
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "CreateFile")
            };
        }
    }

    // The confirmationCode paramater was causing hallucinations and invalid tool calls. Reverted back to the original ApplyDiff tool but keeping this here (block-commented, since it depends
    // on ProposedChangeAction.confirmationCode, which is also commented out in ToolEnums.cs) in case we want to reintroduce ApplyDiff with a confirmationCode in the future.
    /*
    //[McpServerTool(Name = "ApplyDiffWithConfirmationCode")]
    [Produces(DataTag.ChangeId)]
    [Description("Applies or validates a change set. changesetFormat=files → changes dict filePath→newContent (filepath not used). changesetFormat=diff → filepath and unifiedDiff are BOTH REQUIRED (filepath names the single file the diff applies to; omitting it is a common mistake and fails immediately). For changesetFormat=diff, hunk line numbers are treated as a starting guess: if a hunk's declared position doesn't match, this searches nearby lines and re-anchors automatically, so modest line-number drift from an earlier edit to the same file is tolerated. Returns ApplyChangesResult with UndoChangeId on successful apply. The full pre-edit file content is NOT included by default (it's already captured for undo via UndoLastApply/GetOperationDetail) — pass returnDiff=true to get a unified-diff-style preview of what changed instead. IMPORTANT: for changesetFormat=files with action=apply, any file whose content would shrink by more than 50% is rejected with errorCode=ConfirmationRequired — this is a strong signal you submitted only a changed fragment as if it were the whole file, rather than a genuine whole-file rewrite. If the rewrite is really intended, call ApplyDiff again with action=confirmationCode and confirmationCode set to the code from the rejection — do not resend changes/filepath/unifiedDiff on that call, the original changeset is already cached server-side.")]
    public async Task<ToolResult<object>> ApplyDiffWithConfirmationCode([ExternalInputRequired(DataTag.ChangeseFormat)] ChangesetFormat changesetFormat, [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action, [ExternalInputRequired(DataTag.OperationId)] Dictionary<FilePathWrapper, string>? changes = null, [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null, [ToolOption(ToolOptionTag.UnifiedDiff)] string? unifiedDiff = null, [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3, [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, [ToolOption(ToolOptionTag.ConfirmationCode)][Description("Required when action=confirmationCode. The code returned by a prior apply call that was rejected for exceeding the whole-file-rewrite size threshold. Replays that exact cached changeset — do not also pass changes/filepath/unifiedDiff.")] string? confirmationCode = null, // RequestContext<CallToolRequestParams> requestParams = null,
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

            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);
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
                if (!filePathResolved.Validated && string.IsNullOrEmpty(unifiedDiff))
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, "ApplyDiff: both 'filepath' and 'unifiedDiff' are required when changesetFormat=diff.")
                    };
                }

                if (!filePathResolved.Validated)
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
                        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePathResolved.Absolute || d.FilePathWrapper == filePathResolved.Absolute);
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
                        var targetPath = document.FilePathWrapper ?? filePath;
                        var diffChanges = new Dictionary<FilePathWrapper, string>
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
                        _logger.LogError(ex, "ApplyDiff diff apply unexpected exception for '{FilePathWrapper}'", filePathResolved);
                        return new ToolResult<object>()
                        {
                            Success = false,
                            Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyDiff diff apply for '{filePathResolved}'")
                        };
                    }
                }

                if (action == ProposedChangeAction.validate)
                {
                    var validationResult = await _validationEngine.ValidateDiffAsync(filePathResolved.Absolute, unifiedDiff);
                    return validationResult.Success ? new ToolResult<object>()
                    {
                        Success = true,
                        Data = validationResult
                    }

                    : new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"ApplyDiff diff validate failed: {validationResult.Diagnostics.ToInfo()}")
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

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries failed file writes using server-cached content — no need to re-send file contents. specificFiles limits to a subset. retryCount defaults to 3.")]
    public async Task<ToolResult<object>> RetryFailedChanges(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null, [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3, // RequestContext<CallToolRequestParams> requestParams = null,
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
    /// </summary>
    /// <remarks>
    /// Never throws: the apply already succeeded and the files are on disk, so failing the call
    /// would report a landed edit as failed and invite a retry. On failure it instead trips the
    /// unrecoverable breaker, which refuses every subsequent mutating call this session — the
    /// previous behaviour was a log-only warning that no caller and no transcript ever saw.
    /// Returns the result so callers can suppress their own undo advice; the trip happens here
    /// regardless, so a caller that ignores it still cannot proceed.
    /// </remarks>
    internal async Task<BlobWriteResult> WriteBlobForApplyAsync(string toolName, ApplyChangesResult result, string? blobChangeId = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        if (result.SucceededFiles.Count == 0)
        {
            return BlobWriteResult.NotNeeded("no files written — blob not needed");
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
        var blob = await OperationBlobWriter.WriteAsync(
            toolName, changeId, items, _workspaceManager.GetSolutionRoot(), _logger, cancellationToken);

        if (blob.IsIntegrityFailure)
        {
            // Files landed with no undo record. OperationBlobWriter has already logged the
            // exception at Error; the trip is what makes it consequential rather than advisory.
            ((IUnrecoverableBreaker)_workspaceManager).Trip(
                toolName, changeId, blob.Diagnostic ?? "the operation blob could not be written");
        }
        else if (blob.Written && _logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Forensic blob written: {BlobName} (changeId={ChangeId})", blob.FileName, changeId);
        }

        return blob;
    }
    [McpServerTool(Name = "GetDiagnostics")]
    [Produces(DataTag.Report)]
    [Description("Gets compiler diagnostics for a file, project, or the whole solution.")]
    public async Task<ToolResult<object>> GetDiagnostics(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("file/project: also pass scopeName. solution: scopeName is ignored.")]
        [Consumes(DataTag.ProjectName, required: true)][Consumes(DataTag.SourceFilepath, required: false)] ToolScope scope = ToolScope.solution,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for scope=file (as a filePath) and
        // scope=project (as a projectName), ignored for scope=solution.
        [Description("Required for scope=file (a filePath) or scope=project (a projectName). Ignored for scope=solution.")]
        string? scopeName = null,
        [Description("Groups results by diagnostic ID and returns counts instead of the raw list.")]
        bool summarize = false,
        [Description("Caps the raw diagnostics list. Ignored when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        [Description("Caps the number of groups returned. Only used when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.TopN)] int topN = 20,
        [Description("noBuild (default): diagnostics only. quickBuild/fullBuild: additionally runs a build check (see Build tool) and attaches it as BuildVerification.")]
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild, // RequestContext<CallToolRequestParams> requestParams = null,
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
            var groups = relevant.GroupBySeverity(topN);
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
    [Description("Compiles the loaded solution and reports errors/warnings. level=quickBuild uses in-memory Roslyn diagnostics (fast, same check GetDiagnostics does). level=fullBuild shells out to `dotnet build` (slower, catches MSBuild-only failures — NuGet restore, resource copy, post-build events — that quickBuild can't see). Returns BuildSucceeded, ExitCode, ErrorCount/WarningCount, capped Errors/Warnings lists, ErrorSummary/WarningSummary (grouped by diagnostic Id, uncapped, for spotting one cause behind many errors), Duration.")]
    public async Task<ToolResult<object>> Build(
        [Description(ToolParams.Reason)] ToolCallReason reason,
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

    [McpServerTool(Name = "RunTest")]
    [Produces(DataTag.Report)]
    [Description("Runs `dotnet test` against the loaded solution (or a single project) and reports structured results. Returns TotalCount/PassedCount/FailedCount/SkippedCount, a FailureSummary grouping failures by message signature (e.g. \"45 of 50 failures share one cause\") so an agent doesn't have to paginate to notice a pattern, and a capped Results list (filtered by resultsType, then capped by maxDetails). resultsType defaults to \"failed\" so a clean run stays a short summary with no per-test list; pass \"all\" to see every test's outcome. Set summary=true to omit the Results list entirely (just counts + FailureSummary), regardless of resultsType. filter is passed through to `dotnet test --filter` — an unresolvable filter expression is a distinct error from a filter that resolves but matches zero tests.")]
    public async Task<ToolResult<object>> RunTest(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        string? filter = null,
        TestResultsFilter resultsType = TestResultsFilter.failed,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        int timeoutSeconds = 600,
        [Description("If true, omit the per-test Results list from the response entirely — only counts and FailureSummary are returned, independent of resultsType.")] bool summary = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rateLimitError = _workspaceManager.CheckRateLimit("RunTest", 10);
            if (rateLimitError is not null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.TestRunFailed, rateLimitError) };
            }

            var result = await _testRunEngine.RunAsync(scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, cancellationToken);

            if (!result.TryGetData(out var testRunResult))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.TestRunFailed, result.Error?.Message ?? "Test run failed unexpectedly.") };
            }

            if (!testRunResult.RunCompleted)
            {
                return new ToolResult<object>() { Success = false, Data = testRunResult, Error = new ResultError(ToolErrorCode.TestRunFailed, testRunResult.Detail ?? "Test run did not complete."), WorkspaceVersion = _workspaceManager.WorkspaceVersion };
            }

            return new ToolResult<object>() { Success = true, Data = testRunResult, WorkspaceVersion = _workspaceManager.WorkspaceVersion };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunTest failed");
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"RunTest failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and dotnet is on PATH. Details: {ex.Message}") };
        }
    }
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: none of projectName/docCommentId/symbolName/line/column is
    // individually required — the tool needs exactly one full resolution strategy: (projectName +
    // docCommentId), or symbolName (optionally with contextSnippet/lineBefore/lineAfter), or
    // (line + column). Supplying an incomplete subset of any one strategy (e.g. line without column)
    // is accepted by the schema but rejected at runtime with InvalidArgument.
    [McpServerTool(Name = "SafeDeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages in the entire codebase. Distinction from RemoveMember: this tool refuses if ANY usage is found; RemoveMember checks for callers/implementations but allows skipPrecheck. Returns changeId.")]
    public async Task<ToolResult<object>> SafeDeleteUnusedSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Preferred resolution path, together with docCommentId — as returned by LocateSymbol/FindReferences. The most reliable and accurate way to identify the target.")] string projectName = "",
        [Description("Preferred resolution path, together with projectName — as returned by LocateSymbol/FindReferences.")] string docCommentId = "",
        [Description("Fallback resolution path if projectName/docCommentId aren't available. Combine with contextSnippet/lineBefore/lineAfter to disambiguate; symbolName alone is enough if there's only one declaration with that name.")]
        [Consumes(DataTag.SymbolName, required: false)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description("Legacy fallback resolution path if neither of the above is available — 1-based line of the declaration site. Both line and column are required together.")]
        [Consumes(DataTag.StartLine, required: false)] int line = 0,
        [Description("Legacy fallback resolution path — 1-based column of the declaration site. Both line and column are required together.")]
        [Consumes(DataTag.Offset, required: false)] int column = 0, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
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
                Data = new AppliedChangeSummary(changeId, [filePathResolved], $"Deleted unused symbol in {Path.GetFileName(filePathResolved)}.", false)
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

                var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePathResolved, resolution.Symbol!, cancellationToken);
                return await ApplyAndRespondAsync(result);
            }

            // Fallback: symbolName + contextSnippet-based resolution. Added because the tool's own
            // description previously promised this path while the implementation silently ignored
            // contextSnippet/lineBefore/lineAfter entirely, leaving callers with no way to identify a
            // target by anything other than a raw line/column pair (see the next branch) — and no
            // tool exposes a column, only a line, making that pair effectively unobtainable too.
            if (!string.IsNullOrEmpty(symbolName))
            {
                var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                return await ApplyAndRespondAsync(result);
            }

            // Fallback: line/column-based resolution (legacy path) — requires a precise column, which
            // no other tool surfaces; prefer symbolName+contextSnippet above when possible.
            if (line > 0 && column > 0)
            {
                var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePathResolved, line, column, cancellationToken);
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
            _logger.LogError(ex, "SafeDeleteUnusedSymbol failed for '{FilePathWrapper}' at {Line}:{Column} or handle {ProjectName}/{DocCommentId}", filePathResolved, line, column, projectName, docCommentId);
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
    public async Task<ToolResult<object>> CreateProject(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName, [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console", // RequestContext<CallToolRequestParams> requestParams = null,
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
    public async Task<ToolResult<object>> SplitProjectByFolder(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ProjectName, required: true)] string sourceProjectName, [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName, [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName, // RequestContext<CallToolRequestParams> requestParams = null,
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

    // Shared by ReadFile/GetMethodSource/GetFileOutline's document-not-found path. Models that
    // guess a wrong-but-plausible path (most often a missing subfolder) were observed retrying the
    // identical wrong path 2-3 times before giving up, even when ListSolutionItems's own earlier
    // output already showed the real path — the plain "file not found" message gave them nothing to
    // act on. Searching the solution for files sharing the requested filename turns most of these
    // into a one-shot redirect; when nothing matches by filename either, the message issues an
    // explicit, unhedged directive rather than a suggestion, since softer phrasing ("consider
    // calling X") was observed not changing the model's next action.
    private static ResultError BuildFileNotFoundError(Solution solution, string normalizedPath)
    {
        var requestedFileName = Path.GetFileName(normalizedPath);
        var candidates = solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFileName(d.FilePath), requestedFileName, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.FilePath!)
            .Distinct()
            .Take(5)
            .ToList();

        if (candidates.Count > 0)
        {
            return new ResultError("FileNotFound",
                $"'{requestedFileName}' does not exist at '{normalizedPath}'. A file with this name exists at a different path. " +
                $"You MUST retry with the correct path:\n" +
                string.Join("\n", candidates.Select(c => $"  - {c}")));
        }

        return new ResultError("FileNotFound",
            $"'{requestedFileName}' does not exist anywhere in the solution (searched {solution.Projects.Count()} project(s), no filename match). " +
            "You MUST call ListSolutionItems(kind: all) next to see every file actually in the solution before trying another path.");
    }

    // ── Phase 1 — Low-level fallback tools ──────────────────────────────────
    [McpServerTool(Name = "GetMethodSource")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the full source text of a named method or constructor, plus a structured list of its attributes. For a constructor, pass the containing class's name (e.g. methodName: \"OrderService\" for `public OrderService(...)`). Case-sensitive match with case-insensitive fallback. Returns the first match for overloaded names.")]
    public Task<ToolResult<object>> GetMethodSource(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath, [Consumes(DataTag.MethodName, required: true)] string methodName, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        return _readNav.GetMethodSource(reason, filePathResolved, methodName, cancellationToken);
    }

    [McpServerTool(Name = "ReadFile")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the raw text of a file in the loaded solution, verbatim (no reformatting). Pass startLine/endLine (1-based, inclusive) to read a slice instead of the whole file — useful once GetFileOutline or a search result gives you a line range. Whole-file reads past the size threshold are written to .roslynsentinel/largeresults and returned as a resultId (see GetMethodSource) instead of inline text.")]
    public async Task<ToolResult<object>> ReadFile(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath, [Description("1-based, inclusive. Omit to start from the first line.")] int? startLine = null, [Description("1-based, inclusive. Omit to read through the last line.")] int? endLine = null, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
            var normalizedPath = Path.GetFullPath(filePathResolved);
            var document = solution.GetDocumentIdsWithFilePath(normalizedPath).Select(solution.GetDocument).FirstOrDefault() ?? solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => !string.IsNullOrEmpty(d.FilePath) && string.Equals(Path.GetFullPath(d.FilePath), normalizedPath, StringComparison.OrdinalIgnoreCase));

            SourceText sourceText;
            if (document != null)
            {
                sourceText = await document.GetTextAsync(cancellationToken);
            }
            else
            {
                // WriteFile writes any file to disk regardless of extension or whether it belongs
                // to a loaded project — non-.cs files, and .cs files outside every project's globs,
                // never become tracked Documents (PersistentWorkspaceManager only syncs .cs files
                // belonging to a resolvable project into CurrentSolution). Fall back to a raw disk
                // read so ReadFile can see everything WriteFile is able to write.
                var diskContent = await FileIoHelper.ReadAllTextIfExistsAsync(filePathResolved, cancellationToken);
                if (diskContent == null)
                {
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = BuildFileNotFoundError(solution, normalizedPath)
                    };
                }

                sourceText = SourceText.From(diskContent);
            }

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
                        filePath = (string)filePathResolved,
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
                var fullResult = new FileSourceResult { FilePath = (string)filePathResolved, StartLine = 1, EndLine = totalLines, TotalLines = totalLines, Source = fullText };
                var stored = await LargeResultHelper.StoreLargeResultAsync(fullResult, solutionRoot, ResultWrapperType.FileSource, cancellationToken);

                // GetFileOutline never offloads its own result (it has no size threshold of its
                // own), so fileOutline.LargeResult is always null here — the outline's Data is what
                // we want to surface either way. LargeResultInfo itself must stay on the top-level
                // LargeResult property (per ToolResult<T>'s "exactly one of Data/Error/LargeResult"
                // contract), not nested inside Data, or callers checking result.LargeResult (as
                // GetLargeResult-following clients and tests do) will see null and miss the offload.
                object? outlineData = null;
                try
                {
                    var fileOutline = await _readNav.GetFileOutline(reason: "test", filePathResolved, cancellationToken);
                    outlineData = fileOutline.Data;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "GetFileOutline failed for '{FilePathWrapper}'", filePathResolved);
                }

                return new ToolResult<object>
                {
                    Success = true,
                    LargeResult = new LargeResultInfo(resultType: "FileSource", writtenToFile: stored.offloaded, filePath: stored.filePath, resultId: stored.resultId!, sizeBytes: textBytes, totalRecords: 1, message: $"Result is {totalLines} lines, {textBytes} bytes (threshold: {thresholdBytes}). " + $"Use get_large_result(resultId: \"{stored.resultId}\") to page through results, or retry ReadFile with startLine/endLine for just the slice you need, or use GetFileOutline to get the constructors, methods, helpers, members, enums, fields, properties, etc of a file without reading the entire file."),
                    Data = new
                    {
                        totalLines,
                        fileOutline = outlineData
                    },
                    WorkspaceVersion = _workspaceManager.WorkspaceVersion,
                };
            }

            return new ToolResult<object>()
            {
                Success = true,
                Data = new
                {
                    filePath = (string)filePathResolved,
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
            _logger.LogError(ex, "ReadFile failed for '{FilePathWrapper}'", filePathResolved);
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
    public Task<ToolResult<object>> GetFileOutline(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        return _readNav.GetFileOutline(reason, filePathResolved, cancellationToken);
    }

    [McpServerTool(Name = "ListAll")]
    [Produces(DataTag.Report)]
    [Description("Lists every namespace/class/interface/struct/record/enum/enum member/constructor/field/method/property declared anywhere in the loaded solution, one row per symbol with its file, kind, name, container, and line range — the solution-wide equivalent of GetFileOutline. Call this FIRST when you don't already know the exact name of the type/method/field you need — it is cheaper and more reliable than guessing plausible-sounding names and searching for each one individually with SearchSolutionText. Can return a lot of rows on a large solution; narrow with kind and/or projectName first.")]
    public Task<ToolResult<object>> ListAll(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.ListAllKindValues)][ExternalInputRequired(DataTag.SymbolKind, required: false)] ListAllKind kind = ListAllKind.all,
        [Description("Restricts results to one project. Omit to search the whole solution.")]
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _readNav.ListAll(reason, kind, projectName, cancellationToken);
    [McpServerTool(Name = "SearchSolutionText")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches all source files in the loaded solution for pattern, evaluated BOTH as a literal substring and (if it compiles) as a regex in a single pass — there is no search-mode to choose. Only searches documents that are part of a loaded project's source code (e.g. .cs files). For a known symbol (class/method/field/etc. by name), use LocateSymbol instead — it's semantic, not text-based, so it won't false-positive on comments/strings or miss partial-line matches. If you don't know the exact name you're looking for, call ListAll first — it's cheaper and more reliable than guessing plausible-sounding names and searching for each one individually here. Use ListSolutionItems(kind: solutionItems) to see files attached via the .sln's Solution Folders and other non-project files, use ProjectDoc to read plan/handoff/documentation files directly, and use GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file. Returns literalResults (always the complete literal-substring match set) and regexResults (regex matches not already in literalResults — empty when pattern has no regex metacharacters, since every regex match is then also a literal match), plus regexOverlapCount (matches found both ways) and regexPatternValid (false if pattern doesn't compile as a regex — literal search is unaffected). Each match has file path, 1-based line and column, a preview, and enclosingMember (the name of the method/property/constructor/field/etc. containing the match, or null if the match isn't inside any member).")]
    public Task<ToolResult<object>> SearchSolutionText(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("The text to search for — evaluated both as a literal substring and, if it compiles, as a regex.")]
        [ToolOption(ToolOptionTag.Pattern, required: true)] string pattern,
        [Description("Restricts to matching file paths (glob syntax). Omit to search every file.")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
        [Description("Caps total matches scanned.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
        => _readNav.SearchSolutionText(reason, pattern, fileGlob, maxResults, cancellationToken);
    [McpServerTool(Name = "GetOperationDetail")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns a filtered slice of an operation result blob by changeId. offset skips that many filtered items before taking maxItems; pass NextOffset from the previous response to page through the rest. TotalItems reflects the filtered count; HasMorePages is true when more items remain past this page.")]
    public Task<ToolResult<object>> GetOperationDetail(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ChangeId, required: true)] string changeId,
        [Description("Filters items by outcome or path. Accepts prefix synonyms: fail/err → failures, warn/skip → skipped, ok/pass/info/success → succeeded, roll/revert/undo → rolledback, manual/manual_review/needs_manual_review → NeedsManualReview (bridge compiler-error skips), file:<path> to filter by path. Omit for all items. An unrecognised prefix returns an error.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null,
        [Description("Caps how many filtered items this page returns.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50,
        [Description("How many filtered items to skip before taking maxItems. Pass NextOffset from the previous response to continue paging.")]
        [ToolOptionAttribute(ToolOptionTag.Offset)] int offset = 0, // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
        => _readNav.GetOperationDetail(reason, changeId, filter, maxItems, offset, cancellationToken);

    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previously applied batch to their pre-apply state using the forensic blob written at apply time. Covers all apply operations: apply_diff, refactoring-tool writes, and batch-first tools.")]
    public async Task<ToolResult<object>> UndoLastApply(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.OperationId, required: true)] string changeId, // RequestContext<CallToolRequestParams> requestParams = null,
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
                    // Deliberately does not say "ensure the apply completed successfully" — the
                    // old wording did, and it misdiagnosed every real occurrence: in both
                    // recorded cases (run 20260910-013550-398, and the SyncTypeAndFilename
                    // blocker) the apply had completed and the files were on disk. The blob was
                    // missing, not the change. Tools now withhold the changeId rather than issue
                    // an unusable one, so reaching this at all means the id is from another
                    // session, a different solution, or was mistyped.
                    Error = new ResultError("NoOperationBlobFound",
                        $"No operation blob found for changeId '{changeId}' under .roslynsentinel/operations/. " +
                        "This does not mean the change failed — it may well be on disk. It means no undo record " +
                        "exists for that id here. Check the id against the value the applying tool returned, and " +
                        "that this is the same server session and solution; a changeId from an earlier session or " +
                        "a different solution root will not resolve. If the applying tool reported the change as " +
                        "'not reversible', there is no undo record to find and the change must be reverted manually.")
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
                    // Same misdiagnosis as NoOperationBlobFound above: the blob exists, so the
                    // apply plainly ran. What's absent is a pre-image to restore — see
                    // docs/current/blockers/blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md,
                    // where a rename recorded no BeforeSource and this message sent the
                    // investigation after the apply instead of after the blob's contents.
                    Error = new ResultError("NoReversibleItems",
                        $"The operation blob for changeId '{changeId}' was found, but none of its items carry the " +
                        "original file contents needed to revert. The change itself completed — this is a gap in " +
                        "what was recorded, not a failed apply, and it is most common for operations that renamed " +
                        "or created files rather than editing them in place. Revert manually (e.g. via version " +
                        $"control); GetOperationDetail(changeId: \"{changeId}\") shows exactly which files were touched.")
                };
            }

            var failed = new List<string>();
            var revertChanges = new Dictionary<FilePathWrapper, string>();
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
    public async Task<ToolResult<object>> GetWorkspaceHealth(
        [Description(ToolParams.Reason)] ToolCallReason reason,
    // RequestContext<CallToolRequestParams> requestParams = null,
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
    public async Task<ToolResult<object>> ListProjectFrameworkTargets(
        [Description(ToolParams.Reason)] ToolCallReason reason,
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
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetProjectFrameworkSummary")
            };
        }
    }

    // ── get_large_result ────────────────────────────────────────────────────────

    [McpServerTool(Name = "GetLargeResult")]
    [Produces(DataTag.Report)]
    [Description("Pages through a large result that was written to disk because it exceeded the inline size threshold.")]
    public Task<ToolResult<object>> GetLargeResult(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of resultId/filepath must be supplied;
        // neither is individually required but the tool fails if both are omitted.
        [Description("The result's resultId, as returned alongside the original truncated result. Required if filepath is omitted.")]
        [Consumes(DataTag.ResultId)] string? resultId = null,
        [Description("Path to a largeresult_*.json file under .roslynsentinel/largeresults. Required if resultId is omitted.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description("Maximum number of records to return.")]
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [Description("Number of records to skip before taking limit.")]
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        return _readNav.GetLargeResult(reason, resultId, filePathResolved, limit, offset, cancellationToken);
    }
}
