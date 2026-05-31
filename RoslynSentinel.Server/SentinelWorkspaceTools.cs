using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

/// <summary>Structural outline entry returned by get_file_outline.</summary>
public record OutlineItem(string Kind, string Name, string? Container, int StartLine, int EndLine);

/// <summary>Single text-search hit returned by search_solution_text.</summary>
public record TextSearchMatch(string FilePath, int Line, int Column, string Preview);

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
    [Description("""
        Query or update analysis/refactoring feature flags.
        action="list"   — returns all features and their current enabled status. names and enabled are ignored.
        action="get"    — returns enabled status of specific features by name. Requires names.
        action="update" — batch-updates the enabled status of one or more features. Requires enabled
                          as a list of { Key: featureName, Value: true|false } pairs.
        """)]
    public object Features(
        string action,
        List<string>?                  names   = null,
        List<KeyValuePair<string,bool>>? enabled = null)
    {
        return action switch
        {
            "list"   => (object)_config.GetFeatureStatuses(),
            "get"    => _config.GetFeatureStatuses(names),
            "update" => (object)UpdateFeaturesInternal(enabled ?? []),
            _ => throw new ArgumentException(
                $"Unknown action '{action}'. Valid: list, get, update.", nameof(action))
        };
    }

    private string UpdateFeaturesInternal(List<KeyValuePair<string, bool>> updates)
    {
        _config.BatchUpdateFeatureStatus(updates);
        return $"Updated {updates.Count} features.";
    }

    [McpServerTool]
    [Description("Lists projects, files, or dependencies in the loaded solution. kind: projects (all projects in the solution), files (all source files in a project, requires projectName), dependencies (NuGet and project references for a project, requires projectName).")]
    public async Task<object> List(string kind, string? projectName = null)
    {
        if (kind == "projects")
        {
            var solution = await _workspaceManager.GetBranchedSolutionAsync();
            return solution.Projects.Select(p => (object)new { p.Name, p.FilePath }).ToList();
        }
        if (kind == "files")
        {
            if (string.IsNullOrEmpty(projectName))
            {
                throw new ArgumentException("projectName is required when kind=files.");
            }
            try
            {
                var solution = await _workspaceManager.GetBranchedSolutionAsync();
                var project = solution.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
                if (project == null)
                {
                    throw new InvalidOperationException($"Project '{projectName}' not found.");
                }
                return project.Documents.Select(d => d.FilePath ?? d.Name).ToList<object>();
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "List files unexpected exception for project '{ProjectName}'", projectName);
                throw new InvalidOperationException($"List files for project '{projectName}' failed: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }
        if (kind == "dependencies")
        {
            if (string.IsNullOrEmpty(projectName))
            {
                throw new ArgumentException("projectName is required when kind=dependencies.");
            }
            return await _dependencyEngine.GetProjectDependenciesAsync(projectName);
        }
        throw new ArgumentException($"Unknown kind '{kind}'. Valid values: projects, files, dependencies.");
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
    [Description("""
        Applies or validates a proposed change set.
        format: files (dictionary of filePath→newContent) or diff (unified diff string for one file).
        action: apply (write to disk) or validate (dry-run compiler diagnostics).
        For format=files: supply the changes dict. retryCount controls retry on file locks (apply only, default 3).
        For format=diff: supply filePath and unifiedDiff.
        """)]
    public async Task<object> ProposedChange(
        string format,
        string action,
        Dictionary<string, string>? changes = null,
        string? filePath = null,
        string? unifiedDiff = null,
        int retryCount = 3)
    {
        if (format == "files")
        {
            if (changes == null)
            {
                throw new ArgumentException("changes is required when format=files.");
            }
            if (action == "apply")
            {
                return await _workspaceManager.ApplyProposedChangesAsync(changes, retryCount);
            }
            if (action == "validate")
            {
                try
                {
                    return await _validationEngine.ValidateChangesAsync(changes);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProposedChange validate unexpected exception");
                    throw new InvalidOperationException($"ProposedChange validate failed: {ex.GetType().Name}: {ex.Message}", ex);
                }
            }
        }
        else if (format == "diff")
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(unifiedDiff))
            {
                throw new ArgumentException("filePath and unifiedDiff are required when format=diff.");
            }
            if (action == "apply")
            {
                try
                {
                    var solution = await _workspaceManager.GetBranchedSolutionAsync();
                    var document = solution.Projects.SelectMany(p => p.Documents)
                        .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
                    if (document == null)
                    {
                        throw new InvalidOperationException("File not found.");
                    }
                    var oldText = await document.GetTextAsync();
                    var newContent = _diffEngine.ApplyDiff(oldText, unifiedDiff).ToString();
                    var targetPath = document.FilePath ?? filePath;
                    return await _workspaceManager.ApplyProposedChangesAsync(
                        new Dictionary<string, string> { [targetPath] = newContent });
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ProposedChange diff apply unexpected exception for '{FilePath}'", filePath);
                    throw new InvalidOperationException($"ProposedChange diff apply for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                }
            }
            if (action == "validate")
            {
                return await _validationEngine.ValidateDiffAsync(filePath, unifiedDiff);
            }
        }
        throw new ArgumentException($"Unknown format '{format}' or action '{action}'. Valid formats: files, diff. Valid actions: apply, validate.");
    }

    [McpServerTool]
    [Description("Retries committing previously failed file changes using server-side cached content. Token-efficient: no need to re-send file contents.")]
    public async Task<PersistentWorkspaceManager.ApplyChangesResult> RetryFailedChanges(List<string>? specificFiles = null, int retryCount = 3)
    {
        return await _workspaceManager.RetryFailedChangesAsync(specificFiles, retryCount);
    }

    [McpServerTool]
    [Description("""
        Manages a staged change set produced by a refactoring tool.
        action: apply (write to disk), get (return file contents dict), validate (dry-run compiler diagnostics), discard (remove without applying).
        changeId: the id returned by the refactoring tool that staged the changes.
        retryCount: only used for action=apply (default 3).
        """)]
    public async Task<object> StagedChange(string action, string changeId, int retryCount = 3)
    {
        if (action == "apply")
        {
            return await _workspaceManager.ApplyStagedChangesAsync(changeId, retryCount);
        }
        if (action == "get")
        {
            return _workspaceManager.GetStagedChanges(changeId);
        }
        if (action == "validate")
        {
            var stagingChanges = _workspaceManager.GetStagedChanges(changeId);
            return await _validationEngine.ValidateChangesAsync(stagingChanges);
        }
        if (action == "discard")
        {
            var success = _workspaceManager.DiscardStagedChanges(changeId);
            return success ? $"Staged change '{changeId}' discarded." : $"Staged change '{changeId}' not found.";
        }
        throw new ArgumentException($"Unknown action '{action}'. Valid values: apply, get, validate, discard.");
    }

    [McpServerTool]
    [Description("""
        Gets compiler diagnostics for a file, project, or the entire solution.
        scope: file|project|solution.
        scopeName: filePath when scope=file, projectName when scope=project; ignored for scope=solution.
        summarize: when true, groups results by diagnostic ID and returns counts instead of raw details.
        maxDetails: caps the raw detail list (default 50); error/warning counts are always full totals. Only used when summarize=false.
        topN: max groups to return sorted by count descending (default 20). Only used when summarize=true.
        """)]
    public async Task<object> GetDiagnostics(string scope, string? scopeName = null, bool summarize = false, int maxDetails = 50, int topN = 20)
    {
        DiagnosticSummary summary;
        if (scope == "file")
        {
            if (string.IsNullOrEmpty(scopeName))
            {
                throw new ArgumentException("scopeName (filePath) is required when scope=file.");
            }
            summary = await _diagnosticEngine.GetFileDiagnosticsAsync(scopeName);
        }
        else if (scope == "project")
        {
            if (string.IsNullOrEmpty(scopeName))
            {
                throw new ArgumentException("scopeName (projectName) is required when scope=project.");
            }
            summary = await _diagnosticEngine.GetProjectDiagnosticsAsync(scopeName);
        }
        else if (scope == "solution")
        {
            summary = await _diagnosticEngine.GetSolutionDiagnosticsAsync(maxDetails);
        }
        else
        {
            throw new ArgumentException($"Unknown scope '{scope}'. Valid values: file, project, solution.");
        }

        if (!summarize)
        {
            return summary;
        }

        var relevant = summary.Details
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

        return new DiagnosticsSummaryResult(
            TotalIssues: relevant.Count,
            Errors: summary.Errors,
            Warnings: summary.Warnings,
            TopIssues: groups
        );
    }

    [McpServerTool]
    [Description("Safely deletes a symbol (method, property, class) only if it has zero usages in the entire codebase.")]
    public async Task<string> SafeDelete(string filePath, int line, int column)
        => await _structuralRefinementEngine.SafeDeleteSymbolAsync(filePath, line, column);

    [McpServerTool]
    [Description("Creates a new project and adds it to the current solution.")]
    public async Task<string> CreateProject(string projectName, string projectType = "console")
        => await _solutionManagementEngine.CreateProjectAsync(projectName, projectType);

    [McpServerTool]
    [Description("Moves all files under a specific folder from a source project to a new target project, preserving folder structure.")]
    public async Task<string> SplitProjectByFolder(string sourceProjectName, string folderName, string targetProjectName)
        => await _solutionManagementEngine.SplitProjectByFolderAsync(sourceProjectName, folderName, targetProjectName);

    // ── Phase 1 — Low-level fallback tools ──────────────────────────────────

    [McpServerTool]
    [Description("Returns the full source text of a named method. Case-sensitive match with case-insensitive fallback. Returns the first match when names are overloaded.")]
    public async Task<string> GetMethodSource(string filePath, string methodName)
    {
        var solution       = await _workspaceManager.GetBranchedSolutionAsync();
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
            throw new FileNotFoundException(
                $"File not found in solution: {normalizedPath} " +
                $"(existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).");
        }

        var root = await document.GetSyntaxRootAsync()
                   ?? throw new InvalidOperationException("Syntax root not found.");

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .FirstOrDefault(m => m.Identifier.Text.Equals(methodName, StringComparison.Ordinal))
                  ?? root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .FirstOrDefault(m => m.Identifier.Text.Equals(methodName, StringComparison.OrdinalIgnoreCase));

        if (method == null)
        {
            throw new InvalidOperationException($"Method '{methodName}' not found in '{filePath}'.");
        }

        return method.ToFullString();
    }

    [McpServerTool]
    [Description("Returns a structural outline of a file: namespaces, classes, interfaces, methods, and properties with 1-based line ranges. Does not include member bodies.")]
    public async Task<List<OutlineItem>> GetFileOutline(string filePath)
    {
        var solution       = await _workspaceManager.GetBranchedSolutionAsync();
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
            throw new FileNotFoundException(
                $"File not found in solution: {normalizedPath} " +
                $"(existsOnDisk={File.Exists(normalizedPath)}, projectsLoaded={solution.Projects.Count()}).");
        }

        var root = await document.GetSyntaxRootAsync()
                   ?? throw new InvalidOperationException("Syntax root not found.");

        var items = new List<OutlineItem>();

        foreach (var node in root.DescendantNodes())
        {
            string? kind      = null;
            string? name      = null;
            string? container = null;

            switch (node)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    kind = "namespace";
                    name = ns.Name.ToString();
                    break;

                case ClassDeclarationSyntax cls:
                    kind      = "class";
                    name      = cls.Identifier.Text;
                    container = (cls.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString()
                             ?? (cls.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;

                case InterfaceDeclarationSyntax iface:
                    kind      = "interface";
                    name      = iface.Identifier.Text;
                    container = (iface.Parent as BaseNamespaceDeclarationSyntax)?.Name.ToString()
                             ?? (iface.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;

                case MethodDeclarationSyntax method:
                    kind      = "method";
                    name      = method.Identifier.Text;
                    container = (method.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;

                case PropertyDeclarationSyntax prop:
                    kind      = "property";
                    name      = prop.Identifier.Text;
                    container = (prop.Parent as TypeDeclarationSyntax)?.Identifier.Text;
                    break;
            }

            if (kind == null || name == null)
            {
                continue;
            }

            var span = node.GetLocation().GetLineSpan();
            items.Add(new OutlineItem(
                Kind:      kind,
                Name:      name,
                Container: container,
                StartLine: span.StartLinePosition.Line + 1,
                EndLine:   span.EndLinePosition.Line + 1));
        }

        return items;
    }

    [McpServerTool]
    [Description("Searches all source files in the loaded solution for a text pattern or regex. Returns file path, 1-based line and column, and a preview per match. Solution-scoped grep. maxResults caps total matches.")]
    public async Task<List<TextSearchMatch>> SearchSolutionText(
        string  pattern,
        bool    isRegex    = false,
        string? fileGlob   = null,
        int     maxResults = 200)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results  = new List<TextSearchMatch>();

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

                var docPath = document.FilePath ?? "";
                if (!string.IsNullOrEmpty(fileGlob) && !GlobMatchesFileName(docPath, fileGlob))
                {
                    continue;
                }

                var sourceText = (await document.GetTextAsync()).ToString();
                var lines      = sourceText.Split('\n');

                for (int i = 0; i < lines.Length && results.Count < maxResults; i++)
                {
                    var line = lines[i];
                    int col  = -1;

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

                        results.Add(new TextSearchMatch(docPath, i + 1, col + 1, preview));
                    }
                }
            }
        }

        return results;
    }

    private static bool GlobMatchesFileName(string filePath, string glob)
    {
        var fileName     = Path.GetFileName(filePath);
        var regexPattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase);
    }

    // ── Phase 2 — Blob persistence query + undo tools ───────────────────────

    [McpServerTool]
    [Description("Returns a filtered slice of an operation result blob by changeId. filter: 'failures', 'skipped', 'rolledback', 'file:<path>', or null for all items. maxItems caps the returned slice — never dumps the full document.")]
    public async Task<OperationDetailResult> GetOperationDetail(
        string  changeId,
        string? filter   = null,
        int     maxItems = 50)
    {
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        var blobPath     = OperationBlobWriter.FindBlobPath(changeId, solutionRoot);

        if (blobPath == null)
        {
            return new OperationDetailResult
            {
                ChangeId      = changeId,
                BlobName      = "",
                TotalItems    = 0,
                ReturnedItems = 0,
                Filter        = filter,
                Items         = new List<OperationItemRecord>(),
            };
        }

        var json     = await File.ReadAllTextAsync(blobPath);
        var doc      = JsonSerializer.Deserialize<JsonElement>(json);
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
                    "failures"   => allItems.Where(r => r.Outcome == "failed"),
                    "skipped"    => allItems.Where(r => r.Outcome == "skipped"),
                    "rolledback" => allItems.Where(r => r.Outcome == "rolledback"),
                    _            => allItems,
                };
            }
        }

        var slice = filtered.Take(maxItems).ToList();

        return new OperationDetailResult
        {
            ChangeId      = changeId,
            BlobName      = Path.GetFileName(blobPath),
            TotalItems    = allItems.Count,
            ReturnedItems = slice.Count,
            Filter        = filter,
            Items         = slice,
        };
    }

    [McpServerTool]
    [Description("Reverts files from a previously applied batch operation to their pre-apply state, using the forensic blob written at apply time. Only available for operations written by batch-first tools (Phase 4+).")]
    public async Task<string> UndoLastApply(string changeId)
    {
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        var blobPath     = OperationBlobWriter.FindBlobPath(changeId, solutionRoot);

        if (blobPath == null)
        {
            return $"No operation blob found for changeId '{changeId}'. " +
                   "undo_last_apply is only available for operations written by batch-first tools.";
        }

        var json       = await File.ReadAllTextAsync(blobPath);
        var doc        = JsonSerializer.Deserialize<JsonElement>(json);
        var revertable = doc.GetProperty("items")
                           .EnumerateArray()
                           .Select(e => JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!)
                           .Where(r => r.Outcome == "succeeded" && r.BeforeSource != null)
                           .ToList();

        if (revertable.Count == 0)
        {
            return $"No reversible items in blob for changeId '{changeId}' " +
                   "(need Outcome=succeeded with BeforeSource present).";
        }

        var reverted = new List<string>();
        var failed   = new List<string>();

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
        return $"Reverted {reverted.Count} files.{failedPart}";
    }

    // ── Phase 3 — Circuit breaker tools ────────────────────────────────────

    [McpServerTool]
    [Description("Resets the circuit breaker and all failure counters, re-enabling mutating tools. Only call after investigating and addressing the root cause of the failures that tripped the breaker.")]
    public string ResetBreaker()
    {
        _workspaceManager.ResetBreaker();
        return "Circuit breaker reset. Failure counters cleared. Mutating tools re-enabled.";
    }

    [McpServerTool]
    [Description("Returns the current circuit breaker state: severity (ok/caution/halt), trip-condition counters, and thresholds. Use to assess failure health before running large batch operations.")]
    public PersistentWorkspaceManager.BreakerStatusReport GetBreakerStatus()
    {
        return _workspaceManager.GetBreakerStatus();
    }
}
