// ReadFile — SentinelWorkspaceTools. Zero coverage before this file (GetTestCoverageMap flagged
// branches: document == null, startLine/endLine slicing, out-of-range slice, offload threshold).
// Data is returned as an anonymous object (not a named record) for the non-offload paths. Anonymous
// type properties are internal to the declaring assembly, so `dynamic` binding fails cross-assembly
// here — use reflection (GetProperty) instead.

using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests;
using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class ReadFileTests
{
    private FakeWorkspaceManager _workspaceManager;
    private SentinelWorkspaceTools _tools;
    private string _documentPath;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        _documentPath = Path.Combine(Path.GetTempPath(), "ReadFileTests_" + Guid.NewGuid().ToString("N"), "Foo.cs");

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "line1\nline2\nline3\nline4\nline5\n", _documentPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var validationEngine = new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, diffEngine);
        var diagnosticEngine = new DiagnosticEngine(_workspaceManager);
        var solutionManagementEngine = new SolutionManagementEngine(_workspaceManager);
        var structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, config);
        var dependencyEngine = new DependencyEngine(_workspaceManager);
        var projectConsistencyEngine = new ProjectConsistencyEngine(_workspaceManager);
        _tools = new SentinelWorkspaceTools(
            _workspaceManager, validationEngine, diffEngine, diagnosticEngine,
            solutionManagementEngine, structuralRefinementEngine, dependencyEngine,
            projectConsistencyEngine, config, NullLogger<SentinelWorkspaceTools>.Instance,
            new BuildEngine(_workspaceManager, diagnosticEngine),
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance));
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static object? GetProp(object data, string name) =>
        data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(data);

    [Test]
    public async Task ReadFile_WholeFile_ReturnsFullSourceAsync()
    {
        var result = await _tools.ReadFile(_documentPath);

        Assert.That(result.Success, Is.True);
        var data = result.Data!;
        Assert.That((string)GetProp(data, "source")!, Does.Contain("line1"));
        Assert.That((string)GetProp(data, "source")!, Does.Contain("line5"));
        Assert.That((int)GetProp(data, "totalLines")!, Is.EqualTo(6));
    }

    [Test]
    public async Task ReadFile_FileNotInSolution_ReturnsFileNotFoundAsync()
    {
        var missingPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "DoesNotExist.cs");

        var result = await _tools.ReadFile(missingPath);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("FileNotFound"));
    }

    [Test]
    public async Task ReadFile_WithLineRange_ReturnsRequestedSliceAsync()
    {
        var result = await _tools.ReadFile(_documentPath, startLine: 2, endLine: 3);

        Assert.That(result.Success, Is.True);
        var data = result.Data!;
        Assert.That((string)GetProp(data, "source")!, Does.Contain("line2"));
        Assert.That((string)GetProp(data, "source")!, Does.Contain("line3"));
        Assert.That((string)GetProp(data, "source")!, Does.Not.Contain("line4"));
        Assert.That((int)GetProp(data, "startLine")!, Is.EqualTo(2));
        Assert.That((int)GetProp(data, "endLine")!, Is.EqualTo(3));
    }

    [Test]
    public async Task ReadFile_StartLineBeyondEndOfFile_ReturnsInvalidArgumentAsync()
    {
        var result = await _tools.ReadFile(_documentPath, startLine: 100, endLine: 200);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
    }

    [Test]
    public async Task ReadFile_LargerThanThreshold_OffloadsAndReturnsLargeResultInfoAsync()
    {
        var bigDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Big.cs");
        var bigSource = string.Concat(Enumerable.Range(0, 2000).Select(i => $"var line{i} = {i};\n"));
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[] { ("Big.cs", bigSource, bigDocPath) });
        _workspaceManager.SetTestSolution(solution);
        // ReadFile only offloads when GetSolutionRoot() is non-empty; the fake derives that from
        // SolutionPath since the AdhocWorkspace solution here has no FilePath of its own.
        _workspaceManager.SolutionPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Test.sln");

        var result = await _tools.ReadFile(bigDocPath);

        Assert.That(result.Success, Is.True);
        Assert.That(result.LargeResult, Is.Not.Null);
        Assert.That(result.LargeResult!.ResultType, Is.EqualTo("FileSource"));
    }
}
