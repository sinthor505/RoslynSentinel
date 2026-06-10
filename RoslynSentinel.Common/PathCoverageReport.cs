namespace RoslynSentinel.Common;

public record PathCoverageReport(string MethodName, List<string> BranchesToTest);
