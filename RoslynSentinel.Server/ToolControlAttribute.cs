namespace RoslynSentinel.Server;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ToolControlAttribute : Attribute
{
    public ToolControlTag Tag
    {
        get;
    }

    public ToolControlAttribute(ToolControlTag tag, bool required = false)
    {
        this.Tag = tag;
        this.Required = required;
    }

    public bool Required
    {
        get;
    }
}

public enum ToolControlTag
{
    Offset,
    ResultLimit,
    AutoStage,
    Timeout,
    MatchType,
    Filter,
    Pattern,
    MaxDepth,
    Sort,
    Domain,
    Detector,
    Aspect,
    ToolName,
    UnifiedDiff,
    RetryCount,
    ValidateOnApply,
    IsRegex,
    Direction,
    OverrideSymbolName,
    IncludeMethods,
    IncludeProperties,
    IncludeTypes,
    PersistBaseline,
    Preview,
    TopN
}
