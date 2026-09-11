namespace RoslynSentinel.Common;

public record SecurityIssueReport(FilePathWrapper filePath, int Line, int Column, string IssueType, string Description);
