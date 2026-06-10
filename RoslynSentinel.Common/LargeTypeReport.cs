namespace RoslynSentinel.Common;

public record LargeTypeReport(FilePath filePath, string TypeName, int LineCount);
