namespace RoslynSentinel.Common;

public record PerformanceIssueReport(FilePathWrapper FilePath, int Line, int Column, string IssueType, string Description);
