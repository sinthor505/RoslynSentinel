namespace RoslynSentinel.Common;

public readonly struct SymbolHandle
{
    public string ProjectName
    {
        get; init;
    }
    public string DocCommentId
    {
        get; init;
    }

    public SymbolHandle(string projectName, string docCommentId)
    {
        ProjectName = projectName;
        DocCommentId = docCommentId;
    }

    public override string ToString() => $"{ProjectName}::{DocCommentId}";
}
