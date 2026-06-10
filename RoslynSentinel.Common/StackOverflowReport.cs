namespace RoslynSentinel.Common;

public sealed record StackOverflowReport(
    FilePath FilePath,
    int DefiniteCount,
    int SuspiciousCount,
    int InformationalCount,
    List<StackOverflowFinding> Findings,
    string Summary);
