#pragma warning disable CS8618
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Basic;

namespace RoslynSentinel.Tests;

/// <summary>
/// Regression coverage for <see cref="CompilerErrorLookupHelper"/>'s CS0122 branch.
/// See docs/current/project_cs0122_lookup_helper_proposal.md -> this guards against the
/// member-vs-container accessibility confusion traced in PlanImplementVerify run 1, where a
/// model raised a class from internal to public while leaving the actually-inaccessible method
/// private, then reverted the class without ever touching the method.
/// </summary>
[TestFixture]
public class CompilerErrorLookupHelperTests
{
    private IWorkspaceManager _workspaceManager;
    private SymbolNavigationEngine _symbolNavigationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private async Task<DiagnosticReport> CompileAndGetErrorsAsync(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);

        var project = solution.Projects.Single();
        var compilation = await project.GetCompilationAsync();
        var diagnostics = compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToInfo())
            .ToList();

        return new DiagnosticReport(diagnostics.Count == 0, diagnostics);
    }

    [Test]
    public async Task DescribeAsync_Cs0122PrivateMethod_NamesCurrentAccessibilityAndMemberVsContainerNote()
    {
        var report = await CompileAndGetErrorsAsync(
            ("BlockEditHelpers.cs", """
            namespace TestProj;

            public static class BlockEditHelpers
            {
                private static string ReplaceBlockFormatted(string a, string b, string c) => a + b + c;
            }
            """),
            ("BlockConverter.cs", """
            namespace TestProj;

            public class BlockConverter
            {
                public string Convert() => BlockEditHelpers.ReplaceBlockFormatted("a", "b", "c");
            }
            """));

        Assert.That(report.Success, Is.False, "the private call should produce a CS0122");
        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0122"));

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("ReplaceBlockFormatted"));
        Assert.That(description, Does.Contain("currently private"),
            "the current accessibility must be stated affirmatively, not left for the model to infer from silence");
        Assert.That(description, Does.Contain("BlockConverter"),
            "the caller's enclosing type should be named so the sentence reads as a concrete instruction");
        Assert.That(description.ToLowerInvariant(), Does.Contain("containing type"),
            "must warn that raising the class's accessibility does not change the member's own accessibility");
    }

    /// <summary>
    /// Regression coverage for the CS0101/CS0111 branch. See
    /// docs/current/project_readfile_createfile_path_inconsistency_bug.md -> a model that guesses a
    /// wrong-but-plausible path for an existing file gets a CS0101/CS0111 "already contains a
    /// definition" collision with no pointer to the real file, and (per that doc's transcript) can
    /// burn its whole turn budget unable to tell a genuine duplicate apart from a wrong path.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0111DuplicateMember_NamesTheRealCollidingFilePath()
    {
        var report = await CompileAndGetErrorsAsync(
            ("FixtureHelpers/BlockEditHelpers.cs", """
            namespace TestProj.FixtureHelpers;

            public static class BlockEditHelpers
            {
                public static string ReplaceBlockFormatted(string a, string b, string c) => a + b + c;
            }
            """),
            ("BlockEditHelpers.cs", """
            namespace TestProj.FixtureHelpers;

            public static class BlockEditHelpers
            {
                public static string ReplaceBlockFormatted(string a, string b, string c) => a + b + c;
            }
            """));

        Assert.That(report.Success, Is.False, "two files declaring the same type/member should collide");
        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0111").Or.Contain("CS0101"));

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("FixtureHelpers/BlockEditHelpers.cs"),
            "the real (other) file's path must be named so a model with a wrong path can redirect to it");
        Assert.That(description, Does.Contain("ReplaceBlockFormatted"));
    }

    /// <summary>
    /// Regression coverage for identical-diagnostic grouping. See
    /// docs/current/blockers/resolved/blocking_error_movemember_analysisengine_antipatternengine_friction.md
    /// -> one bad MoveMember callSiteFixups value produced 86 identical CS7036 lines, which read as 86
    /// separate problems. Same Id + same Message must collapse to one line with a count and a capped
    /// location list, in first-occurrence order; a distinct diagnostic keeps its own single-line form.
    /// </summary>
    [Test]
    public async Task DescribeAsync_IdenticalDiagnostics_CollapseIntoOneGroupedLine()
    {
        const string ctorMessage = "There is no argument given that corresponds to the required parameter 'workspaceManager' of 'Target.Target(IWorkspaceManager)'";
        var diagnostics = new List<DiagnosticInfo>();
        for (var i = 1; i <= 13; i++)
        {
            diagnostics.Add(new DiagnosticInfo("CS7036", "Error", ctorMessage, $"C:\\repo\\File{i}.cs", i * 10, 5, i * 10, 20));
        }

        diagnostics.Insert(3, new DiagnosticInfo("CS1002", "Error", "; expected", "C:\\repo\\Other.cs", 7, 1, 7, 2));
        var report = new DiagnosticReport(false, diagnostics);

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);
        var lines = description.Split('\n');

        Assert.That(lines, Has.Length.EqualTo(2), "13 identical CS7036s + 1 CS1002 should render as exactly 2 lines");
        Assert.That(lines[0], Does.StartWith("CS7036 (x13): " + ctorMessage + " at: C:\\repo\\File1.cs:10, C:\\repo\\File2.cs:20"),
            "the grouped line comes first (first-occurrence order) and lists locations in original order");
        Assert.That(lines[0], Does.EndWith("(+3 more)"),
            $"locations beyond the cap of {CompilerErrorLookupHelper.MaxGroupedLocations} must be summarized, not listed");
        Assert.That(lines[0], Does.Not.Contain("File11.cs"), "the 11th+ location is past the cap");
        Assert.That(lines[1], Is.EqualTo("CS1002 at C:\\repo\\Other.cs:7: ; expected"),
            "a non-repeated diagnostic keeps the original single-diagnostic format");
    }
}
