namespace RoslynSentinel.Basic;

/// <summary>
/// A file attached to the solution via a .sln Solution Folder (ProjectSection(SolutionItems)),
/// returned by ListSolutionItems(kind: solutionItems). SolutionFolder is the enclosing folder's
/// display name (e.g. "Solution Items").
/// </summary>
public record SolutionItemFile(FilePathWrapper FilePath, string SolutionFolder);
