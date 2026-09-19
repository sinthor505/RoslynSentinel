namespace RoslynSentinel.Common;

/// <summary>Combined payload for ListSolutionItems(kind: all): everything the other kinds return in one call, deduplicated by file where applicable.</summary>
public record SolutionItemsAllResult(List<ProjectInfoEntry> Projects, List<SolutionItemFile> SolutionItems, List<ProjectFilesAndDependencies> ProjectDetails);
