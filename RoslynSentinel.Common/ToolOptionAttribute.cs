namespace RoslynSentinel.Common;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ToolOptionAttribute : Attribute
{
    public ToolOptionTag Tag
    {
        get;
    }

    public ToolOptionAttribute(ToolOptionTag tag, bool required = false)
    {
        this.Tag = tag;
        this.Required = required;
    }

    public bool Required
    {
        get;
    }
}

public enum ToolOptionTag
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
