namespace RoslynSentinel.Git;

/// <summary>Result of operation=stash. Which fields are set depends on the action: list sets Entries,
/// TotalCount and IsTruncated; push sets Created, Message, Note and Status; apply and pop set
/// AppliedIndex, Note and Status.</summary>
public record GitStashResult : GitResult
{
    public string Action { get; set; } = "";
    public List<GitStashEntry> Entries { get; set; } = [];
    public int? TotalCount
    {
        get; set;
    }
    public bool IsTruncated
    {
        get; set;
    }
    public bool? Created
    {
        get; set;
    }
    public int? AppliedIndex
    {
        get; set;
    }
    public string? Message
    {
        get; set;
    }
    public string? Note
    {
        get; set;
    }
    public GitStatusResult? Status
    {
        get; set;
    }
}
