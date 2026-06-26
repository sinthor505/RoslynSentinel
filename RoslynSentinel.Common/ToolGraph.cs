namespace RoslynSentinel.Common;

/// <summary>Describes a tool that produces a given <see cref="DataTag"/>.</summary>
public sealed class ToolDescriptor
{
    public string Name { get; init; } = "";
    /// <summary>All user-facing parameter names (excludes CancellationToken, RequestContext, etc.).</summary>
    public IReadOnlyList<string> AllParameterNames { get; init; } = Array.Empty<string>();
    /// <summary>Parameter names that are required (no default value).</summary>
    public IReadOnlyList<string> RequiredParameterNames { get; init; } = Array.Empty<string>();
    /// <summary>Producer-preference weight; highest wins when multiple producers exist. Ties broken by ordinal name sort.</summary>
    public int PreferenceWeight { get; init; }
}

/// <summary>
/// Maps producer/consumer relationships between tools, keyed on <see cref="DataTag"/>.
/// Built at startup via <see cref="Build"/>; immutable after construction.
/// </summary>
public sealed class ToolGraph
{
    private readonly Dictionary<string, List<string>> _consumersByTag;
    private readonly Dictionary<DataTag, List<ToolDescriptor>> _producersByDataTag;

    public static ToolGraph Empty { get; } = new ToolGraph(null, null);

    internal ToolGraph(
        Dictionary<string, List<string>>? consumersByTag,
        Dictionary<DataTag, List<ToolDescriptor>>? producersByDataTag)
    {
        _consumersByTag = consumersByTag ?? new Dictionary<string, List<string>>();
        _producersByDataTag = producersByDataTag ?? new Dictionary<DataTag, List<ToolDescriptor>>();
    }

    /// <summary>
    /// Builds a <see cref="ToolGraph"/> from an explicit list of tag→descriptor registrations.
    /// Duplicate tool names for the same tag are silently de-duplicated (first wins).
    /// </summary>
    public static ToolGraph Build(IEnumerable<(DataTag Tag, ToolDescriptor Descriptor)> registrations)
    {
        Dictionary<DataTag, List<ToolDescriptor>> producers = new Dictionary<DataTag, List<ToolDescriptor>>();

        foreach ((DataTag tag, ToolDescriptor descriptor) in registrations)
        {
            if (!producers.TryGetValue(tag, out List<ToolDescriptor>? list))
            {
                list = new List<ToolDescriptor>();
                producers[tag] = list;
            }

            bool alreadyRegistered = false;
            foreach (ToolDescriptor existing in list)
            {
                if (existing.Name == descriptor.Name)
                {
                    alreadyRegistered = true;
                    break;
                }
            }

            if (!alreadyRegistered)
            {
                list.Add(descriptor);
            }
        }

        return new ToolGraph(null, producers);
    }

    /// <summary>Returns all tools that produce <paramref name="tag"/>, ordered by preference (highest first), ties by ordinal name.</summary>
    public IReadOnlyList<ToolDescriptor> ProducersOf(DataTag tag)
    {
        if (!_producersByDataTag.TryGetValue(tag, out List<ToolDescriptor>? producers))
        {
            return Array.Empty<ToolDescriptor>();
        }

        List<ToolDescriptor> sorted = new List<ToolDescriptor>(producers);
        sorted.Sort((a, b) =>
        {
            int weightCmp = b.PreferenceWeight.CompareTo(a.PreferenceWeight);
            if (weightCmp != 0)
            {
                return weightCmp;
            }
            return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        return sorted;
    }

    /// <summary>Returns all tools that consume <paramref name="tag"/>, excluding <paramref name="excludeTool"/>.</summary>
    public IReadOnlyList<string> ConsumersOf(string tag, string excludeTool)
    {
        if (!_consumersByTag.TryGetValue(tag, out List<string>? tools))
        {
            return Array.Empty<string>();
        }

        List<string> result = new List<string>();
        foreach (string tool in tools)
        {
            if (tool != excludeTool)
            {
                result.Add(tool);
            }
        }

        return result;
    }
}

public static class ToolHints
{
    public static string? OnSuccess(ToolGraph graph, string selfTool, params string[] producedTags)
    {
        // TODO: implement using ConsumersOf once consumer index is populated
        return null;
    }

    public static string? OnMissingInput(ToolGraph graph, string selfTool, string neededTag)
    {
        // TODO: implement using ProducersOf once callers are migrated to DataTag-keyed lookup
        return null;
    }
}
