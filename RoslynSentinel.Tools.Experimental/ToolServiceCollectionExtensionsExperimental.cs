using Microsoft.Extensions.DependencyInjection.Extensions;

using RoslynSentinel.Tools.Experimental;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Tool-layer DI registration for the Experimental tool classes (see <c>ToolServiceCollectionExtensionsBasic</c>
/// in Tools.Basic for the shared rationale). No <c>AddRoslynSentinelToolImplsExperimental</c> exists because no
/// Experimental tool class has a registered helper dependency.
/// </summary>
public static class ToolServiceCollectionExtensionsExperimental
{
    /// <summary>
    /// Registers every <c>[McpServerToolType]</c> class in Tools.Experimental (one <c>TryAddSingleton</c> each),
    /// with no mode gating. A new Tools.Experimental tool class must be added here.
    /// </summary>
    public static IServiceCollection AddRoslynSentinelToolClassesExperimental(this IServiceCollection services)
    {
        services.TryAddSingleton<CodeTransformTools>();

        return services;
    }
}
