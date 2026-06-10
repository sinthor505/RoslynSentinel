namespace RoslynSentinel.Common;

public record ProjectHealthSummary(string ProjectName, int TotalIssues, List<IssueCategoryCount> IssuesByCategory);
