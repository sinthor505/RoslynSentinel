namespace RoslynSentinel.Basic;

public record ReverseCallGraphNode(
    string MethodName,
    string ContainingType,
    string? FilePath,
    int? Line,
    List<ReverseCallGraphNode> Callers
);
