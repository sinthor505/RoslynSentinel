using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

[McpServerToolType]
public class SentinelSymbolTools
{
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly SemanticSearchEngine _semanticSearchEngine;
    //private readonly MetricsEngine _metricsEngine;
    private readonly InventoryEngine _inventoryEngine;
    // private readonly DeadCodeEngine _deadCodeEngine;
    private readonly AnalysisEngine _analysisEngine;
    // private readonly DocumentationEngine _documentationEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly ProjectStructureEngine _projectStructureEngine;
    // private readonly AsyncSafetyEngine _asyncSafetyEngine;
    // private readonly HealthOrchestrationEngine _healthOrchestrationEngine;
    // private readonly ArchitecturalEngine _architecturalEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    // private readonly DependencyInjectionEngine _dependencyInjectionEngine;
    private readonly DiscoveryEngine _discoveryEngine;
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger<SentinelSymbolTools> _logger;

    public SentinelSymbolTools(
        ImpactAnalyzer impactAnalyzer,
        SemanticSearchEngine semanticSearchEngine,
        // MetricsEngine metricsEngine,
        InventoryEngine inventoryEngine,
        // DeadCodeEngine deadCodeEngine,
        AnalysisEngine analysisEngine,
        // DocumentationEngine documentationEngine,
        DependencyEngine dependencyEngine,
        ProjectStructureEngine projectStructureEngine,
        // AsyncSafetyEngine asyncSafetyEngine,
        // HealthOrchestrationEngine healthOrchestrationEngine,
        // ArchitecturalEngine architecturalEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        // DependencyInjectionEngine dependencyInjectionEngine,
        DiscoveryEngine discoveryEngine,
        ProjectConsistencyEngine projectConsistencyEngine,
        ISolutionProvider workspaceManager,
        SentinelConfiguration config,
        ILogger<SentinelSymbolTools> logger)
    {
        _impactAnalyzer = impactAnalyzer;
        _semanticSearchEngine = semanticSearchEngine;
        // _metricsEngine = metricsEngine;
        _inventoryEngine = inventoryEngine;
        // _deadCodeEngine = deadCodeEngine;
        _analysisEngine = analysisEngine;
        // _documentationEngine = documentationEngine;
        _dependencyEngine = dependencyEngine;
        _projectStructureEngine = projectStructureEngine;
        // _asyncSafetyEngine = asyncSafetyEngine;
        // _healthOrchestrationEngine = healthOrchestrationEngine;
        // _architecturalEngine = architecturalEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        // _dependencyInjectionEngine = dependencyInjectionEngine;
        _discoveryEngine = discoveryEngine;
        _projectConsistencyEngine = projectConsistencyEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool(Name = "LocateSymbol")]
    [Produces(DataTag.SymbolId)]
    [Produces(DataTag.SessionId)]
    [Produces(DataTag.ProjectName)]
    [Description("Locates declaration sites for a symbol by name. Returns SymbolHandles containing projectName, docCommentId, and filePath. exactMatch=false enables prefix/contains search. Filter by containingType or containingNamespace to disambiguate common names.")]
    public async Task<ToolResult<object>> LocateSymbol(
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string symbolName,
        [ExternalInputRequired(DataTag.SymbolKind)] SymbolKindFilter symbolKind = SymbolKindFilter.any,
        [ExternalInputRequired(DataTag.ContainingType)] string? containingType = null,
        [ExternalInputRequired(DataTag.ContainingNamespace)] string? containingNamespace = null,
        [ExternalInputRequired(DataTag.ProjectName)] string? projectName = null,
        [ExternalInputRequired(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.MatchType)] bool exactMatch = true,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default,
        [Description(ToolParams.Reason)] string? reason = null)
    {
        FilePath filePath = _workspaceManager.SetFilePath(filepath);

        try
        {
            var result = await _symbolNavigationEngine.LocateSymbolAsync(symbolName, symbolKind.ToString(), containingType, containingNamespace, projectName, filePath, exactMatch, cancellationToken);
            if (result.Count == 0)
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"Symbol '{symbolName}' not found in the solution" +
                        (projectName != null ? $" (project: {projectName})" : "") +
                        ". Try exactMatch=false for a broader search, or verify the symbol name and searchKind.")
                };
            }

            return new ToolResult<object>
            {
                Success = true,
                Data = result,
                TotalRecords = result.Count,
                WorkspaceVersion = _workspaceManager.WorkspaceVersion
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LocateSymbol failed for '{SymbolName}'", symbolName);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "LocateSymbol")
            };
        }
    }

    [McpServerTool(Name = "InspectSymbol")]
    [Produces(DataTag.SymbolId)]
    [Description("Inspects a symbol in depth. info → type, kind, accessibility, attributes, documentation. blastRadius → all call sites and affected projects.")]
    public async Task<ToolResult<object>> InspectSymbol(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [ToolOption(ToolOptionTag.Aspect)] InspectSymbolAspect aspect,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default,
        [Description(ToolParams.Reason)] string? reason = null)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            if (aspect == InspectSymbolAspect.info)
            {
                var symbolInfo = await _symbolNavigationEngine.GetSymbolInfoAsync(filePath, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (symbolInfo == null)
                {
                    var snippetPreview = contextSnippet.Length > 60 ? contextSnippet[..60] + "…" : contextSnippet;
                    return new ToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception,
                            $"Could not resolve a symbol in '{filePath}' for contextSnippet \"{snippetPreview}\". " +
                            "This means one of: the snippet text does not appear verbatim in the file, it matched a " +
                            "location with no bindable symbol (e.g. whitespace, a keyword, or a comment), or it matched " +
                            "more than one location and lineBefore/lineAfter did not disambiguate. Re-check the snippet " +
                            "against GetMethodSource/GetFileOutline output, or add lineBefore/lineAfter to pin the match.")
                    };
                }
                return new ToolResult<object>
                {
                    Success = true,
                    Data = symbolInfo
                };
            }
            if (aspect == InspectSymbolAspect.blastRadius)
            {
                var result = await _impactAnalyzer.AnalyzeImpactAsync(filePath, contextSnippet, lineBefore, lineAfter);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled aspect '{aspect}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InspectSymbol ({Aspect}) failed in '{FilePath}'", aspect, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "InspectSymbol")
            };
        }
    }

    /// <summary>
    /// <see cref="FindUsagesSearchKind"/> kinds resolve to symbol-relationship facts that are
    /// meaningful for a member (method/property/field/event), not just a type — <c>objectCreations</c>
    /// specifically is the exception: it text-matches "new TypeName(...)" sites and is structurally
    /// incapable of ever matching a member.
    /// </summary>
    private static readonly HashSet<string> MemberSymbolKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Method", "Property", "Field", "Event"
    };

    private async Task<List<object>> RunRelationshipQueryAsync(
        FindUsagesSearchKind searchKind, string name, string? projectName, FilePath filePath, bool sortByFrequency, CancellationToken cancellationToken)
    {
        object result = searchKind switch
        {
            FindUsagesSearchKind.implementorsOf => await _symbolNavigationEngine.FindAllImplementationsAsync(name, projectName, cancellationToken),
            FindUsagesSearchKind.attributeUsages => await _discoveryEngine.FindAttributeUsagesAsync(name, projectName, filePath, cancellationToken),
            FindUsagesSearchKind.objectCreations => await _discoveryEngine.FindObjectCreationSitesAsync(name, filePath, projectName, sortByFrequency, cancellationToken),
            FindUsagesSearchKind.extensionsFor => await _symbolNavigationEngine.FindExtensionMethodsAsync(name, projectName, cancellationToken),
            FindUsagesSearchKind.typesWithAttribute => await _semanticSearchEngine.FindTypesByAttributeAsync(name, cancellationToken),
            FindUsagesSearchKind.methodsByReturnType => await _semanticSearchEngine.FindMethodsByReturnTypeAsync(name, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(searchKind), searchKind, "Unhandled searchKind.")
        };

        // Every FindUsagesSearchKind backing method returns some IEnumerable<T> — normalize to
        // List<object> so broaden-on-empty can report a count and label results uniformly
        // regardless of which kind produced them.
        return ((System.Collections.IEnumerable)result).Cast<object>().ToList();
    }

    [McpServerTool(Name = "QuerySymbolRelationships")]
    [Produces(DataTag.Report)]
    [Description("Queries type-relationship facts by name: implementors of an interface, attribute usages, object-creation sites (new TypeName(...)), extension methods, types carrying an attribute, or methods by return type. projectName/filepath narrow scope. sortByFrequency=true ranks by count (objectCreations only). If the targeted searchKind returns zero results, automatically broadens to all 6 kinds and reports whatever is found, clearly labeled by kind. For call-site/override queries on a method or property (\"who calls this\", \"who overrides this\"), use FindReferences instead — objectCreations only matches 'new TypeName(...)' expressions and will structurally return [] for a method/property name.")]
    public async Task<ToolResult<object>> QuerySymbolRelationships(
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string name,
        [ExternalInputRequired(DataTag.SymbolKind)] FindUsagesSearchKind searchKind,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.Sort)] bool sortByFrequency = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default,
        [Description(ToolParams.Reason)] string? reason = null)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);

            if (searchKind == FindUsagesSearchKind.objectCreations)
            {
                var resolved = await _symbolNavigationEngine.LocateSymbolAsync(name, "any", projectName: projectName, cancellationToken: cancellationToken);
                if (resolved.Count > 0 && resolved.All(s => MemberSymbolKinds.Contains(s.SymbolKind)))
                {
                    var kindsFound = string.Join(", ", resolved.Select(s => s.SymbolKind).Distinct());
                    return new ToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument,
                            $"'{name}' resolves to a {kindsFound} ({resolved.Count} declaration(s) found), not a type — " +
                            "objectCreations only matches 'new TypeName(...)' expressions and is structurally incapable of " +
                            $"returning anything for a member name. Use FindReferences(symbolName: \"{name}\", kind: callers) " +
                            "to find call sites, or kind: implementations for overrides.")
                    };
                }
            }

            var results = await RunRelationshipQueryAsync(searchKind, name, projectName, filePath, sortByFrequency, cancellationToken);
            if (results.Count > 0)
            {
                return new ToolResult<object> { Success = true, Data = results };
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
                    var otherResults = await RunRelationshipQueryAsync(otherKind, name, projectName, filePath, sortByFrequency, cancellationToken);
                    if (otherResults.Count > 0)
                    {
                        broadened[otherKind.ToString()] = otherResults;
                    }
                }
                catch
                {
                    // A kind that doesn't apply to this name (e.g. throws resolving as a type)
                    // is just another empty result for broaden-on-empty purposes — skip it.
                }
            }

            if (broadened.Count == 0)
            {
                return new ToolResult<object>
                {
                    Success = true,
                    Data = results,
                    Warning = $"0 results for '{searchKind}'. Broadened search across all relationship kinds — " +
                        "nothing found under any kind. This is a trustworthy 'not found anywhere' signal, not an error."
                };
            }

            var totalFound = broadened.Sum(kv => kv.Value.Count);
            var summary = string.Join("; ", broadened.Select(kv => $"{kv.Value.Count} under '{kv.Key}'"));
            return new ToolResult<object>
            {
                Success = true,
                Data = broadened,
                Warning = $"0 results for '{searchKind}'. Broadened search across all relationship kinds — " +
                    $"found {totalFound} result(s): {summary}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuerySymbolRelationships ({Kind}) failed for '{Name}'", searchKind, name);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "QuerySymbolRelationships")
            };
        }
    }

    [McpServerTool(Name = "GetBestInsertionPoint")]
    [Produces(DataTag.StartLine)]
    [Description("Returns the best 1-based line number for inserting a new member of memberKind in a type, following standard C# ordering (fields → constructors → destructors → properties → events → methods → nested types).")]
    public async Task<ToolResult<object>> GetBestInsertionPoint(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ContainerName)] string containerName,
        [ExternalInputRequired(DataTag.MemberKind)] string memberKind,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default,
        [Description(ToolParams.Reason)] string? reason = null)
    {
        _ = cancellationToken;
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _discoveryEngine.FindBestInsertionPointAsync(filePath, containerName, memberKind);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetBestInsertionPoint failed for '{ContainerName}' in '{FilePath}'", containerName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetBestInsertionPoint")
            };
        }
    }

    [McpServerTool(Name = "PreviewRenameImpact")]
    [Produces(DataTag.Report)]
    [Description("Previews the impact of renaming a symbol across the solution without applying changes. Returns affected files and location count. " +
        "Resolve the target either by docCommentId+projectName (as returned by LocateSymbol — preferred, unambiguous, no filepath needed) " +
        "or by filepath+symbolName, using contextSnippet/lineBefore/lineAfter to disambiguate if the name appears more than once in the file.")]
    public async Task<ToolResult<object>> PreviewRenameImpact(
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Consumes(DataTag.SymbolName)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.DocCommentId)] string? docCommentId = null,
        [Description(ToolParams.ProjectName)] string? projectName = null,
        [Description(ToolParams.SessionId)] string sessionId = "",
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default,
        [Description(ToolParams.Reason)] string? reason = null)
    {
        FilePath filePath = FilePath.FromWire(filepath ?? string.Empty, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _discoveryEngine.PreviewRenameImpactAsync(
                filePath, symbolName, contextSnippet, lineBefore, lineAfter, docCommentId, projectName, sessionId, cancellationToken);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreviewRenameImpact failed for '{SymbolName}' in '{FilePath}'", symbolName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "PreviewRenameImpact")
            };
        }
    }

    [McpServerTool(Name = "FindReferences")]
    [Produces(DataTag.Report)]
    [Description("Finds call sites and/or implementations for a symbol. kind: callers (call sites only), implementations (overrides/interface implementations only), or all (both, clearly labeled). filepath optional — omit to search by name; supply to pin resolution when the name is ambiguous across files. For type-relationship queries instead (implementors of an interface, attribute usage, object-creation sites, extension methods, types by attribute, methods by return type), use QuerySymbolRelationships.")]
    public async Task<ToolResult<object>> FindReferences(
        [Consumes(DataTag.SymbolName, required: true)] string symbolName,
        [Consumes(DataTag.SymbolKind)] FindReferencesKind kind,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default,
        [Description(ToolParams.Reason)] string? reason = null)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);

            if (kind == FindReferencesKind.callers)
            {
                var result = await _symbolNavigationEngine.FindCallersAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == FindReferencesKind.implementations)
            {
                var result = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == FindReferencesKind.all)
            {
                var callers = await _symbolNavigationEngine.FindCallersAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);
                var implementations = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = new { callers, implementations }
                };
            }
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled kind '{kind}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindReferences ({Kind}) failed for '{SymbolName}'", kind, symbolName);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "FindReferences")
            };
        }
    }

    [McpServerTool(Name = "GetTypeInfo")]
    [Produces(DataTag.Report)]
    [Description("Returns type information. hierarchy → TypeHierarchyReport (base class chain, interfaces, derived types); members → List<TypeMemberDetail> (all public/protected members); both → object with Hierarchy and Members (default). includeInherited=false excludes inherited members (members and both only). For enums, this is also the tool to view current values: each value appears as a Field member with its explicit/ordinal value inline in Signature (e.g. \"Status.Active = 1\"), and inherited System.Enum/ValueType noise (Parse, GetValues, ToString, ...) is excluded automatically regardless of includeInherited. To change an enum's values (add/remove/reorder), use ModifyEnum.")]
    public async Task<ToolResult<object>> GetTypeInfo(
        [Consumes(DataTag.DataType)] string typeName,
        [ToolOptionAttribute(ToolOptionTag.Filter)] TypeInfoInclude include = TypeInfoInclude.both,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [ToolOptionAttribute(ToolOptionTag.Filter)] bool includeInherited = true,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default,
        [Description(ToolParams.Reason)] string? reason = null)
    {
        try
        {
            TypeHierarchyReport? hierarchy = null;
            List<TypeMemberDetail>? members = null;
            if (include == TypeInfoInclude.hierarchy || include == TypeInfoInclude.both)
            {
                hierarchy = await _symbolNavigationEngine.GetTypeHierarchyAsync(typeName, projectName, cancellationToken);
                if (hierarchy.Error is not null)
                {
                    // GetTypeMembersDetailAsync silently returns an empty list for the same
                    // "type not found" condition, so surface the hierarchy lookup's explicit
                    // error here rather than letting either include mode return a bare Success=true.
                    return new ToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.InvalidArgument, hierarchy.Error)
                    };
                }
            }
            if (include == TypeInfoInclude.members || include == TypeInfoInclude.both)
            {
                members = await _symbolNavigationEngine.GetTypeMembersDetailAsync(typeName, projectName, includeInherited);
            }
            if (include == TypeInfoInclude.hierarchy)
            {
                return new ToolResult<object>
                {
                    Success = true,
                    Data = hierarchy!
                };
            }
            if (include == TypeInfoInclude.members)
            {
                var warning = members!.Count == 0
                    ? $"No members found for '{typeName}'. This can mean the type doesn't exist in the solution — retry with include=hierarchy or include=both to confirm — or that it genuinely has no members."
                    : null;
                return new ToolResult<object>
                {
                    Success = true,
                    Data = members!,
                    Warning = warning
                };
            }
            if (include == TypeInfoInclude.both)
            {
                return new ToolResult<object>
                {
                    Success = true,
                    Data = new { Hierarchy = hierarchy, Members = members }
                };
            }
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled include '{include}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTypeInfo ({Include}) failed for '{TypeName}'", include, typeName);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetTypeInfo")
            };
        }
    }
}
