using System.Collections.Concurrent;

namespace RoslynSentinel.Common;

/// <summary>
/// Registry of tool parameters that the Lean schema profile stripped from a tool's emitted input
/// schema (McpToolSchemaPatcher.ApplyLeanProfile) but that the C# method still binds. The argument
/// validator reads the emitted schema as its allow-list, so without this registry it would reject a
/// call passing a hidden parameter as "Unknown parameter". Tool names are matched
/// case-insensitively against the registered names; parameter names are matched ordinally.
/// </summary>
public static class HiddenSchemaParams
{
    private static readonly ConcurrentDictionary<string, HashSet<string>> Hidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records <paramref name="names"/> as hidden-but-accepted for <paramref name="toolName"/>. Registering nothing is a no-op.</summary>
    public static void Register(string toolName, IEnumerable<string> names)
    {
        var added = names.ToList();
        if (added.Count == 0)
        {
            return;
        }

        var set = Hidden.GetOrAdd(toolName, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (set)
        {
            set.UnionWith(added);
        }
    }

    /// <summary>True when <paramref name="parameterName"/> was stripped from <paramref name="toolName"/>'s schema but is still accepted at call time.</summary>
    public static bool IsHidden(string? toolName, string parameterName)
    {
        if (string.IsNullOrEmpty(toolName) || !Hidden.TryGetValue(toolName, out var set))
        {
            return false;
        }

        lock (set)
        {
            return set.Contains(parameterName);
        }
    }

    /// <summary>Empties the registry. Intended for tests.</summary>
    public static void Clear() => Hidden.Clear();
}
