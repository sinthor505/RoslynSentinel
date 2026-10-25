using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// Result of a project- or folder-level edit (create project, split project, namespace-folder preview).
/// Carries no single-file text: those operations describe their changes through the outcome and message.
/// Sits beside <see cref="EngineResultBase"/> until the project-level callers migrate to it.
/// </summary>
public sealed record ProjectEditResult : IOperationResult
{
    public required EditOutcome Outcome
    {
        get; init;
    }

    public FilePathWrapper? FilePath
    {
        get; init;
    }

    public string? Message
    {
        get; init;
    }

    /// <summary><see cref="IOperationResult.Status"/>: the canonical status of <see cref="Outcome"/>. Not serialized.</summary>
    [JsonIgnore]
    public ResultStatus Status => Outcome.ToResultStatus();

    /// <summary><see cref="IOperationResult.IsError"/>: computed from <see cref="Status"/> so it cannot disagree with <see cref="Outcome"/>. Not serialized.</summary>
    [JsonIgnore]
    public bool IsError => Status.IsErrorStatus();

    /// <summary><see cref="Code"/>: the <see cref="ToolErrorCode"/> for <see cref="Outcome"/>; null when not an error. Not serialized.</summary>
    [JsonIgnore]
    public string? Code => Outcome.ToErrorCode();

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new System.Text.Json.JsonSerializerOptions()
    {
        WriteIndented = true
    };

    public string ToJsonSummary()
    {
        return System.Text.Json.JsonSerializer.Serialize(this, _jsonOptions);
    }
}
