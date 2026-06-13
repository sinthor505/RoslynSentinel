namespace RoslynSentinel.Common;

public readonly struct SymbolHandle
{
    public string SessionId { get; init; }
    public string ProjectName { get; init; }
    public string DocCommentId { get; init; }

    public SymbolHandle(string sessionId, string projectName, string docCommentId)
    {
        SessionId = sessionId;
        ProjectName = projectName;
        DocCommentId = docCommentId;
    }

    public override string ToString() => $"{ProjectName}::{DocCommentId}";
}
