namespace RoslynSentinel.Git;

/// <summary>One tag in a tag list.</summary>
public record GitTagEntry
{
    public string Name { get; set; } = "";

    /// <summary>"lightweight" or "annotated".</summary>
    public string Kind { get; set; } = "";

    /// <summary>Abbreviated hash (7+ characters) of the commit the tag points at (peeled for annotated tags).</summary>
    public string TargetHash { get; set; } = "";

    /// <summary>Annotation subject for an annotated tag, otherwise the tagged commit's subject.</summary>
    public string Subject { get; set; } = "";
}
