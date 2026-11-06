namespace RoslynSentinel.Engines.Basic;

public record ProjectConsistencyIssue(
    string IssueType,
    string Description,
    string ProjectName,
    string? FilePath
);
