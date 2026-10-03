namespace RoslynSentinel.Common;

/// <summary>
/// Optional per-tool allow-list: when one is registered in the service collection before the tool
/// classes are, <see cref="McpToolSchemaPatcher.WithSentinelTools{TToolType}"/> registers only the
/// tool methods whose MCP name is in <see cref="ToolNames"/>. Tool classes are otherwise selected
/// whole (ToolClassRegistry), which is too coarse for a toolset that wants some tools of a class and
/// not others. Registered only for the exclusive "claude-lean" mode; absent for every other mode,
/// where every tool of every active class stays registered.
/// </summary>
public sealed class ToolAllowList
{
    public IReadOnlySet<string> ToolNames { get; }

    public ToolAllowList(IEnumerable<string> toolNames)
    {
        ToolNames = new HashSet<string>(toolNames, StringComparer.Ordinal);
    }

    public bool Allows(string toolName) => ToolNames.Contains(toolName);
}
