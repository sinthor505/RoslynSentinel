using System.Text.Json;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Executes one recorded tool call (name + ArgumentsJson verbatim) against a live in-memory MCP client, with the
/// same two-layer error check the agent loop uses. Shared by <see cref="TranscriptReplayTests"/> (replay a whole
/// transcript) and <see cref="TranscriptResume"/> (replay the kept turns before a live resume), so both treat a
/// call's success the same way.
/// </summary>
internal static class TranscriptReplayHelper
{
    public static async Task<(string ResultText, bool IsError)> ReplayToolCallAsync(
        McpClient mcpClient, string toolName, string argumentsJson, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) ?? [];
        }
        catch (JsonException ex)
        {
            return ($$"""{"isError":true,"error":"Malformed recorded arguments JSON: {{ex.Message}}"}""", true);
        }

        CallToolResult result;
        try
        {
            result = await mcpClient.CallToolAsync(
                toolName,
                arguments,
                progress: null,
                options: null,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ($$"""{"isError":true,"error":"MCP call threw: {{ex.Message}}"}""", true);
        }

        var text = result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text)
            .FirstOrDefault() ?? "";

        return (text, result.IsError == true || BodyReportsFailure(text));
    }

    private static bool BodyReportsFailure(string resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultText);
            return doc.RootElement.TryGetProperty("isError", out var errorProp)
                && errorProp.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
