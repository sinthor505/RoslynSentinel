using System.ComponentModel;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// <c>TagTestCategories</c>: plans category attributes for test methods and fixtures (<c>[Category("X")]</c> /
/// <c>[Trait("Category", "X")]</c> / <c>[TestCategory("X")]</c>) from the production types each test references.
/// Dry run by default (read-only plan); <c>dryRun: false</c> applies it through the validated write path. On-demand only in claude-lean
/// (the <c>testCategories</c> toolset), opt-in elsewhere via <c>--mode=TestCategories</c>.
/// </summary>
[McpServerToolType]
public class TestCategoryTaggingTools
{
    private readonly TestCategoryTaggingImpl _impl;

    public TestCategoryTaggingTools(TestCategoryTaggingImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "TagTestCategories")]
    [Produces(DataTag.Report)]
    [Description("Plan test-category attributes: for every test method, find the production types it references (semantic model: calls, " +
        "constructions, member access, typeof/nameof, generic arguments) and propose a category per type, at class level when at least classLevelThreshold " +
        "of a fixture's tests touch it, else at method level. Supports NUnit [Category], xUnit [Trait(\"Category\", ..)] and MSTest [TestCategory]. " +
        "dryRun defaults to true (plan only, nothing written): review that report first, then call again with dryRun: false to apply. " +
        "Apply writes each attribute on its own line directly above its test method or fixture, removes ONLY stale attributes carrying the " +
        "// sentinel:auto-category marker (hand-written attributes are never touched), makes one validated write batch per test project " +
        "(a project whose batch would not compile is reported and left unchanged while the others still apply), and returns the applied-change " +
        "summary (changeIds, affectedFiles, status) plus per-project classLevelAdded, methodLevelAdded, removedStale, skippedExisting and failed counts. " +
        "Re-running after an apply produces no further edits. " +
        "targets (required): comma-separated project names or namespace prefixes whose types become categories. " +
        "testScope: comma-separated test projects to scan (default: every project referencing a test framework). " +
        "excludedTargets: namespaces/types/projects never used as categories. excludedTests: test projects/namespaces/types skipped as callers. " +
        "maxTestShare (default 0.25): a type touched by more than this fraction of all tests is auto-excluded as ubiquitous. " +
        "classLevelThreshold (default 0.5): fraction of a fixture's tests that must touch a type for a class-level attribute. " +
        "framework (enum): Auto (default, resolved per test project), NUnit, XUnit or MSTest. " +
        "reportProject pages the dry-run report (ignored when dryRun is false): omit it for the solution-wide summary (parameters used, auto-excluded ubiquitous types, " +
        "name-collision groups, warnings, and a per-project table of tests scanned, fixtures, class-level and method-level adds, ambiguous fixtures, " +
        "uncategorized tests, stale attributes); pass a test project name from that table for its fixtures, uncategorized test names, stale " +
        "attributes and planned edits. An unknown name returns an error listing the valid project names. " +
        "Attributes this tool writes carry a // sentinel:auto-category marker; only marked attributes are ever reported stale.")]
    public async Task<SentinelCallToolResult<object>> TagTestCategories(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Required. Comma-separated project names or namespace prefixes whose types become categories, e.g. \"RoslynSentinel.Engines.Basic,RoslynSentinel.Common\".")] string targets,
        [Description("Comma-separated test project names to scan. Omit to scan every project that references a known test framework.")] string? testScope = null,
        [Description("Comma-separated namespaces, type full names or projects that must never become categories.")] string? excludedTargets = null,
        [Description("Comma-separated test projects, namespaces or type full names to skip as callers.")] string? excludedTests = null,
        [Description("Fraction 0..1 of all scanned tests above which a type is auto-excluded as ubiquitous. Default 0.25.")] double maxTestShare = 0.25,
        [Description("Fraction 0..1 of a fixture's tests that must touch a type for a class-level attribute. Default 0.5.")] double classLevelThreshold = 0.5,
        [Description("Test framework: Auto (default; resolved per test project from the test framework it references), NUnit, XUnit or MSTest. Exact enum names.")] TestCategoryFramework framework = TestCategoryFramework.Auto,
        [Description("true (default): plan only, write nothing; review the report first. false: apply the plan (add attributes, remove stale marked ones) as one validated batch per test project.")] bool dryRun = true,
        [Description("Dry run only (ignored when dryRun is false). Omit for the solution-wide summary. Pass a test project name from the summary's projects table for that project's detail.")] string? reportProject = null,
        CancellationToken cancellationToken = default) =>
        await _impl.TagAsync(
            targets, testScope, excludedTargets, excludedTests, maxTestShare, classLevelThreshold,
            framework, dryRun, reportProject, cancellationToken);
}
