namespace RoslynSentinel.Common;

public record SecurityIssueReport(FilePath filePath, int Line, int Column, string IssueType, string Description);
