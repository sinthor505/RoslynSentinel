using System.Text.Json.Serialization;

namespace RoslynSentinel.Git;

public record GitResult : IOperationResult
{
    public bool IsError
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }

    /// <summary>Canonical status: Success when not an error, otherwise <see cref="GitErrorCodes.ToResultStatus"/> of <see cref="Code"/>.</summary>
    ResultStatus IOperationResult.Status => IsError ? GitErrorCodes.ToResultStatus(Code) : ResultStatus.Success;

    /// <summary>The code the GitTool tool reports on failure: <see cref="ErrorKind"/>, or the fallback GitError. Null on success.</summary>
    [JsonIgnore]
    public string? Code => IsError ? ErrorKind ?? GitErrorCodes.Fallback : null;

    /// <summary>Machine-readable cause of a failure, one of <see cref="GitErrorCodes"/>, when the cause is
    /// known. Null means unclassified: the GitTool tool then reports the fallback code GitError. Never
    /// serialized with a success payload.</summary>
    [JsonIgnore]
    public string? ErrorKind
    {
        get; set;
    }

    /// <summary>Short actionable next step for a classified failure (which call would work). Null when
    /// the failure is unclassified.</summary>
    [JsonIgnore]
    public string? ErrorDetail
    {
        get; set;
    }

    /// <summary>Hints the engine attached to this result: keys plus call facts. The engine sets the keys and the tool
    /// boundary renders them into the text slots; never serialized. Null when the engine attached none.</summary>
    [JsonIgnore]
    internal IReadOnlyList<GitHint>? Hints
    {
        get; set;
    }
}
