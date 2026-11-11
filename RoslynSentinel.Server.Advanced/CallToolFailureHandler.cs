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
    /// first because it is an unexpected escape (an SDK binding failure or a genuine bug). A JsonException maps to InvalidArgument instead.
    /// </summary>
    public static CallToolResult ToFailureResult(Exception ex, string? toolName)
    {
        if (ex is not ToolException)
        {
            Debug.WriteLine($"Unexpected error in CallTool filter: {ex}");
        }

        // The ISolutionProvider argument is documented as unused by the translator; there is no
        // provider to hand it here (and none is needed).
        var name = string.IsNullOrWhiteSpace(toolName) ? "Tool call" : toolName;
        // Argument-binding failures (SDK converter, e.g. ToolCallReason) are caller mistakes, so they are
        // InvalidArgument here, not in the shared ToolErrorMapper.ToCodeAndMessage (tool-internal catch blocks
        // use that, and an internal JSON parse failure is a real fault). A JsonException thrown inside a tool
        // body also lands here; accepted as rare.
        // Idle restart resets on-demand toolsets while the client still lists their tools (2026-11-10); explain instead of "failed unexpectedly".
        var error = ex is JsonException
            ? new ResultError(ToolErrorCode.InvalidArgument, $"{name}: invalid arguments - {ex.Message} Fix the argument and call again.")
            : ex is McpProtocolException { Message: var message } && message.StartsWith("Unknown tool", StringComparison.Ordinal)
                ? UnknownToolError(toolName, message)
                : ToolErrorMapper.ToResultError(ex, null!, name);
        var envelope = new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = error,
            // A typed throw such as a snippet no-match skips the result marshaller, so its not-found notes are added here.
            Findings = NotFoundEvidence.ForCurrentCall(error.ErrorCode),
        };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, McpJsonUtilities.DefaultOptions) }],
            IsError = true,
        };
    }

    /// <summary>
    /// Explains an unknown-tool protocol error: the tool is in an on-demand toolset (reset by a server restart),
    /// or it is declared but not enabled in this mode, or no tool has that name. A failed lookup falls through to
    /// the not-declared text, so this never throws.
    /// </summary>
    private static ResultError UnknownToolError(string? toolName, string message)
    {
        var tool = toolName ?? string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(tool))
            {
                var open = message.IndexOf('\'');
                var close = message.LastIndexOf('\'');
                tool = open >= 0 && close > open ? message.Substring(open + 1, close - open - 1) : string.Empty;
            }

            var set = ToolsetCatalog.FindSet(tool);
            if (set is not null)
            {
                return new ResultError(
                    ToolErrorCode.TargetIneligible,
                    $"{tool} is not active on this server instance: on-demand toolsets reset when the server restarts. Call "
                    + ToolCallHint.Call("McpToolsetControl", ("toolSet", set.Value.ToString()), ("enabled", "true")) + " and retry.");
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel", StringComparison.Ordinal) == true)
                .ToArray();
            if (McpToolSchemaPatcher.DiscoverAllDeclaredTools(assemblies).Any(t => t.ToolName == tool))
            {
                // TODO ToolSignatureCatalogReflection skips McpServerStatus (Scan_HintCallLiterals_AreValid: "Unknown tool 'McpServerStatus'"), so ToolCallHint.Call may render it without arguments; literal text until that is fixed
                // hint-ok: McpServerStatus is missing from the reflection catalog; literal call keeps its arguments
                return new ResultError(
                    ToolErrorCode.TargetIneligible,
                    $"{tool} exists but is not enabled in this server's mode. McpServerStatus(toolListing: inactive, toolNameFilter: {tool}) shows how it is enabled.");
            }
        }
        catch (Exception)
        {
            // Lookup failure: fall through to the not-declared text rather than surface a second error.
        }

        // hint-ok: McpServerStatus is missing from the reflection catalog; literal call keeps its arguments
        return new ResultError(
            ToolErrorCode.NotFound,
            $"There is no tool named '{tool}' on this server. Check the tool name; McpServerStatus(toolListing: all) lists every tool.");
    }
}
