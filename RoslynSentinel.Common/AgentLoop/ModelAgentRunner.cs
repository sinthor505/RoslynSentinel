using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Drives an LM Studio model through a real MCP tool-calling loop against a real
/// <see cref="McpClient"/> (same in-process pipe-wired client construction as
/// RoslynSentinel.Tests.Advanced's McpTasksHarness* tests). The model's own tool list comes from
/// <see cref="McpClient.ListToolsAsync"/> on the same client, so the harness never hand-maintains a
/// duplicate tool catalog -> it always reflects whatever the running server actually exposes.
/// </summary>
public sealed class ModelAgentRunner
{
    /// <summary>
    /// Tool args/result JSON longer than this is offloaded to a sidecar file in agent.log rather
    /// than inlined -> a single ReadFile result at 35,000 chars made even grepping the log risk
    /// blowing an analysis agent's context window. transcript.json (the structured record replayed
    /// by roslynsentinel-interrogate.ps1) always keeps the full payload; only agent.log is affected.
    /// </summary>
    private const int AgentLogOffloadThresholdChars = 2000;

    /// <summary>
    /// Cumulative running-conversation tokens vs. the model's context window, as a fraction, at
    /// which RunAsync starts logging a warning every turn. A truncated/failed call from hitting the
    /// context limit outright (LM Studio's exceed_context_size_error) is often harder to diagnose
    /// than max_output_tokens truncation (see LmStudioAgentClient.CompleteOnceAsync's Status check)
    /// because nothing before this pointed at cumulative growth as the cause -> this makes the
    /// approach visible in agent.log before the hard failure, not just after.
    /// </summary>
    private const double ContextUsageWarningFraction = 0.8;

    /// <summary>
    /// Process-wide value set once by the ModelEval assembly setup; RunAsync copies it into each new transcript.
    /// Null everywhere else (PlanStepRunner and sub-agent runs), so their transcripts omit the stamp.
    /// </summary>
    // Intentional deviation: a mutable static. One stamp per test process, set before any run.
    public static (string CommitHash, bool Dirty)? ProcessStamp
    {
        get; set;
    }

    private readonly LmStudioAgentClient _llm;
    private readonly McpClient _mcpClient;
    private readonly int _turnCap;
    private readonly TimeSpan _wallClockCap;
    private readonly int _maxTokensPerTurn;
    private readonly int _repeatedFailureLimit;
    private readonly ILogger<ModelAgentRunner> _logger;
    private readonly LoopAssistOptions _loopAssist;

    /// <param name="repeatedFailureLimit">
    /// How many <em>consecutive</em> identically-failing tool calls end the run with
    /// <see cref="AgentStopReason.RepeatedToolFailure"/>. Required, with no default, so every call
    /// site states its own tolerance rather than silently inheriting one -> the eval fixtures want a
    /// high, non-interfering value while an unattended runner wants to bail early.
    /// <para>
    /// Not to be confused with <c>AgentToolErrorAssertions.AssertWithinBudget</c>: that is a
    /// post-hoc NUnit assertion over a finished transcript, which is exactly why it could not stop
    /// run 20260910-013550-398 from spending its last 23 turns re-issuing one failing call. This is
    /// a live in-loop guard.
    /// </para>
    /// </param>
    /// <param name="maxTokensPerTurn">
    /// Per-turn output token ceiling, sent to LM Studio as max_output_tokens. Required, with no
    /// default, for the same reason as <paramref name="repeatedFailureLimit"/>: a silent 8192
    /// default let a model's turn get truncated mid-reasoning with zero tool calls emitted, which
    /// LmStudioAgentClient.CompleteOnceAsync could not yet distinguish from the model genuinely
    /// finishing -> PlanStepRunner then committed the truncated (no-op) turn as a successful step
    /// (run 20260916-211352-610, step 04-rename-symbol-decode). CompleteOnceAsync now throws when
    /// the Responses API reports Status="incomplete", but the budget itself still needs to be sized
    /// per call site: a step expecting substantial up-front reasoning before its first tool call
    /// needs more headroom than a fixture expecting a quick, simple turn.
    /// </param>
    /// <param name="loopAssist">
    /// Loop-assist switches (collapse of repeated rejected calls in the replayed history, plus a fact-only
    /// injection). Null reads ROSLYNSENTINEL_MODELEVAL_LOOP_ASSIST / _ROLE from the environment; off unless set.
    /// </param>
    public ModelAgentRunner(
        LmStudioAgentClient llm,
        McpClient mcpClient,
        int repeatedFailureLimit,
        int maxTokensPerTurn,
        int turnCap = 25,
        TimeSpan? wallClockCap = null,
        ILogger<ModelAgentRunner>? logger = null,
        LoopAssistOptions? loopAssist = null)
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
        _loopAssist = loopAssist ?? LoopAssistOptions.FromEnvironment();
    }

    /// <param name="runId">
    /// Stable identifier for this run, written into the transcript and the "Agent run starting"
    /// agent.log line. Callers that also launch their own server process (PlanStepRunner) should
    /// pass the same value they gave the server's --run-id so the two sides can be joined without
    /// timestamp matching; a caller with no such concept (the in-process ModelEval tests) can omit
    /// it and one is generated so every transcript still carries one.
    /// </param>
    /// <param name="seedTurns">
    /// Turns a previous run already produced (system and user prompt are still the first two parameters), used
    /// to resume from a saved transcript: each seeded turn is replayed into the conversation exactly as the loop
    /// would have built it (the assistant message with its reasoning/content/calls, then one tool message per
    /// recorded call result), copied into the new transcript, and its tool calls are shown to the loop-assist
    /// tracker so a repeat in the seeded turns counts (and its injection follows that turn's results). The live
    /// loop then continues from the last seeded turn's number, so <c>turnCap</c> must already include the seeded
    /// turns. The caller is responsible for the tool-side state (re-executing the calls on a fresh fixture).
    /// </param>
    public async Task<AgentRunResult> RunAsync(
        string systemPrompt,
        string userPrompt,
        string transcriptDirectory,
        CancellationToken cancellationToken = default,
        string? runId = null,
        IReadOnlyList<AgentTranscriptTurn>? seedTurns = null)
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
        if (ProcessStamp is { } stamp)
        {
            transcript.CommitHash = stamp.CommitHash;
            transcript.WorkingTreeDirty = stamp.Dirty;
        }
        transcript.Tools.AddRange(toolDefinitions.Select(t => t.Name));
        var assistTracker = new LoopAssistTracker(_loopAssist);
        if (assistTracker.Enabled)
        {
            transcript.AssistCount = 0;
        }

        await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);
        var overallStopwatch = Stopwatch.StartNew();
        var stopReason = AgentStopReason.TurnCapExceeded;
        var turnNumber = 0;
        if (seedTurns is { Count: > 0 })
        {
            turnNumber = SeedConversation(seedTurns, messages, transcript, assistTracker);
            await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);
        }

        // Consecutive-failure tracking for the repeated-failure breaker. Consecutive rather than
        // cumulative: three different tools failing once each is a model exploring, while the same
        // call failing three times running is a model stuck, and only the latter should end the run.
        string? lastFailureSignature = null;
        var consecutiveFailures = 0;
        var firstFailureTurn = 0;
        RepeatedFailureDetail? repeatedFailure = null;

        // Server loop-latch refusals per tool since the last successful call (the server releases the latch on any success, so
        // the count restarts then). The model calling a refused tool again after the first refusal is the LoopLatchIgnored stop.
        var latchRefusals = new Dictionary<string, int>(StringComparer.Ordinal);

        // Cumulative context tracking (see ContextUsageWarningFraction). contextLength is resolved
        // once and cached (-1 if LM Studio couldn't report one, e.g. an older server) so a slow/down
        // context-length lookup never adds latency to every turn.
        var contextLength = await _llm.GetContextLengthAsync(cancellationToken);
        var cumulativeTokens = 0;

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
            // With loop assist off, replayMessages is `messages` itself: the request is unchanged. On, repeated
            // rejected calls are collapsed in this replay copy only; `messages` and the transcript keep every turn.
            var turnAssists = new List<AgentAssist>();
            var replayMessages = assistTracker.CollapseForReplay(messages, turnAssists);
            var modelMessage = await _llm.CompleteAsync(replayMessages, toolDefinitions, _maxTokensPerTurn, cancellationToken);
            turnStopwatch.Stop();
            var turnCompletedAt = DateTimeOffset.Now;

            _logger.LogInformation(
                "Turn {Turn}: model responded in {Latency} - {ToolCallCount} tool call(s). Tokens: input {InputTokens} (cached {CachedInputTokens}), output {OutputTokens} (reasoning {ReasoningTokens}), total {TotalTokens}. First token {TimeToFirstTokenSeconds} s, generation {GenerationSeconds} s, {TokensPerSecond} tok/s. Reasoning: {Reasoning} Content: {Content}",
                turnNumber, turnStopwatch.Elapsed, modelMessage.ToolCalls.Count,
                modelMessage.InputTokens?.ToString() ?? "n/a", modelMessage.CachedInputTokens?.ToString() ?? "n/a",
                modelMessage.OutputTokens?.ToString() ?? "n/a", modelMessage.ReasoningTokens?.ToString() ?? "n/a",
                modelMessage.TotalTokens?.ToString() ?? "n/a",
                modelMessage.TimeToFirstTokenSeconds?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
                modelMessage.GenerationSeconds?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
                modelMessage.TokensPerSecond?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a",
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
            RecordAssists(transcript, turnRecord, turnAssists);
            messages.Add(modelMessage);
            await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);

            // Prefer LM Studio's own reported total_tokens (exact) over PromptTokenEstimator (a
            // chars/4 heuristic), falling back only when a backend omits usage. cumulativeTokens is
            // a running total across the whole conversation, not per-turn, since that is what
            // eventually triggers LM Studio's exceed_context_size_error. Caveat: the fallback
            // estimator below still keys off the process-wide LlmOptions.Model, which can differ from
            // the per-call model a SubAgent/SubAgentEval run was given. That only skews this
            // heuristic's estimate (never correctness), so it is left as-is.
            if (modelMessage.TotalTokens is not null)
            {
                cumulativeTokens = modelMessage.TotalTokens.Value;
            }
            else
            {
                cumulativeTokens += PromptTokenEstimator.EstimateTokens(
                    LlmOptions.Model ?? "", (modelMessage.Content ?? "") + (modelMessage.ReasoningContent ?? ""));

            }

            if (contextLength > 0 && cumulativeTokens >= contextLength * ContextUsageWarningFraction)
            {
                _logger.LogWarning(
                    "Turn {Turn}: cumulative conversation tokens (~{Cumulative}) have reached {Percent:P0} of " +
                    "the model's {ContextLength}-token context window - further turns risk failing outright " +
                    "with LM Studio's exceed_context_size_error.",
                    turnNumber, cumulativeTokens, (double)cumulativeTokens / contextLength, contextLength);
            }

            if (modelMessage.ToolCalls.Count == 0)
            {
                // Token-based only: the Responses API gives no finish_reason, so a full window is the one signal we have.
                if (contextLength > 0 && cumulativeTokens >= contextLength)
                {
                    stopReason = AgentStopReason.ContextExhausted;
                    _logger.LogWarning(
                        "Turn {Turn}: model made no tool calls with {Cumulative} of {ContextLength} context tokens used - " +
                        "context window exhausted, not a genuine finish.",
                        turnNumber, cumulativeTokens, contextLength);
                }
                else
                {
                    stopReason = AgentStopReason.ModelFinished;
                    _logger.LogInformation("Turn {Turn}: model made no tool calls, treating as finished.", turnNumber);
                }

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
            // Injections wait until every tool result of this turn is in `messages`: a message between a call and
            // its result would break the call/result pairing.
            var pendingInjections = new List<AgentAssist>();
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

                var injection = assistTracker.Observe(toolCall, resultJson, isError);
                if (injection is not null)
                {
                    pendingInjections.Add(injection);
                }

                if (!isError)
                {
                    lastFailureSignature = null;
                    consecutiveFailures = 0;
                    latchRefusals.Clear();
                    continue;
                }

                if (ToolLoopLatch.RefusalMessage(toolCall.Name, resultJson) is { } refusalMessage)
                {
                    var refusalCount = latchRefusals.GetValueOrDefault(toolCall.Name) + 1;
                    latchRefusals[toolCall.Name] = refusalCount;
                    RecordAssists(transcript, turnRecord, [new AgentAssist(AgentAssist.LatchKind, toolCall.Name, refusalCount, refusalMessage)]);
                    await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);
                    if (refusalCount > LoopLatchOptions.IgnoredRefusalLimit)
                    {
                        stopReason = AgentStopReason.LoopLatchIgnored;
                        _logger.LogWarning(
                            "Turn {Turn}: {Tool} was refused by the server loop latch {Count} times and the model kept calling it (limit: {Limit} calls after the first refusal) - stopping the run.",
                            turnNumber, toolCall.Name, refusalCount, LoopLatchOptions.IgnoredRefusalLimit);
                        break;
                    }
                }

                var signature = BuildFailureSignature(toolCall.Name, toolCall.ArgumentsJson, resultJson);
                // TODO two identical failing calls alternated per turn (A,B,A,B: 10-chain run 301, 2026-11-10, T8-T19) never trip this consecutive-only breaker; Breaker_DoesNotTrip_WhenFailuresAreNotConsecutive pins that as "exploring" - decide whether repeats of the same signature since the last success should count.
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
                        "[{Signature}] across turns {FirstTurn}-{LastTurn} - tripping the repeated-failure " +
                        "breaker (limit {Limit}) instead of burning the remaining turn budget.",
                        turnNumber, toolCall.Name, consecutiveFailures, signature,
                        firstFailureTurn, turnNumber, _repeatedFailureLimit);
                    break;
                }
            }

            if (pendingInjections.Count > 0)
            {
                foreach (var pending in pendingInjections)
                {
                    messages.Add(assistTracker.CreateAssistMessage(pending.Text));
                }

                RecordAssists(transcript, turnRecord, pendingInjections);
                await WriteTranscriptAsync(transcript, transcriptDirectory, cancellationToken);
            }

            if (stopReason is AgentStopReason.RepeatedToolFailure or AgentStopReason.LoopLatchIgnored)
            {
                break;
            }
        }

        // The outcome sentence is added only when loop assist is on, so the off-path line is unchanged.
        var assistOutcome = assistTracker.Enabled
            ? " " + LoopAssistTracker.DescribeRunOutcome(stopReason == AgentStopReason.ModelFinished, transcript.AssistCount ?? 0)
            : "";
        _logger.LogInformation("Agent run finished: {StopReason} after {TurnCount} turn(s), {Elapsed}.{AssistOutcome}", stopReason, turnNumber, overallStopwatch.Elapsed, assistOutcome);

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
    /// Replays <paramref name="seedTurns"/> into <paramref name="messages"/> and <paramref name="transcript"/> the way the
    /// live loop builds them (assistant message, one tool message per call, then any loop-assist injection after the
    /// turn's results), observing each call with the tracker. The seeded copies carry no Assists of their own: the
    /// tracker recomputes them, so a source transcript recorded with a different assist setting is seeded consistently
    /// with this run's setting. Returns the last seeded turn number.
    /// </summary>
    // TODO seeded failures do not feed the consecutive-failure breaker (lastFailureSignature/consecutiveFailures live in RunAsync); harmless while the limit is high, as in the eval fixtures.
    private int SeedConversation(
        IReadOnlyList<AgentTranscriptTurn> seedTurns,
        List<AgentChatMessage> messages,
        AgentTranscript transcript,
        LoopAssistTracker assistTracker)
    {
        foreach (var seed in seedTurns)
        {
            var turn = new AgentTranscriptTurn
            {
                TurnNumber = seed.TurnNumber,
                ModelMessage = seed.ModelMessage,
                ModelLatency = seed.ModelLatency,
                StartedAt = seed.StartedAt,
                CompletedAt = seed.CompletedAt,
                ToolCalls = [.. seed.ToolCalls],
            };
            transcript.Turns.Add(turn);
            messages.Add(seed.ModelMessage);

            var pendingInjections = new List<AgentAssist>();
            foreach (var call in seed.ToolCalls)
            {
                messages.Add(new AgentChatMessage { Role = "tool", ToolCallId = call.ToolCallId, Content = call.ResultJson });
                var injection = assistTracker.Observe(
                    new AgentToolCall { Id = call.ToolCallId, Name = call.ToolName, ArgumentsJson = call.ArgumentsJson },
                    call.ResultJson, call.IsError);
                if (injection is not null)
                {
                    pendingInjections.Add(injection);
                }
            }

            foreach (var pending in pendingInjections)
            {
                messages.Add(assistTracker.CreateAssistMessage(pending.Text));
            }

            RecordAssists(transcript, turn, pendingInjections);
        }

        return seedTurns[^1].TurnNumber;
    }

    /// <summary>
    /// Stamps <paramref name="assists"/> on <paramref name="turn"/> (Assists), adds them to the run's AssistCount and
    /// logs one agent.log line each. No-op for an empty list, so a turn that got no assist keeps Assists null.
    /// The log lines start "Turn N: loop assist" and match none of Parse-AgentLog.ps1's record patterns.
    /// </summary>
    private void RecordAssists(AgentTranscript transcript, AgentTranscriptTurn turn, List<AgentAssist> assists)
    {
        if (assists.Count == 0)
        {
            return;
        }

        turn.Assists ??= [];
        turn.Assists.AddRange(assists);
        transcript.AssistCount = (transcript.AssistCount ?? 0) + assists.Count;
        foreach (var assist in assists)
        {
            _logger.LogInformation(
                "Turn {Turn}: loop assist ({Kind}) for {Tool}, repeat count {RepeatCount}: {Text}",
                turn.TurnNumber, assist.Kind, assist.ToolName, assist.RepeatCount, assist.Text);
        }
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
    /// Finds <paramref name="propertyName"/> at the root or shallowly nested beneath it -> error
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
            return ($$"""{"isError":true,"error":"Malformed tool-call arguments JSON: {{JsonEncode(ex.Message)}}"}""", true, stopwatch.Elapsed, startedAt, DateTimeOffset.Now);
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
            return ($$"""{"isError":true,"error":"MCP call threw: {{JsonEncode(ex.Message)}}"}""", true, stopwatch.Elapsed, startedAt, DateTimeOffset.Now);
        }

        stopwatch.Stop();
        var completedAt = DateTimeOffset.Now;
        var text = result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text)
            .FirstOrDefault() ?? "";

        // Check both envelope layers: the outer MCP-protocol IsError and the inner domain-level
        // SentinelCallToolResult<T>.IsError -> see [[project_searchmode_literal_override_bug]], now fixed
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
                && doc.RootElement.TryGetProperty("isError", out var errorProp)
                && errorProp.ValueKind == JsonValueKind.True;
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
    /// naming that file and the original size. transcript.json is unaffected -> this only changes
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
            : $"[{json.Length / 1024.0:F1} KB offloaded -> {sidecarFileName}]";
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
