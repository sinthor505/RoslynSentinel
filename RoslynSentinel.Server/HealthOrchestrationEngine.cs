using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

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

public record ComprehensiveHealthReport(
    int TotalIssues, 
    List<IssueCategoryCount> TotalIssuesByCategory, 
    List<ProjectHealthSummary> ProjectSummaries,
    bool HasMore,
    int? NextProjectOffset,
    string StatusMessage);

public class HealthOrchestrationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ProjectStructureEngine _projectStructureEngine;
    private readonly AnalysisEngine _analysisEngine;
    private readonly AsyncSafetyEngine _asyncSafetyEngine;
    private readonly SentinelConfiguration _config;

    public HealthOrchestrationEngine(
        PersistentWorkspaceManager workspaceManager,
        ProjectStructureEngine projectStructureEngine,
        AnalysisEngine analysisEngine,
        AsyncSafetyEngine asyncSafetyEngine,
        SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _projectStructureEngine = projectStructureEngine;
        _analysisEngine = analysisEngine;
        _asyncSafetyEngine = asyncSafetyEngine;
        _config = config;
    }

    public async Task<ComprehensiveHealthReport> GenerateComprehensiveHealthReportAsync(
        List<HealthEngineType>? engines = null,
        string? projectName = null,
        string? filePath = null,
        int offset = 0,
        int limit = 10,
        int timeoutSeconds = 25,
        CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var sw = Stopwatch.StartNew();
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var projectSummaries = new ConcurrentBag<ProjectHealthSummary>();
        var targetEngines = engines ?? Enum.GetValues<HealthEngineType>().ToList();

        var allProjects = solution.Projects.OrderBy(p => p.Name).ToList();
        if (!string.IsNullOrEmpty(projectName))
        {
            allProjects = allProjects.Where(p => p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var pagedProjects = allProjects.Skip(offset).Take(limit).ToList();
        bool hasMore = allProjects.Count > (offset + limit);

        _ = Task.WhenAll(pagedProjects.Select(p => p.GetCompilationAsync(cancellationToken)));

        try 
        {
            var projectTasks = pagedProjects.Select(async project =>
            {
                if (cts.IsCancellationRequested) return;

                var projectCategoryCounts = new ConcurrentDictionary<string, int>();
                var engineTasks = new List<Task>();

                // 1. Structure
                if (targetEngines.Contains(HealthEngineType.Structure))
                {
                    engineTasks.Add(Task.Run(async () => {
                        var smells = await _projectStructureEngine.FindStructuralSmellsAsync(projectName: project.Name, filePath: filePath, cancellationToken: cts.Token);
                        foreach (var smell in smells) IncrementCount(projectCategoryCounts, ExtractCategory(smell));
                    }, cts.Token));
                }

                // 2. Architecture
                if (targetEngines.Contains(HealthEngineType.Architecture))
                {
                    engineTasks.Add(Task.Run(async () => {
                        var items = await _analysisEngine.FindLargeTypesAsync(projectName: project.Name, cancellationToken: cts.Token);
                        IncrementCount(projectCategoryCounts, "LargeType", items.Count);
                    }, cts.Token));
                    engineTasks.Add(Task.Run(async () => {
                        var items = await _analysisEngine.FindLargeMethodsAsync(projectName: project.Name, cancellationToken: cts.Token);
                        IncrementCount(projectCategoryCounts, "LargeMethod", items.Count);
                    }, cts.Token));
                }

                // 3. Performance (Respect Toggles)
                if (targetEngines.Contains(HealthEngineType.Performance))
                {
                    if (_config.IsFeatureEnabled("BoxingAllocation"))
                    {
                        engineTasks.Add(Task.Run(async () => {
                            var items = await _analysisEngine.FindBoxingAllocationsAsync(filePath: filePath, projectName: project.Name, cancellationToken: cts.Token);
                            IncrementCount(projectCategoryCounts, "BoxingAllocation", items.Count);
                        }, cts.Token));
                    }
                    if (_config.IsFeatureEnabled("InefficientStringComparison"))
                    {
                        engineTasks.Add(Task.Run(async () => {
                            var items = await _analysisEngine.DetectInefficientStringComparisonsAsync(filePath: filePath, projectName: project.Name, cancellationToken: cts.Token);
                            IncrementCount(projectCategoryCounts, "InefficientStringComparison", items.Count);
                        }, cts.Token));
                    }
                }

                // 4. Safety (Respect Toggles)
                if (targetEngines.Contains(HealthEngineType.Safety))
                {
                    if (_config.IsFeatureEnabled("AsyncVoidUsage"))
                    {
                        engineTasks.Add(Task.Run(async () => {
                            var items = await _asyncSafetyEngine.DetectAsyncVoidMethodsAsync(filePath: filePath ?? "", cancellationToken: cts.Token);
                            IncrementCount(projectCategoryCounts, "AsyncVoidUsage", items.Count);
                        }, cts.Token));
                    }
                    if (_config.IsFeatureEnabled("EmptyCatchBlocks"))
                    {
                        engineTasks.Add(Task.Run(async () => {
                            var items = await _analysisEngine.CheckForEmptyCatchBlocksAsync(filePath: filePath, projectName: project.Name, cancellationToken: cts.Token);
                            IncrementCount(projectCategoryCounts, "EmptyCatchBlock", items.Count);
                        }, cts.Token));
                    }
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
        }
        catch (OperationCanceledException) { }

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

        var status = cts.IsCancellationRequested 
            ? $"Analysis timed out after {timeoutSeconds}s. Returning partial results." 
            : $"Completed analysis of {pagedProjects.Count} projects in {sw.Elapsed.TotalSeconds:F1}s.";

        return new ComprehensiveHealthReport(
            grandTotalIssues,
            totalCategoryCounts.Select(kvp => new IssueCategoryCount(kvp.Key, kvp.Value)).OrderByDescending(c => c.Count).ToList(),
            projectSummaries.OrderByDescending(p => p.TotalIssues).ToList(),
            hasMore,
            hasMore ? offset + limit : null,
            status
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
