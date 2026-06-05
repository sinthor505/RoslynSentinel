namespace RoslynSentinel.Server;

public sealed class ToolGraph
{
    private readonly Dictionary<string, List<string>> _consumersByTag;
    private readonly Dictionary<string, List<string>> _producersByTag;

    public IReadOnlyList<string> ConsumersOf(string tag, string excludeTool)
    {
        if (this._consumersByTag.TryGetValue(tag, out List<string>? tools) == false)
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

    // ProducersOf: identical against _producersByTag
}

public static class ToolHints
{
    public static string? OnSuccess(ToolGraph graph, string selfTool, params string[] producedTags)
    {
        // for each produced tag, gather ConsumersOf(tag, selfTool); join, cap length, return null if empty
        foreach (string tool in producedTags)
        {

        }

        return "ToolHints.OnSuccess is not implemented";
    }

    public static string? OnMissingInput(ToolGraph graph, string selfTool, string neededTag)
    {
        // ProducersOf(neededTag, selfTool) -> "Get a valid {neededTag} from: tool1, tool2"

        return "ToolHints.OnMissingInput is not implemented";
    }
}
