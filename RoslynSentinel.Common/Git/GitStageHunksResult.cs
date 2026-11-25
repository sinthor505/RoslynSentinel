namespace RoslynSentinel.Tools.Basic;

/// <summary>Result of staging selected hunks of one file. The inherited GitStatusResult members are the
/// repository status after the stage (copied from a StatusAsync result), so the caller sees the new index
/// state without a second call. Only the index changes; the working tree is never touched.</summary>
public record GitStageHunksResult : GitStatusResult
{
    public string Path { get; set; } = "";

    /// <summary>The hunks that were staged, as numbered in the listing the selection was made from.</summary>
    public List<GitHunkEntry> StagedHunks { get; set; } = [];

    /// <summary>How many unstaged hunks the file still has after the stage.</summary>
    public int RemainingUnstagedHunks
    {
        get; set;
    }

    /// <summary>Set when the remaining hunk count is not what the selection predicts (verify with a staged diff), or when the status could not be read after the stage.</summary>
    public string? Warning
    {
        get; set;
    }

    /// <summary>Set when a hunk was staged only in part: which lines went in and that the rest of that hunk stays unstaged.</summary>
    public string? Note
    {
        get; set;
    }
}
