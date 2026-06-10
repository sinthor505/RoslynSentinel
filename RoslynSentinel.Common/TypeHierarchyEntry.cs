namespace RoslynSentinel.Common;

public record TypeHierarchyEntry(
    string TypeName,
    string? FilePath,
    int? Line,
    string Kind
);
