using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

[McpServerToolType]
public class SentinelIntelligenceTools
{
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly SemanticSearchEngine _semanticSearchEngine;
    private readonly MetricsEngine _metricsEngine;
    private readonly InventoryEngine _inventoryEngine;
    private readonly DeadCodeEngine _deadCodeEngine;
    private readonly AnalysisEngine _analysisEngine;
    private readonly DocumentationEngine _documentationEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly ProjectStructureEngine _projectStructureEngine;
    private readonly AsyncSafetyEngine _asyncSafetyEngine;
    private readonly HealthOrchestrationEngine _healthOrchestrationEngine;
    private readonly ArchitecturalEngine _architecturalEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly DependencyInjectionEngine _dependencyInjectionEngine;
    private readonly DiscoveryEngine _discoveryEngine;
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
    private readonly BreakingChangeEngine _breakingChangeEngine;
    private readonly CloneDetectionEngine _cloneDetectionEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelIntelligenceTools> _logger;

    public SentinelIntelligenceTools(
        ImpactAnalyzer impactAnalyzer,
        SemanticSearchEngine semanticSearchEngine,
        MetricsEngine metricsEngine,
        InventoryEngine inventoryEngine,
        DeadCodeEngine deadCodeEngine,
        AnalysisEngine analysisEngine,
        DocumentationEngine documentationEngine,
        DependencyEngine dependencyEngine,
        ProjectStructureEngine projectStructureEngine,
        AsyncSafetyEngine asyncSafetyEngine,
        HealthOrchestrationEngine healthOrchestrationEngine,
        ArchitecturalEngine architecturalEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        DependencyInjectionEngine dependencyInjectionEngine,
        DiscoveryEngine discoveryEngine,
        ProjectConsistencyEngine projectConsistencyEngine,
        BreakingChangeEngine breakingChangeEngine,
        CloneDetectionEngine cloneDetectionEngine,
        PersistentWorkspaceManager workspaceManager,
        SentinelConfiguration config,
        ILogger<SentinelIntelligenceTools> logger)
    {
        _impactAnalyzer = impactAnalyzer;
        _semanticSearchEngine = semanticSearchEngine;
        _metricsEngine = metricsEngine;
        _inventoryEngine = inventoryEngine;
        _deadCodeEngine = deadCodeEngine;
        _analysisEngine = analysisEngine;
        _documentationEngine = documentationEngine;
        _dependencyEngine = dependencyEngine;
        _projectStructureEngine = projectStructureEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _healthOrchestrationEngine = healthOrchestrationEngine;
        _architecturalEngine = architecturalEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _dependencyInjectionEngine = dependencyInjectionEngine;
        _discoveryEngine = discoveryEngine;
        _projectConsistencyEngine = projectConsistencyEngine;
        _breakingChangeEngine = breakingChangeEngine;
        _cloneDetectionEngine = cloneDetectionEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Generates a paged health report across one or more engines: Structure, Modernization, Performance, Safety, Architecture. Null engines → all engines. projectName/filePath narrow scope. offset/limit page project results. timeoutSeconds defaults to 25.")]
    public async Task<ToolResult<object>> GetComprehensiveHealthReport(
        List<HealthEngineType>? engines = null,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath)] string? filePath = null,
        [Consumes(DataTag.Offset)] int offset = 0,
        [Consumes(DataTag.Limit)] int limit = 10,
        int timeoutSeconds = 25)
    {
        try
        {
            var result = await _healthOrchestrationEngine.GenerateComprehensiveHealthReportAsync(engines, projectName, filePath, offset, limit, timeoutSeconds);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetComprehensiveHealthReport failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetComprehensiveHealthReport failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Returns deep metrics for the entire solution or a single project. projectName=null → solution-wide.")]
    [Consumes(DataTag.ProjectName)]
    public async Task<ToolResult<object>> GetSolutionMetrics(string? projectName = null)
    {
        try
        {
            var result = await _metricsEngine.GetSolutionMetricsAsync(projectName);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSolutionMetrics failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetSolutionMetrics failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Returns a structured report of all namespaces, classes, methods, and properties in a file.")]
    public async Task<ToolResult<object>> GetCodeInventory([Consumes(DataTag.SourceFilepath, required: true)] string filePath)
    {
        try
        {
            //return await _inventoryEngine.GetCodeInventoryAsync(filePath);
            var results = await _inventoryEngine.GetCodeInventoryAsync(filePath);

            // convert results to json
            var jsonResults = System.Text.Json.JsonSerializer.Serialize(results);

            if (jsonResults.Length < ScanResultHelper.ThresholdBytes)
            {
                return new ToolResult<object>
                {
                    Success = true,
                    Data = results,
                    TotalRecords = results.Methods.Count
                };
            }
            else
            {

                var summary = await SentinelScanTools.StoreScanResultAsync(results, _workspaceManager.GetSolutionRoot(), ScanWrapperType.CodeInventoryReport);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = summary,
                    TotalRecords = results.Methods.Count,
                    LargeResult = new LargeResultInfo(
                        ResultType: typeof(CodeInventoryReport).Name,
                        WrittenToFile: summary.offloaded,
                        FilePath: summary.filePath,
                        ScanId: summary.scanId,
                        SizeBytes: summary.jsonBytes.Length,
                        TotalRecords: results.Methods.Count,
                        Message: $"Result written to file ({summary.jsonBytes.Length} bytes, {results.Methods.Count} records). " +
                                   $"Use get_scan_result(scanId: \"{summary.scanId}\") to page through results. " +
                                   "Pass limit and offset to control page size (default limit: 50).")

                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCodeInventory failed for '{FilePath}'", filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetCodeInventory failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Inspects a symbol in depth. aspect: info (type, kind, accessibility, attributes, documentation → SymbolHoverInfo) or blastRadius (all call sites and affected projects if symbol changes → ImpactReport). contextSnippet: verbatim substring identifying the symbol. lineBefore/lineAfter disambiguate.")]
    [Consumes(DataTag.SourceFilepath)]
    [Consumes(DataTag.ContextSnippet)]
    [Consumes(DataTag.SymbolName)]
    [Consumes(DataTag.ProjectName)]
    public async Task<ToolResult<object>> InspectSymbol(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        string aspect,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
        try
        {
            if (aspect == "info")
            {
                var symbolInfo = await _symbolNavigationEngine.GetSymbolInfoAsync(filePath, contextSnippet, lineBefore, lineAfter);
                if (symbolInfo == null) return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError("", "Symbol info not found.")
                };
                return new ToolResult<object>
                {
                    Success = true,
                    Data = symbolInfo
                };
            }
            if (aspect == "blastRadius")
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
                Error = new ResultError("", $"Unknown aspect '{aspect}'. Valid values: info, blastRadius.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InspectSymbol ({Aspect}) failed in '{FilePath}'", aspect, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"InspectSymbol failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("""
        Locates all declaration sites for a symbol by name — no file path required.
        Returns structured results whose FilePath and ContextSnippet fields can be passed directly
        to inspect_symbol, find_references, get_call_graph, rename_symbol, and all other
        filePath-gated tools, eliminating the need for search_solution_text as a bootstrap step.

        symbolName: simple or fully-qualified name (e.g. "GetById" or "Acme.Data.Repo.GetById").
        symbolKind: optional filter — "type", "method", "property", "field", "event", or "any" (default).
        projectName: optional — restricts search to a single project.
        exactMatch: true (default) for exact name match; false for prefix/contains search (discovery mode).

        When multiple results are returned (overloads, partial classes), inspect Signature and
        ContainingType to identify the target, then pass the chosen FilePath + ContextSnippet
        to the next tool call.
        """)]
    public async Task<ToolResult<object>> LocateSymbol(
        [Consumes(DataTag.SymbolName, required: true)] string symbolName,
        string symbolKind = "any",
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        bool exactMatch = true)
    {
        try
        {
            var result = await _symbolNavigationEngine.LocateSymbolAsync(symbolName, symbolKind, projectName, exactMatch);
            if (result.Count == 0)
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError("", $"Symbol '{symbolName}' not found in the solution" +
                        (projectName != null ? $" (project: {projectName})" : "") +
                        ". Try exactMatch=false for a broader search, or verify the symbol name and kind.")
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
                Error = new ResultError("", $"LocateSymbol failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Scans for all DI registrations (AddSingleton/AddScoped/AddTransient) across the solution or in a scoped project/file. Returns service type, implementation type, lifetime, and source location. lifetimeFilter: Singleton, Scoped, or Transient.")]
    public async Task<ToolResult<object>> GetDiRegistrations(
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath)] string? filePath = null,
        string? lifetimeFilter = null)
    {
        try
        {
            var result = await _dependencyInjectionEngine.FindDiRegistrationsAsync(projectName, filePath, lifetimeFilter);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDiRegistrations failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetDiRegistrations failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Builds a call graph for a method. direction: forward (what the method calls → CallGraphNode tree), reverse (who calls this method → ReverseCallGraphNode tree), tree (markdown call-tree string). maxDepth defaults to 3.")]
    public async Task<ToolResult<object>> GetCallGraph(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        string direction = "forward",
        int maxDepth = 3)
    {
        try
        {
            if (direction == "forward")
            {
                var fwd = await _symbolNavigationEngine.GetCallGraphAsync(filePath, methodName, maxDepth);
                if (fwd == null)
                {
                    return new ToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError("", $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                            "Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). " +
                            "Use get_document_outline to list available methods in the file.")
                    };
                }
                return new ToolResult<object>
                {
                    Success = true,
                    Data = fwd
                };
            }
            if (direction == "reverse")
            {
                var rev = await _symbolNavigationEngine.GetReverseCallGraphAsync(filePath, methodName, maxDepth);
                if (rev == null)
                {
                    return new ToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError("", $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                        "Ensure the file is part of the loaded solution and the method name exactly matches (case-sensitive). " +
                        "Use get_document_outline to list available methods in the file.")
                    };
                }
                return new ToolResult<object>
                {
                    Success = true,
                    Data = rev
                };
            }
            if (direction == "tree")
            {
                var result = await _analysisEngine.GenerateCallTreeAsync(filePath, methodName, maxDepth);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"Unknown direction '{direction}'. Valid values: forward, reverse, tree.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCallGraph ({Direction}) failed for '{MethodName}'", direction, methodName);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetCallGraph failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Returns the folder path where a file should reside based on its declared namespace. Use to plan file moves.")]
    public async Task<ToolResult<string>> MoveFileToNamespaceFolder([Consumes(DataTag.SourceFilepath, required: true)] string filePath)
    {
        try
        {
            var result = await _projectStructureEngine.MoveFileToNamespaceFolderAsync(filePath);
            return new ToolResult<string>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveFileToNamespaceFolder failed for '{FilePath}'", filePath);
            return new ToolResult<string>
            {
                Success = false,
                Error = new ResultError("", $"MoveFileToNamespaceFolder failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("""
        Queries symbol relationships by name. Use locate_symbol instead when you want to find
        where a symbol is declared and get its file path.

        kind values and what they return:
          implementorsOf     — all classes/structs implementing a named interface or deriving from a class
          attributeUsages    — all sites where a named attribute is applied
          objectCreations    — all instantiation sites for a named type (new T(...))
          extensionsFor      — all extension methods defined for a named type
          typesWithAttribute — all types decorated with a named attribute (syntax-level; faster than attributeUsages)
          methodsByReturnType — all methods whose return type matches the given name

        projectName/filePath narrow scope where supported.
        sortByFrequency=true ranks by frequency (supported for objectCreations).
        """)]
    public async Task<ToolResult<object>> FindUsages(
        [Consumes(DataTag.SymbolName, required: true)] string name,
        string kind,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath)] string? filePath = null,
        bool sortByFrequency = false)
    {
        try
        {
            if (kind == "implementorsOf")
            {
                var result = await _symbolNavigationEngine.FindAllImplementationsAsync(name, projectName);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == "attributeUsages")
            {
                var result = await _discoveryEngine.FindAttributeUsagesAsync(name, projectName, filePath);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == "objectCreations")
            {
                var result = await _discoveryEngine.FindObjectCreationSitesAsync(name, filePath, projectName, sortByFrequency);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == "extensionsFor")
            {
                var result = await _symbolNavigationEngine.FindExtensionMethodsAsync(name, projectName);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == "typesWithAttribute")
            {
                var result = await _semanticSearchEngine.FindTypesByAttributeAsync(name);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == "methodsByReturnType")
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
                Error = new ResultError("", $"Unknown kind '{kind}'. Valid values: implementorsOf, attributeUsages, objectCreations, extensionsFor, typesWithAttribute, methodsByReturnType.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindUsages ({Kind}) failed for '{Name}'", kind, name);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"FindUsages failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Returns the public API surface of a project. persistBaseline=false (default) → full List<ApiSurfaceEntry> with signatures, virtuality, and XML docs (for SDK documentation/API review). persistBaseline=true → compact List<PublicApiMember> baseline for passing to scan_breaking_changes. filePath scopes to a single file (persistBaseline=true only). includeMethods/includeProperties/includeTypes filter output (persistBaseline=false only). Returns a scanId and writes scan results to disk when output result payload exceeds the inline size threshold. Use get_scan_result(scanId) to retrieve the results.")]
    public async Task<ToolResult<object>> GetPublicApiSurface(
        [Consumes(DataTag.ProjectName, required: true)] string? projectName = null,
        bool persistBaseline = false,
        [Consumes(DataTag.SourceFilepath)] string? filePath = null,
        bool includeMethods = true,
        bool includeProperties = true,
        bool includeTypes = true)
    {
        try
        {
            ToolResult<object> toolResult = new ToolResult<object>() { Success = false };

            if (persistBaseline)
            {
                var apiResult = await _breakingChangeEngine.GetPublicApiSurfaceAsync(projectName, filePath);

                var summaryResults = await SentinelScanTools.StoreScanResultAsync(apiResult, _workspaceManager.GetSolutionRoot(), ScanWrapperType.ApiSurfaceEntryList);

                if (summaryResults.offloaded)
                {
                    toolResult = new ToolResult<object>
                    {
                        Success = true,
                        TotalRecords = apiResult.Count,
                        HasMore = false,
                        LargeResult = new LargeResultInfo(
                            ResultType: typeof(ApiSurfaceEntry).Name,
                            WrittenToFile: true,
                            FilePath: summaryResults.filePath,
                            ScanId: summaryResults.scanId,
                            SizeBytes: summaryResults.jsonBytes.Length,
                            TotalRecords: apiResult.Count,
                            Message: $"Result written to file ({summaryResults.jsonBytes.Length} bytes, {apiResult.Count} records). " +
                                           $"Use get_scan_result(scanId: \"{summaryResults.scanId}\") to page through results. " +
                                           "Pass limit and offset to control page size (default limit: 50).")
                    };
                }
                else
                {
                    return new ToolResult<object>
                    {
                        Success = true,
                        Data = apiResult,
                        TotalRecords = apiResult.Count
                    };
                }
            }
            else
            {
                if (string.IsNullOrEmpty(projectName))
                {
                    toolResult = new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError("", "projectName is required when persistBaseline=false.")
                    };
                    return toolResult;
                }

                var apiResult = await _discoveryEngine.GetPublicApiSurfaceAsync(projectName, includeMethods, includeProperties, includeTypes);

                var summaryResults = await SentinelScanTools.StoreScanResultAsync(apiResult, _workspaceManager.GetSolutionRoot(), ScanWrapperType.ApiSurfaceEntryList);

                if (summaryResults.offloaded)
                {
                    toolResult = new ToolResult<object>
                    {
                        Success = true,
                        TotalRecords = apiResult.Count,
                        HasMore = false,
                        LargeResult = new LargeResultInfo(
                            ResultType: typeof(ApiSurfaceEntry).Name,
                            WrittenToFile: true,
                            FilePath: summaryResults.filePath,
                            ScanId: summaryResults.scanId,
                            SizeBytes: summaryResults.jsonBytes.Length,
                            TotalRecords: apiResult.Count,
                            Message: $"Result written to file ({summaryResults.jsonBytes.Length} bytes, {apiResult.Count} records). " +
                                           $"Use get_scan_result(scanId: \"{summaryResults.scanId}\") to page through results. " +
                                           "Pass limit and offset to control page size (default limit: 50).")
                    };
                }
                else
                {
                    return new ToolResult<object>
                    {
                        Success = true,
                        Data = apiResult,
                        TotalRecords = apiResult.Count
                    };
                }
            }

            return toolResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPublicApiSurface failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetPublicApiSurface failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Returns the best 1-based line number for inserting a new member of memberKind in a type, following standard C# ordering (fields → constructors → destructors → properties → events → methods → nested types).")]
    public async Task<ToolResult<object>> GetBestInsertionPoint(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.ContainerName)] string containerName,
        [Consumes(DataTag.MemberKind)] string memberKind)
    {
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
                Error = new ResultError("", $"GetBestInsertionPoint failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Previews the impact of renaming a symbol across the solution without applying changes. Returns affected files and location count. contextSnippet disambiguates overloads; lineBefore/lineAfter provide further disambiguation.")]
    public async Task<ToolResult<object>> PreviewRenameImpact(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.SymbolName)] string symbolName,
        [Consumes(DataTag.ContextSnippet)] string? contextSnippet = null,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
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
                Error = new ResultError("", $"PreviewRenameImpact failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("""
        Finds all call sites or implementations for a symbol.
        kind: callers (→ List<CallerInfo>) or implementations (→ List<ImplementationInfo>).

        filePath is optional. When omitted, the symbol is resolved by name across the solution —
        use this when you don't yet have the file path (e.g. before calling locate_symbol).
        Supply filePath to pin resolution to a specific declaring file (required when symbolName
        is ambiguous across multiple files).

        contextSnippet disambiguates overloads; lineBefore/lineAfter provide further disambiguation.
        """)]
    public async Task<ToolResult<object>> FindReferences(
        [Consumes(DataTag.SymbolName, required: true)] string symbolName,
        string kind,
        [Consumes(DataTag.SourceFilepath)] string? filePath = null,
        [Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet = null,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
        try
        {
            if (kind == "callers")
            {
                var result = await _symbolNavigationEngine.FindCallersAsync(filePath, symbolName, contextSnippet, lineBefore, lineAfter);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = result
                };
            }
            if (kind == "implementations")
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
                Error = new ResultError("", $"Unknown kind '{kind}'. Valid values: callers, implementations.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindReferences ({Kind}) failed for '{SymbolName}'", kind, symbolName);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"FindReferences failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("""
        Traces a variable's complete lifetime from declaration through every read, write, ref/out pass, return, and closure capture, across all code paths (loops, conditionals, try/catch) in the enclosing scope. lineNumber: 1-based line of the declaration (disambiguates same-name variables). Returns: TypeName, DeclarationLine, ScopeDescription, IsDefinitelyAssigned, IsAlwaysAssigned, IsCapturedInClosure, and Accesses list with Line, Column, AccessKind (Declaration/Read/Write/Ref/Out/Return/Capture), ContextStack (method > if > for ancestry), IsInLoop, IsInConditional.
        """)]
    public async Task<ToolResult<object>> TraceVariableLifetime(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.SymbolName)] string variableName,
        [Consumes(DataTag.StartLine)] int lineNumber)
    {
        try
        {
            var result = await _symbolNavigationEngine.TraceVariableLifetimeAsync(filePath, variableName, lineNumber);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TraceVariableLifetime failed for '{VariableName}' in '{FilePath}'", variableName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"TraceVariableLifetime failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Returns type information. include: hierarchy (base class chain, interfaces, derived types → TypeHierarchyReport), members (all public/protected members with full metadata → List<TypeMemberDetail>), both (default → object with Hierarchy and Members). includeInherited=false excludes inherited members (applies to members and both).")]
    public async Task<ToolResult<object>> GetTypeInfo(
        string typeName,
        string include = "both",
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        bool includeInherited = true)
    {
        try
        {
            TypeHierarchyReport? hierarchy = null;
            List<TypeMemberDetail>? members = null;
            if (include == "hierarchy" || include == "both")
            {
                hierarchy = await _symbolNavigationEngine.GetTypeHierarchyAsync(typeName, projectName);
            }
            if (include == "members" || include == "both")
            {
                members = await _symbolNavigationEngine.GetTypeMembersDetailAsync(typeName, projectName, includeInherited);
            }
            if (include == "hierarchy")
            {
                return new ToolResult<object>
                {
                    Success = true,
                    Data = hierarchy!
                };
            }
            if (include == "members")
            {
                return new ToolResult<object>
                {
                    Success = true,
                    Data = members!
                };
            }
            if (include == "both")
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
                Error = new ResultError("", $"Unknown include '{include}'. Valid values: hierarchy, members, both.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTypeInfo ({Include}) failed for '{TypeName}'", include, typeName);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetTypeInfo failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Returns each project's TargetFramework value. Use before check_project_consistency to see the full framework landscape. No parameters.")]
    public async Task<ToolResult<object>> ListProjectFrameworkTargets()
    {
        try
        {
            var result = await _projectConsistencyEngine.GetProjectFrameworkSummaryAsync();
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
                Error = new ResultError("", $"GetProjectFrameworkSummary failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Compares a previously captured API surface baseline against current code and reports breaking changes: removed types, removed/renamed members, signature changes. Workflow: (1) call get_public_api_surface with persistBaseline=true to capture baseline, (2) make code changes, (3) call this tool with the baseline list. Scope with projectName/filePath matching step 1.")]
    public async Task<ToolResult<object>> ScanBreakingChanges(
        List<PublicApiMember> baseline,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath)] string? filePath = null)
    {
        try
        {
            var result = await _breakingChangeEngine.DetectBreakingChangesAsync(baseline, projectName, filePath);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanBreakingChanges failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"ScanBreakingChanges failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("""
        Finds duplicate statement sequences within the methods of a single class using structural hashing (SyntaxKind-based — matches regardless of variable names or literal values). Returns clone groups with: StatementCount, HasControlFlowExit (flag only, does not block finding), SnippetPreview, CapturedVariables (would become parameters if extracted), ProducedVariables (would need to be returned if extracted), and Occurrences (method, start line, end line, file). minStatements=3 for aggressive detection, 6+ for substantial clones only.
        """)]
    public async Task<ToolResult<object>> ScanDuplicateBlocksInClass(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.ContainerName)] string className,
        int minStatements = 4)
    {
        try
        {
            var result = await _cloneDetectionEngine.FindDuplicateBlocksInClassAsync(filePath, className, minStatements);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanDuplicateBlocksInClass failed for '{ClassName}' in '{FilePath}'", className, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"ScanDuplicateBlocksInClass failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }
}
