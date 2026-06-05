namespace RoslynSentinel.Server;

public enum DataTag
{
    SourceFilepath,
    SolutionFilepath,
    ProjectName,
    SymbolName,
    ScanId,
    OperationId,
    ContextSnippet,
    StartLine,
    EndLine,
    LineBefore,
    LineAfter,
    ContainerName,
    MemberKind,
    Offset,
    Limit,
    Scope
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class ConsumesAttribute : Attribute
{
    public DataTag Tag
    {
        get;
    }

    public ConsumesAttribute(DataTag tag, bool required = false)
    {
        this.Tag = tag;
        this.Required = required;
    }

    public bool Required
    {
        get;
    }
}
