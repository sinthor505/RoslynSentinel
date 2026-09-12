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
    /// <summary>
    /// Tool args/result JSON longer than this is offloaded to a sidecar file in agent.log rather
    /// than inlined — a single ReadFile result at 35,000 chars made even grepping the log risk
    /// blowing an analysis agent's context window. transcript.json (the structured record replayed
    /// by roslynsentinel-interrogate.ps1) always keeps the full payload; only agent.log is affected.
    /// </summary>
    private const int AgentLogOffloadThresholdChars = 2000;

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

    /// <param name="runId">
    /// Stable identifier for this run, written into the transcript and the "Agent run starting"
    /// agent.log line. Callers that also launch their own server process (PlanStepRunner) should
    /// pass the same value they gave the server's --run-id so the two sides can be joined without
    /// timestamp matching; a caller with no such concept (the in-process ModelEval tests) can omit
    /// it and one is generated so every transcript still carries one.
    /// </param>
    public async Task<AgentRunResult> RunAsync(
        string systemPrompt,
        string userPrompt,
        string transcriptDirectory,
        CancellationToken cancellationToken = default,
        string? runId = null)
    {
        runId ??= DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

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

        // Message shape is load-bearing: Parse-AgentLog.ps1's header regex is
        // '^Agent run starting in .*?\. Exposing (?<count>\d+) tool\(s\): (?<tools>.*?)\. User
        // prompt:\r?\n(?<prompt>.*)$' with a *lazy* tools capture, so anything inserted between
        // "tool(s): ..." and the literal ". User prompt:\n" would be swallowed into ToolsExposed
        // instead of being ignored. RunId is logged as a separate line/event instead of interleaved
        // into this one, leaving the header regex untouched.
        _logger.LogInformation(
            "Agent run starting in {Directory}. Exposing {ToolCount} tool(s): {ToolNames}. User prompt:\n{UserPrompt}",
            transcriptDirectory, toolDefinitions.Count, string.Join(", ", knownToolNames), userPrompt);
        _logger.LogInformation("RunId: {RunId}", runId);

        var transcript = new AgentTranscript { RunId = runId, SystemPrompt = systemPrompt, UserPrompt = userPrompt };
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
            var turnStartedAt = DateTimeOffset.Now;
            var turnStopwatch = Stopwatch.StartNew();
            var modelMessage = await _llm.CompleteAsync(messages, toolDefinitions, _maxTokensPerTurn, cancellationToken);
            turnStopwatch.Stop();
            var turnCompletedAt = DateTimeOffset.Now;

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
                StartedAt = turnStartedAt,
                CompletedAt = turnCompletedAt,
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

            var callIndexInTurn = 0;
            foreach (var toolCall in modelMessage.ToolCalls)
            {
                callIndexInTurn++;
                _logger.LogInformation(
                    "Turn {Turn}: calling {Tool} with args: {Args}",
                    turnNumber, toolCall.Name,
                    FormatForAgentLog(turnNumber, callIndexInTurn, toolCall.Name, "args", toolCall.ArgumentsJson, transcriptDirectory));

                var (resultJson, isError, latency, callStartedAt, callCompletedAt) = await ExecuteToolCallAsync(toolCall, cancellationToken);
                turnRecord.ToolCalls.Add(new AgentToolCallRecord
                {
                    ToolName = toolCall.Name,
                    ArgumentsJson = toolCall.ArgumentsJson,
                    ResultJson = resultJson,
                    IsError = isError,
                    Latency = latency,
                    StartedAt = callStartedAt,
                    CompletedAt = callCompletedAt,
                    ToolCallId = toolCall.Id,
                });

                var loggedResult = FormatForAgentLog(turnNumber, callIndexInTurn, toolCall.Name, "result", resultJson, transcriptDirectory);
                if (isError)
                {
                    _logger.LogWarning(
                        "Turn {Turn}: {Tool} FAILED in {Latency}. Result: {Result}",
                        turnNumber, toolCall.Name, latency, loggedResult);
                }
                else
                {
                    _logger.LogInformation(
                        "Turn {Turn}: {Tool} succeeded in {Latency}. Result: {Result}",
                        turnNumber, toolCall.Name, latency, loggedResult);
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

    private async Task<(string ResultJson, bool IsError, TimeSpan Latency, DateTimeOffset StartedAt, DateTimeOffset CompletedAt)> ExecuteToolCallAsync(
        AgentToolCall toolCall, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        Dictionary<string, object?> arguments;
        try
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(toolCall.ArgumentsJson) ?? [];
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            return ($$"""{"success":false,"error":"Malformed tool-call arguments JSON: {{JsonEncode(ex.Message)}}"}""", true, stopwatch.Elapsed, startedAt, DateTimeOffset.Now);
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
            return ($$"""{"success":false,"error":"MCP call threw: {{JsonEncode(ex.Message)}}"}""", true, stopwatch.Elapsed, startedAt, DateTimeOffset.Now);
        }

        stopwatch.Stop();
        var completedAt = DateTimeOffset.Now;
        var text = result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text)
            .FirstOrDefault() ?? "";

        // Check both envelope layers: the outer MCP-protocol IsError and the inner domain-level
        // ToolResult<T>.Success — see [[project_searchmode_literal_override_bug]], now fixed
        // server-side via a CallToolFilter, but the harness still checks both defensively rather
        // than trusting either alone.
        var isError = result.IsError == true || BodyReportsFailure(text);
        return (text, isError, stopwatch.Elapsed, startedAt, completedAt);
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

    /// <summary>
    /// Returns <paramref name="json"/> unchanged when it's short enough for agent.log, otherwise
    /// writes it to a sidecar file under <paramref name="transcriptDirectory"/> and returns a stub
    /// naming that file and the original size. transcript.json is unaffected — this only changes
    /// what <see cref="_logger"/> is handed for the human-readable agent.log line.
    /// </summary>
    /// <remarks>
    /// The "args" stub is itself a small JSON object, not free text: Parse-AgentLog.ps1's
    /// "calling ... with args: {...}" line requires the args capture to look like a JSON object
    /// (<c>\{.*\}$</c>), so a bracketed free-text stub there would silently drop the whole tool
    /// call from its parsed output instead of degrading gracefully. The "result" line has no such
    /// anchor, so its stub can stay human-readable.
    /// </remarks>
    private static string FormatForAgentLog(
        int turnNumber, int callIndexInTurn, string toolName, string kind, string json, string transcriptDirectory)
    {
        if (json.Length <= AgentLogOffloadThresholdChars)
        {
            return json;
        }

        var sidecarFileName = $"turn-{turnNumber}-{callIndexInTurn:D2}-{toolName}-{kind}.json";
        var sidecarPath = Path.Combine(transcriptDirectory, sidecarFileName);
        Directory.CreateDirectory(transcriptDirectory);
        File.WriteAllText(sidecarPath, json);

        return kind == "args"
            ? $$"""{"_offloaded":true,"_sizeBytes":{{json.Length}},"_file":"{{sidecarFileName}}"}"""
            : $"[{json.Length / 1024.0:F1} KB offloaded → {sidecarFileName}]";
    }

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
