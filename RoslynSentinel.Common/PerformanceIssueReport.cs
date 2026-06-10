namespace RoslynSentinel.Common;

public record PerformanceIssueReport(FilePath FilePath, int Line, int Column, string IssueType, string Description);
