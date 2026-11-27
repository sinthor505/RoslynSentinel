namespace RoslynSentinel.Git;

/// <summary>Result of operation=tag. Which fields are set depends on the action: list sets Tags,
/// TotalCount and IsTruncated; create sets Created, TargetHash and Annotated; delete sets Deleted,
/// PreviousTargetHash and Note.</summary>
public record GitTagResult : GitResult
{
    public string Action { get; set; } = "";
    public List<GitTagEntry> Tags { get; set; } = [];
    public int? TotalCount
    {
        get; set;
    }
    public bool IsTruncated
    {
        get; set;
    }
    public string? Created
    {
        get; set;
    }
    public string? Deleted
    {
        get; set;
    }
    public string? TargetHash
    {
        get; set;
    }
    public string? PreviousTargetHash
    {
        get; set;
    }
    public bool? Annotated
    {
        get; set;
    }
    public string? Note
    {
        get; set;
    }
}
