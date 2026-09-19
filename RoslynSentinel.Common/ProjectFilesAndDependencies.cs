namespace RoslynSentinel.Common;

/// <summary>One project's aggregated files and dependencies, as returned within ListSolutionItems(kind: all).</summary>
public record ProjectFilesAndDependencies(string ProjectName, List<string> Files, ProjectDependencyReport Dependencies);
