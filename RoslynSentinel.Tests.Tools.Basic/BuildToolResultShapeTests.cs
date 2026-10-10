using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Shape of the projected BuildResult returned by the Build tool (quickBuild): green builds carry counts only,
/// failed builds carry a breakdown and a capped detail list, and the full list is readable through GetLargeResult.
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

        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var config = new SentinelConfiguration();
        var diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        var validationEngine = new ValidationEngine(_workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        _workspaceTools = new WorkspaceTools(
            _workspaceManager, validationEngine, new DiffEngine(), diagnosticEngine,
            new SolutionManagementEngine(_workspaceManager), new StructuralRefinementEngine(_workspaceManager, config),
            new DependencyEngine(_workspaceManager), new ProjectConsistencyEngine(_workspaceManager), config,
            NullLogger<WorkspaceTools>.Instance, new BuildEngine(_workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(_workspaceManager),
            new WorkspaceReadNavigationImpl(_workspaceManager, NullLogger<WorkspaceReadNavigationImpl>.Instance),
            WriteToolAdviceHelper.WithAllToolsExposed());
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
    public async Task Build_QuickBuild_FailedSolution_ReturnsBreakdownByProjectAndFirstNDetails()
    {
        SetSource(ManyErrorsSource(21), "Many.cs");

        var result = await _workspaceTools.Build(reason: "test message", BuildVerifyLevel.quickBuild, maxDetails: 5);

        Assert.That(result.IsError, Is.True);
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.Outcome, Is.EqualTo(BuildOutcome.Failed));
        Assert.That(data.Errors, Has.Count.EqualTo(5), "maxDetails caps the detail list.");
        Assert.That(data.OmittedErrorCount, Is.EqualTo(16), "21 errors minus the 5 listed.");
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
    public async Task Build_QuickBuild_Green_HasNoListsAndNoTails()
    {
        SetSource(SimpleSource, "Test.cs");

        var result = await _workspaceTools.Build(reason: "test message", BuildVerifyLevel.quickBuild);

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
        Assert.That(data.FullDiagnosticsResultId, Is.Null);
        Assert.That(data.OmittedErrorCount, Is.Zero);
    }

    [Test]
    public async Task Build_QuickBuild_FailedWithMoreThanMaxDetails_ReturnsFullDiagnosticsResultIdReadableByGetLargeResult()
    {
        SetSource(ManyErrorsSource(25), "Many.cs");

        var result = await _workspaceTools.Build(reason: "test message", BuildVerifyLevel.quickBuild);

        Assert.That(result.IsError, Is.True);
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.Errors, Has.Count.EqualTo(20), "default maxDetails is 20.");
        Assert.That(data.OmittedErrorCount, Is.EqualTo(5));
        Assert.That(data.FullDiagnosticsResultId, Is.Not.Null.And.Not.Empty);

        // Raw results page over the stored text in bounded windows (charLimit), so read every window back.
        var reassembled = new StringBuilder();
        int? offset = 0;
        var pageCount = 0;
        while (offset is not null)
        {
            var page = await _workspaceTools.GetLargeResult(reason: "test message", resultId: data.FullDiagnosticsResultId, offset: offset.Value, charLimit: LargeResultHelper.OffloadThresholdBytes);

            Assert.That(page.IsError, Is.False, "the stored full-diagnostics payload must be readable by its resultId.");
            var pageDataType = page.SuccessData!.GetType();
            reassembled.Append((string)pageDataType.GetProperty("text")!.GetValue(page.SuccessData)!);
            offset = (int?)pageDataType.GetProperty("nextOffset")!.GetValue(page.SuccessData);
            pageCount++;
            Assert.That(pageCount, Is.LessThan(10), "Paging should terminate quickly.");
        }

        using var doc = JsonDocument.Parse(reassembled.ToString());
        JsonElement errors = doc.RootElement.EnumerateObject()
            .First(p => string.Equals(p.Name, "Errors", StringComparison.OrdinalIgnoreCase)).Value;
        Assert.That(errors.GetArrayLength(), Is.EqualTo(25), "the stored list is uncapped: every error, not just the 20 listed.");
    }

    [Test]
    public async Task Build_QuickBuild_IncludeWarnings_ReturnsWarningList()
    {
        SetSource(WarningOnlySource, "Warn.cs");

        var result = await _workspaceTools.Build(reason: "test message", BuildVerifyLevel.quickBuild, includeWarnings: true);

        Assert.That(result.IsError, Is.False, "a warning does not fail the build.");
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.Outcome, Is.EqualTo(BuildOutcome.Succeeded));
        Assert.That(data.WarningCount, Is.GreaterThan(0));
        Assert.That(data.Warnings, Is.Not.Empty, "includeWarnings=true returns the warning list.");
    }

    [Test]
    public async Task Build_DefaultMaxDetails_IsTwenty()
    {
        SetSource(ManyErrorsSource(21), "Many.cs");

        var result = await _workspaceTools.Build(reason: "test message", BuildVerifyLevel.quickBuild);

        Assert.That(result.IsError, Is.True);
        var data = (BuildResult)result.SuccessData!;
        Assert.That(data.Errors, Has.Count.EqualTo(20));
        Assert.That(data.OmittedErrorCount, Is.EqualTo(1));
    }
}
