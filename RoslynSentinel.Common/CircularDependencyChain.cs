namespace RoslynSentinel.Common;

public record CircularDependencyChain(List<string> Cycle, string CycleType, List<string?> FilePaths);
