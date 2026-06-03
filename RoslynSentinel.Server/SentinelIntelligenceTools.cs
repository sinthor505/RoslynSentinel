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
    [Description("Generates a paged comprehensive health report. engines: Structure, Modernization, Performance, Safety, Architecture. offset/limit for project paging.")]
    public async Task<ToolResult<object>> GetComprehensiveHealthReport(
        List<HealthEngineType>? engines = null,
        string? projectName = null,
        string? filePath = null,
        int offset = 0,
        int limit = 10,
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
    [Description("Gets deep metrics for the entire solution or a specific project.")]
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
    [Description("Generates a structured report of all namespaces, classes, methods, and properties in a file.")]
    public async Task<ToolResult<object>> GetCodeInventory(string filePath)
    {
        try
        {
            //return await _inventoryEngine.GetCodeInventoryAsync(filePath);
            var results = await _inventoryEngine.GetCodeInventoryAsync(filePath);

            // convert results to json
            var jsonResults = System.Text.Json.JsonSerializer.Serialize(results);

            if (jsonResults.Length < ScanResultOffloadHelper.ThresholdBytes)
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

                var summary = await ScanResultOffloadHelper.TryOffloadAsync(results, _workspaceManager.SolutionPath);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = summary,
                    TotalRecords = results.Methods.Count,
                    LargeResult = new LargeResultInfo(
                        WrittenToFile: summary.offloaded,
                        FilePath: summary.filePath,
                        OperationId: summary.operationId,
                        SizeBytes: summary.jsonBytes.Length,
                        TotalRecords: results.Methods.Count,
                        Message: $"Result written to file ({summary.jsonBytes.Length} bytes, {results.Methods.Count} records). " +
                                   $"Use get_scan_result(changeId: \"{summary.operationId}\") to page through results. " +
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
    [Description("Inspects a symbol in depth. aspect: info (type, kind, accessibility, attributes, documentation — returns SymbolHoverInfo) or blastRadius (all call sites and affected projects if the symbol is changed — returns ImpactReport). Provide contextSnippet: a verbatim substring identifying the symbol. Provide lineBefore/lineAfter to disambiguate.")]
    public async Task<ToolResult<object>> InspectSymbol(
        string filePath,
        string contextSnippet,
        string aspect,
        string? lineBefore = null,
        string? lineAfter = null)
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
    [Description("Scans for all DI registrations (AddSingleton/AddScoped/AddTransient) across the solution or in a specific project/file. Returns service type, implementation type, lifetime, and source location for each registration. Use lifetimeFilter to narrow results ('Singleton', 'Scoped', 'Transient').")]
    public async Task<ToolResult<object>> GetDiRegistrations(
        string? projectName = null,
        string? filePath = null,
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
    [Description("Builds a call graph for a method. direction: forward (what the method calls, returns CallGraphNode tree), reverse (who calls this method, returns ReverseCallGraphNode tree), tree (markdown call-tree string). maxDepth: traversal depth (default 3).")]
    public async Task<ToolResult<object>> GetCallGraph(
        string filePath,
        string methodName,
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
    public async Task<ToolResult<string>> MoveFileToNamespaceFolder(string filePath)
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
    [Description("Finds symbols by name using various lookup strategies. kind: implementorsOf (all types implementing or deriving from the named type), attributeUsages (all usages of a named attribute across the solution), objectCreations (all new T() sites for a type name), extensionsFor (extension methods for a target type), typesWithAttribute (types decorated with a specific attribute), methodsByReturnType (methods returning a specific type). projectName and filePath narrow the scope where supported. sortByFrequency ranks results by frequency (supported for kind=objectCreations).")]
    public async Task<ToolResult<object>> FindByName(
        string name,
        string kind,
        string? projectName = null,
        string? filePath = null,
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
            _logger.LogError(ex, "FindByName ({Kind}) failed for '{Name}'", kind, name);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"FindByName failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Description("Returns the public API surface of a project. persistBaseline=false (default): returns full List<ApiSurfaceEntry> with type/member signatures, virtuality, and XML doc — use for SDK documentation or API review. persistBaseline=true: returns compact List<PublicApiMember> baseline suitable for passing to scan_breaking_changes to detect removed or renamed members. filePath scopes to a single file (only used when persistBaseline=true). includeMethods/includeProperties/includeTypes filter the output (only used when persistBaseline=false).")]
    public async Task<ToolResult<object>> GetPublicApiSurface(
        string? projectName = null,
        bool persistBaseline = false,
        string? filePath = null,
        bool includeMethods = true,
        bool includeProperties = true,
        bool includeTypes = true)
    {
        try
        {
            if (persistBaseline)
            {
                var apiResult = await _breakingChangeEngine.GetPublicApiSurfaceAsync(projectName, filePath);
                return new ToolResult<object>
                {
                    Success = true,
                    Data = apiResult
                };
            }
            if (string.IsNullOrEmpty(projectName))
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError("", "projectName is required when persistBaseline=false.")
                };
            }

            //return await _discoveryEngine.GetPublicApiSurfaceAsync(projectName, includeMethods, includeProperties, includeTypes);
            var result = await _discoveryEngine.GetPublicApiSurfaceAsync(projectName, includeMethods, includeProperties, includeTypes);
            var summaryResults = await ScanResultOffloadHelper.TryOffloadAsync(result, _workspaceManager.GetSolutionRoot());

            if (summaryResults.offloaded)
            {
                var apiResult = new ToolResult<object>
                {
                    Success = true,
                    TotalRecords = result.Count,
                    HasMore = false,
                    LargeResult = new LargeResultInfo(
                        WrittenToFile: true,
                        FilePath: summaryResults.filePath,
                        OperationId: summaryResults.operationId,
                        SizeBytes: summaryResults.jsonBytes.Length,
                        TotalRecords: result.Count,
                        Message: $"Result written to file ({summaryResults.jsonBytes.Length} bytes, {result.Count} records). " +
                                       $"Use get_scan_result(changeId: \"{summaryResults.operationId}\") to page through results. " +
                                       "Pass limit and offset to control page size (default limit: 50).")
                };
                return apiResult;
            }

            return new ToolResult<object>
            {
                Success = true,
                Data = result,
                TotalRecords = result.Count,
                HasMore = false,
            };
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
    [Description("Returns the best 1-based line number where a new member of the given kind should be inserted in a type, following standard C# ordering (fields → constructors → destructors → properties → events → methods → nested types).")]
    public async Task<ToolResult<object>> GetBestInsertionPoint(string filePath, string containerName, string memberKind)
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
    [Description("Previews the impact of renaming a symbol across the solution without applying changes. Returns affected files and location count. symbolName: the current name. contextSnippet: optional verbatim substring to disambiguate. Provide lineBefore and/or lineAfter when the snippet could match multiple locations.")]
    public async Task<ToolResult<object>> PreviewRenameImpact(string filePath, string symbolName, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
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
    [Description("Finds all call sites (kind=callers) or implementing members/overrides (kind=implementations) for a symbol, without requiring line/column coordinates. symbolName: the method/property/field name. contextSnippet: optional verbatim substring of the declaration to disambiguate overloads. Provide lineBefore/lineAfter for further disambiguation. Returns List<CallerInfo> for callers, List<ImplementationInfo> for implementations.")]
    public async Task<ToolResult<object>> FindReferences(
        string filePath,
        string symbolName,
        string kind,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null)
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
        Traces a variable's complete lifetime from declaration through every read, write, ref/out pass,
        return, and closure capture, across all code paths (loops, conditionals, try/catch) in the
        enclosing scope.
        filePath: file containing the variable declaration.
        variableName: the exact identifier name.
        lineNumber: 1-based line of the declaration (used to disambiguate variables with the same name).
        Returns: TypeName, DeclarationLine, ScopeDescription, IsDefinitelyAssigned, IsAlwaysAssigned,
        IsCapturedInClosure, and an Accesses list with Line, Column, AccessKind (Declaration/Read/Write/
        Ref/Out/Return/Capture), ContextStack (method > if > for ancestry), IsInLoop, IsInConditional.
        """)]
    public async Task<ToolResult<object>> TraceVariableLifetime(string filePath, string variableName, int lineNumber)
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
    [Description("Returns type information for a named type. include: hierarchy (base class chain, interfaces, derived types — returns TypeHierarchyReport), members (all public/protected members with full metadata — returns List<TypeMemberDetail>), both (returns object with Hierarchy and Members properties). projectName scopes the lookup. includeInherited controls whether inherited members appear (default true; used for include=members and both).")]
    public async Task<ToolResult<object>> GetTypeInfo(
        string typeName,
        string include = "both",
        string? projectName = null,
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
    [Description("Returns a summary of every project's TargetFramework value. Use this before check_project_consistency to see the full framework landscape across the solution.")]
    public async Task<ToolResult<object>> GetProjectFrameworkSummary()
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
    [Description("Compares a previously captured API surface (baseline) against the current code and reports breaking changes: removed types, removed/renamed members, and signature changes. Workflow: (1) call get_public_api_surface_snapshot to capture the baseline, (2) make code changes, (3) call this tool with the baseline list. Scope with projectName or filePath as in step 1.")]
    public async Task<ToolResult<object>> ScanBreakingChanges(
        List<PublicApiMember> baseline,
        string? projectName = null,
        string? filePath = null)
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
        Finds duplicate statement sequences within the methods of a single class.
        Uses structural hashing (SyntaxKind-based) so blocks match regardless of
        variable names or literal values — the same control-flow shape is a clone.

        Returns groups of matching block locations including:
        • StatementCount — how many statements the clone spans
        • HasControlFlowExit — true if any statement is a return/break/continue/throw
          (flag only; does not block the finding)
        • SnippetPreview — first 120 chars of the first statement
        • CapturedVariables — variables that would need to become parameters if extracted
        • ProducedVariables — variables that would need to be returned if extracted
        • Occurrences — method name, start line, end line, file path for each copy

        Use this to find Extract-Method candidates within a single class before the
        duplication spreads. Set minStatements lower (e.g. 3) for aggressive detection,
        higher (e.g. 6) for only substantial clones.
        """)]
    public async Task<ToolResult<object>> ScanDuplicateBlocksInClass(
        string filePath,
        string className,
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
