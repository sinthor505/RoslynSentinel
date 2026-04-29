using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record SolutionMetrics(
    int ProjectCount,
    int TotalFiles,
    long TotalLines,
    int TotalTypes,
    int TotalMethods,
    List<ProjectMetric> Projects
);

public record ProjectMetric(
    string Name,
    int FileCount,
    long LineCount,
    int TypeCount,
    int MethodCount
);

public class MetricsEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public MetricsEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<SolutionMetrics> GetSolutionMetricsAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var projectMetrics = new List<ProjectMetric>();

        int totalTypes = 0;
        int totalMethods = 0;
        long totalLines = 0;
        int totalFiles = 0;

        var projects = solution.Projects;
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var project in projects)
        {
            int pTypes = 0;
            int pMethods = 0;
            long pLines = 0;
            int pFiles = 0;

            foreach (var document in project.Documents)
            {
                pFiles++;
                var text = await document.GetTextAsync(cancellationToken);
                pLines += text.Lines.Count;

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root != null)
                {
                    pTypes += root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Count();
                    pMethods += root.DescendantNodes().OfType<MethodDeclarationSyntax>().Count();
                }
            }

            projectMetrics.Add(new ProjectMetric(project.Name, pFiles, pLines, pTypes, pMethods));
            totalTypes += pTypes;
            totalMethods += pMethods;
            totalLines += pLines;
            totalFiles += pFiles;
        }

        return new SolutionMetrics(
            solution.ProjectIds.Count,
            totalFiles,
            totalLines,
            totalTypes,
            totalMethods,
            projectMetrics
        );
    }
}
