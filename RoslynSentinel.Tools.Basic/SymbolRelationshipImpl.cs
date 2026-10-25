using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

public class SymbolRelationshipImpl
{
    private readonly DiscoveryEngine _discoveryEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger _logger;
    /// <summary>
    /// <see cref = "FindUsagesSearchKind"/> kinds resolve to symbol-relationship facts that are
    /// meaningful for a member (method/property/field/event), not just a type -> <c>objectCreations</c>
    /// specifically is the exception: it text-matches "new TypeName(...)" sites and is structurally
    /// incapable of ever matching a member.
    /// </summary>
    private static readonly HashSet<string> MemberSymbolKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Method",
        "Property",
        "Field",
        "Event"
    };
    public SymbolRelationshipImpl(DiscoveryEngine discoveryEngine, SymbolNavigationEngine symbolNavigationEngine, ISolutionProvider workspaceManager, ILogger logger)
    {
        _discoveryEngine = discoveryEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    private async Task<List<object>> RunRelationshipQueryAsync(FindUsagesSearchKind searchKind, string name, string? projectName, string filePath, bool sortByFrequency, bool includeTests, string? hasAttribute, List<string> hiddenTestProjects, CancellationToken cancellationToken)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        // attributeUsages with includeTests false and no project/file scope is a view filter: scan every
        // document, then drop test-project hits here and record their project names in hiddenTestProjects.
        var hideTestHits = searchKind == FindUsagesSearchKind.attributeUsages && !includeTests && string.IsNullOrEmpty(projectName) && string.IsNullOrEmpty(filePath);
        object result = searchKind switch
        {
            FindUsagesSearchKind.implementorsOf => await _symbolNavigationEngine.FindAllImplementationsAsync(name, projectName, cancellationToken),
            FindUsagesSearchKind.attributeUsages => await _discoveryEngine.FindAttributeUsagesAsync(name, projectName, filePathResolved, includeTests: includeTests || hideTestHits, hasAttribute: hasAttribute, cancellationToken: cancellationToken),
            FindUsagesSearchKind.objectCreations => await _discoveryEngine.FindObjectCreationSitesAsync(name, filePathResolved, projectName, sortByFrequency, cancellationToken),
            FindUsagesSearchKind.extensionsFor => await _symbolNavigationEngine.FindExtensionMethodsAsync(name, projectName, cancellationToken),
            FindUsagesSearchKind.typesWithAttribute => await _discoveryEngine.FindTypesByAttributeAsync(name, includeTests, projectName, cancellationToken),
            FindUsagesSearchKind.methodsByReturnType => await _discoveryEngine.FindMethodsByReturnTypeAsync(name, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(searchKind), searchKind, "Unhandled searchKind.")
        };
        if (hideTestHits)
        {
            var testDocProjects = await BuildTestDocumentMapAsync(filePathResolved.Absolute, cancellationToken);
            result = ExcludeTestHits((List<AttributeUsageSite>)result, s => s.FilePath.Absolute, testDocProjects, hiddenTestProjects);
        }
        // Every FindUsagesSearchKind backing method returns some IEnumerable<T> -> normalize to
        // List<object> so broaden-on-empty can report a count and label results uniformly
        // regardless of which kind produced them.
        return ((System.Collections.IEnumerable)result).Cast<object>().ToList();
    }

    public async Task<SentinelCallToolResult<object>> QuerySymbolRelationships(ToolCallReason reason, string name, FindUsagesSearchKind searchKind, string? projectName = null, string? filePath = null, bool sortByFrequency = false, bool includeTests = true, string? hasAttribute = null, CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
            if (searchKind == FindUsagesSearchKind.objectCreations)
            {
                var resolved = await _symbolNavigationEngine.LocateSymbolAsync(name, "any", projectName: projectName, cancellationToken: cancellationToken);
                if (resolved.Count > 0 && resolved.All(s => MemberSymbolKinds.Contains(s.SymbolKind)))
                {
                    var kindsFound = string.Join(", ", resolved.Select(s => s.SymbolKind).Distinct());
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"'{name}' resolves to a {kindsFound} ({resolved.Count} declaration(s) found), not a type - " + "objectCreations only matches 'new TypeName(...)' expressions and is structurally incapable of " + $"returning anything for a member name. Use {ToolCallHint.Call("Search", ("mode", "callers"), ("query", name))} " + $"to find call sites, or {ToolCallHint.Call("Search", ("mode", "implementations"), ("query", name))} for overrides.")
                    };
                }
            }

            var hiddenTestProjects = new List<string>();
            var results = await RunRelationshipQueryAsync(searchKind, name, projectName, filePathResolved, sortByFrequency, includeTests, hasAttribute, hiddenTestProjects, cancellationToken);
            if (results.Count > 0)
            {
                var listResult = await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(results, _workspaceManager.GetSolutionRoot(), nameof(FindUsagesSearchKind), ResultWrapperType.SymbolRelationshipResultList, totalRecords: results.Count, cancellationToken: cancellationToken);
                if (hiddenTestProjects.Count == 0)
                {
                    return listResult;
                }
                return listResult with { StatusMessage = HiddenTestHitsHint(hiddenTestProjects).TrimStart() };
            }

            if (!string.IsNullOrWhiteSpace(hasAttribute))
            {
                // Broaden-on-empty cannot honor the hasAttribute filter, so skip it.
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = results,
                    TotalRecords = 0,
                    StatusMessage = $"0 results for '{searchKind}' with hasAttribute '{hasAttribute}'. Broaden-on-empty was skipped because it cannot apply the hasAttribute filter; drop hasAttribute to broaden."
                };
            }

            // Broaden-on-empty: the targeted kind genuinely ran and came back empty (and, for
            // objectCreations, the semantic guard above didn't already reject it). Re-run the
            // other 5 kinds so a real "found under a different relationship kind" doesn't get
            // silently missed just because the caller guessed the wrong one.
            var otherKinds = Enum.GetValues<FindUsagesSearchKind>().Where(k => k != searchKind).ToList();
            var broadened = new Dictionary<string, List<object>>();
            foreach (var otherKind in otherKinds)
            {
                try
                {
                    var otherResults = await RunRelationshipQueryAsync(otherKind, name, projectName, filePathResolved, sortByFrequency, includeTests, null, new List<string>(), cancellationToken);
                    if (otherResults.Count > 0)
                    {
                        broadened[otherKind.ToString()] = otherResults;
                    }
                }
                catch
                {
                    // A kind that doesn't apply to this name (e.g. throws resolving as a type)
                    // is just another empty result for broaden-on-empty purposes -> skip it.
                }
            }

            if (broadened.Count == 0)
            {
                // Only claim a global miss when the whole solution was searched; a narrowed scope
                // (test projects excluded, or a single project) must say what was actually covered.
                var scopeNote = includeTests && string.IsNullOrEmpty(projectName) ? "This is a trustworthy 'not found anywhere' signal, not an error." : "Searched scope: " + (string.IsNullOrEmpty(projectName) ? "non-test projects only" : $"project '{projectName}'" + (includeTests ? string.Empty : " (non-test)")) + (includeTests ? string.Empty : "; pass includeTests: true to include test projects") + ". This is NOT a 'not found anywhere' signal." + HiddenTestHitsHint(hiddenTestProjects) + "";
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = results,
                    WarningDetails = $"0 results for '{searchKind}'. Broadened search across all relationship kinds - " + "nothing found under any kind. " + scopeNote
                };
            }

            var totalFound = broadened.Sum(kv => kv.Value.Count);
            var summary = string.Join("; ", broadened.Select(kv => $"{kv.Value.Count} under '{kv.Key}'"));
            var broadenedResult = await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(broadened, _workspaceManager.GetSolutionRoot(), nameof(FindUsagesSearchKind), ResultWrapperType.BroadenedSymbolRelationshipResults, totalRecords: totalFound, cancellationToken: cancellationToken);
            return broadenedResult with
            {
                WarningDetails = $"0 results for '{searchKind}'. Broadened search across all relationship kinds - " + $"found {totalFound} result(s): {summary}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuerySymbolRelationships ({Kind}) failed for '{Name}'", searchKind, name);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "QuerySymbolRelationships")
            };
        }
    }

    public async Task<SentinelCallToolResult<BestInsertionResult, ResultError>> GetBestInsertionPoint(ToolCallReason reason, string filePath, string containerName, InsertionMemberKind memberKind, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var result = await _discoveryEngine.FindBestInsertionPointAsync(filePathResolved, containerName, memberKind.ToString(), cancellationToken: cancellationToken);
            return new SentinelCallToolResult<BestInsertionResult, ResultError>
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetBestInsertionPoint failed for '{ContainerName}' in '{FilePathWrapper}'", containerName, filePathResolved);
            return new SentinelCallToolResult<BestInsertionResult, ResultError>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetBestInsertionPoint")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> PreviewRenameImpact(ToolCallReason reason, string? filePath = null, string? symbolName = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, string? docCommentId = null, string? projectName = null, CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var result = await _discoveryEngine.PreviewRenameImpactAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter, docCommentId, projectName, cancellationToken);
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreviewRenameImpact failed for '{SymbolName}' in '{FilePathWrapper}'", symbolName, filePathResolved);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "PreviewRenameImpact")
            };
        }
    }

    // Maps every test-project document path (OrdinalIgnoreCase) to its project name. An explicit filePath that
    // lives in a test project returns an empty map: naming a test project always includes it.
    private async Task<Dictionary<string, string>> BuildTestDocumentMapAsync(string resolvedAbsolutePath, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // READCHOKEPOINT-CAST: same cast as SymbolNavigationImpl.LocateSymbolTool; ISolutionProvider has no GetSolutionAsync.
        var solution = await ((IWorkspaceReader)_workspaceManager).GetSolutionAsync(ReadSource.Committed, cancellationToken);
        foreach (var project in solution.Projects)
        {
            if (!TestProjectDetector.IsTestProject(project))
            {
                continue;
            }
            foreach (var document in project.Documents)
            {
                if (!string.IsNullOrEmpty(document.FilePath))
                {
                    map[document.FilePath] = project.Name;
                }
            }
        }
        if (!string.IsNullOrEmpty(resolvedAbsolutePath) && map.ContainsKey(resolvedAbsolutePath))
        {
            map.Clear();
        }
        return map;
    }

    // Drops hits whose file is a test-project document, recording the project name of each dropped hit.
    private static List<T> ExcludeTestHits<T>(List<T> items, Func<T, string?> filePathOf, IReadOnlyDictionary<string, string> testDocProjects, List<string> droppedProjectNames)
    {
        var kept = new List<T>(items.Count);
        foreach (var item in items)
        {
            string? path = filePathOf(item);
            if (path != null && testDocProjects.TryGetValue(path, out var projectName))
            {
                droppedProjectNames.Add(projectName);
                continue;
            }
            kept.Add(item);
        }
        return kept;
    }

    private static string DroppedTestHitsNote(List<string> droppedProjectNames)
    {
        if (droppedProjectNames.Count == 0)
        {
            return string.Empty;
        }
        int projects = droppedProjectNames.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return $" {droppedProjectNames.Count} reference(s) in {projects} test project(s) excluded because includeTests is false; pass includeTests: true (needed for blast-radius checks).";
    }

    // Hint for attributeUsages hits hidden by includeTests: false. Leading space so it appends to a status message.
    private static string HiddenTestHitsHint(List<string> hiddenTestProjects)
    {
        if (hiddenTestProjects.Count == 0)
        {
            return string.Empty;
        }
        var perProject = hiddenTestProjects.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Select(g => $"{g.Key} {g.Count()}");
        return $" +{hiddenTestProjects.Count} more in test projects ({string.Join(", ", perProject)}), hidden because includeTests is false - rerun with includeTests: true to include them.";
    }

    public async Task<SentinelCallToolResult<object>> FindReferences(ToolCallReason reason, string symbolName, FindReferencesKind kind, string? filePath = null, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, bool includeTests = true, CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
            var testDocProjects = includeTests
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : await BuildTestDocumentMapAsync(filePathResolved.Absolute, cancellationToken);
            var droppedProjectNames = new List<string>();
            if (kind == FindReferencesKind.callers)
            {
                var result = await _symbolNavigationEngine.FindCallersAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                result = ExcludeTestHits(result, c => c.FilePath, testDocProjects, droppedProjectNames);
                var summary = SummarizeListResult.Build(result, c => c.FilePath);
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = new
                    {
                        callers = result,
                        summary
                    },
                    StatusMessage = summary.ToSummaryMessage("caller") + DroppedTestHitsNote(droppedProjectNames),
                    ListSummary = summary
                };
            }

            if (kind == FindReferencesKind.implementations)
            {
                var result = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
                result = ExcludeTestHits(result, i => i.FilePath, testDocProjects, droppedProjectNames);
                var summary = SummarizeListResult.Build(result, i => i.FilePath ?? "(unknown)");
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = new
                    {
                        implementations = result,
                        summary
                    },
                    StatusMessage = summary.ToSummaryMessage("implementation") + DroppedTestHitsNote(droppedProjectNames),
                    ListSummary = summary
                };
            }

            if (kind == FindReferencesKind.all)
            {
                var callers = await _symbolNavigationEngine.FindCallersAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
                // The implementations half is structurally inapplicable for a concrete target (sealed type,
                // struct, non-virtual member): that is "nothing to implement", not a failure of the whole
                // all-query, so the callers half must still be returned.
                var implementationsSkipped = false;
                List<ImplementationInfo> implementations;
                try
                {
                    implementations = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
                }
                catch (ToolTargetIneligibleException)
                {
                    implementations = [];
                    implementationsSkipped = true;
                }

                callers = ExcludeTestHits(callers, c => c.FilePath, testDocProjects, droppedProjectNames);
                implementations = ExcludeTestHits(implementations, i => i.FilePath, testDocProjects, droppedProjectNames);
                var combinedPaths = callers.Select(c => c.FilePath).Concat(implementations.Select(i => i.FilePath ?? "(unknown)")).ToList();
                var summary = SummarizeListResult.Build(combinedPaths, p => p);
                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = new
                    {
                        callers,
                        implementations,
                        summary
                    },
                    StatusMessage = summary.ToSummaryMessage("hit") + DroppedTestHitsNote(droppedProjectNames)
                        + (implementationsSkipped ? " Implementations skipped: the target cannot have implementations (sealed/static type or non-virtual member)." : string.Empty),
                    ListSummary = summary
                };
            }

            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled kind '{kind}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindReferences ({Kind}) failed for '{SymbolName}'", kind, symbolName);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "FindReferences")
            };
        }
    }
}