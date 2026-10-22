using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>How the SubAgent tool asks the dispatched model to shape its final message.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SubAgentResponseFormat
{
    /// <summary>Free text. The model's final message is returned verbatim.</summary>
    Text,

    /// <summary>
    /// The prompt is extended with an instruction to end with a single JSON value. The model's final
    /// message is still returned verbatim: there is no server-side schema validation, so a caller
    /// must parse defensively.
    /// </summary>
    Json,
}

/// <summary>Outcome of one SubAgent call: the dispatched model's answer plus how the run ended.</summary>
public sealed record SubAgentResult : IOperationResult
{
    /// <summary>The dispatched model's final message, verbatim. Empty if the run produced no message.</summary>
    public required string Output { get; init; }

    /// <summary>
    /// True only when the model stopped on its own within the caps. When false, <see cref="Output"/>
    /// is whatever the model last said and is probably not a finished answer; see <see cref="StopReason"/>.
    /// </summary>
    public required bool Converged { get; init; }

    /// <summary>The <c>AgentStopReason</c> the loop ended with, as text.</summary>
    public required string StopReason { get; init; }

    public required int TurnCount { get; init; }

    /// <summary>The run's transcript.json, for a follow-up read.</summary>
    public required string TranscriptPath { get; init; }

    /// <summary><see cref="IOperationResult.IsError"/>: true when the model did not converge. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => !Converged;

    /// <summary><see cref="IOperationResult.Status"/>: Failed when not converged, else Success. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => IsError ? ResultStatus.Failed : ResultStatus.Success;

    /// <summary><see cref="Code"/>: Exception when not converged; null otherwise. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ToolErrorCode.Exception : null;

    /// <summary><see cref="Message"/>: names the stop reason when not converged; null otherwise. Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => IsError ? $"Sub-agent stopped without converging: {StopReason}" : null;
}
