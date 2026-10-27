using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Experimental;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Shared service registration helpers used by both the stdio server (Program.cs)
/// and the separate HTTP host (RoslynSentinel.HttpHost).
/// </summary>
public static class RoslynSentinelServiceExtensionsAdvanced
{
    /// <summary>
    /// Registers the LM Studio-backed <see cref="ILlmClient"/>. Kept in the Server project because
    /// <c>AddHttpClient</c> needs Microsoft.Extensions.Http, which no lower layer references. Hosts call it
    /// after <c>AddRoslynSentinelEnginesAdvanced</c>; a test that needs <see cref="ILlmClient"/> registers a fake instead.
    /// </summary>
    public static IServiceCollection AddRoslynSentinelLlmClient(this IServiceCollection services)
    {
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

        return services;
    }

    /// <summary>
    /// Registers all MCP tool classes (mode-conditional, with optional per-class
    /// <paramref name="includeTools"/>/<paramref name="excludeTools"/> overrides -> see
    /// <see cref="ServerStartupHelpers.ResolveActiveToolClasses"/>) and the centralized error filter.
    /// Delegates the modes/tools/filters Advanced shares with Basic to
    /// <see cref="RoslynSentinelServiceExtensionsBasic.AddRoslynSentinelToolsBasic"/> (the shared
    /// base -> this used to be a fully separate hand-duplicated list, which is how it drifted out
    /// of sync with Basic's filter set: Basic's content-drift-check filter was missing here for a
    /// time because nothing forced the two lists to stay in sync). Only registers the additional
    /// tool classes/modes Advanced has that Basic doesn't.
    /// </summary>
    public static IMcpServerBuilder AddRoslynSentinelToolsAdvanced(
        this IMcpServerBuilder mcpBuilder,
        IServiceCollection services,
        HashSet<string> activeModes,
        HashSet<string>? includeTools = null,
        HashSet<string>? excludeTools = null)
    {
        var resolvedIncludeTools = includeTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedExcludeTools = excludeTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Registers Workspace-mode tools, Refactor-mode's RefactoringStructuralTools, and both
        // request filters (including the drift-check filter).
        // Basic's registration only acts on the Basic tool classes in ModeToToolClasses, so an
        // Advanced-only --include-tools name (e.g. AsyncifyTools) is inert there -> it's
        // handled below against the same map instead.
        mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes, resolvedIncludeTools, resolvedExcludeTools);

        // Unconditional, mode-independent dependencies of the Advanced tool classes (ToolGraph, FailureRouter).
        services.AddRoslynSentinelToolImplsAdvanced();

        var activeToolClasses = ServerStartupHelpers.ResolveActiveToolClasses(
            activeModes,
            ToolClassRegistry.ModeToToolClasses,
            resolvedIncludeTools,
            resolvedExcludeTools);

        // claude-lean: AdvancedRefactoringTools is an on-demand class (McpToolsetControlTool may add its tools
        // later). Registering the class here is dependency-only: the per-tool allow-list that
        // WithSentinelTools reads makes it register none of its tools at startup. See the same widening
        // in AddRoslynSentinelToolsBasic.
        if (activeModes.Contains(ToolClassRegistry.ClaudeLeanMode)
            && ToolClassRegistry.ClaudeLeanOnDemandToolClasses.Contains("AdvancedRefactoringTools")
            && !resolvedExcludeTools.Contains("AdvancedRefactoringTools"))
        {
            activeToolClasses.Add("AdvancedRefactoringTools");
        }

        if (activeToolClasses.Contains("IntelligenceTools"))
        {
            services.AddSingleton<IntelligenceTools>();
            mcpBuilder.WithSentinelTools<IntelligenceTools>();
        }
        if (activeToolClasses.Contains("ScanTools"))
        {
            services.AddSingleton<ScanTools>();
            mcpBuilder.WithSentinelTools<ScanTools>();
        }
        if (activeToolClasses.Contains("AdvancedRefactoringTools"))
        {
            // RefactoringStructuralTools already registered above via Basic.
            services.AddSingleton<AdvancedRefactoringTools>();
            mcpBuilder.WithSentinelTools<AdvancedRefactoringTools>();
        }
        if (activeToolClasses.Contains("ModernizationTools"))
        {
            services.AddSingleton<ModernizationTools>();
            mcpBuilder.WithSentinelTools<ModernizationTools>();
        }
        if (activeToolClasses.Contains("QualityTools"))
        {
            services.AddSingleton<QualityTools>();
            mcpBuilder.WithSentinelTools<QualityTools>();
        }
        if (activeToolClasses.Contains("GenerationTools"))
        {
            services.AddSingleton<GenerationTools>();
            mcpBuilder.WithSentinelTools<GenerationTools>();
        }
        if (activeToolClasses.Contains("CommentingTools"))
        {
            services.AddSingleton<CommentingTools>();
            mcpBuilder.WithSentinelTools<CommentingTools>();
        }
        if (activeToolClasses.Contains("CodeTransformTools"))
        {
            services.AddSingleton<CodeTransformTools>();
            mcpBuilder.WithSentinelTools<CodeTransformTools>();
        }
        if (activeToolClasses.Contains("AsyncifyTools"))
        {
            services.AddSingleton<AsyncifyTools>();
            mcpBuilder.WithSentinelTools<AsyncifyTools>();
        }
        if (activeToolClasses.Contains("SubAgentEvalTools"))
        {
            services.AddSingleton<SubAgentEvalTools>();
            mcpBuilder.WithSentinelTools<SubAgentEvalTools>();
        }
        if (activeToolClasses.Contains("SubAgentTools"))
        {
            services.AddSingleton<SubAgentTools>();
            mcpBuilder.WithSentinelTools<SubAgentTools>();
        }

        return mcpBuilder;
    }
}
