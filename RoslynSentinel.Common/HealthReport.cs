namespace RoslynSentinel.Common;

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
    int DocumentCount,
    DateTime? LastLoadedAt = null,
    int StaleDocumentCount = 0,
    bool RequiresReload = false,
    List<string>? SampleStaleFiles = null
);
