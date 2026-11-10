namespace RoslynSentinel.Common;

/// <summary>
/// One project's aggregated files and dependencies, as returned within ListSolutionItems(kind: all). ListSolutionItems(kind: files)
/// and (kind: dependencies) without a projectName return the same group per project, with <paramref name="Dependencies"/>
/// respectively <paramref name="Files"/> null (not asked for).
/// </summary>
public record ProjectFilesAndDependencies(string ProjectName, List<string>? Files, ProjectDependencyReport? Dependencies);
