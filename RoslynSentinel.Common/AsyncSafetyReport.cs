namespace RoslynSentinel.Common;

public record AsyncSafetyReport(FilePathWrapper filePath, string MethodName, string Reason);
