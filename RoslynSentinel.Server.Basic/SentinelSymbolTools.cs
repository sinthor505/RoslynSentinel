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
    private readonly PersistentWorkspaceManager _workspaceManager;
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
        PersistentWorkspaceManager workspaceManager,
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
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = _workspaceManager.SetFilePath(filepath);

        try
        {
            var result = await _symbolNavigationEngine.LocateSymbolAsync(symbolName, symbolKind.ToString(), containingType, containingNamespace, projectName, filePath, exactMatch);
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
                TotalRecords = result.Count
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LocateSymbol failed for '{SymbolName}'", symbolName);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"LocateSymbol failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
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
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            if (aspect == InspectSymbolAspect.info)
            {
                var symbolInfo = await _symbolNavigationEngine.GetSymbolInfoAsync(filePath, contextSnippet, lineBefore, lineAfter);
                if (symbolInfo == null) return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, "Symbol info not found.")
                };
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
                Error = new ResultError(ToolErrorCode.Exception, $"InspectSymbol failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "FindUsages")]
    [Produces(DataTag.Report)]
    [Description("Queries symbol relationships by name. projectName/filepath narrow scope. sortByFrequency=true ranks by count (objectCreations only).")]
    public async Task<ToolResult<object>> FindUsages(
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string name,
        [ExternalInputRequired(DataTag.SymbolKind)] FindUsagesSearchKind searchKind,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.Sort)] bool sortByFrequency = false,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);

            if (searchKind == FindUsagesSearchKind.implementorsOf)
            {
                var result = await _symbolNavigationEngine.FindAllImplementationsAsync(name, projectName);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (searchKind == FindUsagesSearchKind.attributeUsages)
            {
                var result = await _discoveryEngine.FindAttributeUsagesAsync(name, projectName, filePath);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (searchKind == FindUsagesSearchKind.objectCreations)
            {
                var result = await _discoveryEngine.FindObjectCreationSitesAsync(name, filePath, projectName, sortByFrequency);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (searchKind == FindUsagesSearchKind.extensionsFor)
            {
                var result = await _symbolNavigationEngine.FindExtensionMethodsAsync(name, projectName);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (searchKind == FindUsagesSearchKind.typesWithAttribute)
            {
                var result = await _semanticSearchEngine.FindTypesByAttributeAsync(name);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (searchKind == FindUsagesSearchKind.methodsByReturnType)
            {
                var result = await _semanticSearchEngine.FindMethodsByReturnTypeAsync(name);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled searchKind '{searchKind}'.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindUsages ({Kind}) failed for '{Name}'", searchKind, name);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"FindUsages failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
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
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
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
                Error = new ResultError(ToolErrorCode.Exception, $"GetBestInsertionPoint failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "PreviewRenameImpact")]
    [Produces(DataTag.Report)]
    [Description("Previews the impact of renaming a symbol across the solution without applying changes. Returns affected files and location count.")]
    public async Task<ToolResult<object>> PreviewRenameImpact(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName)] string symbolName,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _discoveryEngine.PreviewRenameImpactAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);
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
                Error = new ResultError(ToolErrorCode.Exception, $"PreviewRenameImpact failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "FindReferences")]
    [Produces(DataTag.Report)]
    [Description("Finds all call sites or implementations for a symbol. filepath optional — omit to search by name; supply to pin resolution when the name is ambiguous across files.")]
    public async Task<ToolResult<object>> FindReferences(
        [Consumes(DataTag.SymbolName, required: true)] string symbolName,
        [Consumes(DataTag.SymbolKind)] FindReferencesKind kind,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);

            if (kind == FindReferencesKind.callers)
            {
                var result = await _symbolNavigationEngine.FindCallersAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);
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
                Error = new ResultError(ToolErrorCode.Exception, $"FindReferences failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "GetTypeInfo")]
    [Produces(DataTag.Report)]
    [Description("Returns type information. hierarchy → TypeHierarchyReport (base class chain, interfaces, derived types); members → List<TypeMemberDetail> (all public/protected members); both → object with Hierarchy and Members (default). includeInherited=false excludes inherited members (members and both only).")]
    public async Task<ToolResult<object>> GetTypeInfo(
        [Consumes(DataTag.DataType)] string typeName,
        [ToolOptionAttribute(ToolOptionTag.Filter)] TypeInfoInclude include = TypeInfoInclude.both,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [ToolOptionAttribute(ToolOptionTag.Filter)] bool includeInherited = true,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            TypeHierarchyReport? hierarchy = null;
            List<TypeMemberDetail>? members = null;
            if (include == TypeInfoInclude.hierarchy || include == TypeInfoInclude.both)
            {
                hierarchy = await _symbolNavigationEngine.GetTypeHierarchyAsync(typeName, projectName);
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
                return new ToolResult<object>
                {
                    Success = true,
                    Data = members!
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
                Error = new ResultError(ToolErrorCode.Exception, $"GetTypeInfo failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }
}
