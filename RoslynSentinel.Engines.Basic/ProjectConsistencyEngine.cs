using System.Xml.Linq;

using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Engines.Basic;

public record ProjectConsistencyIssue(
    string IssueType,
    string Description,
    string ProjectName,
    string? FilePath
);

/// <summary>
/// Checks solution-level consistency: TargetFramework alignment across all projects
/// and project naming convention adherence.
/// NuGet package version consistency is handled by SolutionStructureEngine.CheckPackageInconsistencyAsync.
/// </summary>
public class ProjectConsistencyEngine
{
    private readonly IWorkspaceManager _workspaceManager;

    public ProjectConsistencyEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }
}

public record ProjectFrameworkSummary(string ProjectName, string TargetFramework, string? FilePath);
