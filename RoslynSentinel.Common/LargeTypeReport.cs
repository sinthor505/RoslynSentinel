namespace RoslynSentinel.Common;

public record LargeTypeReport(FilePathWrapper filePath, string TypeName, int LineCount);
