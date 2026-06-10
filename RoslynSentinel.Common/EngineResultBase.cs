using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

public record EngineResultBase
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
    }   // non-null if Modified
    public FilePath FilePath
    {
        get; init;
    }
    public string Message { get; init; } = ""; // for error details in case of failure
    public bool IsCommitted { get; init; } = false; // whether changes have been applied to the workspace (vs. just in-memory)
    public int ChangeId
    {
        get; init;
    }
}
