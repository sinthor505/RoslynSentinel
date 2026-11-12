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
}

/// <summary>Why the agent loop stopped.</summary>
public enum AgentStopReason
{
    ModelFinished,
    TurnCapExceeded,
    WallClockCapExceeded,
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

    /// <summary>The model sent tool-call markup as text instead of a tool call on two consecutive turns (the first got a nudge); nothing was parsed.</summary>
    MalformedToolCall,

    /// <summary>A caller-supplied target predicate held after a live turn's tool results were appended.</summary>
    TargetReached,
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
    public required int TurnCount
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
