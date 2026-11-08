using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

// SolutionItemFile, ProjectInfoEntry, ProjectFilesAndDependencies, SolutionItemsAllResult remain
// declared in WorkspaceTools.cs (their original declaration site) to avoid a CS0101
// collision - SolutionItemsAllResult in particular already exists in
// RoslynSentinel.Common/LargeResultHelper.cs, confirming it must not be re-declared here.
public record SolutionFileInfo(string Path, string Format);

/// <summary>
/// Plain DI-constructed implementation backing WorkspaceProjectManagementTools. Method bodies are
/// moved verbatim from WorkspaceTools (Decision 7 step 2) - not yet MCP-agnostic in return
/// type, per Decision 1-Amendment's explicit scope (mechanical shim only for this pass).
/// </summary>
public class WorkspaceProjectManagementImpl
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly SolutionStructureEngine _solutionStructureEngine;
    private readonly ILogger _logger;

    private const int ListWorkspaceSolutionsMaxFilesWalked = 5000;

    public WorkspaceProjectManagementImpl(IWorkspaceManager workspaceManager, StructuralRefinementEngine structuralRefinementEngine, ILogger logger, SolutionStructureEngine solutionStructureEngine)
    {
        _workspaceManager = workspaceManager;
        _structuralRefinementEngine = structuralRefinementEngine;
        _solutionStructureEngine = solutionStructureEngine;
        _logger = logger;
    }

    public async Task<SentinelCallToolResult<object>> ListSolutionItems(ToolCallReason reason, SolutionItemsKind kind,
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (kind == SolutionItemsKind.projects)
            {
                var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
                var projectInfos = solution.Projects.Select(p => new ProjectInfoEntry(p.Name, p.FilePath)).ToList();
                return new SentinelCallToolResult<object> { IsError = false, SuccessData = projectInfos, TotalRecords = projectInfos.Count };
            }

            if (kind == SolutionItemsKind.solutionItems)
            {
                var solutionRoot = _workspaceManager.GetSolutionRoot();
                if (solutionRoot is null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.SolutionNotLoaded, SolutionNotLoadedMessage.Plain)
                    };
                }

                var items = _workspaceManager.GetSolutionFolderItems().Select(i => new SolutionItemFile(new FilePathWrapper(Path.GetFullPath(Path.Combine(solutionRoot, i.RelativePath)), solutionRoot), i.SolutionFolder)).ToList();
                return new SentinelCallToolResult<object> { IsError = false, SuccessData = items, TotalRecords = items.Count };
            }

            if (kind == SolutionItemsKind.files)
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "projectName is required when kind=files.")
                    };
                }

                try
                {
                    var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
                    var project = ProjectLookup.FindProject(solution, projectName, ignoreCase: true);
                    if (project == null)
                    {
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.NotFound, $"Project '{projectName}' not found.")
                        };
                    }

                    var sep = Path.DirectorySeparatorChar;
                    var files = project.Documents.Select(d => d.FilePath ?? d.Name).Where(p => !p.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) && !p.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)).ToList();
                    return new SentinelCallToolResult<object> { IsError = false, SuccessData = files, TotalRecords = files.Count };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "List files unexpected exception for project '{ProjectName}'", projectName);
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"List files for project '{projectName}'")
                    };
                }
            }

            if (kind == SolutionItemsKind.dependencies)
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "projectName is required when kind=dependencies.")
                    };
                }

                var result = await _solutionStructureEngine.GetProjectDependenciesAsync(projectName, cancellationToken);
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = result
                };
            }

            if (kind == SolutionItemsKind.all)
            {
                var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
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

                    var dependencies = await _solutionStructureEngine.GetProjectDependenciesAsync(project.Name, cancellationToken);
                    projectDetails.Add(new ProjectFilesAndDependencies(project.Name, filesByPath.Values.ToList(), dependencies));
                }

                var combined = new SolutionItemsAllResult(projectInfos, solutionItems, projectDetails);
                var totalRecords = projectInfos.Count + solutionItems.Count + projectDetails.Sum(p => p.Files.Count);
                return new SentinelCallToolResult<object> { IsError = false, SuccessData = combined, TotalRecords = totalRecords };
            }

            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown kind '{kind}'. Valid values: {string.Join(", ", Enum.GetNames<SolutionItemsKind>())}." + " " + ToolCallHint.Next("ListSolutionItems", "with one of the valid kinds.", ("kind", "projects")))
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List ({Kind}) failed", kind);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "List")
            };
        }
    }

    public SentinelCallToolResult<List<SolutionFileInfo>, ResultError> ListWorkspaceSolutions(ToolCallReason reason, string workspacePath, CancellationToken cancellationToken = default)
    {
        workspacePath = FilePathWrapper.NormalizeWirePath(workspacePath);
        if (!Directory.Exists(workspacePath))
        {
            return new SentinelCallToolResult<List<SolutionFileInfo>, ResultError>
            {
                IsError = true,
                ErrorData = new ResultError("InvalidArgument", $"Directory not found: '{workspacePath}'")
            };
        }

        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var pathRoot = Path.GetPathRoot(fullWorkspacePath);
        if (!string.IsNullOrEmpty(pathRoot) && string.Equals(
                fullWorkspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return new SentinelCallToolResult<List<SolutionFileInfo>, ResultError>
            {
                IsError = true,
                ErrorData = new ResultError("InvalidArgument", $"workspacePath '{workspacePath}' resolves to the drive root '{pathRoot}'. Pass a real project/repo directory instead - scanning an entire drive is not supported.")
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
                        return new SentinelCallToolResult<List<SolutionFileInfo>, ResultError>
                        {
                            IsError = true,
                            ErrorData = new ResultError("InvalidArgument", $"workspacePath '{workspacePath}' contains more than {ListWorkspaceSolutionsMaxFilesWalked} matching files - this looks like too broad a root. Pass a narrower project/repo directory instead.")
                        };
                    }
                }
            }

            files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return new SentinelCallToolResult<List<SolutionFileInfo>, ResultError>
            {
                IsError = false,
                SuccessData = files,
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
            return new SentinelCallToolResult<List<SolutionFileInfo>, ResultError>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.Exception, $"ListWorkspaceSolutions failed unexpectedly ({ex.GetType().Name}) while scanning '{workspacePath}'. Data: {ex.Message}")
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

    private static readonly (string Dir, string DocType)[] ProjectDocSubdirs = [("plans", "plan"), ("handoffs", "handoff"), ("completed", "completed_work"), ("documentation", "documentation"),];

    private string BuildPostLoadHint(string solutionRoot, IReadOnlyList<string> projectFilePaths)
    {
        var parts = new List<string>();
        var solutionItems = _workspaceManager.GetSolutionFolderItems();
        if (solutionItems.Count > 0)
        {
            parts.Add($"{solutionItems.Count} file(s) attached via Solution Folders in the .sln (not visible to SearchSolutionText - list them with {ToolCallHint.Call("ListSolutionItems", ("kind", "solutionItems"))}).");
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
                parts.Add($"docs/{dir}/ has {count} file(s) - read with {ToolCallHint.Call("ProjectDoc", ("action", "read"), ("docType", docType))}.");
            }
        }

        var unrestored = BuildUnrestoredHint(FindUnadvisedUnrestoredProjects(projectFilePaths, AdvisedRestoreStates));
        if (unrestored.Length > 0)
        {
            parts.Add(unrestored);
        }

        return parts.Count > 0 ? " " + string.Join(" ", parts) : "";
    }

    // Flags projects whose obj/project.assets.json is missing or older than the .csproj or the nearest
    // Directory.Packages.props. Cheap file-system checks only; a project whose files cannot be read is skipped.
    internal static IReadOnlyList<string> FindUnrestoredProjects(IEnumerable<string> csprojPaths)
        => AnalyzeRestoreState(csprojPaths).Select(f => f.Name).ToList();

    // Empty string when nothing is flagged. Names up to 3 projects, then "(+N more)".
    internal static string BuildUnrestoredHint(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return "";
        }

        var shown = string.Join(", ", names.Take(3));
        var more = names.Count > 3 ? $" (+{names.Count - 3} more)" : "";
        return $"Unrestored project(s) (no current obj/project.assets.json): {shown}{more}. Run {ToolCallHint.Call("Build", ("level", "fullBuild"))} then {ToolCallHint.Call("LoadSolution", ("forceReload", "true"))}.";
    }

    // "1.2" -> "1.2.0", "1.2.3.0" -> "1.2.3", prerelease lower-cased; null for floating/range/property versions,
    // which are not comparable and so never count as a mismatch.
    private static string? NormalizePlainVersion(string text)
    {
        var match = PlainVersionRegex.Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var parts = new List<int>();
        foreach (var part in match.Groups[1].Value.Split('.'))
        {
            if (!int.TryParse(part, out var number))
            {
                return null;
            }

            parts.Add(number);
        }

        while (parts.Count < 3)
        {
            parts.Add(0);
        }

        if (parts.Count == 4 && parts[3] == 0)
        {
            parts.RemoveAt(3);
        }

        return string.Join('.', parts) + match.Groups[2].Value.ToLowerInvariant();
    }

    // Only unconditional items are compared: a Condition may exclude the item from this restore, so its absence
    // from project.assets.json proves nothing.
    private static bool IsUnconditional(XElement element)
        => element.AncestorsAndSelf().All(e => e.Attribute("Condition") is null);

    // PackageVersion Include -> Version from Directory.Packages.props (unconditional entries only; an id declared
    // twice maps to null = not comparable). Empty when the file is absent or not parseable.
    private static Dictionary<string, string?> ReadCentralVersions(string? propsPath)
    {
        var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (propsPath is null)
        {
            return versions;
        }

        try
        {
            foreach (var element in XDocument.Load(propsPath).Descendants().Where(e => e.Name.LocalName == "PackageVersion" && IsUnconditional(e)))
            {
                var id = (string?)element.Attribute("Include");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var version = (string?)element.Attribute("Version");
                versions[id] = versions.ContainsKey(id) ? null : version;
            }
        }
        catch (XmlException)
        {
            versions.Clear();
        }

        return versions;
    }

    // projectFileDependencyGroups: { "<tfm>": [ "<id> >= <version>", ... ] } -> id -> the range strings seen across TFMs.
    private static Dictionary<string, List<string>> ReadAssetPackageRanges(JsonElement root)
    {
        var ranges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("projectFileDependencyGroups", out var groups) || groups.ValueKind != JsonValueKind.Object)
        {
            return ranges;
        }

        foreach (var group in groups.EnumerateObject().Where(g => g.Value.ValueKind == JsonValueKind.Array))
        {
            foreach (var entry in group.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
            {
                var text = entry.GetString() ?? "";
                var space = text.IndexOf(' ');
                var id = space < 0 ? text : text[..space];
                if (!ranges.TryGetValue(id, out var list))
                {
                    ranges[id] = list = [];
                }

                list.Add(space < 0 ? "" : text[(space + 1)..].Trim());
            }
        }

        return ranges;
    }

    // Target framework monikers the restore recorded: project.frameworks keys and project.restore.originalTargetFrameworks.
    private static List<string> ReadAssetFrameworks(JsonElement root)
    {
        var frameworks = new List<string>();
        if (!root.TryGetProperty("project", out var project) || project.ValueKind != JsonValueKind.Object)
        {
            return frameworks;
        }

        if (project.TryGetProperty("frameworks", out var keyed) && keyed.ValueKind == JsonValueKind.Object)
        {
            frameworks.AddRange(keyed.EnumerateObject().Select(p => p.Name));
        }

        if (project.TryGetProperty("restore", out var restore) && restore.ValueKind == JsonValueKind.Object
            && restore.TryGetProperty("originalTargetFrameworks", out var original) && original.ValueKind == JsonValueKind.Array)
        {
            frameworks.AddRange(original.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? ""));
        }

        return frameworks;
    }

    // project.restore.frameworks.<tfm>.projectReferences keys are absolute project paths; compare by file name so a
    // path-spelling difference (8.3 names, drive aliases) can never keep a project flagged forever.
    private static HashSet<string> ReadAssetProjectReferenceNames(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object
            && project.TryGetProperty("restore", out var restore) && restore.ValueKind == JsonValueKind.Object
            && restore.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Object)
        {
            foreach (var framework in frameworks.EnumerateObject().Where(f => f.Value.ValueKind == JsonValueKind.Object))
            {
                if (framework.Value.TryGetProperty("projectReferences", out var references) && references.ValueKind == JsonValueKind.Object)
                {
                    names.UnionWith(references.EnumerateObject().Select(r => Path.GetFileName(r.Name.Replace('\\', '/'))));
                }
            }
        }

        return names;
    }

    // Every unconditional PackageReference must be recorded by the restore, at the same plain version when both
    // versions are comparable (csproj Version / VersionOverride, else the Directory.Packages.props PackageVersion).
    // Only additions and version changes are checked; packages the restore recorded that the csproj does not list
    // (Directory.Build.props, SDK-implicit) are not evidence of staleness.
    private static string? FindPackageMismatch(XDocument project, Dictionary<string, string?> central, Dictionary<string, List<string>> assetRanges)
    {
        foreach (var reference in project.Descendants().Where(e => e.Name.LocalName == "PackageReference" && IsUnconditional(e)))
        {
            var id = (string?)reference.Attribute("Include");
            if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(['$', '@', '*', ';']) >= 0)
            {
                continue;
            }

            if (!assetRanges.TryGetValue(id, out var ranges))
            {
                return $"package {id} added";
            }

            var wanted = (string?)reference.Attribute("Version")
                ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value
                ?? (string?)reference.Attribute("VersionOverride")
                ?? (central.TryGetValue(id, out var centralVersion) ? centralVersion : null);
            var wantedNormalized = wanted is null ? null : NormalizePlainVersion(wanted);
            if (wantedNormalized is null)
            {
                continue;
            }

            var recorded = ranges.Select(r => r.StartsWith(">=", StringComparison.Ordinal) ? NormalizePlainVersion(r[2..]) : null).ToList();
            if (recorded.Count > 0 && recorded.All(r => r is not null) && !recorded.Contains(wantedNormalized))
            {
                return $"package {id} version changed";
            }
        }

        return null;
    }

    // Unconditional TargetFramework(s) must be among the frameworks the restore recorded (prefix match covers
    // net8.0-windows -> net8.0-windows7.0), and unconditional ProjectReferences among the recorded project files.
    // Skipped when the assets file records no frameworks at all (nothing to compare against).
    private static string? FindFrameworkOrProjectReferenceMismatch(XDocument project, JsonElement assetsRoot)
    {
        var assetFrameworks = ReadAssetFrameworks(assetsRoot);
        if (assetFrameworks.Count > 0)
        {
            var wanted = project.Descendants()
                .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks" && IsUnconditional(e))
                .SelectMany(e => e.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Where(t => !t.Contains('$'));
            foreach (var framework in wanted)
            {
                if (!assetFrameworks.Any(a => a.StartsWith(framework, StringComparison.OrdinalIgnoreCase)))
                {
                    return $"framework {framework} added";
                }
            }
        }

        var assetProjects = ReadAssetProjectReferenceNames(assetsRoot);
        foreach (var reference in project.Descendants().Where(e => e.Name.LocalName == "ProjectReference" && IsUnconditional(e)))
        {
            var include = (string?)reference.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include) || include.IndexOfAny(['$', '@', '*', ';']) >= 0)
            {
                continue;
            }

            var fileName = Path.GetFileName(include.Replace('\\', '/'));
            if (!assetProjects.Contains(fileName))
            {
                return $"project reference {fileName} added";
            }
        }

        return null;
    }

    // Why the restore recorded in project.assets.json no longer describes the project, or null when it still does.
    // A csproj that cannot be parsed is skipped (null); an assets file that cannot be parsed counts as unrestored.
    private static string? FindRestoreInputMismatch(string csproj, string? packagesPropsPath, string assetsPath)
    {
        XDocument project;
        try
        {
            project = XDocument.Load(csproj);
        }
        catch (XmlException)
        {
            return null;
        }

        JsonDocument assets;
        try
        {
            using var stream = File.OpenRead(assetsPath);
            assets = JsonDocument.Parse(stream);
        }
        catch (JsonException)
        {
            return "assets file unreadable";
        }

        using (assets)
        {
            var root = assets.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "assets file unreadable";
            }

            return FindPackageMismatch(project, ReadCentralVersions(packagesPropsPath), ReadAssetPackageRanges(root))
                ?? FindFrameworkOrProjectReferenceMismatch(project, root);
        }
    }

    // Flags a project when obj/project.assets.json is missing, or when it is older than the csproj / nearest
    // Directory.Packages.props AND its recorded restore inputs (package ids and versions, target frameworks, project
    // references) no longer match the project. The timestamp alone is not enough: a restore whose inputs are
    // unchanged is a no-op that never rewrites project.assets.json, so after a csproj touch without an input change
    // (checkout, non-package edit) no Build can ever make the assets file newer. Cheap in the common case: an assets
    // file newer than both files is accepted without being parsed. A project whose files cannot be read is skipped.
    // State is a signature of the flagged condition (file times + reason) used to avoid repeating advice.
    private static List<(string Name, string Path, string State)> AnalyzeRestoreState(IEnumerable<string> csprojPaths)
    {
        var flagged = new List<(string Name, string Path, string State)>();
        foreach (var csproj in csprojPaths)
        {
            try
            {
                if (!File.Exists(csproj) || Path.GetDirectoryName(csproj) is not { } projectDir)
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(csproj);
                var assets = Path.Combine(projectDir, "obj", "project.assets.json");
                if (!File.Exists(assets))
                {
                    flagged.Add((name, csproj, "assets missing"));
                    continue;
                }

                var assetsTime = File.GetLastWriteTimeUtc(assets);
                var csprojTime = File.GetLastWriteTimeUtc(csproj);
                string? propsPath = null;
                for (var dir = new DirectoryInfo(projectDir); dir is not null && propsPath is null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, "Directory.Packages.props");
                    propsPath = File.Exists(candidate) ? candidate : null;
                }

                var propsTime = propsPath is null ? DateTime.MinValue : File.GetLastWriteTimeUtc(propsPath);
                if (assetsTime >= csprojTime && assetsTime >= propsTime)
                {
                    continue;
                }

                var mismatch = FindRestoreInputMismatch(csproj, propsPath, assets);
                if (mismatch is not null)
                {
                    flagged.Add((name, csproj, $"{csprojTime.Ticks}|{propsTime.Ticks}|{assetsTime.Ticks}|{mismatch}"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Unreadable project: no hint for it rather than failing the load response.
            }
        }

        return flagged;
    }

    // Unrestored projects not yet advised about in their current state. A project already advised in an identical
    // state (same file times and reason) is dropped: the advised Build ran and changed nothing, so repeating it
    // cannot change the outcome. Entries for projects no longer flagged are forgotten, so a later change to the
    // project (new times) is advised again.
    internal static IReadOnlyList<string> FindUnadvisedUnrestoredProjects(IEnumerable<string> csprojPaths, ConcurrentDictionary<string, string> advised)
    {
        var paths = csprojPaths.ToList();
        var flagged = AnalyzeRestoreState(paths);
        var flaggedPaths = flagged.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(p => !flaggedPaths.Contains(p)))
        {
            advised.TryRemove(path, out _);
        }

        var fresh = new List<string>();
        foreach (var (name, path, state) in flagged)
        {
            if (advised.TryGetValue(path, out var previous) && previous == state)
            {
                continue;
            }

            advised[path] = state;
            fresh.Add(name);
        }

        return fresh;
    }

    private static readonly Regex PlainVersionRegex = new(@"^(\d+(?:\.\d+){0,3})(-[0-9A-Za-z.\-]+)?$", RegexOptions.Compiled);

    // Restore state already advised about, keyed by csproj path -> state signature (mtimes + mismatch reason).
    // Deliberate deviation from the stateless hint helpers: a project that is still flagged in an identical state
    // after the advised Build (a restore that rewrites nothing) must not make every LoadSolution repeat the
    // same advice. Process-wide because the workspace manager is a singleton per server process.
    private static readonly ConcurrentDictionary<string, string> AdvisedRestoreStates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SentinelCallToolResult<object>> LoadSolution(ToolCallReason reason, string solutionPath, string? baseRepoDir = null,
        bool forceReload = false, int timeoutSeconds = 30, CancellationToken cancellationToken = default)
    {
        try
        {
            if (timeoutSeconds < 1 || timeoutSeconds > 3600)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"LoadSolution: timeoutSeconds must be between 1 and 3600 (default 30); got {timeoutSeconds}." + " " + ToolCallHint.Next("LoadSolution", "with timeoutSeconds between 1 and 3600.", ("solutionPath", solutionPath)))
                };
            }
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            if (_workspaceManager.SolutionLoadStatus == SolutionLoadStatus.Loading
                && await _workspaceManager.WaitForLoadAsync(timeout, cancellationToken) == SolutionLoadStatus.Loading)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.SolutionLoadTimeout, "LoadSolution timed out: " + SolutionNotLoadedMessage.LoadWaitTimedOut(timeout))
                };
            }
            var wasAlreadyLoaded = IsAlreadyLoadedPath(solutionPath, out var currentPath);
            if (!forceReload && wasAlreadyLoaded)
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = $"Solution '{currentPath}' is already loaded - no changes made. Pass forceReload:true to discard in-memory state and re-open it from disk."
                };
            }

            await _workspaceManager.LoadSolutionAsync(solutionPath, baseRepoDir, timeout, cancellationToken);
            var solutionRoot = _workspaceManager.GetSolutionRoot();
            if (solutionRoot != null)
            {
                var projectFilePaths = (await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken))?.Projects
                    .Select(p => p.FilePath).OfType<string>().ToList() ?? new List<string>();
                var isReload = forceReload && wasAlreadyLoaded;
                var verb = isReload ? "reloaded" : "loaded";
                var reloadNote = isReload
                    ? " In-memory analysis state was discarded and rebuilt from what's on disk now - any edits made by tools since the last load are reflected; anything else is unaffected."
                    : "";
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = $"Solution {verb}: {solutionPath}.{reloadNote}{BuildPostLoadHint(solutionRoot, projectFilePaths)}"
                };
            }
            else
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception, $"LoadSolution failed: Workspace root is null after loading '{solutionPath}'.")
                };
            }
        }
        catch (SolutionLoadTimeoutException ex)
        {
            _logger.LogError(ex, "LoadSolution timed out for '{SolutionPath}'", solutionPath);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = new ResultError(ex.ErrorCode, $"LoadSolution '{solutionPath}' timed out: {ex.Message}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadSolution failed for '{SolutionPath}'", solutionPath);
            var codeAndMessage = ex is ToolException toolEx
                ? (toolEx.ErrorCode, toolEx.Message)
                : (ToolErrorCode.Exception, $"failed unexpectedly ({ex.GetType().Name}): {ex.Message}");
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = new ResultError(codeAndMessage.Item1, $"LoadSolution '{solutionPath}' {codeAndMessage.Item2}" + (codeAndMessage.Item1 == ToolErrorCode.NotFound ? " Use ListWorkspaceSolutions to find the solution file, then call LoadSolution again with its path." : " Check the server log for MSBuild errors, then retry LoadSolution (add forceReload: true if a solution is already loaded)."))
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> CreateProject(ToolCallReason reason, string projectName, string projectType = "console",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _solutionStructureEngine.CreateProjectAsync(projectName, projectType, cancellationToken);
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateProject failed for '{ProjectName}'", projectName);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "CreateProject")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> SplitProjectByFolder(ToolCallReason reason, string sourceProjectName, string folderName,
        string targetProjectName, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _solutionStructureEngine.SplitProjectByFolderAsync(sourceProjectName, folderName, targetProjectName, cancellationToken);
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SplitProjectByFolder failed for '{SourceProjectName}'", sourceProjectName);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SplitProjectByFolder")
            };
        }
    }

    public async Task<SentinelCallToolResult<List<ProjectFrameworkSummary>, ResultError>> ListProjectFrameworkTargets(ToolCallReason reason, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _solutionStructureEngine.GetProjectFrameworkSummaryAsync(cancellationToken);
            return new SentinelCallToolResult<List<ProjectFrameworkSummary>, ResultError>
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetProjectFrameworkSummary failed");
            return new SentinelCallToolResult<List<ProjectFrameworkSummary>, ResultError>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetProjectFrameworkSummary")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> DeleteUnusedSymbol(ToolCallReason reason, string filePath,
        string projectName = "", string docCommentId = "", string? symbolName = null,
        string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null,
        int line = 0, int column = 0, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        async Task<SentinelCallToolResult<object>> ApplyAndRespondAsync(DocumentEditResult result)
        {
            var changes = new Dictionary<FilePathWrapper, string>
            {
                [filePathResolved] = result.UpdatedText
            };
            var apply = await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount: 3, validateChanges: true, cancellationToken: cancellationToken);
            if (apply.IsError)
            {
                var reason = apply.ValidationResult is not null ? $"introduces new compiler errors - change not applied. Fix diagnostics and retry: {apply.ValidationResult.Diagnostics.ToJson()}" : $"failed to write to disk: {apply.Summary}";
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception, $"DeleteUnusedSymbol {reason}")
                };
            }

            var changeId = Guid.NewGuid().ToString("n")[..8];
            await OperationBlobHelper.WriteBlobForApplyAsync(_logger, _workspaceManager, "safe_delete_unused_symbol", apply, changeId, cancellationToken);
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = new AppliedChangeSummary(changeId, [filePathResolved], $"Deleted unused symbol in {Path.GetFileName(filePathResolved)}.", false, Findings: apply.Findings)
            };
        }

        try
        {
            if (!string.IsNullOrEmpty(docCommentId) && !string.IsNullOrEmpty(projectName))
            {
                SymbolResolution resolution = await _workspaceManager.ResolveFromWireAsync(projectName, docCommentId, cancellationToken);
                if (!resolution.Resolved)
                {
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(resolution.Error!.Code == EngineErrorCode.SymbolNotResolved ? ToolErrorCode.NotFound : ToolErrorCode.Exception, resolution.Error!.Message)
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

            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "DeleteUnusedSymbol requires one of: (projectName, docCommentId) for handle-based resolution, (symbolName, optionally with contextSnippet/lineBefore/lineAfter) for name-based resolution, or (line, column) for legacy line/column-based resolution.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteUnusedSymbol failed for '{FilePathWrapper}' at {Line}:{Column} or handle {ProjectName}/{DocCommentId}", filePathResolved, line, column, projectName, docCommentId);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "DeleteUnusedSymbol")
            };
        }
    }
}
