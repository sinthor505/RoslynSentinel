using System.Diagnostics;
using System.Text.Json;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// The logic of the call-tool telemetry filter (<c>AddToolTelemetryFilter</c> in
/// <see cref="RoslynSentinelServiceExtensionsBasic"/>), kept out of the filter lambda so tests can drive it without
/// building an McpRequestContext. Times the inner call and records it on <see cref="ToolTelemetry"/>.
/// Public rather than internal because Server.Advanced has no InternalsVisibleTo for its tests.
/// </summary>
public static class ToolTelemetryRecorder
{
    /// <summary>
    /// Runs <paramref name="next"/> and records one call for <paramref name="toolName"/>. A result whose
    /// <c>IsError</c> is true counts as an error with the code read by <see cref="ExtractErrorCode"/>; an exception
    /// counts as an error with the exception type name as code and is rethrown unchanged. Recording never throws and
    /// never alters the result. With a null <paramref name="telemetry"/> it is a plain pass-through.
    /// </summary>
    public static async ValueTask<CallToolResult> RecordAsync(
        ToolTelemetry? telemetry, string? toolName, Func<ValueTask<CallToolResult>> next)
    {
        if (telemetry is null)
        {
            return await next();
        }

        var followedOriginator = BeginGuidance(telemetry, toolName);
        var collector = OpenCollector();
        try
        {
            var started = Stopwatch.GetTimestamp();
            CallToolResult result;
            try
            {
                result = await next();
            }
            catch (Exception ex)
            {
                Record(telemetry, toolName, isError: true, ex.GetType().Name, Stopwatch.GetElapsedTime(started));
                FinishGuidance(telemetry, toolName, collector, followedOriginator, succeeded: false);
                throw;
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            var isError = result.IsError == true;
            Record(telemetry, toolName, isError, isError ? ExtractErrorCode(result) : null, elapsed);
            FinishGuidance(telemetry, toolName, collector, followedOriginator, succeeded: !isError);
            return result;
        }
        finally
        {
            collector?.Dispose();
        }
    }

    /// <summary>
    /// The <c>errorData.errorCode</c> of the first JSON text block (the body shape ToolErrorMapper writes), else a
    /// top-level <c>errorCode</c>, else <see cref="ToolTelemetry.UnknownErrorCode"/> (plain-text refusals such as the
    /// breakers' messages carry no code).
    /// </summary>
    public static string ExtractErrorCode(CallToolResult result)
    {
        try
        {
            foreach (var block in result.Content ?? [])
            {
                if (block is not TextContentBlock { Text: { Length: > 0 } text })
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (root.TryGetProperty("errorData", out var errorData)
                    && errorData.ValueKind == JsonValueKind.Object
                    && errorData.TryGetProperty("errorCode", out var nested)
                    && nested.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(nested.GetString()))
                {
                    return nested.GetString()!;
                }

                if (root.TryGetProperty("errorCode", out var top)
                    && top.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(top.GetString()))
                {
                    return top.GetString()!;
                }

                // Only the first JSON object block is the body; later blocks are alias notes.
                break;
            }
        }
        catch (JsonException)
        {
            // Not JSON (plain-text refusal): no code to read.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tool telemetry error-code extraction failed: {ex}");
        }

        return ToolTelemetry.UnknownErrorCode;
    }

    // Guidance step 1 (before the call): expire stale suggestions and match this call against the pending ones. Returns the
    // originating tool of the followed suggestion, or null. Never throws.
    private static string? BeginGuidance(ToolTelemetry telemetry, string? toolName)
    {
        try
        {
            return telemetry.BeginGuidanceCall(toolName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tool guidance begin failed: {ex}");
            return null;
        }
    }

    // Opens the per-call collector that ToolCallHint (and the marshaller's GuidanceHint route) record into; null if opening
    // somehow fails, in which case the call simply goes untracked. Never throws.
    private static ToolGuidanceCollector? OpenCollector()
    {
        try
        {
            return ToolGuidanceCollector.Open();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tool guidance collector open failed: {ex}");
            return null;
        }
    }

    // Guidance steps 2 and 3 (after the call, also when it errored): credit a successful follow, then register the
    // suggestions the call emitted. The call itself is recorded first so the originating tool already has its entry. Never throws.
    private static void FinishGuidance(
        ToolTelemetry telemetry, string? toolName, ToolGuidanceCollector? collector, string? followedOriginator, bool succeeded)
    {
        try
        {
            if (succeeded)
            {
                telemetry.RecordGuidanceFollowedSucceeded(followedOriginator);
            }

            if (collector is not null)
            {
                telemetry.IssueGuidance(toolName, collector.Suggestions);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Tool guidance finish failed: {ex}");
        }
    }

    private static void Record(ToolTelemetry telemetry, string? toolName, bool isError, string? errorCode, TimeSpan duration)
    {
        try
        {
            telemetry.Record(toolName, isError, errorCode, duration);
        }
        catch (Exception ex)
        {
            // Telemetry must never break a call.
            Debug.WriteLine($"Tool telemetry record failed: {ex}");
        }
    }
}
