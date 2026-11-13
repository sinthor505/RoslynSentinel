using System.Collections.ObjectModel;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// The logic of the error-policy call-tool filter (<c>AddErrorPolicyFilter</c> in <see cref="RoslynSentinelServiceExtensionsBasic"/>),
/// kept out of the filter lambda so tests can drive it without an McpRequestContext (same split as <see cref="LoopLatchFilter"/>).
/// Public because Server.Advanced has no InternalsVisibleTo for its tests. Plan: docs/current/plans/plan_error_policy_pointer.md, Step 6.
/// </summary>
public static class ErrorPolicyFilter
{
    /// <summary>The pointer sentence for a key, used in <see cref="ErrorPolicyMode.Pointer"/>.</summary>
    public static string PointerText(ErrorPolicyKey key) =>
        "Your instructions have an '" + ErrorPolicyDocument.RenderedHeading(key) + "' rule for this case; apply it.";

    /// <summary>
    /// The text to append for a key: the pointer in Pointer mode; the document's entry in Inline mode (null when there is no
    /// document or it lacks the key); null in Off mode.
    /// </summary>
    public static string? TextFor(ErrorPolicyMode mode, ErrorPolicyDocument? document, ErrorPolicyKey key)
    {
        return mode switch
        {
            ErrorPolicyMode.Pointer => PointerText(key),
            ErrorPolicyMode.Inline => document is not null && document.Entries.TryGetValue(key, out var entry) ? entry : null,
            _ => null,
        };
    }

    /// <summary>
    /// Runs the call, then, when the result is a not-found error that classifies to a key with text, appends that text as ONE
    /// trailing text block. The first block is never modified. Success results, other errors, unclassified errors and any
    /// internal failure return the result untouched: this filter never breaks a call.
    /// The suffix is also skipped when the call's ToolCallContext has HasEditEffectFact set, because an edit effect from this session explains the miss.
    /// </summary>
    public static async ValueTask<CallToolResult> InvokeAsync(
        ErrorPolicyMode mode,
        ErrorPolicyDocument? document,
        string? toolName,
        IDictionary<string, JsonElement>? arguments,
        ILogger? logger,
        Func<ValueTask<CallToolResult>> next)
    {
        var result = await next();
        if (mode == ErrorPolicyMode.Off || string.IsNullOrEmpty(toolName))
        {
            return result;
        }

        try
        {
            if (!IsErrorResult(result))
            {
                return result;
            }

            if (ToolCallContext.Current?.HasEditEffectFact == true)
            {
                logger?.LogInformation("Error policy suffix skipped for the {Tool} error: an edit effect from this session explains the miss.", toolName);
                return result;
            }

            var key = ErrorPolicyClassifier.Classify(
                toolName,
                arguments is null ? null : new ReadOnlyDictionary<string, JsonElement>(arguments),
                ToolTelemetryRecorder.ExtractErrorCode(result),
                ToolCallContext.Current?.MissingTargetKind);
            if (key is null || result.Content is null)
            {
                return result;
            }

            var wireName = ErrorPolicyKeys.ToWireName(key.Value);
            var text = TextFor(mode, document, key.Value);
            if (string.IsNullOrEmpty(text))
            {
                logger?.LogInformation("Error policy {Key} has no text in {Mode} mode; the {Tool} error is unchanged.", wireName, mode, toolName);
                return result;
            }

            logger?.LogInformation("Error policy {Key} appended to the {Tool} error in {Mode} mode.", wireName, toolName, mode);
            result.Content = [.. result.Content, new TextContentBlock { Text = text }];
        }
        catch (Exception ex)
        {
            // A diagnostic aid must never break a call.
            logger?.LogDebug(ex, "Error-policy filter failed for {Tool}; the result is unchanged.", toolName);
        }

        return result;
    }

    /// <summary>
    /// True when the result is a failure: the transport flag is set, or the first non-empty text block is a JSON object whose
    /// top-level <c>isError</c> is true. The second case is needed because this filter sits inside ToolError, which copies that
    /// flag onto <c>IsError</c> only outside itself.
    /// </summary>
    private static bool IsErrorResult(CallToolResult result)
    {
        if (result.IsError == true)
        {
            return true;
        }

        var text = result.Content?.OfType<TextContentBlock>().Select(b => b.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        if (text is null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("isError", out var flag)
                && flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            // Plain text is not a failure envelope.
            return false;
        }
    }
}
