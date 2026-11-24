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
        HashSet<string>? excludeTools = null,
        IReadOnlySet<string>? hideTools = null)
    {
        var resolvedIncludeTools = includeTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedExcludeTools = excludeTools ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Registers Workspace-mode tools, Refactor-mode's RefactoringStructuralTools, and both
        // request filters (including the drift-check filter).
        // Basic's registration only acts on the Basic tool classes in ModeToToolClasses, so an
        // Advanced-only --include-tools name (e.g. AsyncifyTools) is inert there -> it's
        // handled below against the same map instead.
        mcpBuilder.AddRoslynSentinelToolsBasic(services, activeModes, resolvedIncludeTools, resolvedExcludeTools, hideTools);

        // Unconditional, mode-independent dependencies of the Advanced tool classes (ToolGraph, FailureRouter).
        services.AddRoslynSentinelToolImplsAdvanced();

        var activeToolClasses = ServerStartupHelpers.ResolveActiveToolClasses(
            activeModes,
            ToolClassRegistry.ModeToToolClasses,
            resolvedIncludeTools,
            resolvedExcludeTools);

        // claude-lean: AdvancedRefactoringTools is on demand for all its tools (ChangeSignature and MoveMember are core
        // in CoreRefactoringTools, ToolClassRegistry.ClaudeLeanToolNames), which McpToolsetControlTool may add later.
        // ToolSurfaceStartupPopulation applies the per-tool allow-list to the startup tools, keeping those tools out of
        // the startup surface. See the same widening in AddRoslynSentinelToolsBasic.
        if (activeModes.Contains(ToolClassRegistry.ClaudeLeanMode)
            && ToolClassRegistry.ClaudeLeanOnDemandToolClasses.Contains("AdvancedRefactoringTools")
            && !resolvedExcludeTools.Contains("AdvancedRefactoringTools"))
        {
            activeToolClasses.Add("AdvancedRefactoringTools");
        }

        // Sees the claude-lean widening above, so it stays after that block.
        RoslynSentinelServiceExtensionsBasic.AddStartupToolClasses(services, ToolClassRegistry.SelectStartupToolTypes(ToolClassRegistry.AdvancedToolClassTypes, activeToolClasses, activeModes.Contains(ToolClassRegistry.ClaudeLeanMode)));

        return mcpBuilder;
    }
}
