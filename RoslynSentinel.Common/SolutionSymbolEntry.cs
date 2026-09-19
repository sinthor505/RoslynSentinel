namespace RoslynSentinel.Common;

/// <summary>Single solution-wide symbol entry returned by ListAll -> an OutlineItem plus the file it was found in.</summary>
public record SolutionSymbolEntry(FilePathWrapper FilePath, string Kind, string Name, string? Container, int StartLine, int EndLine);
