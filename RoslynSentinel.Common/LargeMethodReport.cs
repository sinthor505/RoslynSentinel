namespace RoslynSentinel.Common;

public record LargeMethodReport(FilePathWrapper filePath, string TypeName, string MethodName, int LineCount);
