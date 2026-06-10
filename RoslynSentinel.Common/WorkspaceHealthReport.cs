namespace RoslynSentinel.Common;

/// <summary>
/// Workspace health report from <see cref="MsToolAugmentEngine.GetWorkspaceHealthAsync"/>.
/// Fixes false-negative from standard roslyn-diagnose.
/// </summary>
public record WorkspaceHealthReport(
    bool IsOperational,
    bool HasLoadedSolution,
    string? LoadedSolutionPath,
    int ProjectCount,
    int DocumentCount,
    List<string> LoadErrors,
    string Summary);
