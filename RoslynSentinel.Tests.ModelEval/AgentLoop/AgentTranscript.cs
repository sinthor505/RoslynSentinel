namespace RoslynSentinel.Tests.ModelEval.AgentLoop;

/// <summary>
/// Full record of one <see cref="ModelAgentRunner.RunAsync"/> execution: every message sent to the
/// model, every response, and every tool call + raw result. Written to disk as JSON per run so a
/// failure can be diagnosed after the fact without rerunning (see <see cref="AgentRunResult.TranscriptPath"/>).
/// </summary>
public sealed class AgentTranscript
{
    /// <summary>
    /// Stable identifier for this run, so a transcript or agent.log line can be attributed to its
    /// run without inferring from timestamps — the same value that, for PlanStepRunner, is also
    /// passed to the server as --run-id and stamped on every server-side log line.
    /// </summary>
    public string RunId { get; set; } = "";

    /// <summary>The system prompt the run was seeded with — set once, before any turn runs.</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>The user prompt the run was seeded with — set once, before any turn runs.</summary>
    public string UserPrompt { get; set; } = "";

    public List<AgentTranscriptTurn> Turns { get; } = [];
}

public sealed class AgentTranscriptTurn
{
    public required int TurnNumber { get; init; }
    public required AgentChatMessage ModelMessage { get; init; }
    public required TimeSpan ModelLatency { get; init; }

    /// <summary>Local wall-clock time the model call for this turn was issued, for joining against Serilog server-log timestamps.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Local wall-clock time the model call for this turn completed.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    public List<AgentToolCallRecord> ToolCalls { get; init; } = [];
}

public sealed class AgentToolCallRecord
{
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public required string ResultJson { get; init; }
    public required bool IsError { get; init; }
    public required TimeSpan Latency { get; init; }

    /// <summary>Local wall-clock time this tool call was issued, for joining against Serilog server-log timestamps.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Local wall-clock time this tool call completed.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>The model's own tool-call id (already used to pair the tool-role reply message) — free correlation against a raw MCP trace.</summary>
    public required string ToolCallId { get; init; }
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
    public required AgentStopReason StopReason { get; init; }
    public required AgentTranscript Transcript { get; init; }
    public required string TranscriptPath { get; init; }
    public required int TurnCount { get; init; }

    /// <summary>Set only when <see cref="StopReason"/> is <see cref="AgentStopReason.RepeatedToolFailure"/>.</summary>
    public RepeatedFailureDetail? RepeatedFailure { get; init; }

    /// <summary>True only when the model stopped on its own (no more tool calls) within the caps — says nothing about whether the task was actually done correctly.</summary>
    public bool Converged => StopReason == AgentStopReason.ModelFinished;
}
