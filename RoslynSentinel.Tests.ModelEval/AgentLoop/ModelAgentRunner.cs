using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly int _repeatedFailureLimit;
    private readonly ILogger<ModelAgentRunner> _logger;

    /// <param name="repeatedFailureLimit">
    /// How many <em>consecutive</em> identically-failing tool calls end the run with
    /// <see cref="AgentStopReason.RepeatedToolFailure"/>. Required, with no default, so every call
    /// site states its own tolerance rather than silently inheriting one — the eval fixtures want a
    /// high, non-interfering value while an unattended runner wants to bail early.
    /// <para>
    /// Not to be confused with <c>AgentToolErrorAssertions.AssertWithinBudget</c>: that is a
    /// post-hoc NUnit assertion over a finished transcript, which is exactly why it could not stop
    /// run 20260910-013550-398 from spending its last 23 turns re-issuing one failing call. This is
    /// a live in-loop guard.
    /// </para>
    /// </param>
    public ModelAgentRunner(
        LmStudioAgentClient llm,
        McpClient mcpClient,
        int repeatedFailureLimit,
        int turnCap = 25,
        TimeSpan? wallClockCap = null,
        int maxTokensPerTurn = 8192,
        ILogger<ModelAgentRunner>? logger = null)
    {
        if (repeatedFailureLimit < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(repeatedFailureLimit), repeatedFailureLimit,
                "repeatedFailureLimit must be at least 1. Pass a deliberately high value (the eval " +
                "fixtures use 10) to make the breaker effectively inert rather than disabling it.");
        }

        _llm = llm;
        _mcpClient = mcpClient;
        _repeatedFailureLimit = repeatedFailureLimit;
        _turnCap = turnCap;
        _wallClockCap = wallClockCap ?? TimeSpan.FromMinutes(10);
        _maxTokensPerTurn = maxTokensPerTurn;
        _logger = logger ?? NullLogger<ModelAgentRunner>.Instance;
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

        _logger.LogInformation(
            "Agent run starting in {Directory}. Exposing {ToolCount} tool(s): {ToolNames}. User prompt:\n{UserPrompt}",
            transcriptDirectory, toolDefinitions.Count, string.Join(", ", knownToolNames), userPrompt);

        var transcript = new AgentTranscript { SystemPrompt = systemPrompt, UserPrompt = userPrompt };
        await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);
        var overallStopwatch = Stopwatch.StartNew();
        var stopReason = AgentStopReason.TurnCapExceeded;
        var turnNumber = 0;

        // Consecutive-failure tracking for the repeated-failure breaker. Consecutive rather than
        // cumulative: three different tools failing once each is a model exploring, while the same
        // call failing three times running is a model stuck, and only the latter should end the run.
        string? lastFailureSignature = null;
        var consecutiveFailures = 0;
        var firstFailureTurn = 0;
        RepeatedFailureDetail? repeatedFailure = null;

        while (turnNumber < _turnCap)
        {
            if (overallStopwatch.Elapsed > _wallClockCap)
            {
                stopReason = AgentStopReason.WallClockCapExceeded;
                _logger.LogWarning("Turn {Turn}: wall-clock cap ({Cap}) exceeded, stopping.", turnNumber, _wallClockCap);
                break;
            }

            turnNumber++;
            var turnStopwatch = Stopwatch.StartNew();
            var modelMessage = await _llm.CompleteAsync(messages, toolDefinitions, _maxTokensPerTurn, cancellationToken);
            turnStopwatch.Stop();

            _logger.LogInformation(
                "Turn {Turn}: model responded in {Latency} — {ToolCallCount} tool call(s). Reasoning: {Reasoning} Content: {Content}",
                turnNumber, turnStopwatch.Elapsed, modelMessage.ToolCalls.Count,
                string.IsNullOrWhiteSpace(modelMessage.ReasoningContent) ? "(none)" : modelMessage.ReasoningContent,
                string.IsNullOrWhiteSpace(modelMessage.Content) ? "(none)" : modelMessage.Content);

            var turnRecord = new AgentTranscriptTurn
            {
                TurnNumber = turnNumber,
                ModelMessage = modelMessage,
                ModelLatency = turnStopwatch.Elapsed,
            };
            transcript.Turns.Add(turnRecord);
            messages.Add(modelMessage);
            await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);

            if (modelMessage.ToolCalls.Count == 0)
            {
                stopReason = AgentStopReason.ModelFinished;
                _logger.LogInformation("Turn {Turn}: model made no tool calls, treating as finished.", turnNumber);
                break;
            }

            var unknownCall = modelMessage.ToolCalls.FirstOrDefault(tc => !knownToolNames.Contains(tc.Name));
            if (unknownCall is not null)
            {
                stopReason = AgentStopReason.UnknownToolRequested;
                _logger.LogWarning("Turn {Turn}: model requested unknown tool '{Tool}', stopping.", turnNumber, unknownCall.Name);
                break;
            }

            foreach (var toolCall in modelMessage.ToolCalls)
            {
                _logger.LogInformation(
                    "Turn {Turn}: calling {Tool} with args: {Args}",
                    turnNumber, toolCall.Name, toolCall.ArgumentsJson);

                var (resultJson, isError, latency) = await ExecuteToolCallAsync(toolCall, cancellationToken);
                turnRecord.ToolCalls.Add(new AgentToolCallRecord
                {
                    ToolName = toolCall.Name,
                    ArgumentsJson = toolCall.ArgumentsJson,
                    ResultJson = resultJson,
                    IsError = isError,
                    Latency = latency,
                });

                if (isError)
                {
                    _logger.LogWarning(
                        "Turn {Turn}: {Tool} FAILED in {Latency}. Result: {Result}",
                        turnNumber, toolCall.Name, latency, resultJson);
                }
                else
                {
                    _logger.LogInformation(
                        "Turn {Turn}: {Tool} succeeded in {Latency}. Result: {Result}",
                        turnNumber, toolCall.Name, latency, resultJson);
                }

                messages.Add(new AgentChatMessage
                {
                    Role = "tool",
                    ToolCallId = toolCall.Id,
                    Content = resultJson,
                });
                await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);

                if (!isError)
                {
                    lastFailureSignature = null;
                    consecutiveFailures = 0;
                    continue;
                }

                var signature = BuildFailureSignature(toolCall.Name, toolCall.ArgumentsJson, resultJson);
                if (signature == lastFailureSignature)
                {
                    consecutiveFailures++;
                }
                else
                {
                    lastFailureSignature = signature;
                    consecutiveFailures = 1;
                    firstFailureTurn = turnNumber;
                }

                if (consecutiveFailures >= _repeatedFailureLimit)
                {
                    stopReason = AgentStopReason.RepeatedToolFailure;
                    repeatedFailure = new RepeatedFailureDetail(
                        toolCall.Name, signature, consecutiveFailures,
                        firstFailureTurn, turnNumber, toolCall.ArgumentsJson, resultJson);
                    _logger.LogWarning(
                        "Turn {Turn}: {Tool} failed {Count} consecutive time(s) with the same signature " +
                        "[{Signature}] across turns {FirstTurn}-{LastTurn} — tripping the repeated-failure " +
                        "breaker (limit {Limit}) instead of burning the remaining turn budget.",
                        turnNumber, toolCall.Name, consecutiveFailures, signature,
                        firstFailureTurn, turnNumber, _repeatedFailureLimit);
                    break;
                }
            }

            if (stopReason == AgentStopReason.RepeatedToolFailure)
            {
                break;
            }
        }

        _logger.LogInformation("Agent run finished: {StopReason} after {TurnCount} turn(s), {Elapsed}.", stopReason, turnNumber, overallStopwatch.Elapsed);

        var transcriptPath = await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);

        return new AgentRunResult
        {
            StopReason = stopReason,
            Transcript = transcript,
            TranscriptPath = transcriptPath,
            TurnCount = turnNumber,
            RepeatedFailure = repeatedFailure,
        };
    }

    /// <summary>
    /// Identity of a tool failure, for deciding whether two consecutive failures are "the same
    /// failure again" or genuine forward motion. Keyed on tool + error code + target path, so a
    /// model that fixes one argument and hits a different error is not counted as looping.
    /// </summary>
    /// <remarks>
    /// Falls back to a message prefix when no <c>errorCode</c> is present, so a tool that reports
    /// failure without one still trips the breaker rather than looping forever under the radar.
    /// The prefix is truncated because several error messages embed the offending content itself,
    /// which would otherwise make every attempt look unique.
    /// </remarks>
    private static string BuildFailureSignature(string toolName, string argumentsJson, string resultJson)
    {
        var errorCode = TryReadStringProperty(resultJson, "errorCode");
        var target = TryReadStringProperty(argumentsJson, "filePath")
            ?? TryReadStringProperty(argumentsJson, "filepath")
            ?? TryReadStringProperty(argumentsJson, "docCommentId")
            ?? "";

        if (errorCode is null)
        {
            var message = TryReadStringProperty(resultJson, "message")
                ?? TryReadStringProperty(resultJson, "error")
                ?? resultJson;
            errorCode = "msg:" + message[..Math.Min(120, message.Length)];
        }

        return $"{toolName}|{errorCode}|{target}";
    }

    private static string? TryReadStringProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return FindStringProperty(doc.RootElement, propertyName, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds <paramref name="propertyName"/> at the root or shallowly nested beneath it — error
    /// codes on this server sit under a "data"/"error" envelope about as often as at the top level.
    /// Depth-bounded so a large successful payload isn't walked exhaustively.
    /// </summary>
    private static string? FindStringProperty(JsonElement element, string propertyName, int depth)
    {
        if (depth > 3 || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(propertyName, out var direct) && direct.ValueKind == JsonValueKind.String)
        {
            return direct.GetString();
        }

        foreach (var child in element.EnumerateObject())
        {
            if (child.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var nested = FindStringProperty(child.Value, propertyName, depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
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
