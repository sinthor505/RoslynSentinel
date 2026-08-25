using System.Diagnostics;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
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
    /// Registers all Roslyn analysis engine singletons into the DI container: every engine
    /// Basic registers (via <see cref="RoslynSentinelServiceExtensionsBasic.AddRoslynSentinelEnginesBasic"/>,
    /// the shared base — Advanced does not maintain its own separate copy of that list) plus the
    /// additional engines only Advanced's tool classes need.
    /// </summary>
    public static IServiceCollection AddRoslynSentinelEnginesAdvanced(this IServiceCollection services)
    {
        services.AddRoslynSentinelEnginesBasic();

        // Advanced-only engines: not registered by Basic (either because Basic's tool classes
        // don't use them, or because they're deliberately gated to the fuller Advanced tool set).
        services.AddSingleton<MetricsEngine>();
        services.AddSingleton<CodeHealingEngine>();
        services.AddSingleton<PerformanceEngine>();
        services.AddSingleton<SecurityEngine>();
        services.AddSingleton<TestingEngine>();
        services.AddSingleton<ModernizationEngine>();
        services.AddSingleton<DependencyInjectionEngine>();
        services.AddSingleton<ArchitecturalEngine>();
        services.AddSingleton<AdvancedRefactoringEngine>();
        services.AddSingleton<DocumentationEngine>();
        services.AddSingleton<SecurityAndSafetyEngine>();
        services.AddSingleton<ApiIntegrationEngine>();
        services.AddSingleton<AsyncOptimizationEngine>();
        services.AddSingleton<AdvancedTypeEngine>();
        services.AddSingleton<ModernLoggingEngine>();
        services.AddSingleton<LogicOptimizationEngine>();
        services.AddSingleton<ModernizationUpgradeEngine>();
        services.AddSingleton<AsyncSafetyEngine>();
        services.AddSingleton<DeadCodeEngine>();
        services.AddSingleton<RefinementEngine>();
        services.AddSingleton<AdvancedLogicEngine>();
        services.AddSingleton<AdvancedStructuralEngine>();
        services.AddSingleton<ApiAutomationEngine>();
        services.AddSingleton<HealthOrchestrationEngine>();
        services.AddSingleton<AntiPatternEngine>();
        services.AddSingleton<CloneDetectionEngine>();
        services.AddSingleton<OutParamRefactoringEngine>();
        services.AddSingleton<PathDrivenTestEngine>();
        services.AddSingleton<AsyncBatchEngine>();
        services.AddSingleton<MigrationLedger>();
        services.AddSingleton<CommentingEngine>();

        // LmStudioClient talks to a locally-hosted LM Studio server (OpenAI-compatible
        // /v1/chat/completions). AddHttpClient<LmStudioClient> registers the concrete type keyed
        // to its own HttpClient; the extra AddSingleton<ILlmClient> below forwards to that same
        // instance so CommentingEngine (which depends on the interface) resolves it.
        services.AddHttpClient<LmStudioClient>(client =>
        {
            client.BaseAddress = new Uri(LlmOptions.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(LlmOptions.TimeoutSeconds);
        });
        services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<LmStudioClient>());

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
    /// Delegates the modes/tools/filters Advanced shares with Basic to
    /// <see cref="RoslynSentinelServiceExtensionsBasic.AddRoslynSentinelToolsBasic"/> (the shared
    /// base — this used to be a fully separate hand-duplicated list, which is how it drifted out
    /// of sync with Basic's filter set: Basic's content-drift-check filter was missing here for a
    /// time because nothing forced the two lists to stay in sync). Only registers the additional
    /// tool classes/modes Advanced has that Basic doesn't.
    /// </summary>
    public static IMcpServerBuilder AddRoslynSentinelToolsAdvanced(
        this IMcpServerBuilder mcpBuilder,
        IServiceCollection services,
        HashSet<string> activeModes)
    {
        // Registers Workspace-mode tools, Refactor-mode's SentinelRefactoringTools/
        // SentinelAugmentTools, and both request filters (including the drift-check filter).
        mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes);

        if (activeModes.Contains("Intelligence"))
        {
            services.AddSingleton<SentinelIntelligenceTools>();
            mcpBuilder.WithTools<SentinelIntelligenceTools>();
            services.AddSingleton<SentinelScanTools>();
            mcpBuilder.WithTools<SentinelScanTools>();
        }
        if (activeModes.Contains("Refactor"))
        {
            // SentinelRefactoringTools/SentinelAugmentTools already registered above via Basic.
            services.AddSingleton<SentinelAdvancedRefactoringTools>();
            mcpBuilder.WithTools<SentinelAdvancedRefactoringTools>();
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
            services.AddSingleton<SentinelCommentingTools>();
            mcpBuilder.WithTools<SentinelCommentingTools>();
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

        return mcpBuilder;
    }

    /// <summary>
    /// Pre-warms MSBuildLocator (which takes ~5–8 s on first call) and optionally auto-loads a solution.
    /// Should be called after <see cref="Microsoft.Extensions.Hosting.IHost.Build"/> / <see cref="Microsoft.AspNetCore.Builder.WebApplication.Build"/>.
    /// </summary>
    public static void WarmupAndAutoLoadAdvanced(this IServiceProvider services, string? solutionPath, ILogger? logger = null, string? baseRepoDirectory = null)
    {
        logger?.LogInformation("Pre-warming MSBuildLocator and workspace manager...");
        var warmupStart = System.Diagnostics.Stopwatch.StartNew();
        var workspaceManager = services.GetRequiredService<PersistentWorkspaceManager>();
        warmupStart.Stop();
        logger?.LogInformation("MSBuildLocator pre-warm complete in {Ms}ms", warmupStart.ElapsedMilliseconds);

        if (!string.IsNullOrEmpty(baseRepoDirectory))
        {
            workspaceManager.BaseRepoDirectory = baseRepoDirectory;
        }

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
