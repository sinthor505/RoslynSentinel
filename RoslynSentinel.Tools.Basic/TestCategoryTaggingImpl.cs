using Microsoft.Extensions.Logging;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Backs <see cref="TestCategoryTaggingTools"/>: validates the tool arguments, runs the read-only
/// <see cref="TestCategoryTaggingEngine"/> planner and shapes its plan into a paged report (solution-wide
/// summary, or one test project's detail). Never throws; every failure is a structured
/// <see cref="SentinelCallToolResult{T}"/> error that names the parameter and a correct value.
/// </summary>
public class TestCategoryTaggingImpl
{
    private const string ToolName = "TagTestCategories";

    private readonly TestCategoryTaggingEngine _engine;
    private readonly ILogger _logger;

    public TestCategoryTaggingImpl(TestCategoryTaggingEngine engine, ILogger logger)
    {
        _engine = engine;
        _logger = logger;
    }

    /// <summary>Plans (dry run only for now) and returns the summary or, when <paramref name="reportProject"/> is set, that project's detail.</summary>
    public async Task<SentinelCallToolResult<object>> TagAsync(
        string? targets,
        string? testScope,
        string? excludedTargets,
        string? excludedTests,
        double maxTestShare,
        double classLevelThreshold,
        string? framework,
        bool dryRun,
        string? reportProject,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targets))
        {
            return Error(ToolErrorCode.InvalidArgument,
                "targets is required. Pass a comma-separated list of project names or namespace prefixes whose types become categories, e.g. \"RoslynSentinel.Engines.Basic,RoslynSentinel.Common\".");
        }

        if (!TryParseFramework(framework, out var parsedFramework))
        {
            return Error(ToolErrorCode.InvalidArgument,
                $"framework '{framework}' is not recognized. Valid values: auto, nunit, xunit, mstest (omit for auto).");
        }

        if (double.IsNaN(maxTestShare) || maxTestShare < 0 || maxTestShare > 1)
        {
            return Error(ToolErrorCode.InvalidArgument,
                $"maxTestShare must be between 0 and 1 (a fraction of all scanned tests); got {maxTestShare}. Omit it for the default 0.25.");
        }

        if (double.IsNaN(classLevelThreshold) || classLevelThreshold < 0 || classLevelThreshold > 1)
        {
            return Error(ToolErrorCode.InvalidArgument,
                $"classLevelThreshold must be between 0 and 1 (a fraction of a fixture's tests); got {classLevelThreshold}. Omit it for the default 0.5.");
        }

        if (!dryRun)
        {
            return Error(ToolErrorCode.NotImplemented,
                "dryRun: false (applying the category attributes) is not implemented yet. Call with dryRun: true (the default) to get the plan; " +
                "applying edits and removing stale attributes arrive in a later release.");
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
                    parsedFramework),
                cancellationToken);

            if (plan.Error is not null)
            {
                return new SentinelCallToolResult<object> { IsError = true, ErrorData = plan.Error };
            }

            if (string.IsNullOrWhiteSpace(reportProject))
            {
                return Success(BuildSummary(plan));
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

            return Success(BuildDetail(plan, project));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Tool} failed while planning test categories", ToolName);
            return Error(ToolErrorCode.Exception,
                "Planning test categories failed unexpectedly. Check that the solution is loaded and compiles (Build), then retry; the server log has the details.");
        }
    }

    private static object BuildSummary(TestCategoryPlan plan) => new
    {
        mode = "dryRun",
        view = "summary",
        parametersUsed = ShapeParameters(plan.Parameters),
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
        projects = plan.Projects.Select(p => new
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

    private static bool TryParseFramework(string? value, out TestCategoryFramework framework)
    {
        framework = TestCategoryFramework.Auto;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                return true;
            case "nunit":
                framework = TestCategoryFramework.NUnit;
                return true;
            case "xunit":
                framework = TestCategoryFramework.XUnit;
                return true;
            case "mstest":
                framework = TestCategoryFramework.MSTest;
                return true;
            default:
                return false;
        }
    }

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
