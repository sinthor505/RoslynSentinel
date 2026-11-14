using System.Text.Json;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Model-side filter decorator over an <see cref="IAgentToolHost"/>. The server still registers every tool its
/// --include-tools selects; the model is only shown the allowed tools and may only call the allowed tools.
/// </summary>
public sealed class FilteringToolHost : IAgentToolHost
{
    private readonly IAgentToolHost _inner;
    private readonly HashSet<string> _allowed;
    private readonly string[] _allowedOrdered;

    /// <summary>Wraps <paramref name="inner"/> so that only <paramref name="allowedToolNames"/> are listed or callable.</summary>
    /// <param name="inner">The host that talks to the server.</param>
    /// <param name="allowedToolNames">Tool names the model may see and call; matched ordinally.</param>
    public FilteringToolHost(IAgentToolHost inner, IEnumerable<string> allowedToolNames)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(allowedToolNames);
        _inner = inner;
        _allowedOrdered = allowedToolNames.Distinct(StringComparer.Ordinal).ToArray();
        _allowed = new HashSet<string>(_allowedOrdered, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = await _inner.ListToolsAsync(cancellationToken);
        return tools.Where(t => _allowed.Contains(t.Name)).ToList();
    }

    /// <summary>
    /// Forwards an allowed call to the inner host. A call to any other name returns an error result without
    /// reaching the inner host, and the result lists the callable tools.
    /// </summary>
    /// <inheritdoc/>
    public Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (_allowed.Contains(toolName))
        {
            return _inner.CallToolAsync(toolName, arguments, cancellationToken);
        }

        var json = JsonSerializer.Serialize(new
        {
            error = "ToolNotAvailable",
            message = $"Tool '{toolName}' is not available in this session. Callable tools: {string.Join(", ", _allowedOrdered)}.",
        });
        return Task.FromResult(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = json }],
        });
    }
}
