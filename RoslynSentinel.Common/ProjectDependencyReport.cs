namespace RoslynSentinel.Common;


public record ProjectDependencyReport(
    List<string> ProjectReferences,
    List<string> PackageReferences
);

