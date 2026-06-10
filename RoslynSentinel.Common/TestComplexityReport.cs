namespace RoslynSentinel.Common;

public record TestComplexityReport(string MethodName, int CyclomaticComplexity, List<string> ConditionalsToTest);
