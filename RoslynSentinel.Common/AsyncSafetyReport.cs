namespace RoslynSentinel.Common;

public record AsyncSafetyReport(FilePath filePath, string MethodName, string Reason);
