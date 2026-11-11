using System.Diagnostics;
using System.Text.Json;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// The logic of the loop-latch call-tool filter (<c>AddLoopLatchFilter</c> in <see cref="RoslynSentinelServiceExtensionsBasic"/>),
/// kept out of the filter lambda so tests can drive it without building an McpRequestContext (same split as
/// <see cref="ToolTelemetryRecorder"/>). Public rather than internal because Server.Advanced has no InternalsVisibleTo for its tests.
/// See <see cref="ToolLoopLatch"/> and docs/current/proposals/proposal_loop_latch.md.
/// </summary>
public static class LoopLatchFilter
{
    /// <summary>
    /// With the flag on (<see cref="LoopLatchOptions.Enabled"/>) and a latch present: refuses a call of a latched tool without
    /// running <paramref name="next"/>; otherwise runs it and records the outcome (an <c>IsError</c> result is a failure, with
    /// its error code and message read from the body; anything else is a success). With the flag off, no latch, or no tool name it
    /// is a plain pass-through. Latch bookkeeping never throws into the call; an exception from <paramref name="next"/> is rethrown
    /// unrecorded (the catch-all filter is inside this one, so only a client abort reaches here).
    /// </summary>
    public static async ValueTask<CallToolResult> InvokeAsync(
        ToolLoopLatch? latch, string? agentId, string? toolName, Func<string, bool>? isCallable, Func<ValueTask<CallToolResult>> next)
    {
        if (latch is null || !LoopLatchOptions.Enabled || string.IsNullOrEmpty(toolName))
        {
            return await next();
        }

        try
        {
            var refusal = latch.TryRefuse(agentId, toolName, isCallable);
            if (refusal is not null)
            {
                return Refusal(refusal);
            }
        }
        catch (Exception ex)
        {
            // A guardrail must never become the thing that blocks a valid call.
            Debug.WriteLine($"Loop latch pre-check failed: {ex}");
        }

        var result = await next();

        try
        {
            if (result.IsError == true)
            {
                var (code, message, diagnosticText) = ReadFailure(result);
                latch.RecordOutcome(agentId, toolName, succeeded: false, code, message, diagnosticText);
            }
            else
            {
                latch.RecordOutcome(agentId, toolName, succeeded: true, errorCode: null, errorMessage: null);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Loop latch post-check failed: {ex}");
        }

        return result;
    }

    /// <summary>
    /// The refusal as a normal failure body (<c>isError</c>, <c>errorData.errorCode</c> TargetIneligible, <c>errorData.message</c>),
    /// serialized like <see cref="CallToolFailureHandler.ToFailureResult"/>, so telemetry and the harness read it like any
    /// other tool error. TargetIneligible fits: the tool exists and its arguments may be fine, but this call cannot be made now.
    /// </summary>
    public static CallToolResult Refusal(string message)
    {
        var envelope = new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(ToolErrorCode.TargetIneligible, message),
        };
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(envelope, McpJsonUtilities.DefaultOptions) }],
            IsError = true,
        };
    }

    /// <summary>
    /// The error code (<see cref="ToolTelemetryRecorder.ExtractErrorCode"/>), the message, and the text to search for a
    /// diagnostic id (message plus <c>errorData.structuredDetail</c>) of a failed result. A body that is not a JSON failure
    /// envelope (a plain-text refusal such as an argument-validation rejection) has the whole text as its message.
    /// </summary>
    public static (string Code, string? Message, string? DiagnosticText) ReadFailure(CallToolResult result)
    {
        var code = ToolTelemetryRecorder.ExtractErrorCode(result);
        var text = result.Content?.OfType<TextContentBlock>().Select(b => b.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        if (text is null)
        {
            return (code, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errorData", out var errorData) && errorData.ValueKind == JsonValueKind.Object)
                {
                    var message = errorData.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    var detail = errorData.TryGetProperty("structuredDetail", out var d) ? d.GetRawText() : null;
                    return (code, message, detail is null ? message : message + " " + detail);
                }

                foreach (var name in new[] { "message", "error" })
                {
                    if (root.TryGetProperty(name, out var top) && top.ValueKind == JsonValueKind.String)
                    {
                        return (code, top.GetString(), top.GetString());
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Plain text: fall through and use it whole.
        }

        return (code, text, text);
    }
}
