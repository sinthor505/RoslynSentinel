using System.Text.Json.Serialization;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Full record of one <see cref="ModelAgentRunner.RunAsync"/> execution: every message sent to the
/// model, every response, and every tool call + raw result. Written to disk as JSON per run so a
/// failure can be diagnosed after the fact without rerunning (see <see cref="AgentRunResult.TranscriptPath"/>).
/// </summary>
public sealed class AgentTranscript
{
    /// <summary>
    /// Stable identifier for this run, so a transcript or agent.log line can be attributed to its
    /// run without inferring from timestamps -> the same value that, for PlanStepRunner, is also
    /// passed to the server as --run-id and stamped on every server-side log line.
    /// </summary>
    public string RunId { get; set; } = "";

    /// <summary>The system prompt the run was seeded with -> set once, before any turn runs.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>The user prompt the run was seeded with -> set once, before any turn runs.</summary>
    public string UserPrompt { get; set; } = "";

    /// <summary>
    /// Names of the tools offered to the model for this run, in the order sent in the request's tools array. Always
    /// written, so "was tool X available in this run" is answerable from any transcript.
    /// </summary>
    public List<string> Tools { get; } = [];

    /// <summary>
    /// How many assists (collapse or injection, see <see cref="AgentAssist"/>) the run's turns received. Null, and so
    /// omitted from the JSON, unless loop assist was on; 0 then means it was on and never needed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AssistCount
    {
        get; set;
    }

    /// <summary>
    /// Git HEAD of the working tree under test, stamped by the ModelEval assembly setup (see <see cref="ModelAgentRunner.ProcessStamp"/>).
    /// Null means the run was not stamped (PlanStepRunner and sub-agent runs), so the property is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitHash
    {
        get; set;
    }

    /// <summary>
    /// True when the working tree under test had uncommitted changes at stamp time. Null means the run was not stamped
    /// (PlanStepRunner and sub-agent runs), so the property is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WorkingTreeDirty
    {
        get; set;
    }

    /// <summary>
    /// Error policy mode in force for the run (off, pointer or inline), stamped like <see cref="CommitHash"/>.
    /// Null means the run was not stamped (or the transcript predates this), so the property is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorPolicyMode
    {
        get; set;
    }

    /// <summary>
    /// Content hash of the error policy file in force for the run; empty when stamped with no policy file.
    /// Null means the run was not stamped (or the transcript predates this), so the property is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorPolicyHash
    {
        get; set;
    }

    /// <summary>
    /// Hash of the workflow-contract text the run used (first 12 hex of SHA-256).
    /// Null means the run was not stamped, so the property is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractHash
    {
        get; set;
    }

    /// <summary>
    /// Where the workflow-contract Endings were placed: "system" or "system+user".
    /// Null means the run was not stamped, so the property is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractPlacement
    {
        get; set;
    }

    /// <summary>
    /// Id of the scenario the run executed, passed to <see cref="ModelAgentRunner.RunAsync"/> by the code that runs a scenario
    /// so a saved transcript can be re-scored against its scenario without results.csv.
    /// Null means the run had no scenario (or the transcript predates this field), so the property is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ScenarioId
    {
        get; set;
    }

    /// <summary>
    /// LM Studio model key the run used, stamped from the client by <see cref="ModelAgentRunner.RunAsync"/>.
    /// Null means the transcript predates this field, so the property is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelKey
    {
        get; set;
    }

    /// <summary>
    /// Sampling temperature sent for the run (<see cref="LlmOptions.Temperature"/>). Null means the server default was
    /// used (nothing was sent) or the transcript predates this field; either way it is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature
    {
        get; set;
    }

    /// <summary>
    /// Nucleus sampling top-p sent for the run (<see cref="LlmOptions.TopP"/>). Null means the server default was
    /// used (nothing was sent) or the transcript predates this field; either way it is omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP
    {
        get; set;
    }

    /// <summary>
    /// Model's loaded context length in tokens for the run. Null when unknown or the transcript predates this field; omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextLength
    {
        get; set;
    }

    /// <summary>
    /// Sampler and model-load settings for the run; null, and so omitted from the JSON, for transcripts written before this was recorded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunSettings? Settings
    {
        get; set;
    }

    /// <summary>
    /// Reasoning replay policy mode text, for example "pressure:0.7,pinned:3", "all" or "window:10". Null when the transcript predates this field; omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningReplayMode
    {
        get; set;
    }

    /// <summary>Thinking settings the run used; null when the transcript predates this field; omitted from the JSON.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ThinkingOptions? Thinking
    {
        get; set;
    }

    /// <summary>
    /// Turn numbers whose request first dropped a block of reasoning. Null when none or the transcript predates this field; omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? ReasoningDropTurns
    {
        get; set;
    }

    /// <summary>
    /// Tool name to description, for the same tools as <see cref="Tools"/>. Null, and so omitted from the JSON, for transcripts written before this was recorded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ToolDescriptions
    {
        get; set;
    }

    /// <summary>
    /// Tool name to the tool's parameters JSON schema, for the same tools as <see cref="Tools"/>. Null, and so omitted from the JSON, for transcripts written before this was recorded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, System.Text.Json.JsonElement>? ToolSchemas
    {
        get; set;
    }

    /// <summary>
    /// Why the run ended. Set only when the run ends, so the intermediate writes during a run omit it. Null, and so omitted from the JSON, for transcripts written before this was recorded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentStopReason? StopReason
    {
        get; set;
    }

    public List<AgentTranscriptTurn> Turns { get; } = [];

    /// <summary>
    /// Run-level totals computed when the run ends, so intermediate writes omit it; null, and so omitted from the JSON, for transcripts written before this was recorded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunTotals? Summary
    {
        get; set;
    }

    // Models that emit reasoning inline wrap it in think tags; an unclosed or bare tag is not an answer either.
    private static readonly System.Text.RegularExpressions.Regex ThinkTags = new(
        @"<think>[\s\S]*?</think>|</?think>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string WithoutThinkTags(string? text) =>
        string.IsNullOrEmpty(text) ? "" : ThinkTags.Replace(text, "").Trim();

    /// <summary>
    /// The model's final text: the last turn's Content, or its ReasoningContent when Content is blank
    /// (some models put the whole answer in reasoning), or "" when there is no turn or both are blank.
    /// </summary>
    public string FinalMessage()
    {
        if (Turns.Count == 0)
        {
            return "";
        }

        var last = Turns[^1].ModelMessage;
        var content = WithoutThinkTags(last.Content);
        return content.Length > 0 ? content : WithoutThinkTags(last.ReasoningContent);
    }

    /// <summary>
    /// <see cref="FinalMessage"/> for a run that genuinely finished: StopReason is ModelFinished, or null for a transcript written before StopReason was recorded. Any other stop returns "".
    /// A run stopped by a cap, a breaker or an error ends on a mid-task message, which is not an answer. Grading sites use this; SubAgent callers keep FinalMessage().
    /// </summary>
    public string FinalMessageIfFinished() =>
        StopReason is null or AgentStopReason.ModelFinished ? FinalMessage() : "";

    /// <summary>True when <see cref="FinalMessage"/> came from reasoning because the last turn's Content was blank.</summary>
    public bool FinalMessageIsFromReasoning() =>
        Turns.Count > 0
        && WithoutThinkTags(Turns[^1].ModelMessage.Content).Length == 0
        && WithoutThinkTags(Turns[^1].ModelMessage.ReasoningContent).Length > 0;
}

public sealed class AgentTranscriptTurn
{
    public required int TurnNumber
    {
        get; init;
    }
    public required AgentChatMessage ModelMessage
    {
        get; init;
    }
    public required TimeSpan ModelLatency
    {
        get; init;
    }

    /// <summary>Local wall-clock time the model call for this turn was issued, for joining against Serilog server-log timestamps.</summary>
    public required DateTimeOffset StartedAt
    {
        get; init;
    }

    /// <summary>Local wall-clock time the model call for this turn completed.</summary>
    public required DateTimeOffset CompletedAt
    {
        get; init;
    }

    public List<AgentToolCallRecord> ToolCalls { get; init; } = [];

    /// <summary>The loop-assist assists this turn received; null, and so omitted from the JSON, when none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AgentAssist>? Assists
    {
        get; set;
    }

    /// <summary>The thinking decision for this turn; null, and so omitted from the JSON, when the run predates it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TurnThinking? Thinking
    {
        get; set;
    }
}

public sealed class AgentToolCallRecord
{
    public required string ToolName
    {
        get; init;
    }
    public required string ArgumentsJson
    {
        get; init;
    }
    public required string ResultJson
    {
        get; init;
    }
    public required bool IsError
    {
        get; init;
    }
    public required TimeSpan Latency
    {
        get; init;
    }

    /// <summary>Local wall-clock time this tool call was issued, for joining against Serilog server-log timestamps.</summary>
    public required DateTimeOffset StartedAt
    {
        get; init;
    }

    /// <summary>Local wall-clock time this tool call completed.</summary>
    public required DateTimeOffset CompletedAt
    {
        get; init;
    }

    /// <summary>The model's own tool-call id (already used to pair the tool-role reply message) -> free correlation against a raw MCP trace.</summary>
    public required string ToolCallId
    {
        get; init;
    }

    /// <summary>Text of the result's trailing content blocks as the model saw them; null, and so omitted from the JSON, when there were none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrailingText
    {
        get; init;
    }

    /// <summary>
    /// The result's MCP <c>_meta</c> object as the server sent it (for example <c>nearMissSubstitutions</c>); null, and so omitted from the JSON, when the result
    /// carried none. Harness-side evidence only: it is never part of the model-facing tool message (see ModelAgentRunner.ModelFacingToolContent).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonObject? Meta
    {
        get; init;
    }
}

/// <summary>
/// Run-level totals for one transcript, computed when the run ends. Nullable members are null when no turn reported it.
/// </summary>
public sealed class RunTotals
{
    /// <summary>Wall-clock duration of the whole run.</summary>
    public TimeSpan TotalDuration
    {
        get; init;
    }

    /// <summary>Sum of the model latency across all turns.</summary>
    public TimeSpan TotalModelTime
    {
        get; init;
    }

    /// <summary>Sum of the latency across all tool calls.</summary>
    public TimeSpan TotalToolTime
    {
        get; init;
    }

    /// <summary>Number of model turns in the run.</summary>
    public int TurnCount
    {
        get; init;
    }

    /// <summary>Number of tool calls in the run.</summary>
    public int ToolCallCount
    {
        get; init;
    }

    /// <summary>Number of tool calls whose result was an error.</summary>
    public int ToolErrorCount
    {
        get; init;
    }

    /// <summary>Total input tokens across turns, null when no turn reported it.</summary>
    public int? InputTokens
    {
        get; init;
    }

    /// <summary>Total cached input tokens across turns, null when no turn reported it.</summary>
    public int? CachedInputTokens
    {
        get; init;
    }

    /// <summary>Total new (uncached) input tokens across turns, null when no turn reported it.</summary>
    public int? NewInputTokens
    {
        get; init;
    }

    /// <summary>Total output tokens across turns, null when no turn reported it.</summary>
    public int? OutputTokens
    {
        get; init;
    }

    /// <summary>Total reasoning tokens across turns, null when no turn reported it.</summary>
    public int? ReasoningTokens
    {
        get; init;
    }

    /// <summary>Number of turns that requested thinking.</summary>
    public int ThinkingTurnCount
    {
        get; init;
    }

    /// <summary>Number of turns whose reasoning exceeded the observed budget.</summary>
    public int OverBudgetTurnCount
    {
        get; init;
    }

    /// <summary>Mean output tokens per turn, null when no turn reported it.</summary>
    public double? AverageOutputTokensPerTurn
    {
        get; init;
    }

    /// <summary>Mean reasoning tokens per turn, null when no turn reported it.</summary>
    public double? AverageReasoningTokensPerTurn
    {
        get; init;
    }

    /// <summary>Mean wall-clock duration per turn.</summary>
    public TimeSpan AverageTurnDuration
    {
        get; init;
    }

    /// <summary>Count of tool errors per error code.</summary>
    public Dictionary<string, int> ToolErrorCodes { get; init; } = [];

    /// <summary>Count of compiler errors per CS error code.</summary>
    public Dictionary<string, int> CompilerErrorCodes { get; init; } = [];

    /// <summary>Set true only by scripts/Backfill-TranscriptSummary.ps1; null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Backfilled
    {
        get; init;
    }

    /// <summary>Matches a compiler diagnostic id such as CS0103 inside a tool result.</summary>
    private static readonly System.Text.RegularExpressions.Regex CompilerCodePattern = new(@"\bCS\d{4}\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds the totals from the transcript's turns; totalDuration is the run's wall clock.
    /// Mirrors the logic in scripts/Backfill-TranscriptSummary.ps1; keep the two in step.
    /// </summary>
    public static RunTotals FromTranscript(AgentTranscript transcript, TimeSpan totalDuration)
    {
        var turns = transcript.Turns;
        var calls = turns.SelectMany(turn => turn.ToolCalls).ToList();

        var toolErrorCodes = new Dictionary<string, int>();
        var compilerErrorCodes = new Dictionary<string, int>();
        foreach (var failed in calls.Where(call => call.IsError))
        {
            var errorCode = ToolErrorCodeOf(failed.ResultJson);
            toolErrorCodes[errorCode] = toolErrorCodes.GetValueOrDefault(errorCode) + 1;
            foreach (var code in CompilerCodePattern.Matches(failed.ResultJson).Select(match => match.Value).Distinct())
            {
                compilerErrorCodes[code] = compilerErrorCodes.GetValueOrDefault(code) + 1;
            }
        }

        var messages = turns.Select(turn => turn.ModelMessage).ToList();
        var inputTokens = SumOrNull(messages.Select(message => message.InputTokens));
        var cachedInputTokens = SumOrNull(messages.Select(message => message.CachedInputTokens));
        var outputTokens = SumOrNull(messages.Select(message => message.OutputTokens));
        var reasoningTokens = SumOrNull(messages.Select(message => message.ReasoningTokens));

        var totalModelTime = turns.Select(turn => turn.ModelLatency).Aggregate(TimeSpan.Zero, (sum, latency) => sum + latency);
        var totalToolTime = calls.Select(call => call.Latency).Aggregate(TimeSpan.Zero, (sum, latency) => sum + latency);
        var averageTurnDuration = turns.Count == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((totalModelTime + totalToolTime).Ticks / turns.Count);

        return new RunTotals
        {
            TotalDuration = totalDuration,
            TotalModelTime = totalModelTime,
            TotalToolTime = totalToolTime,
            TurnCount = turns.Count,
            ToolCallCount = calls.Count,
            ToolErrorCount = calls.Count(call => call.IsError),
            InputTokens = inputTokens,
            CachedInputTokens = cachedInputTokens,
            NewInputTokens = inputTokens is int input ? input - (cachedInputTokens ?? 0) : null,
            OutputTokens = outputTokens,
            ReasoningTokens = reasoningTokens,
            AverageOutputTokensPerTurn = AverageOver(outputTokens, messages.Count(message => message.OutputTokens is not null)),
            AverageReasoningTokensPerTurn = AverageOver(reasoningTokens, messages.Count(message => message.ReasoningTokens is not null)),
            AverageTurnDuration = averageTurnDuration,
            ToolErrorCodes = toolErrorCodes,
            CompilerErrorCodes = compilerErrorCodes,
        };

        // Sum of the non-null values, or null when every value is null.
        static int? SumOrNull(IEnumerable<int?> values)
        {
            int? total = null;
            foreach (var value in values)
            {
                if (value is int number)
                {
                    total = (total ?? 0) + number;
                }
            }
            return total;
        }

        static double? AverageOver(int? total, int count) =>
            total is int sum && count > 0 ? (double)sum / count : null;

        // Reads root.errorData.errorCode from a tool result; "unknown" when the result is not JSON or lacks a string code.
        static string ToolErrorCodeOf(string resultJson)
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(resultJson);
                var root = document.RootElement;
                if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                    && root.TryGetProperty("errorData", out var errorData)
                    && errorData.ValueKind == System.Text.Json.JsonValueKind.Object
                    && errorData.TryGetProperty("errorCode", out var errorCode)
                    && errorCode.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return errorCode.GetString() ?? "unknown";
                }
                return "unknown";
            }
            catch (System.Text.Json.JsonException)
            {
                return "unknown";
            }
        }
    }
}

/// <summary>Why the agent loop stopped.</summary>
public enum AgentStopReason
{
    ModelFinished,
    TurnCapExceeded,
    WallClockCapExceeded,
    /// <summary>
    /// The model named a tool outside the list it was shown on <see cref="ModelAgentRunner.ConsecutiveUnavailableToolLimit"/>
    /// consecutive calls. Each such call before that got the host's error result as its tool message and the run continued.
    /// </summary>
    UnknownToolRequested,

    /// <summary>
    /// The same tool failed the same way N consecutive times and the loop was cut short. Distinct
    /// from <see cref="TurnCapExceeded"/> on purpose: "looped on one error" and "ran out of turns
    /// making progress" call for completely different follow-up, and run 20260910-013550-398 was
    /// reported as the latter when it was really the former for its last 23 turns.
    /// </summary>
    RepeatedToolFailure,

    /// <summary>
    /// The model kept calling a tool the server's loop latch had disabled: after the first refusal it called the same tool
    /// <see cref="LoopLatchOptions.IgnoredRefusalLimit"/> more times. The model is past recovery; stopping here instead of
    /// letting it burn turns to <see cref="RepeatedToolFailure"/>.
    /// </summary>
    LoopLatchIgnored,

    /// <summary>
    /// A tool-less turn whose backend-reported TotalTokens reached the model's context window. The model's reply was likely
    /// cut short or empty because the window is full, so this is NOT a genuine finish.
    /// </summary>
    ContextExhausted,

    /// <summary>The model's streamed response degenerated into placeholder or repeated tool calls on two attempts; no call from it was executed; not a genuine finish.</summary>
    StreamRunaway,

    /// <summary>The model sent tool-call markup as text instead of a tool call on two consecutive turns (the first got a nudge); nothing was parsed.</summary>
    MalformedToolCall,

    /// <summary>The model repeated the same successful tool call (same tool, same arguments apart from reason) after the runner nudged it for doing so (see ModelAgentRunner.IdenticalSuccessNudgeAt).</summary>
    RepeatedIdenticalCall,

    /// <summary>A caller-supplied target predicate held after a live turn's tool results were appended.</summary>
    TargetReached,

    /// <summary>The run was cancelled, e.g. by an early-stop cancellation token; set when OperationCanceledException propagates out of the loop.</summary>
    Cancelled,

    /// <summary>An unhandled exception ended the run.</summary>
    Exception,

    /// <summary>
    /// The model's streamed response ended without a <c>response.completed</c> event on two attempts (the host closed the connection
    /// early); nothing from it was recorded or executed; not a genuine finish. Declared last on purpose: stop reasons are persisted
    /// as numbers in transcript.json, so inserting a member earlier would renumber the stored ones.
    /// </summary>
    StreamTruncated,

    /// <summary>
    /// The host echoed a sampler value (temperature or top_p) different from the one the run sent, so the run was refused;
    /// nothing from the model was recorded or executed; not a genuine finish. Declared last on purpose: stop reasons are persisted
    /// as numbers in transcript.json, so inserting a member earlier would renumber the stored ones.
    /// </summary>
    SamplerMismatch,

    /// <summary>
    /// The model's streamed text or reasoning repeated one sentence K times (see StreamPhraseLoopDetector), so the stream was aborted
    /// and not retried; nothing from the turn was recorded or executed; not a genuine finish. Declared last on purpose: stop reasons
    /// are persisted as numbers in transcript.json, so inserting a member earlier would renumber the stored ones.
    /// </summary>
    ReasoningLoopDetected,
}

/// <summary>
/// What tripped the repeated-failure breaker, carried out of the loop so a caller can report or
/// persist it without re-deriving it from the transcript.
/// </summary>
/// <param name="ToolName">The tool that kept failing.</param>
/// <param name="Signature">The tool+errorCode+target identity the consecutive failures shared.</param>
/// <param name="FailureCount">How many consecutive failures were seen (equals the configured limit).</param>
/// <param name="FirstTurn">Turn the streak started on.</param>
/// <param name="LastTurn">Turn the breaker tripped on.</param>
/// <param name="ArgumentsJson">Arguments of the final failing call.</param>
/// <param name="ResultJson">Result body of the final failing call.</param>
public sealed record RepeatedFailureDetail(
    string ToolName,
    string Signature,
    int FailureCount,
    int FirstTurn,
    int LastTurn,
    string ArgumentsJson,
    string ResultJson);

public sealed class AgentRunResult
{
    public required AgentStopReason StopReason
    {
        get; init;
    }
    public required AgentTranscript Transcript
    {
        get; init;
    }
    public required string TranscriptPath
    {
        get; init;
    }
    /// <summary>Recorded turns: the number of transcript turns (the same value as <c>Transcript.Summary.TurnCount</c>). A turn whose model call was dropped mid-call is not counted here; see <see cref="StartedTurnCount"/>.</summary>
    public required int TurnCount
    {
        get; init;
    }

    /// <summary>Turns started, including one dropped mid-call (wall-clock cap, stream runaway or truncation, context overflow) that never got a transcript turn. 0 when not set.</summary>
    public int StartedTurnCount
    {
        get; init;
    }

    /// <summary>Set only when <see cref="StopReason"/> is <see cref="AgentStopReason.RepeatedToolFailure"/>.</summary>
    public RepeatedFailureDetail? RepeatedFailure
    {
        get; init;
    }

    /// <summary>True only when the model stopped on its own (no more tool calls) within the caps -> says nothing about whether the task was actually done correctly. A <see cref="AgentStopReason.ContextExhausted"/> stop is therefore not converged. <see cref="AgentStopReason.TargetReached"/> is excluded on purpose: no Converged consumer (SubAgent, PlanStepRunner) passes a target predicate.</summary>
    public bool Converged => StopReason == AgentStopReason.ModelFinished;
}
