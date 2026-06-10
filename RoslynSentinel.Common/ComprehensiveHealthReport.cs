namespace RoslynSentinel.Common;

public record ComprehensiveHealthReport(
    int TotalIssues, 
    List<IssueCategoryCount> TotalIssuesByCategory, 
    List<ProjectHealthSummary> ProjectSummaries,
    bool HasMore,
    int? NextProjectOffset,
    string StatusMessage);
