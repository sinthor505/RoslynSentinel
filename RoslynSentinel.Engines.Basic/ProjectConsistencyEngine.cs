namespace RoslynSentinel.Engines.Basic;

public record ProjectConsistencyIssue(
    string IssueType,
    string Description,
    string ProjectName,
    string? FilePath
);

public record ProjectFrameworkSummary(string ProjectName, string TargetFramework, string? FilePath);
