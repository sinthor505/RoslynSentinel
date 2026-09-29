namespace RoslynSentinel.Common;

public record ComprehensiveHealthReport(
    int TotalIssues,
    List<IssueCategoryCount> TotalIssuesByCategory,
    List<ProjectHealthSummary> ProjectSummaries,
    bool HasMoreData,
    int? NextProjectOffset,
    string StatusMessage);
