using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Shape of the projected BuildResult returned by the BuildTool tool (quickBuild): green builds carry counts only,
/// failed builds carry a breakdown and every error inline (the result marshaller pages a long list).
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class BuildToolResultShapeTests
{
    private IWorkspaceManager _workspaceManager;
    private WorkspaceTools _workspaceTools;
    private string _tempDir;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BuildToolResultShapeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        _workspaceTools = TestTools.WorkspaceTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private static string SimpleSource => "namespace TestProj; public class Order { public int Id { get; set; } }";

    // One CS0246 (undeclared type) per field, so the error count is exactly 'count'.
    private static string ManyErrorsSource(int count) =>
        "namespace TestProj; public class Many { "
        + string.Concat(Enumerable.Range(1, count).Select(i => $"public UndeclaredType F{i}; "))
        + "}";

    // A compiling source with one CS0219 warning (assigned but never used) and no errors.
    private static string WarningOnlySource => "namespace TestProj; public class Warn { public void M() { int unused = 0; } }";

    [Test]
    public async Task Build_QuickBuild_FailedSolution_ReturnsBreakdownByProjectAndAllErrors()
    {
        SetSource(ManyErrorsSource(21), "Many.cs");

        var result = await _workspaceTools.BuildTool(reason: "test message", BuildVerifyLevel.quickBuild, maxDetails: 5);

        Assert.That(result.IsError, Is.True);
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.Outcome, Is.EqualTo(BuildOutcome.Failed));
        Assert.That(data.Errors, Has.Count.EqualTo(21), "maxDetails no longer caps errors: all of them are inline.");
        Assert.That(data.Breakdown, Is.Not.Null);

        var project = data.Breakdown!.ByProject.Single();
        Assert.That(project.Project, Is.EqualTo("TestProj"));
        Assert.That(project.ErrorCount, Is.EqualTo(21), "the breakdown counts every error, not just the listed ones.");
        Assert.That(project.IsRootCause, Is.True);

        var file = data.Breakdown.ByFile.Single();
        Assert.That(file.File, Does.Contain("Many.cs"));
        Assert.That(file.ErrorCount, Is.EqualTo(21));
        Assert.That(data.SuppressedDownstreamErrorCount, Is.Zero);
    }

    [Test]
    public async Task Build_QuickBuild_FailedProjectScope_BreakdownNamesTheProject()
    {
        SetSource(ManyErrorsSource(3), "Many.cs");

        var result = await _workspaceTools.BuildTool(reason: "test message", BuildVerifyLevel.quickBuild, ToolScope.project, "TestProj");

        Assert.That(result.IsError, Is.True);
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.Breakdown, Is.Not.Null);
        Assert.That(data.Breakdown!.ByProject.Single().Project, Is.EqualTo("TestProj"), "project-scope diagnostics must carry the project name, not (unknown).");
    }

    [Test]
    public async Task Build_QuickBuild_FailedFileScope_BreakdownNamesTheProject()
    {
        // File scope resolves scopeName to an absolute path, so the document must carry that absolute FilePath.
        var filePath = Path.Combine(_tempDir, "Many.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", Path.Combine(_tempDir, "TestProj.csproj"), [("Many.cs", ManyErrorsSource(3), filePath)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _workspaceTools.BuildTool(reason: "test message", BuildVerifyLevel.quickBuild, ToolScope.file, filePath);

        Assert.That(result.IsError, Is.True);
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.Breakdown, Is.Not.Null);
        Assert.That(data.Breakdown!.ByProject.Single().Project, Is.EqualTo("TestProj"), "file-scope diagnostics must carry the owning project name, not (unknown).");
    }

    [Test]
    public async Task Build_QuickBuild_Green_HasNoListsAndNoTails()
    {
        SetSource(SimpleSource, "Test.cs");

        var result = await _workspaceTools.BuildTool(reason: "test message", BuildVerifyLevel.quickBuild);

        Assert.That(result.IsError, Is.False);
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.Outcome, Is.EqualTo(BuildOutcome.Succeeded));
        Assert.That(data.ProjectsCompiled, Is.Not.Empty);
        Assert.That(data.Errors, Is.Empty);
        Assert.That(data.Warnings, Is.Empty);
        Assert.That(data.ErrorSummary, Is.Empty);
        Assert.That(data.WarningSummary, Is.Empty);
        Assert.That(data.Breakdown, Is.Null);
        Assert.That(data.StdoutTail, Is.Null);
        Assert.That(data.StderrTail, Is.Null);
    }

    [Test]
    public async Task Build_QuickBuild_MoreErrorsThanTheOldCap_ReturnsAllOfThemInline()
    {
        SetSource(ManyErrorsSource(25), "Many.cs");

        var result = await _workspaceTools.BuildTool(reason: "test message", BuildVerifyLevel.quickBuild);

        Assert.That(result.IsError, Is.True);
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.ErrorCount, Is.EqualTo(25));
        Assert.That(data.Errors, Has.Count.EqualTo(25), "the old default cap was 20; every error is inline now and the marshaller pages a long list.");
    }

    [Test]
    public async Task Build_QuickBuild_IncludeWarnings_ReturnsWarningList()
    {
        SetSource(WarningOnlySource, "Warn.cs");

        var result = await _workspaceTools.BuildTool(reason: "test message", BuildVerifyLevel.quickBuild, includeWarnings: true);

        Assert.That(result.IsError, Is.False, "a warning does not fail the build.");
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.Outcome, Is.EqualTo(BuildOutcome.Succeeded));
        Assert.That(data.WarningCount, Is.GreaterThan(0));
        Assert.That(data.Warnings, Is.Not.Empty, "includeWarnings=true returns the warning list.");
    }
}
