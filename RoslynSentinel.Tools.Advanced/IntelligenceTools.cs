using System.ComponentModel;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Advanced;

[McpServerToolType]
public class IntelligenceTools
{
    private readonly AntiPatternEngine _antiPatternEngine;
    private readonly ImpactAnalyzer _impactAnalyzer;
    private readonly MetricsEngine _metricsEngine;
    private readonly InventoryEngine _inventoryEngine;
    private readonly SolutionStructureEngine _solutionStructureEngine;
    private readonly AsyncAnalysisEngine _asyncSafetyEngine;
    private readonly HealthOrchestrationEngine _healthOrchestrationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly DependencyInjectionEngine _dependencyInjectionEngine;
    private readonly DiscoveryEngine _discoveryEngine;
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger<IntelligenceTools> _logger;
    public IntelligenceTools(ImpactAnalyzer impactAnalyzer, MetricsEngine metricsEngine, InventoryEngine inventoryEngine, DeadCodeEngine deadCodeEngine, DocumentationEngine documentationEngine, SolutionStructureEngine solutionStructureEngine, AsyncAnalysisEngine asyncSafetyEngine, HealthOrchestrationEngine healthOrchestrationEngine, SymbolNavigationEngine symbolNavigationEngine, DependencyInjectionEngine dependencyInjectionEngine, DiscoveryEngine discoveryEngine, ISolutionProvider workspaceManager, AntiPatternEngine antiPatternEngine, SentinelConfiguration config, ILogger<IntelligenceTools> logger)
    {
        _impactAnalyzer = impactAnalyzer;
        _metricsEngine = metricsEngine;
        _inventoryEngine = inventoryEngine;
        _solutionStructureEngine = solutionStructureEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _healthOrchestrationEngine = healthOrchestrationEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _dependencyInjectionEngine = dependencyInjectionEngine;
        _discoveryEngine = discoveryEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
        _antiPatternEngine = antiPatternEngine;
    }

    [McpServerTool(Name = "GetComprehensiveHealthReport")]
    [Produces(DataTag.Report)]
    [Description("Generates a paged health report across one or more engines (Structure, Modernization, Performance, Safety, Architecture).")]
    public async Task<SentinelCallToolResult<object>> GetComprehensiveHealthReportTool([Description(ToolParams.Reason)] ToolCallReason reason, [Description("Engines to include. Omit for all.")] List<HealthEngineType>? engines = null, [Description("Restricts to one project. Omit for the whole solution.")][Consumes(DataTag.ProjectName)] string? projectName = null, [Description("Restricts to one file. Omit for the whole solution.")][Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null, [Description("Number of project results to skip before taking limit.")][ToolOption(ToolOptionTag.Offset)] int offset = 0, [Description("Maximum number of project results to return.")][ToolOption(ToolOptionTag.ResultLimit)] int limit = 10, [Description("Maximum seconds to spend before returning partial results.")][ToolOption(ToolOptionTag.Timeout)] int timeoutSeconds = 25, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var result = await _healthOrchestrationEngine.GenerateComprehensiveHealthReportAsync(engines, projectName, filePath, offset, limit, timeoutSeconds, cancellationToken);
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetComprehensiveHealthReport failed");
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetComprehensiveHealthReport")
            };
        }
    }

    [McpServerTool(Name = "GetSolutionMetrics")]
    [Produces(DataTag.Report)]
    [Description("Returns deep metrics for the solution or a single project.")]
    public async Task<SentinelCallToolResult<object>> GetSolutionMetricsTool([Description(ToolParams.Reason)] ToolCallReason reason, [Description("Restricts to one project. Omit for the whole solution.")][ExternalInputRequired(DataTag.ProjectName)] string? projectName = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _metricsEngine.GetSolutionMetricsAsync(projectName, cancellationToken);
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSolutionMetrics failed");
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetSolutionMetrics")
            };
        }
    }

    [McpServerTool(Name = "GetCodeInventory")]
    [Produces(DataTag.Report)]
    [Description("Returns a structured inventory of namespaces, classes, methods, and properties in a file.")]
    public async Task<SentinelCallToolResult<object>> GetCodeInventoryTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var results = await _inventoryEngine.GetCodeInventoryAsync(resolvedFilePath, cancellationToken);
            return new SentinelCallToolResult<object> { IsError = false, SuccessData = results, TotalRecords = results.Methods.Count };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCodeInventory failed for '{FilePathWrapper}'", resolvedFilePath);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetCodeInventory")
            };
        }
    }

    [McpServerTool(Name = "GetDiRegistrations")]
    [Produces(DataTag.Report)]
    [Description("Scans for DI registrations (AddSingleton/AddScoped/AddTransient) across the solution or a scoped project/file.")]
    public async Task<SentinelCallToolResult<object>> GetDiRegistrationsTool([Description(ToolParams.Reason)] ToolCallReason reason, [Description("Restricts to one project. Omit for the whole solution.")][Consumes(DataTag.ProjectName)] string? projectName = null, [Description("Restricts to one file. Omit for the whole solution.")][Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null, [Description("Filters by lifetime (Singleton/Scoped/Transient). Omit for all.")][ToolOption(ToolOptionTag.Filter)] string? lifetimeFilter = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var result = await _dependencyInjectionEngine.FindDiRegistrationsAsync(projectName, resolvedFilePath, lifetimeFilter, cancellationToken);
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDiRegistrations failed");
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetDiRegistrations")
            };
        }
    }

    [McpServerTool(Name = "GetCallGraph")]
    [Produces(DataTag.ResultOnly)]
    [Description("Builds a multi-level call graph for a method.")]
    public async Task<SentinelCallToolResult<object>> GetCallGraphTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.SymbolName, required: true)] string methodName, [Description("forward: what it calls. reverse: who calls it. tree: markdown call-tree string.")][ToolOption(ToolOptionTag.Direction)] string direction = "forward", [Description("Maximum levels of depth to traverse.")][ToolOption(ToolOptionTag.MaxDepth)] int maxDepth = 3, [Description(ToolParams.ContextSnippet)] string? contextSnippet = null, [Description(ToolParams.LineBefore)] string? lineBefore = null, [Description(ToolParams.LineAfter)] string? lineAfter = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            if (direction == "forward")
            {
                var fwd = await _symbolNavigationEngine.GetCallGraphAsync(resolvedFilePath, methodName, maxDepth, contextSnippet, lineBefore, lineAfter, cancellationToken);

                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = fwd
                };
            }

            if (direction == "reverse")
            {
                var rev = await _symbolNavigationEngine.GetReverseCallGraphAsync(resolvedFilePath, methodName, maxDepth, contextSnippet, lineBefore, lineAfter, cancellationToken);

                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = rev
                };
            }

            if (direction == "tree")
            {
                var result = await _antiPatternEngine.GenerateCallTreeAsync(resolvedFilePath, methodName, maxDepth, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (RefactoringToolHelpers.GuardEngineResult(result, "GetCallGraph", $"method '{methodName}' in '{resolvedFilePath}'", ToolCallHint.Call("GetFileOutline"), NoOpHandling.Error($"No call tree could be built for '{methodName}' in '{resolvedFilePath}'.")) is { } outcomeGuard)
                {
                    return outcomeGuard;
                }

                return new SentinelCallToolResult<object>
                {
                    IsError = false,
                    SuccessData = result
                };
            }

            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown direction '{direction}'. Valid values: forward, reverse, tree.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCallGraph ({Direction}) failed for '{MethodName}'", direction, methodName);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GetCallGraph")
            };
        }
    }

    [McpServerTool(Name = "PreviewMoveFileToNamespaceFolder")]
    [Produces(DataTag.Report)]
    [Description("Returns the folder path a file should reside in based on its declared namespace.")]
    public async Task<SentinelCallToolResult<string>> PreviewMoveFileToNamespaceFolderTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var result = await _solutionStructureEngine.PreviewMoveFileToNamespaceFolderAsync(resolvedFilePath, cancellationToken);
            // NoCandidatesFound means the file is already in its namespace folder: an idempotent no-op, not an error.
            if (result.IsError && result.Outcome != EditOutcome.NoCandidatesFound)
            {
                return new SentinelCallToolResult<string>
                {
                    IsError = true,
                    ErrorData = new ResultError(result.Code ?? ToolErrorCode.Exception, result.Message ?? "PreviewMoveFileToNamespaceFolder failed.")
                };
            }

            return new SentinelCallToolResult<string>
            {
                IsError = false,
                SuccessData = result.ToJsonSummary()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PreviewMoveFileToNamespaceFolder failed for '{FilePathWrapper}'", resolvedFilePath);
            return new SentinelCallToolResult<string>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "PreviewMoveFileToNamespaceFolder")
            };
        }
    }

    [McpServerTool(Name = "TraceVariableLifetime")]
    [Produces(DataTag.Report)]
    [Description("Traces a local variable/parameter's full lifetime: every read, write, ref/out pass, return, and capture across all code paths. Use Search(mode: references) for methods/properties/fields instead.")]
    public async Task<SentinelCallToolResult<object>> TraceVariableLifetimeTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Description("Name of the local variable or parameter to trace.")][Consumes(DataTag.SymbolName)] string variableName, [Description("1-based declaration line; disambiguates duplicate names in the file.")][Consumes(DataTag.StartLine)] int lineNumber, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var result = await _symbolNavigationEngine.TraceVariableLifetimeAsync(resolvedFilePath, variableName, lineNumber, cancellationToken);
            if (result.IsError)
            {
                var hint = result.Code switch
                {
                    ToolErrorCode.NotFound => $" Call {ToolCallHint.Call("GetFileOutline", ("filePath", filePath))} on the file to confirm the enclosing method's line range, then retry with a lineNumber inside it.",
                    ToolErrorCode.InvalidArgument => $" lineNumber is 1-based; call {ToolCallHint.Call("GetFileOutline", ("filePath", filePath))} to find a valid line.",
                    _ => string.Empty
                };
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(result.Code!, result.Error! + hint)
                };
            }
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TraceVariableLifetime failed for '{VariableName}' in '{FilePathWrapper}'", variableName, resolvedFilePath);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "TraceVariableLifetime")
            };
        }
    }
}