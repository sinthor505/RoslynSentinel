namespace RoslynSentinel.Common;

public record VariableAccess(
    FilePath FilePath,
    int Line,
    int Column,
    string AccessKind,
    string ContextStack,
    bool IsInLoop,
    bool IsInConditional
);
