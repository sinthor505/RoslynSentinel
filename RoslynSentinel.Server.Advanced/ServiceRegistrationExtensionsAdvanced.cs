using System.Diagnostics;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Shared service registration helpers used by both the stdio server (Program.cs)
/// and the separate HTTP host (RoslynSentinel.HttpHost).
/// </summary>
public static class RoslynSentinelServiceExtensionsAdvanced
{
    /// <summary>
    /// Registers all Roslyn analysis engine singletons into the DI container.
    /// </summary>
    public static IServiceCollection AddRoslynSentinelEnginesAdvanced(this IServiceCollection services)
    {
        services.AddSingleton<SentinelConfiguration>();
        services.AddSingleton<PersistentWorkspaceManager>();
        services.AddSingleton<DiffEngine>();
        services.AddSingleton<ValidationEngine>();
        services.AddSingleton<ImpactAnalyzer>();
        services.AddSingleton<RefactoringEngine>();
        services.AddSingleton<MetricsEngine>();
        services.AddSingleton<CodeHealingEngine>();
        services.AddSingleton<AnalysisEngine>();
        services.AddSingleton<PerformanceEngine>();
        services.AddSingleton<SecurityEngine>();
        services.AddSingleton<TestingEngine>();
        services.AddSingleton<CodeGenerationEngine>();
        services.AddSingleton<ModernizationEngine>();
        services.AddSingleton<DependencyInjectionEngine>();
        services.AddSingleton<ThreadSafetyEngine>();
        services.AddSingleton<ArchitecturalEngine>();
        services.AddSingleton<AdvancedRefactoringEngine>();
        services.AddSingleton<DocumentationEngine>();
        services.AddSingleton<SecurityAndSafetyEngine>();
        services.AddSingleton<ApiIntegrationEngine>();
        services.AddSingleton<InventoryEngine>();
        services.AddSingleton<AsyncOptimizationEngine>();
        services.AddSingleton<InstrumentationEngine>();
        services.AddSingleton<AdvancedTypeEngine>();
        services.AddSingleton<ModernLoggingEngine>();
        services.AddSingleton<CodeFlowEngine>();
        services.AddSingleton<StructuralRefinementEngine>();
        services.AddSingleton<LogicOptimizationEngine>();
        services.AddSingleton<SemanticSearchEngine>();
        services.AddSingleton<ModernizationUpgradeEngine>();
        services.AddSingleton<AsyncSafetyEngine>();
        services.AddSingleton<ProjectStructureEngine>();
        services.AddSingleton<DeadCodeEngine>();
        services.AddSingleton<SyntaxUpgradeEngine>();
        services.AddSingleton<RefinementEngine>();
        services.AddSingleton<DiagnosticEngine>();
        services.AddSingleton<SolutionManagementEngine>();
        services.AddSingleton<MappingEngine>();
        services.AddSingleton<IDEStyleEngine>();
        services.AddSingleton<StandardRefactoringEngine>();
        services.AddSingleton<ImmutabilityEngine>();
        services.AddSingleton<CodeStyleEngine>();
        services.AddSingleton<DependencyEngine>();
        services.AddSingleton<AdvancedLogicEngine>();
        services.AddSingleton<AdvancedStructuralEngine>();
        services.AddSingleton<SemanticRefactoringLibrary>();
        services.AddSingleton<GranularRefactoringEngine>();
        services.AddSingleton<ApiAutomationEngine>();
        services.AddSingleton<ControlFlowEngine>();
        services.AddSingleton<HealthOrchestrationEngine>();
        services.AddSingleton<SymbolNavigationEngine>();
        services.AddSingleton<AntiPatternEngine>();
        services.AddSingleton<CloneDetectionEngine>();
        services.AddSingleton<OutParamRefactoringEngine>();
        services.AddSingleton<DiscoveryEngine>();
        services.AddSingleton<MsToolAugmentEngine>();
        services.AddSingleton<CodeStyleAnalysisEngine>();
        services.AddSingleton<ProjectConsistencyEngine>();
        services.AddSingleton<BreakingChangeEngine>();
        services.AddSingleton<PathDrivenTestEngine>();
        services.AddSingleton<StackOverflowEngine>();
        services.AddSingleton<AsyncBatchEngine>();
        services.AddSingleton<MigrationLedger>();

        // ToolGraph + FailureRouter — pilot: scans SentinelAsyncifyTools for [Produces] attributes.
        ToolGraph toolGraph = BuildToolGraph(new[] { typeof(SentinelAsyncifyTools) });
        services.AddSingleton(toolGraph);
        services.AddSingleton<FailureRouter>();

        return services;
    }

    // ── ToolGraph builder ──────────────────────────────────────────────────────

    private static ToolGraph BuildToolGraph(IEnumerable<Type> toolTypes)
    {
        List<(DataTag Tag, ToolDescriptor Descriptor)> registrations = new List<(DataTag, ToolDescriptor)>();

        foreach (Type toolType in toolTypes)
        {
            foreach (MethodInfo method in toolType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                McpServerToolAttribute? toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr == null)
                {
                    continue;
                }

                string toolName = toolAttr.Name ?? method.Name;

                List<string> allParams = new List<string>();
                List<string> requiredParams = new List<string>();

                foreach (ParameterInfo p in method.GetParameters())
                {
                    Type pt = p.ParameterType;
                    if (pt == typeof(CancellationToken))
                    {
                        continue;
                    }
                    if (pt.IsGenericType && (pt.GetGenericTypeDefinition().Name.StartsWith("RequestContext", StringComparison.Ordinal)
                                          || pt.GetGenericTypeDefinition().FullName?.Contains("RequestContext", StringComparison.Ordinal) == true))
                    {
                        continue;
                    }

                    string paramName = p.Name ?? "";
                    allParams.Add(paramName);
                    if (!p.HasDefaultValue)
                    {
                        requiredParams.Add(paramName);
                    }
                }

                foreach (ProducesAttribute produces in method.GetCustomAttributes<ProducesAttribute>())
                {
                    ToolDescriptor descriptor = new ToolDescriptor
                    {
                        Name = toolName,
                        AllParameterNames = allParams,
                        RequiredParameterNames = requiredParams,
                        PreferenceWeight = produces.Preference,
                    };
                    registrations.Add((produces.Tag, descriptor));
                }
            }
        }

        return ToolGraph.Build(registrations);
    }

    /// <summary>
    /// Registers all MCP tool classes (mode-conditional) and the centralized error filter.
    /// </summary>
    public static IMcpServerBuilder AddRoslynSentinelToolsAdvanced(
        this IMcpServerBuilder mcpBuilder,
        IServiceCollection services,
        HashSet<string> activeModes)
    {
        if (activeModes.Contains("Workspace"))
        {
            services.AddSingleton<SentinelWorkspaceTools>();
            mcpBuilder.WithTools<SentinelWorkspaceTools>();
            services.AddSingleton<DocumentationTools>();
            mcpBuilder.WithTools<DocumentationTools>();
            services.AddSingleton<SentinelSymbolTools>();
            mcpBuilder.WithTools<SentinelSymbolTools>();
            services.AddSingleton<GitTools>();
            mcpBuilder.WithTools<GitTools>();
        }
        if (activeModes.Contains("Intelligence"))
        {
            services.AddSingleton<SentinelIntelligenceTools>();
            mcpBuilder.WithTools<SentinelIntelligenceTools>();
            services.AddSingleton<SentinelScanTools>();
            mcpBuilder.WithTools<SentinelScanTools>();
        }
        if (activeModes.Contains("Refactor"))
        {
            services.AddSingleton<SentinelRefactoringTools>();
            mcpBuilder.WithTools<SentinelRefactoringTools>();
            services.AddSingleton<SentinelAdvancedRefactoringTools>();
            mcpBuilder.WithTools<SentinelAdvancedRefactoringTools>();
            services.AddSingleton<SentinelAugmentTools>();
            mcpBuilder.WithTools<SentinelAugmentTools>();
        }
        if (activeModes.Contains("Modernize"))
        {
            services.AddSingleton<SentinelModernizationTools>();
            mcpBuilder.WithTools<SentinelModernizationTools>();
        }
        if (activeModes.Contains("Quality"))
        {
            services.AddSingleton<SentinelQualityTools>();
            mcpBuilder.WithTools<SentinelQualityTools>();
        }
        if (activeModes.Contains("Generation"))
        {
            services.AddSingleton<SentinelGenerationTools>();
            mcpBuilder.WithTools<SentinelGenerationTools>();
        }
        if (activeModes.Contains("Refactor") || activeModes.Contains("Modernize") ||
            activeModes.Contains("Quality") || activeModes.Contains("Generation"))
        {
            services.AddSingleton<SentinelCodemodTools>();
            mcpBuilder.WithTools<SentinelCodemodTools>();
        }
        if (activeModes.Contains("Asyncify"))
        {
            services.AddSingleton<SentinelAsyncifyTools>();
            mcpBuilder.WithTools<SentinelAsyncifyTools>();
        }

        // Centralized error-to-success filter:
        // Converts "No solution is loaded" InvalidOperationException into a successful
        // CallToolResult so the agent displays the helpful message rather than a generic error.
        mcpBuilder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
                ModelContextProtocol.Protocol.CallToolRequestParams,
                ModelContextProtocol.Protocol.CallToolResult>(
                async (context, cancellationToken) =>
                {
                    try
                    {
                        return await next(context, cancellationToken);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.StartsWith("No solution is loaded", StringComparison.Ordinal))
                    {
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = ex.Message }],
                            IsError = false,
                        };
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Unexpected error in CallTool filter: {ex}");

                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = ex.Message }],
                            IsError = false,
                        };
                    }
                }));
        });

        return mcpBuilder;
    }

    /// <summary>
    /// Pre-warms MSBuildLocator (which takes ~5–8 s on first call) and optionally auto-loads a solution.
    /// Should be called after <see cref="Microsoft.Extensions.Hosting.IHost.Build"/> / <see cref="Microsoft.AspNetCore.Builder.WebApplication.Build"/>.
    /// </summary>
    public static void WarmupAndAutoLoadAdvanced(this IServiceProvider services, string? solutionPath, ILogger? logger = null)
    {
        logger?.LogInformation("Pre-warming MSBuildLocator and workspace manager...");
        var warmupStart = System.Diagnostics.Stopwatch.StartNew();
        var workspaceManager = services.GetRequiredService<PersistentWorkspaceManager>();
        warmupStart.Stop();
        logger?.LogInformation("MSBuildLocator pre-warm complete in {Ms}ms", warmupStart.ElapsedMilliseconds);

        if (!string.IsNullOrEmpty(solutionPath))
        {
            logger?.LogInformation("Auto-loading solution: {Path}", solutionPath);
            _ = workspaceManager.LoadSolutionAsync(solutionPath)
                .ContinueWith(
                    t => logger?.LogError(t.Exception!.GetBaseException(), "Auto-load solution failed: {Path}", solutionPath),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
