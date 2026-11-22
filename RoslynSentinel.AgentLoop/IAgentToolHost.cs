using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// The tool surface a <see cref="ModelAgentRunner"/> drives: lists the tools the model may call and
/// executes one call. <see cref="McpClientToolHost"/> is the production implementation over a real
/// <see cref="ModelContextProtocol.Client.McpClient"/>; other implementations let a caller replay a
/// recorded conversation against a fake tool host.
/// </summary>
public interface IAgentToolHost
{
    /// <summary>Lists the tools offered to the model, in the form the runner sends them to the backend.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken);

    /// <summary>Executes one tool call and returns the raw MCP result.</summary>
    /// <param name="toolName">Name of the tool, as listed by <see cref="ListToolsAsync"/>.</param>
    /// <param name="arguments">Parsed JSON arguments of the call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);
}
