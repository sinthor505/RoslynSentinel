namespace RoslynSentinel.Common;

/// <summary>A project entry returned by ListSolutionItems(kind: projects).</summary>
public record ProjectInfoEntry(string Name, string? FilePath);
