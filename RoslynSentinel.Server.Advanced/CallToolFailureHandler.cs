using System.Diagnostics;
using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Turns an exception that escaped a tool call into the same structured failure a tool returns on its
/// own: a <see cref="SentinelCallToolResult{T}"/> body (<c>isError</c>, <c>errorData</c> with
/// <c>errorCode</c>, <c>message</c>, <c>detail</c>, <c>structuredDetail</c>) built by
/// <see cref="ToolErrorMapper.ToResultError"/>, so a typed throw (<see cref="ToolException"/>) keeps its
/// code and payload on the wire instead of collapsing to plain text. Used by the central call-tool filter
/// (<c>AddWorkspaceNotLoadedFilter</c> in <see cref="RoslynSentinelServiceExtensionsBasic"/>).
/// Public rather than internal because Server.Advanced has no InternalsVisibleTo for its tests.
/// </summary>
public static class CallToolFailureHandler
{
    /// <summary>
    /// Runs <paramref name="invoke"/> and maps any exception it throws to a failure result. The one
    /// exception left alone is an <see cref="OperationCanceledException"/> raised while
    /// <paramref name="requestCancellationToken"/> is cancelled (the client aborted the request): that is
    /// rethrown unchanged for the SDK to handle. An <see cref="OperationCanceledException"/> or
    /// <see cref="TimeoutException"/> with the request token still live is an internal cancellation or
    /// timeout and becomes the structured Cancelled / TimedOut error.
    /// </summary>
    public static async ValueTask<CallToolResult> InvokeAsync(
        Func<ValueTask<CallToolResult>> invoke, string? toolName, CancellationToken requestCancellationToken)
    {
        try
        {
            return await invoke();
        }
        catch (OperationCanceledException) when (requestCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToFailureResult(ex, toolName);
        }
    }

    /// <summary>
    /// Builds the failure <see cref="CallToolResult"/> for <paramref name="ex"/>: protocol <c>IsError</c> true
    /// and one text block holding the serialized failure envelope, serialized with the options the SDK uses
    /// for a tool's own return value so the two shapes match. A non-<see cref="ToolException"/> is logged
    /// first because it is an unexpected escape (an SDK binding failure or a genuine bug).
    /// </summary>
    public static CallToolResult ToFailureResult(Exception ex, string? toolName)
    {
        if (ex is not ToolException)
        {
            Debug.WriteLine($"Unexpected error in CallTool filter: {ex}");
        }

        var envelope = new SentinelCallToolResult<object>
        {
            IsError = true,
            // The ISolutionProvider argument is documented as unused by the translator; there is no
            // provider to hand it here (and none is needed).
            ErrorData = ToolErrorMapper.ToResultError(ex, null!, string.IsNullOrWhiteSpace(toolName) ? "Tool call" : toolName),
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, McpJsonUtilities.DefaultOptions) }],
            IsError = true,
        };
    }
}
