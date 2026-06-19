using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

[McpServerToolType]
public class SentinelIntelligenceTools
{
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly SemanticSearchEngine _semanticSearchEngine;
    private readonly MetricsEngine _metricsEngine;
    private readonly InventoryEngine _inventoryEngine;
    // private readonly DeadCodeEngine _deadCodeEngine;
    private readonly AnalysisEngine _analysisEngine;
    // private readonly DocumentationEngine _documentationEngine;
    private readonly DependencyEngine _dependencyEngine;
    private readonly ProjectStructureEngine _projectStructureEngine;
    private readonly AsyncSafetyEngine _asyncSafetyEngine;
    private readonly HealthOrchestrationEngine _healthOrchestrationEngine;
    // private readonly ArchitecturalEngine _architecturalEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly DependencyInjectionEngine _dependencyInjectionEngine;
    private readonly DiscoveryEngine _discoveryEngine;
    private readonly ProjectConsistencyEngine _projectConsistencyEngine;
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
        PersistentWorkspaceManager workspaceManager,
        SentinelConfiguration config,
        ILogger<SentinelIntelligenceTools> logger)
    {
        _impactAnalyzer = impactAnalyzer;
        _semanticSearchEngine = semanticSearchEngine;
        _metricsEngine = metricsEngine;
        _inventoryEngine = inventoryEngine;
        // _deadCodeEngine = deadCodeEngine;
        _analysisEngine = analysisEngine;
        // _documentationEngine = documentationEngine;
        _dependencyEngine = dependencyEngine;
        _projectStructureEngine = projectStructureEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _healthOrchestrationEngine = healthOrchestrationEngine;
        // _architecturalEngine = architecturalEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _dependencyInjectionEngine = dependencyInjectionEngine;
        _discoveryEngine = discoveryEngine;
        _projectConsistencyEngine = projectConsistencyEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool(Name = "GetComprehensiveHealthReport")]
    [Produces(DataTag.Report)]
    [Description("Generates a paged health report across one or more engines: Structure, Modernization, Performance, Safety, Architecture. Null engines → all engines. projectName/filePath narrow scope. offset/limit page project results. timeoutSeconds defaults to 25.")]
    public async Task<ToolResult<object>> GetComprehensiveHealthReport(
        List<HealthEngineType>? engines = null,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 10,
        [ToolOption(ToolOptionTag.Timeout)] int timeoutSeconds = 25,
        IProgress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = _workspaceManager.SetFilePath(filepath);

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
                Error = new ResultError(ToolErrorCode.Exception, $"GetComprehensiveHealthReport failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "GetSolutionMetrics")]
    [Produces(DataTag.Report)]
    [Description("Returns deep metrics for the entire solution or a single project. projectName=null → solution-wide.")]
    public async Task<ToolResult<object>> GetSolutionMetrics(
        [ExternalInputRequired(DataTag.ProjectName)] string? projectName = null,
        IProgress<string>? progress = null,
        CancellationToken? cancellationToken = default)
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
                Error = new ResultError(ToolErrorCode.Exception, $"GetSolutionMetrics failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "GetCodeInventory")]
    [Produces(DataTag.Report)]
    [Description("Returns a structured report of all namespaces, classes, methods, and properties in a file.")]
    public async Task<ToolResult<object>> GetCodeInventory(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        IProgress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

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
                        resultType: typeof(CodeInventoryReport).Name,
                        writtenToFile: summary.offloaded,
                        filePath: summary.filePath.Absolute.ToString(),
                        scanId: summary.scanId,
                        sizeBytes: summary.jsonBytes.Length,
                        totalRecords: results.Methods.Count,
                        message: $"Result written to file ({summary.jsonBytes.Length} bytes, {results.Methods.Count} records). " +
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
                Error = new ResultError(ToolErrorCode.Exception, $"GetCodeInventory failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "GetDiRegistrations")]
    [Produces(DataTag.Report)]
    [Description("Scans for all DI registrations (AddSingleton/AddScoped/AddTransient) across the solution or in a scoped project/file. Returns service type, implementation type, lifetime, and source location. lifetimeFilter: Singleton, Scoped, or Transient.")]
    public async Task<ToolResult<object>> GetDiRegistrations(
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.Filter)] string? lifetimeFilter = null,
        IProgress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = _workspaceManager.SetFilePath(filepath);
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
                Error = new ResultError(ToolErrorCode.Exception, $"GetDiRegistrations failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "GetCallGraph")]
    [Produces(DataTag.ResultOnly)]
    [Description("Builds a call graph for a method. direction: forward (what the method calls → CallGraphNode tree), reverse (who calls this method → ReverseCallGraphNode tree), tree (markdown call-tree string). maxDepth defaults to 3.")]
    public async Task<ToolResult<object>> GetCallGraph(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        [ToolOption(ToolOptionTag.Direction)] string direction = "forward",
        [ToolOption(ToolOptionTag.MaxDepth)] int maxDepth = 3,
        IProgress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

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
                        Error = new ResultError(ToolErrorCode.Exception, $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
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
                        Error = new ResultError(ToolErrorCode.Exception, $"Method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
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
                Error = new ResultError(ToolErrorCode.Exception, $"Unknown direction '{direction}'. Valid values: forward, reverse, tree.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCallGraph ({Direction}) failed for '{MethodName}'", direction, methodName);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"GetCallGraph failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "PreviewMoveFileToNamespaceFolder")]
    [Produces(DataTag.Report)]
    [Description("Returns the folder path where a file should reside based on its declared namespace. Use to plan file moves.")]
    public async Task<ToolResult<string>> PreviewMoveFileToNamespaceFolder(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        IProgress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _projectStructureEngine.PreviewMoveFileToNamespaceFolderAsync(filePath);
            return new ToolResult<string>
            {
                Success = true,
                Data = result.ToJsonSummary()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreviewMoveFileToNamespaceFolder failed for '{FilePath}'", filePath);
            return new ToolResult<string>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"PreviewMoveFileToNamespaceFolder failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "TraceVariableLifetime")]
    [Produces(DataTag.Report)]
    [Description("""
        Traces a variable's complete lifetime from declaration through every read, write, ref/out pass, return, and closure capture, across all code paths (loops, conditionals, try/catch) in the enclosing scope. lineNumber: 1-based line of the declaration (disambiguates same-name variables). Returns: TypeName, DeclarationLine, ScopeDescription, IsDefinitelyAssigned, IsAlwaysAssigned, IsCapturedInClosure, and Accesses list with Line, Column, AccessKind (Declaration/Read/Write/Ref/Out/Return/Capture), ContextStack (method > if > for ancestry), IsInLoop, IsInConditional.
        """)]
    public async Task<ToolResult<object>> TraceVariableLifetime(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName)] string variableName,
        [Consumes(DataTag.StartLine)] int lineNumber,
        IProgress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

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
                Error = new ResultError(ToolErrorCode.Exception, $"TraceVariableLifetime failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

}
