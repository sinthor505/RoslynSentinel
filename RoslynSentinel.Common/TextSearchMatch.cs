namespace RoslynSentinel.Common;

/// <summary>Single text-search hit returned by search_solution_text.</summary>
public record TextSearchMatch(FilePathWrapper filePath, int Line, int Column, string Preview, MatchKind MatchedAs, string? EnclosingMember = null);
