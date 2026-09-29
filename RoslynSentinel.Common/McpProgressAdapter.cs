using ModelContextProtocol;

namespace RoslynSentinel.Common;

/// <summary>
/// Forwards engine progress reports to the MCP progress sink synchronously. Deliberately not
/// Progress&lt;T&gt;, which posts reports asynchronously and can reorder them or deliver them
/// after the tool call has returned.
/// </summary>
public sealed class McpProgressAdapter(IProgress<ProgressNotificationValue> inner) : IProgress<EngineProgress>
{
    public void Report(EngineProgress value) =>
        inner.Report(new ProgressNotificationValue { Progress = value.Progress, Total = value.Total, Message = value.Message });
}

public static class McpProgressExtensions
{
    public static IProgress<EngineProgress>? ToEngineProgress(this IProgress<ProgressNotificationValue>? progress) =>
        progress is null ? null : new McpProgressAdapter(progress);
}
