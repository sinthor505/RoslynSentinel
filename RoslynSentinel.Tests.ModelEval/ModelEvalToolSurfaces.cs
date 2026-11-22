using Microsoft.Extensions.DependencyInjection;

using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Ready-made <c>configureToolSurface</c> callbacks for <see cref="ModelEvalMcpEnvironment.CreateAsync"/>, for worlds that
/// need a surface other than the default (<c>AddRoslynSentinelToolsAdvanced</c> with the <see cref="ChainEvalToolSurface"/>
/// modes and hidden tools). Each is a method group, so a caller passes it as is or calls it from its own callback and then
/// adds request filters.
/// </summary>
internal static class ModelEvalToolSurfaces
{
    /// <summary>Fresh set per call: the registration call may keep the instance.</summary>
    public static HashSet<string> NewBasicModes() =>
        new(new[] { "Refactor", "Workspace" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Basic tool surface: <c>AddRoslynSentinelToolsBasic</c> with the "Refactor" and "Workspace" modes and no hidden
    /// tools (the Advanced-only tool classes and the WholeFileWrite tools are not registered). The mode strings are the
    /// exact ones AddRoslynSentinelToolsBasic checks ("Refactor", not "Refactoring").
    /// </summary>
    public static void Basic(IMcpServerBuilder mcpBuilder, IServiceCollection services) =>
        mcpBuilder.AddRoslynSentinelToolsBasic(services, NewBasicModes());

    /// <summary>Fresh set per call: the registration call may keep the instance.</summary>
    public static HashSet<string> NewWorkspaceAndSymbolNavigationModes() =>
        new(new[] { "Workspace", "SymbolNavigation" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The small Advanced surface the fake-model runner tests use (<see cref="ModelEvalFakeModelEnvironment"/>'s default):
    /// <c>AddRoslynSentinelToolsAdvanced</c> with the "Workspace" and "SymbolNavigation" modes (McpServerStatus,
    /// LocateSymbol, GetFileOutline; "Workspace" alone no longer covers LocateSymbol since the DI split) and no hidden tools.
    /// </summary>
    public static void WorkspaceAndSymbolNavigation(IMcpServerBuilder mcpBuilder, IServiceCollection services) =>
        mcpBuilder.AddRoslynSentinelToolsAdvanced(services, NewWorkspaceAndSymbolNavigationModes());
}
