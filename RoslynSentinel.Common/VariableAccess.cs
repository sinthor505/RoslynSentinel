namespace RoslynSentinel.Common;

public record VariableAccess(
    FilePathWrapper FilePath,
    int Line,
    int Column,
    string AccessKind,
    string ContextStack,
    bool IsInLoop,
    bool IsInConditional
);
