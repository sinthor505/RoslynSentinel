namespace RoslynSentinel.Common;

public record LargeMethodReport(FilePath filePath, string TypeName, string MethodName, int LineCount);
