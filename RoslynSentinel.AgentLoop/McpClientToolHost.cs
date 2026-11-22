using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// <see cref="IAgentToolHost"/> over a real <see cref="McpClient"/>. The tool list and calls are
/// forwarded unchanged; the mapping to <see cref="AgentToolDefinition"/> is the one the runner
/// always used, so the model sees the same tool catalog as before the seam existed.
/// </summary>
public sealed class McpClientToolHost(McpClient client) : IAgentToolHost
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var mcpTools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return mcpTools.Select(t => new AgentToolDefinition
        {
            Name = t.Name,
            Description = t.Description,
            ParametersSchema = t.JsonSchema,
        }).ToList();
    }

    /// <inheritdoc/>
    public async Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken) =>
        await client.CallToolAsync(
            toolName,
            arguments,
            progress: null,
            options: null,
            cancellationToken: cancellationToken);
}
