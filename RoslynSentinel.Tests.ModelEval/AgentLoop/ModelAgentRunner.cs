using System.Diagnostics;
using System.Text.Json;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Tests.ModelEval.AgentLoop;

/// <summary>
/// Drives an LM Studio model through a real MCP tool-calling loop against a real
/// <see cref="McpClient"/> (same in-process pipe-wired client construction as
/// RoslynSentinel.Tests.Advanced's McpTasksHarness* tests). The model's own tool list comes from
/// <see cref="McpClient.ListToolsAsync"/> on the same client, so the harness never hand-maintains a
/// duplicate tool catalog — it always reflects whatever the running server actually exposes.
/// </summary>
public sealed class ModelAgentRunner
{
    private readonly LmStudioAgentClient _llm;
    private readonly McpClient _mcpClient;
    private readonly int _turnCap;
    private readonly TimeSpan _wallClockCap;
    private readonly int _maxTokensPerTurn;

    public ModelAgentRunner(
        LmStudioAgentClient llm,
        McpClient mcpClient,
        int turnCap = 25,
        TimeSpan? wallClockCap = null,
        int maxTokensPerTurn = 2048)
    {
        _llm = llm;
        _mcpClient = mcpClient;
        _turnCap = turnCap;
        _wallClockCap = wallClockCap ?? TimeSpan.FromMinutes(10);
        _maxTokensPerTurn = maxTokensPerTurn;
    }

    public async Task<AgentRunResult> RunAsync(
        string systemPrompt,
        string userPrompt,
        string transcriptDirectory,
        CancellationToken cancellationToken = default)
    {
        var mcpTools = await _mcpClient.ListToolsAsync(cancellationToken: cancellationToken);
        var toolDefinitions = mcpTools.Select(t => new AgentToolDefinition
        {
            Name = t.Name,
            Description = t.Description,
            ParametersSchema = t.JsonSchema,
        }).ToList();
        var knownToolNames = toolDefinitions.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var messages = new List<AgentChatMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userPrompt },
        };

        var transcript = new AgentTranscript();
        var overallStopwatch = Stopwatch.StartNew();
        var stopReason = AgentStopReason.TurnCapExceeded;
        var turnNumber = 0;

        while (turnNumber < _turnCap)
        {
            if (overallStopwatch.Elapsed > _wallClockCap)
            {
                stopReason = AgentStopReason.WallClockCapExceeded;
                break;
            }

            turnNumber++;
            var turnStopwatch = Stopwatch.StartNew();
            var modelMessage = await _llm.CompleteAsync(messages, toolDefinitions, _maxTokensPerTurn, cancellationToken);
            turnStopwatch.Stop();

            var turnRecord = new AgentTranscriptTurn
            {
                TurnNumber = turnNumber,
                ModelMessage = modelMessage,
                ModelLatency = turnStopwatch.Elapsed,
            };
            transcript.Turns.Add(turnRecord);
            messages.Add(modelMessage);

            if (modelMessage.ToolCalls.Count == 0)
            {
                stopReason = AgentStopReason.ModelFinished;
                break;
            }

            var unknownCall = modelMessage.ToolCalls.FirstOrDefault(tc => !knownToolNames.Contains(tc.Name));
            if (unknownCall is not null)
            {
                stopReason = AgentStopReason.UnknownToolRequested;
                break;
            }

            foreach (var toolCall in modelMessage.ToolCalls)
            {
                var (resultJson, isError, latency) = await ExecuteToolCallAsync(toolCall, cancellationToken);
                turnRecord.ToolCalls.Add(new AgentToolCallRecord
                {
                    ToolName = toolCall.Name,
                    ArgumentsJson = toolCall.ArgumentsJson,
                    ResultJson = resultJson,
                    IsError = isError,
                    Latency = latency,
                });

                messages.Add(new AgentChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Content = resultJson,
                });
            }
        }

        var transcriptPath = await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);

        return new AgentRunResult
        {
            StopReason = stopReason,
            Transcript = transcript,
            TranscriptPath = transcriptPath,
            TurnCount = turnNumber,
        };
    }

    private async Task<(string ResultJson, bool IsError, TimeSpan Latency)> ExecuteToolCallAsync(
        AgentToolCall toolCall, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Dictionary<string, object?> arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(toolCall.ArgumentsJson) ?? [];
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            return ($$"""{"success":false,"error":"Malformed tool-call arguments JSON: {{JsonEncode(ex.Message)}}"}""", true, stopwatch.Elapsed);
        }

        CallToolResult result;
        try
        {
            result = await _mcpClient.CallToolAsync(
                toolCall.Name,
                arguments!,
                progress: null,
                options: null,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return ($$"""{"success":false,"error":"MCP call threw: {{JsonEncode(ex.Message)}}"}""", true, stopwatch.Elapsed);
        }

        stopwatch.Stop();
        var text = result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text)
            .FirstOrDefault() ?? "";

        // Check both envelope layers: the outer MCP-protocol IsError and the inner domain-level
        // ToolResult<T>.Success — see [[project_searchmode_literal_override_bug]], now fixed
        // server-side via a CallToolFilter, but the harness still checks both defensively rather
        // than trusting either alone.
        var isError = result.IsError == true || BodyReportsFailure(text);
        return (text, isError, stopwatch.Elapsed);
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
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("success", out var successProp)
                && successProp.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string JsonEncode(string value) => JsonSerializer.Serialize(value)[1..^1];

    private static async Task<string> WriteTranscriptAsync(
        AgentTranscript transcript, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "transcript.json");
        var json = JsonSerializer.Serialize(transcript, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return path;
    }
}
