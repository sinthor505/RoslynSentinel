using System.Reflection;

using ModelContextProtocol.Server;

namespace RoslynSentinel;

/// <summary>
/// Access level for a tool method during an unrecoverable breaker trip.
/// </summary>
public enum UnrecoverableBreakerAccess
{
    /// <summary>
    /// Tool is blocked during an unrecoverable breaker trip (default if attribute absent).
    /// </summary>
    Blocked,

    /// <summary>
    /// Tool is allowed during an unrecoverable breaker trip.
    /// </summary>
    Allowed
}

/// <summary>
/// Marks a tool method as allowed during an unrecoverable breaker trip.
/// Absence of this attribute defaults to Blocked (safe allow-list model: a forgotten tool is refused,
/// not accidentally enabled). The unrecoverable breaker enforces session-fatal drift safety during
/// recovery by restricting tools to a small whitelist of read-only inspection and abort operations.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class UnrecoverableBreakerAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the UnrecoverableBreakerAttribute.
    /// </summary>
    public UnrecoverableBreakerAttribute(UnrecoverableBreakerAccess access)
    {
        Access = access;
    }

    /// <summary>
    /// Access level for this tool method during an unrecoverable breaker trip.
    /// </summary>
    public UnrecoverableBreakerAccess Access { get; }
}

/// <summary>
/// Policy for determining which tool names are allowed during an unrecoverable breaker trip.
/// Uses reflection to discover tool methods marked with [UnrecoverableBreaker(Allowed)].
/// </summary>
public static class UnrecoverableBreakerPolicy
{
    private static readonly Lazy<HashSet<string>> AllowedToolNamesInternal = new(
        () => BuildAllowedToolNames(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Returns the set of tool names allowed during an unrecoverable breaker trip.
    /// </summary>
    public static IReadOnlyCollection<string> AllowedToolNames => AllowedToolNamesInternal.Value;

    /// <summary>
    /// Determines whether a tool name is allowed during an unrecoverable breaker trip.
    /// </summary>
    /// <param name="toolName">The tool name to check (null or empty returns false).</param>
    /// <returns>True if the tool is allowed; false otherwise.</returns>
    public static bool IsAllowed(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
            return false;

        return AllowedToolNamesInternal.Value.Contains(toolName);
    }

    /// <summary>
    /// Discovers all tool methods with [UnrecoverableBreaker(Allowed)] by reflecting over loaded assemblies.
    /// </summary>
    private static HashSet<string> BuildAllowedToolNames()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel.") ?? false);

            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Use only the types that loaded successfully
                    types = ex.Types.Where(t => t != null).ToArray()!;
                }

                foreach (var type in types)
                {
                    var toolTypeAttr = type.GetCustomAttribute<McpServerToolTypeAttribute>();
                    if (toolTypeAttr == null)
                        continue;

                    var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var method in methods)
                    {
                        var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                        if (toolAttr == null)
                            continue;

                        var breakerAttr = method.GetCustomAttribute<UnrecoverableBreakerAttribute>();
                        if (breakerAttr?.Access == UnrecoverableBreakerAccess.Allowed)
                        {
                            var toolName = toolAttr.Name ?? method.Name;
                            if (!string.IsNullOrEmpty(toolName))
                            {
                                result.Add(toolName);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // If reflection fails, return empty set (safe default: refuse all)
        }

        return result;
    }
}
