using Microsoft.Extensions.Logging;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Backs <see cref="TestCategoryTaggingTools"/>: validates the tool arguments, runs the read-only
/// <see cref="TestCategoryTaggingEngine"/> planner and either shapes its plan into a paged report (dryRun: solution-wide
/// summary, or one test project's detail) or applies it through <see cref="TestCategoryApplyEngine"/> (one validated
/// write batch per test project). Never throws; every failure is a structured
/// <see cref="SentinelCallToolResult{T}"/> error that names the parameter and a correct value.
/// </summary>
public class TestCategoryTaggingImpl
{
    private const string ToolName = "TagTestCategories";

    private readonly TestCategoryTaggingEngine _engine;
    private readonly TestCategoryApplyEngine _applyEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger _logger;

    public TestCategoryTaggingImpl(
        TestCategoryTaggingEngine engine,
        TestCategoryApplyEngine applyEngine,
        IWorkspaceManager workspaceManager,
        ILogger logger)
    {
        _engine = engine;
        _applyEngine = applyEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    /// <summary>
    /// Plans, then (dryRun) returns the summary or, when <paramref name="reportProject"/> is set, that project's detail, or
    /// (dryRun: false) applies the plan and returns the applied-change summary with per-project counts.
    /// </summary>
    public async Task<SentinelCallToolResult<object>> TagAsync(
        string? targets,
        string? testScope,
        string? excludedTargets,
        string? excludedTests,
        double maxTestShare,
        double classLevelThreshold,
        TestCategoryFramework framework,
        bool dryRun,
        string? reportProject,
        string? applyProjects,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targets))
        {
            return Error(ToolErrorCode.InvalidArgument,
                "targets is required. Pass a comma-separated list of project names or namespace prefixes whose types become categories, e.g. \"RoslynSentinel.Engines.Basic,RoslynSentinel.Common\".");
        }

        if (double.IsNaN(maxTestShare) || maxTestShare < 0 || maxTestShare > 1)
        {
            return Error(ToolErrorCode.InvalidArgument,
                $"maxTestShare must be between 0 and 1 (a fraction of all scanned tests, i.e. of the testScope set); got {maxTestShare}. Omit it for the default 0.25.");
        }

        if (double.IsNaN(classLevelThreshold) || classLevelThreshold < 0 || classLevelThreshold > 1)
        {
            return Error(ToolErrorCode.InvalidArgument,
                $"classLevelThreshold must be between 0 and 1 (a fraction of a fixture's tests); got {classLevelThreshold}. Omit it for the default 0.5.");
        }

        try
        {
            var plan = await _engine.PlanAsync(
                new TestCategoryPlanOptions(
                    targets,
                    NullIfBlank(testScope),
                    NullIfBlank(excludedTargets),
                    NullIfBlank(excludedTests),
                    maxTestShare,
                    classLevelThreshold,
                    framework),
                cancellationToken);

            if (plan.Error is not null)
            {
                return new SentinelCallToolResult<object> { IsError = true, ErrorData = plan.Error };
            }

            // Planning above always ran over the full testScope, so shares and exclusions do not depend on applyProjects.
            // applyProjects only narrows what is applied and reported (the boundary), never the plan itself.
            var (selected, applyError) = ResolveApplyProjects(plan, applyProjects);
            if (applyError is not null)
            {
                return applyError;
            }

            if (!dryRun)
            {
                return await ApplyPlanAsync(plan with { Projects = selected }, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(reportProject))
            {
                return Success(BuildSummary(plan, selected, string.IsNullOrWhiteSpace(applyProjects) ? null : selected.Select(p => p.ProjectName).ToList()));
            }

            var project = plan.Projects.FirstOrDefault(p => string.Equals(p.ProjectName, reportProject.Trim(), StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                var valid = plan.Projects.Count == 0
                    ? "none (no test project was scanned; check testScope and targets)"
                    : string.Join(", ", plan.Projects.Select(p => p.ProjectName));
                return Error(ToolErrorCode.InvalidArgument,
                    $"reportProject '{reportProject}' is not a scanned test project. Valid values: {valid}. Omit reportProject for the solution-wide summary.");
            }

            if (!selected.Contains(project))
            {
                return Error(ToolErrorCode.InvalidArgument,
                    $"reportProject '{reportProject}' is outside applyProjects ({string.Join(", ", selected.Select(p => p.ProjectName))}). Pass a project listed in applyProjects, or omit reportProject.");
            }

            return Success(BuildDetail(plan, project));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Tool} failed while planning or applying test categories", ToolName);

            // ToolException subclasses (SessionHalted, SolutionNotLoaded, ...) carry their own code and an agent-facing
            // message; the mapper keeps them instead of flattening every failure to a generic Exception.
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, ToolName),
            };
        }
    }

    /// <summary>
    /// Resolves the applyProjects CSV (case-insensitive, trimmed) against the scanned test projects. Null/blank selects every
    /// scanned project. An unknown name is an InvalidArgument error listing the valid names. The returned projects keep plan order.
    /// </summary>
    private static (IReadOnlyList<TestProjectPlan> Projects, SentinelCallToolResult<object>? Error) ResolveApplyProjects(
        TestCategoryPlan plan,
        string? applyProjects)
    {
        var names = (applyProjects ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            return (plan.Projects, null);
        }

        var selected = new List<TestProjectPlan>();
        foreach (var name in names)
        {
            var match = plan.Projects.FirstOrDefault(p => string.Equals(p.ProjectName, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                var valid = plan.Projects.Count == 0
                    ? "none (no test project was scanned; check testScope and targets)"
                    : string.Join(", ", plan.Projects.Select(p => p.ProjectName));
                return ([], Error(ToolErrorCode.InvalidArgument,
                    $"applyProjects '{name}' is not a scanned test project. Valid values: {valid}. Omit applyProjects to apply to every scanned project."));
            }

            if (!selected.Contains(match))
            {
                selected.Add(match);
            }
        }

        return (plan.Projects.Where(selected.Contains).ToList(), null);
    }

    private async Task<SentinelCallToolResult<object>> ApplyPlanAsync(TestCategoryPlan plan, CancellationToken cancellationToken)
    {
        var result = await _applyEngine.ApplyAsync(plan, _logger, cancellationToken);
        var summary = TestCategoryApplyReport.Build(plan, result, _workspaceManager.WorkspaceVersion);

        if (TestCategoryApplyReport.AllFailed(result))
        {
            var failures = summary.Projects.Where(p => p.Status == "failed").ToList();
            var message = "No test project's batch could be applied; every file is unchanged. " +
                string.Join(" ", failures.Select(f => $"{f.Project}: {f.Error}"));
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(
                    failures[0].ErrorCode ?? ToolErrorCode.Exception,
                    $"{ToolName}: {message}",
                    StructuredDetail: failures.Cast<object>().ToList()),
            };
        }

        return Success(summary);
    }

    private static object BuildSummary(TestCategoryPlan plan, IReadOnlyList<TestProjectPlan> shownProjects, IReadOnlyList<string>? applyProjects) => new
    {
        mode = "dryRun",
        view = "summary",
        parametersUsed = ShapeParameters(plan.Parameters),
        applyProjects,
        totalTestsScanned = plan.TotalTestsScanned,
        autoExcludedTypes = plan.AutoExcludedTypes.Select(u => new
        {
            category = u.CategoryName,
            type = u.TypeFullName,
            testsTouching = u.TestsTouching,
            totalTests = u.TotalTests,
            share = Round(u.Share),
        }).ToList(),
        collisionGroups = plan.CollisionGroups.Select(g => new
        {
            simpleName = g.SimpleName,
            members = g.Members.Select(m => new { type = m.TypeFullName, category = m.CategoryName }).ToList(),
        }).ToList(),
        warnings = plan.Warnings,
        projects = shownProjects.Select(p => new
        {
            project = p.ProjectName,
            framework = p.Framework.ToString(),
            testsScanned = p.TestsScanned,
            fixtures = p.Fixtures.Count,
            classLevelAdds = p.ClassLevelAdds,
            methodLevelAdds = p.MethodLevelAdds,
            ambiguousFixtures = p.AmbiguousFixtures,
            uncategorized = p.UncategorizedTests.Count,
            staleToRemove = p.StaleToRemove,
            skippedExisting = p.SkippedExisting,
        }).ToList(),
        next = "Pass reportProject: <project name> to page in one project's fixtures, uncategorized tests, stale attributes and planned edits.",
    };

    private static object BuildDetail(TestCategoryPlan plan, TestProjectPlan project) => new
    {
        mode = "dryRun",
        view = "project",
        project = project.ProjectName,
        framework = project.Framework.ToString(),
        testsScanned = project.TestsScanned,
        classLevelAdds = project.ClassLevelAdds,
        methodLevelAdds = project.MethodLevelAdds,
        ambiguousFixtures = project.AmbiguousFixtures,
        staleToRemove = project.StaleToRemove,
        skippedExisting = project.SkippedExisting,
        fixtures = project.Fixtures.Select(f => new
        {
            fixture = f.FixtureName,
            file = f.FilePath,
            line = f.Line,
            testCount = f.TestCount,
            isAmbiguous = f.IsAmbiguous,
            classLevelCategories = f.ClassLevelCategories,
            typeShares = f.TypeShares.Select(s => new
            {
                category = s.CategoryName,
                type = s.TypeFullName,
                testsTouching = s.TestsTouching,
                share = Round(s.Share),
            }).ToList(),
        }).ToList(),
        uncategorizedTests = project.UncategorizedTests
            .Select(t => new { fixture = t.FixtureName, method = t.MethodName, file = t.FilePath, line = t.Line })
            .ToList(),
        staleAttributes = project.Edits
            .Where(e => e.Kind == TestCategoryEditKind.RemoveStale)
            .Select(ShapeEdit)
            .ToList(),
        plannedEdits = project.Edits
            .Where(e => e.Kind == TestCategoryEditKind.Add)
            .Select(ShapeEdit)
            .ToList(),
        warnings = plan.Warnings,
    };

    private static object ShapeEdit(PlannedCategoryEdit e) => new
    {
        kind = e.Kind.ToString(),
        level = e.Level.ToString(),
        file = e.FilePath,
        line = e.Line,
        fixture = e.FixtureName,
        method = e.MethodName,
        category = e.CategoryName,
        attribute = e.AttributeText,
        staleReason = e.StaleReason?.ToString(),
    };

    private static object? ShapeParameters(TestCategoryParametersUsed? p) => p is null
        ? null
        : new
        {
            targets = p.Targets,
            testScope = p.TestScope,
            excludedTargets = p.ExcludedTargets,
            excludedTests = p.ExcludedTests,
            maxTestShare = p.MaxTestShare,
            classLevelThreshold = p.ClassLevelThreshold,
            framework = p.Framework.ToString(),
        };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static double Round(double value) => Math.Round(value, 3);

    private static SentinelCallToolResult<object> Success(object data) =>
        new SentinelCallToolResult<object> { IsError = false, SuccessData = data };

    private static SentinelCallToolResult<object> Error(string code, string message) =>
        new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(code, $"{ToolName}: {message}"),
        };
}
