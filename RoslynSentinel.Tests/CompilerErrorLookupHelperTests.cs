#pragma warning disable CS8618
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Basic;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

/// <summary>
/// Regression coverage for <see cref="CompilerErrorLookupHelper"/>'s CS0122 branch.
/// See docs/current/project_cs0122_lookup_helper_proposal.md — this guards against the
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
    /// docs/current/project_readfile_createfile_path_inconsistency_bug.md — a model that guesses a
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
}
