using System.Reflection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RoslynSentinel.Tools.Advanced;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Tool-layer DI registration for the Advanced flavor (see <c>ToolServiceCollectionExtensionsBasic</c> in
/// Tools.Basic for the shared rationale). All <c>TryAdd*</c>; mode gating and MCP exposure stay in the Server project.
/// </summary>
public static class ToolServiceCollectionExtensionsAdvanced
{
    /// <summary>
    /// Registers the dependencies of the Advanced tool classes: <see cref="ToolGraph"/> (scanned from
    /// <see cref="AsyncifyTools"/>'s <c>[Produces]</c> attributes) and <see cref="FailureRouter"/>. The Advanced
    /// tools have no <c>*Impl</c> helpers registered in DI.
    /// </summary>
    public static IServiceCollection AddRoslynSentinelToolImplsAdvanced(this IServiceCollection services)
    {
        // ToolGraph + FailureRouter -> pilot: scans AsyncifyTools for [Produces] attributes.
        services.TryAddSingleton(BuildToolGraph(new[] { typeof(AsyncifyTools) }));
        services.TryAddSingleton<FailureRouter>();

        return services;
    }

    /// <summary>
    /// Registers every <c>[McpServerToolType]</c> class in Tools.Advanced (one <c>TryAddSingleton</c> each), with
    /// no mode gating. A host registers only its active classes itself; a test calls this to get all of them.
    /// A new Tools.Advanced tool class must be added here.
    /// </summary>
    public static IServiceCollection AddRoslynSentinelToolClassesAdvanced(this IServiceCollection services)
    {
        services.TryAddSingleton<AdvancedRefactoringTools>();
        services.TryAddSingleton<AsyncifyTools>();
        services.TryAddSingleton<CommentingTools>();
        services.TryAddSingleton<GenerationTools>();
        services.TryAddSingleton<IntelligenceTools>();
        services.TryAddSingleton<ModernizationTools>();
        services.TryAddSingleton<QualityTools>();
        services.TryAddSingleton<ScanTools>();
        services.TryAddSingleton<SubAgentEvalTools>();
        services.TryAddSingleton<SubAgentTools>();

        return services;
    }

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
}
