namespace RoslynSentinel.Common;

/// <summary>Structural outline entry returned by get_file_outline.</summary>
public record OutlineItem(string Kind, string Name, string? Container, int StartLine, int EndLine);
