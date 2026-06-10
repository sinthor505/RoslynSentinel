using System.Xml.Linq;

using Microsoft.CodeAnalysis;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

public record ProjectConsistencyIssue(
    string IssueType,
    string Description,
    string ProjectName,
    string? FilePath
);

/// <summary>
/// Checks solution-level consistency: TargetFramework alignment across all projects
/// and project naming convention adherence.
/// NuGet package version consistency is handled by DependencyEngine.CheckPackageInconsistencyAsync.
/// </summary>
public class ProjectConsistencyEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public ProjectConsistencyEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<List<ProjectConsistencyIssue>> CheckConsistencyAsync(CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var issues = new List<ProjectConsistencyIssue>();

        var projects = solution.Projects.ToList();
        if (projects.Count == 0)
        {
            return issues;
        }

        issues.AddRange(await CheckTargetFrameworkConsistencyAsync(projects, ct));
        issues.AddRange(CheckNamingConventions(projects));

        return issues;
    }

    private static async Task<List<ProjectConsistencyIssue>> CheckTargetFrameworkConsistencyAsync(
        List<Project> projects, CancellationToken ct)
    {
        var issues = new List<ProjectConsistencyIssue>();

        // Read TargetFramework from .csproj XML for each project that has a file path
        var frameworkByProject = new Dictionary<string, (string Framework, FilePath filePath)>();

        foreach (var project in projects)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(project.FilePath) || !File.Exists(project.FilePath))
            {
                continue;
            }

            try
            {
                var xml = await File.ReadAllTextAsync(project.FilePath, ct);
                var doc = XDocument.Parse(xml);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

                // Handle both <TargetFramework> and <TargetFrameworks>
                var tfElement = doc.Descendants(ns + "TargetFramework").FirstOrDefault()
                             ?? doc.Descendants("TargetFramework").FirstOrDefault();
                var tfsElement = doc.Descendants(ns + "TargetFrameworks").FirstOrDefault()
                              ?? doc.Descendants("TargetFrameworks").FirstOrDefault();

                var framework = tfElement?.Value?.Trim()
                    ?? tfsElement?.Value?.Split(';').FirstOrDefault()?.Trim();

                if (!string.IsNullOrEmpty(framework))
                {
                    frameworkByProject[project.Name] = (framework, project.FilePath);
                }
            }
            catch (Exception)
            {
                // Malformed .csproj — skip
            }
        }

        if (frameworkByProject.Count < 2)
        {
            return issues;
        }

        // Find the most common framework as the "standard"
        var frameworks = frameworkByProject.Values
            .GroupBy(v => v.Framework)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (frameworks.Count <= 1)
        {
            return issues;
        }

        var dominant = frameworks[0].Key;
        var dominantCount = frameworks[0].Count();

        foreach (var (projectName, (framework, filePath)) in frameworkByProject)
        {
            if (framework == dominant)
            {
                continue;
            }

            issues.Add(new ProjectConsistencyIssue(
                "TargetFrameworkMismatch",
                $"Project targets '{framework}' but {dominantCount} other project(s) target '{dominant}'. " +
                "Mismatched frameworks can cause dependency resolution failures and runtime errors.",
                projectName,
                filePath));
        }

        return issues;
    }

    private static List<ProjectConsistencyIssue> CheckNamingConventions(List<Project> projects)
    {
        var issues = new List<ProjectConsistencyIssue>();

        // Detect the root prefix (most common first segment of project names)
        var prefixes = projects
            .Select(p => p.Name.Split('.').FirstOrDefault() ?? "")
            .Where(p => !string.IsNullOrEmpty(p))
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (prefixes.Count == 0 || prefixes[0].Count() < 2)
        {
            return issues;
        }

        var dominantPrefix = prefixes[0].Key;
        var minPrefixCount = Math.Max(2, projects.Count / 4);

        // Only enforce if there's a clear dominant prefix (used by at least 25% of projects)
        if (prefixes[0].Count() < minPrefixCount)
        {
            return issues;
        }

        foreach (var project in projects)
        {
            if (!project.Name.StartsWith(dominantPrefix, StringComparison.Ordinal))
            {
                issues.Add(new ProjectConsistencyIssue(
                    "NamingConventionViolation",
                    $"Project '{project.Name}' does not follow the solution naming convention. " +
                    $"Most projects are prefixed with '{dominantPrefix}'. " +
                    "Consistent naming helps IDE project selection and CI script targeting.",
                    project.Name,
                    project.FilePath));
            }
        }

        return issues;
    }

    /// <summary>
    /// Returns a structured summary of all project names and their TargetFramework values.
    /// Useful for a quick overview before diving into specific inconsistencies.
    /// </summary>
    public async Task<List<ProjectFrameworkSummary>> GetProjectFrameworkSummaryAsync(CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<ProjectFrameworkSummary>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            string? framework = null;

            if (!string.IsNullOrEmpty(project.FilePath) && File.Exists(project.FilePath))
            {
                try
                {
                    var xml = await File.ReadAllTextAsync(project.FilePath, ct);
                    var doc = XDocument.Parse(xml);
                    framework = doc.Descendants("TargetFramework").FirstOrDefault()?.Value?.Trim()
                             ?? doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value?.Trim();
                }
                catch (Exception) { /* malformed — leave null */ }
            }

            results.Add(new ProjectFrameworkSummary(project.Name, framework ?? "unknown", project.FilePath));
        }

        return results.OrderBy(r => r.ProjectName).ToList();
    }
}

public record ProjectFrameworkSummary(string ProjectName, string TargetFramework, string? FilePath);
