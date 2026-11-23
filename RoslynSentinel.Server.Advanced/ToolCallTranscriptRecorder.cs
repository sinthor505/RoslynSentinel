using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// The logic of the call-transcript filter (<c>AddToolCallTranscriptFilter</c> in <see cref="RoslynSentinelServiceExtensionsBasic"/>),
/// kept out of the filter lambda so tests can drive it without building an McpRequestContext. Public rather than internal
/// because Server.Advanced has no InternalsVisibleTo for its tests.
/// </summary>
public static class ToolCallTranscriptRecorder
{
    /// <summary>
    /// Runs <paramref name="next"/> and appends one line to <paramref name="transcript"/>. Returns the very same
    /// <see cref="CallToolResult"/> untouched; an exception is logged as an error line and rethrown unchanged. Writing never
    /// throws. With a null transcript it is a plain pass-through. The arguments are copied before <paramref name="next"/> runs
    /// because the argument validator rewrites the dictionary in place.
    /// </summary>
    public static async ValueTask<CallToolResult> RecordAsync(
        ToolCallTranscript? transcript,
        string? toolName,
        IDictionary<string, JsonElement>? arguments,
        Func<ValueTask<CallToolResult>> next)
    {
        if (transcript is null)
        {
            return await next();
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        JsonNode? snapshot = ToolCallTranscript.SnapshotArguments(arguments);
        CallToolResult result;
        try
        {
            result = await next();
        }
        catch (Exception ex)
        {
            Append(transcript, startedUtc, toolName, snapshot, isError: true, $"{ex.GetType().Name}: {ex.Message}", Stopwatch.GetElapsedTime(started));
            throw;
        }

        Append(transcript, startedUtc, toolName, snapshot, result.IsError == true, JoinText(result), Stopwatch.GetElapsedTime(started));
        return result;
    }

    /// <summary>The text of the response's text content blocks, joined with a newline; null when there is none.</summary>
    public static string? JoinText(CallToolResult result)
    {
        var texts = (result.Content ?? []).OfType<TextContentBlock>().Select(b => b.Text).ToList();
        return texts.Count == 0 ? null : string.Join("\n", texts);
    }

    private static void Append(
        ToolCallTranscript transcript, DateTimeOffset startedUtc, string? toolName, JsonNode? arguments, bool isError, string? response, TimeSpan duration)
    {
        try
        {
            // Read after the inner call: the identity filter set the context, and the echo filter assigned the call id, before this filter ran.
            var context = ToolCallContext.Current;
            transcript.Record(startedUtc, toolName, arguments, isError, response, (long)duration.TotalMilliseconds, context?.ToolCallId, context?.AgentId);
        }
        catch (Exception ex)
        {
            // A diagnostic aid must never break a call.
            Debug.WriteLine($"Tool call transcript record failed: {ex}");
        }
    }
}
