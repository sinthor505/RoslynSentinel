using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

public record EngineResultBase : IOperationResult
{
    protected static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new System.Text.Json.JsonSerializerOptions()
    {
        WriteIndented = true
    };

    public EditOutcome Outcome
    {
        get; init;
    } = EditOutcome.Unset;
    [JsonIgnore]
    public string? UpdatedText
    {
        get; init;
    }
    public FilePathWrapper FilePath
    {
        get; init;
    }
    public string Message { get; init; } = ""; // for error details in case of failure
    public bool IsCommitted { get; init; } = false; // whether changes have been applied to the workspace (vs. just in-memory)
    public string ChangeId
    {
        get; init;
    } = string.Empty;

    /// <summary>Structured candidates when the failure was an ambiguous match (see <see cref="ToolAmbiguousMatchException"/>); null otherwise. Not serialized, so <see cref="ToJsonSummary"/> output is unchanged.</summary>
    [JsonIgnore]
    public IReadOnlyList<AmbiguousCandidate>? Candidates
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
    public virtual string? Code => Outcome.ToErrorCode();
}
