using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RoslynSentinel.Server;

public enum HealthEngineType
{
    Structure,
    Modernization,
    Performance,
    Safety,
    Architecture
}

public record IssueCategoryCount(string Category, int Count);
public record ProjectHealthSummary(string ProjectName, int TotalIssues, List<IssueCategoryCount> IssuesByCategory);
public record ComprehensiveHealthReport(int TotalIssues, List<IssueCategoryCount> TotalIssuesByCategory, List<ProjectHealthSummary> ProjectSummaries);

public class HealthOrchestrationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ProjectStructureEngine _projectStructureEngine;
    private readonly AnalysisEngine _analysisEngine;
    private readonly AsyncSafetyEngine _asyncSafetyEngine;

    public HealthOrchestrationEngine(
        PersistentWorkspaceManager workspaceManager,
        ProjectStructureEngine projectStructureEngine,
        AnalysisEngine analysisEngine,
        AsyncSafetyEngine asyncSafetyEngine)
    {
        _workspaceManager = workspaceManager;
        _projectStructureEngine = projectStructureEngine;
        _analysisEngine = analysisEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
    }

    public async Task<ComprehensiveHealthReport> GenerateComprehensiveHealthReportAsync(
        List<HealthEngineType>? engines = null,
        string? projectName = null,
        string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var projectSummaries = new ConcurrentBag<ProjectHealthSummary>();
        var targetEngines = engines ?? Enum.GetValues<HealthEngineType>().ToList();

        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        // Parallelize Project Processing
        var projectTasks = projects.Select(async project =>
        {
            var projectCategoryCounts = new ConcurrentDictionary<string, int>();
            var engineTasks = new List<Task>();

            // 1. Structure
            if (targetEngines.Contains(HealthEngineType.Structure))
            {
                engineTasks.Add(Task.Run(async () => {
                    var smells = await _projectStructureEngine.FindStructuralSmellsAsync(projectName: project.Name, filePath: filePath, cancellationToken: cancellationToken);
                    foreach (var smell in smells) IncrementCount(projectCategoryCounts, ExtractCategory(smell));
                }));
            }

            // 2. Architecture
            if (targetEngines.Contains(HealthEngineType.Architecture))
            {
                engineTasks.Add(Task.Run(async () => {
                    var items = await _analysisEngine.FindLargeTypesAsync(projectName: project.Name, cancellationToken: cancellationToken);
                    IncrementCount(projectCategoryCounts, "LargeType", items.Count);
                }));
                engineTasks.Add(Task.Run(async () => {
                    var items = await _analysisEngine.FindLargeMethodsAsync(projectName: project.Name, cancellationToken: cancellationToken);
                    IncrementCount(projectCategoryCounts, "LargeMethod", items.Count);
                }));
                engineTasks.Add(Task.Run(async () => {
                    var items = await _analysisEngine.DetectLongParameterListsAsync(projectName: project.Name, cancellationToken: cancellationToken);
                    IncrementCount(projectCategoryCounts, "LongParameterList", items.Count);
                }));
                engineTasks.Add(Task.Run(async () => {
                    var items = await _analysisEngine.FindUninstantiatedTypesAsync(projectName: project.Name, cancellationToken: cancellationToken);
                    IncrementCount(projectCategoryCounts, "UninstantiatedType", items.Count);
                }));
            }

            // 3. Performance
            if (targetEngines.Contains(HealthEngineType.Performance))
            {
                engineTasks.Add(Task.Run(async () => {
                    var items = await _analysisEngine.FindBoxingAllocationsAsync(filePath: filePath, projectName: project.Name, cancellationToken: cancellationToken);
                    IncrementCount(projectCategoryCounts, "BoxingAllocation", items.Count);
                }));
                engineTasks.Add(Task.Run(async () => {
                    var items = await _analysisEngine.DetectInefficientStringComparisonsAsync(filePath: filePath, projectName: project.Name, cancellationToken: cancellationToken);
                    IncrementCount(projectCategoryCounts, "InefficientStringComparison", items.Count);
                }));
                engineTasks.Add(Task.Run(async () => {
                    var items = await _analysisEngine.DetectReflectionUsageAsync(filePath: filePath, projectName: project.Name, cancellationToken: cancellationToken);
                    IncrementCount(projectCategoryCounts, "ReflectionUsage", items.Count);
                }));
            }

            // 4. Safety
            if (targetEngines.Contains(HealthEngineType.Safety))
            {
                engineTasks.Add(Task.Run(async () => {
                    var items = await _asyncSafetyEngine.DetectAsyncVoidMethodsAsync(filePath: filePath ?? "", cancellationToken: cancellationToken);
                    // Filter async void results by project name if we can, otherwise use current project's documents
                    // For now, AsyncSafetyEngine doesn't support projectName filter, so we use document lookup if filePath is provided.
                }));
                engineTasks.Add(Task.Run(async () => {
                    var items = await _analysisEngine.DetectMismatchedAwaitAsync(filePath: filePath, projectName: project.Name, cancellationToken: cancellationToken);
                    IncrementCount(projectCategoryCounts, "MismatchedAwait", items.Count);
                }));
                engineTasks.Add(Task.Run(async () => {
                    var items = await _analysisEngine.CheckForEmptyCatchBlocksAsync(filePath: filePath, projectName: project.Name, cancellationToken: cancellationToken);
                    IncrementCount(projectCategoryCounts, "EmptyCatchBlock", items.Count);
                }));
            }

            await Task.WhenAll(engineTasks);

            var projectTotal = projectCategoryCounts.Values.Sum();
            if (projectTotal > 0)
            {
                projectSummaries.Add(new ProjectHealthSummary(
                    project.Name,
                    projectTotal,
                    projectCategoryCounts.Select(kvp => new IssueCategoryCount(kvp.Key, kvp.Value)).OrderByDescending(c => c.Count).ToList()
                ));
            }
        });

        await Task.WhenAll(projectTasks);

        // Aggregate Totals
        var totalCategoryCounts = new Dictionary<string, int>();
        int grandTotalIssues = 0;

        foreach (var summary in projectSummaries)
        {
            grandTotalIssues += summary.TotalIssues;
            foreach (var cat in summary.IssuesByCategory)
            {
                if (!totalCategoryCounts.ContainsKey(cat.Category)) totalCategoryCounts[cat.Category] = 0;
                totalCategoryCounts[cat.Category] += cat.Count;
            }
        }

        return new ComprehensiveHealthReport(
            grandTotalIssues,
            totalCategoryCounts.Select(kvp => new IssueCategoryCount(kvp.Key, kvp.Value)).OrderByDescending(c => c.Count).ToList(),
            projectSummaries.OrderByDescending(p => p.TotalIssues).ToList()
        );
    }

    private string ExtractCategory(string smell)
    {
        if (smell.StartsWith("["))
        {
            var endBracket = smell.IndexOf(']');
            if (endBracket > 0) return smell.Substring(1, endBracket - 1);
        }
        return "Unknown";
    }

    private void IncrementCount(ConcurrentDictionary<string, int> counts, string category, int amount = 1)
    {
        if (amount == 0) return;
        counts.AddOrUpdate(category, amount, (key, old) => old + amount);
    }
}
