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
}
