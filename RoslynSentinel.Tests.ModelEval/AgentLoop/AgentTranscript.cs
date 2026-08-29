namespace RoslynSentinel.Tests.ModelEval.AgentLoop;

/// <summary>
/// Full record of one <see cref="ModelAgentRunner.RunAsync"/> execution: every message sent to the
/// model, every response, and every tool call + raw result. Written to disk as JSON per run so a
/// failure can be diagnosed after the fact without rerunning (see <see cref="AgentRunResult.TranscriptPath"/>).
/// </summary>
public sealed class AgentTranscript
{
    public List<AgentTranscriptTurn> Turns { get; } = [];
}

public sealed class AgentTranscriptTurn
{
    public required int TurnNumber { get; init; }
    public required AgentChatMessage ModelMessage { get; init; }
    public required TimeSpan ModelLatency { get; init; }
    public List<AgentToolCallRecord> ToolCalls { get; init; } = [];
}

public sealed class AgentToolCallRecord
{
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public required string ResultJson { get; init; }
    public required bool IsError { get; init; }
    public required TimeSpan Latency { get; init; }
}

/// <summary>Why the agent loop stopped.</summary>
public enum AgentStopReason
{
    ModelFinished,
    TurnCapExceeded,
    WallClockCapExceeded,
    UnknownToolRequested,
}

public sealed class AgentRunResult
{
    public required AgentStopReason StopReason { get; init; }
    public required AgentTranscript Transcript { get; init; }
    public required string TranscriptPath { get; init; }
    public required int TurnCount { get; init; }

    /// <summary>True only when the model stopped on its own (no more tool calls) within the caps — says nothing about whether the task was actually done correctly.</summary>
    public bool Converged => StopReason == AgentStopReason.ModelFinished;
}
