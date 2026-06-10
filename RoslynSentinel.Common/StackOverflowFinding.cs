namespace RoslynSentinel.Common;

public sealed record StackOverflowFinding(
    string Kind,
    StackOverflowRisk Risk,
    FilePath FilePath,
    int LineNumber,
    string ContainingMember,
    string Description,
    string? CyclePath = null,
    string? Recommendation = null);
