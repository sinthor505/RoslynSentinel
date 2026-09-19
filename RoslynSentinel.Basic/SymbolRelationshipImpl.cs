using System.ComponentModel;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Basic;

public class SymbolRelationshipImpl
{
    private readonly DiscoveryEngine _discoveryEngine;
    private readonly SemanticSearchEngine _semanticSearchEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger _logger;

    /// <summary>
    /// <see cref="FindUsagesSearchKind"/> kinds resolve to symbol-relationship facts that are
    /// meaningful for a member (method/property/field/event), not just a type -> <c>objectCreations</c>
    /// specifically is the exception: it text-matches "new TypeName(...)" sites and is structurally
    /// incapable of ever matching a member.
    /// </summary>
    private static readonly HashSet<string> MemberSymbolKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Method", "Property", "Field", "Event"
    };

    public SymbolRelationshipImpl(
        DiscoveryEngine discoveryEngine,
        SemanticSearchEngine semanticSearchEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        ISolutionProvider workspaceManager,
        ILogger logger)
    {
        _discoveryEngine = discoveryEngine;
        _semanticSearchEngine = semanticSearchEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    private async Task<List<object>> RunRelationshipQueryAsync(
        FindUsagesSearchKind searchKind, string name, string? projectName, FilePathWrapper filepath, bool sortByFrequency, CancellationToken cancellationToken)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        object result = searchKind switch
        {
            FindUsagesSearchKind.implementorsOf => await _symbolNavigationEngine.FindAllImplementationsAsync(name, projectName, cancellationToken),
            FindUsagesSearchKind.attributeUsages => await _discoveryEngine.FindAttributeUsagesAsync(name, projectName, filePathResolved, cancellationToken),
            FindUsagesSearchKind.objectCreations => await _discoveryEngine.FindObjectCreationSitesAsync(name, filePathResolved, projectName, sortByFrequency, cancellationToken),
            FindUsagesSearchKind.extensionsFor => await _symbolNavigationEngine.FindExtensionMethodsAsync(name, projectName, cancellationToken),
            FindUsagesSearchKind.typesWithAttribute => await _semanticSearchEngine.FindTypesByAttributeAsync(name, cancellationToken),
            FindUsagesSearchKind.methodsByReturnType => await _semanticSearchEngine.FindMethodsByReturnTypeAsync(name, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(searchKind), searchKind, "Unhandled searchKind.")
        };

        // Every FindUsagesSearchKind backing method returns some IEnumerable<T> -> normalize to
        // List<object> so broaden-on-empty can report a count and label results uniformly
        // regardless of which kind produced them.
        return ((System.Collections.IEnumerable)result).Cast<object>().ToList();
    }

    public async Task<SentinelCallToolResult<object>> QuerySymbolRelationships(
        ToolCallReason reason,
        string name,
        FindUsagesSearchKind searchKind,
        string? projectName = null,
        string? filepath = null,
        bool sortByFrequency = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);

            if (searchKind == FindUsagesSearchKind.objectCreations)
            {
                var resolved = await _symbolNavigationEngine.LocateSymbolAsync(name, "any", projectName: projectName, cancellationToken: cancellationToken);
                if (resolved.Count > 0 && resolved.All(s => MemberSymbolKinds.Contains(s.SymbolKind)))
                {
                    var kindsFound = string.Join(", ", resolved.Select(s => s.SymbolKind).Distinct());
                    return new SentinelCallToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument,
                            $"'{name}' resolves to a {kindsFound} ({resolved.Count} declaration(s) found), not a type - " +
                            "objectCreations only matches 'new TypeName(...)' expressions and is structurally incapable of " +
                            $"returning anything for a member name. Use FindReferences(symbolName: \"{name}\", kind: callers) " +
                            "to find call sites, or kind: implementations for overrides.")
                    };
                }
            }

            var results = await RunRelationshipQueryAsync(searchKind, name, projectName, filePathResolved, sortByFrequency, cancellationToken);
            if (results.Count > 0)
            {
                return await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                    results, _workspaceManager.GetSolutionRoot(), nameof(FindUsagesSearchKind), ResultWrapperType.SymbolRelationshipResultList,
                    totalRecords: results.Count, cancellationToken: cancellationToken);
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
                    var otherResults = await RunRelationshipQueryAsync(otherKind, name, projectName, filePathResolved, sortByFrequency, cancellationToken);
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
                return new SentinelCallToolResult<object>
                {
                    Success = true,
                    Data = results,
                    Warning = $"0 results for '{searchKind}'. Broadened search across all relationship kinds - " +
                        "nothing found under any kind. This is a trustworthy 'not found anywhere' signal, not an error."
                };
            }

            var totalFound = broadened.Sum(kv => kv.Value.Count);
            var summary = string.Join("; ", broadened.Select(kv => $"{kv.Value.Count} under '{kv.Key}'"));
            var broadenedResult = await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                broadened, _workspaceManager.GetSolutionRoot(), nameof(FindUsagesSearchKind), ResultWrapperType.BroadenedSymbolRelationshipResults,
                totalRecords: totalFound, cancellationToken: cancellationToken);
            return broadenedResult with
            {
                Warning = $"0 results for '{searchKind}'. Broadened search across all relationship kinds - " +
                    $"found {totalFound} result(s): {summary}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuerySymbolRelationships ({Kind}) failed for '{Name}'", searchKind, name);
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "QuerySymbolRelationships")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> GetBestInsertionPoint(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string containerName,
        InsertionMemberKind memberKind,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _discoveryEngine.FindBestInsertionPointAsync(filePathResolved, containerName, memberKind.ToString());
            return new SentinelCallToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetBestInsertionPoint failed for '{ContainerName}' in '{FilePathWrapper}'", containerName, filePathResolved);
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetBestInsertionPoint")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> PreviewRenameImpact(
        ToolCallReason reason,
        string? filepath = null,
        string? symbolName = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        string? docCommentId = null,
        string? projectName = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath ?? string.Empty, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _discoveryEngine.PreviewRenameImpactAsync(
                filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter, docCommentId, projectName, cancellationToken);
            return new SentinelCallToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreviewRenameImpact failed for '{SymbolName}' in '{FilePathWrapper}'", symbolName, filePathResolved);
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "PreviewRenameImpact")
            };
        }
    }

    public async Task<SentinelCallToolResult<object>> FindReferences(
        ToolCallReason reason,
        string symbolName,
        FindReferencesKind kind,
        string? filepath = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePathResolved = _workspaceManager.SetFilePath(filepath);

            if (kind == FindReferencesKind.callers)
            {
                var result = await _symbolNavigationEngine.FindCallersAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                return new SentinelCallToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == FindReferencesKind.implementations)
            {
                var result = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter);
                return new SentinelCallToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == FindReferencesKind.all)
            {
                var callers = await _symbolNavigationEngine.FindCallersAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter);
                var implementations = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePathResolved, symbolName, contextSnippet, lineBefore, lineAfter);
                return new SentinelCallToolResult<object>
                {
                    Success = true,
                    Data = new { callers, implementations }
                };
            }
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled kind '{kind}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindReferences ({Kind}) failed for '{SymbolName}'", kind, symbolName);
            return new SentinelCallToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "FindReferences")
            };
        }
    }
}
