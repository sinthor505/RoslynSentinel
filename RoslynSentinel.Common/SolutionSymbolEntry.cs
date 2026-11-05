namespace RoslynSentinel.Common;

/// <summary>Single solution-wide symbol entry returned by ListAll -> an OutlineItem plus the file it was found in. DocCommentId is set only for Kind "namespace" rows (N:Full.Qualified.Name, ready for RenameSymbol); null for every other kind.</summary>
public record SolutionSymbolEntry(FilePathWrapper FilePath, string Kind, string Name, string? Container, int StartLine, int EndLine, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DocCommentId = null);
