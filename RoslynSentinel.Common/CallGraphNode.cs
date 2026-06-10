namespace RoslynSentinel.Common;

public record CallGraphNode(
    string MethodName,
    string ContainingType,
    string? FilePath,
    int? Line,
    List<CallGraphNode> Callees
);
