namespace RoslynSentinel.Common;

public sealed record StackOverflowReport(
    FilePathWrapper FilePath,
    int DefiniteCount,
    int SuspiciousCount,
    int InformationalCount,
    List<StackOverflowFinding> Findings,
    string Summary);
