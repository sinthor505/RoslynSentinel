namespace RoslynSentinel.Server;

public record HealthReport(
    bool Healthy,
    HealthComponents Components,
    WorkspaceStatus Workspace,
    List<string> Capabilities,
    List<HealthIssue> Errors,
    List<HealthIssue> Warnings
);

public record HealthComponents(
    bool RoslynAvailable,
    string RoslynVersion,
    bool MsBuildFound,
    string? MsBuildVersion,
    bool DotnetSdkAvailable,
    string? DotnetSdkVersion
);

public record WorkspaceStatus(
    int State,
    bool SolutionLoaded,
    string? SolutionPath,
    int ProjectCount,
    int DocumentCount
);

public record HealthIssue(
    string Code,
    string Message,
    string? Details = null,
    string? Suggestions = null
);
