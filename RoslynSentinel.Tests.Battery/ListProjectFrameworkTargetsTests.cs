// ListProjectFrameworkTargets — SentinelWorkspaceTools. Zero coverage before this file. The tool itself
// is a thin try/catch wrapper with no branches of its own; the real branching lives in
// ProjectConsistencyEngine.GetProjectFrameworkSummaryAsync, which reads each project's .csproj file
// directly off disk (not from the in-memory Roslyn solution) looking for TargetFramework/TargetFrameworks
// elements. TestSolutionBuilder's on-disk overload only sets Project.FilePath — it doesn't write the
// .csproj content — so the fixture writes real .csproj files itself.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class ListProjectFrameworkTargetsTests
{
    private FakeWorkspaceManager _workspaceManager;
    private SentinelWorkspaceTools _tools;
    private string _tempDir;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        _tempDir = Path.Combine(Path.GetTempPath(), "ListProjectFrameworkTargetsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

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
            new BuildEngine(_workspaceManager, diagnosticEngine));
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

    [Test]
    public async Task ListProjectFrameworkTargets_ProjectWithTargetFramework_ReturnsItAsync()
    {
        var csprojPath = Path.Combine(_tempDir, "TestProj.csproj");
        await File.WriteAllTextAsync(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var docPath = Path.Combine(_tempDir, "Foo.cs");

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            csprojPath,
            new[] { ("Foo.cs", "public class Foo { }\n", docPath) });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.ListProjectFrameworkTargets();

        Assert.That(result.Success, Is.True);
        var data = (List<ProjectFrameworkSummary>)result.Data!;
        Assert.That(data, Has.Count.EqualTo(1));
        Assert.That(data[0].ProjectName, Is.EqualTo("TestProj"));
        Assert.That(data[0].TargetFramework, Is.EqualTo("net8.0"));
    }

    [Test]
    public async Task ListProjectFrameworkTargets_CsprojMissingFromDisk_ReturnsUnknownAsync()
    {
        var csprojPath = Path.Combine(_tempDir, "Ghost.csproj");
        var docPath = Path.Combine(_tempDir, "Foo.cs");

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "Ghost",
            csprojPath,
            new[] { ("Foo.cs", "public class Foo { }\n", docPath) });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.ListProjectFrameworkTargets();

        Assert.That(result.Success, Is.True);
        var data = (List<ProjectFrameworkSummary>)result.Data!;
        Assert.That(data, Has.Count.EqualTo(1));
        Assert.That(data[0].TargetFramework, Is.EqualTo("unknown"));
    }

    [Test]
    public async Task ListProjectFrameworkTargets_MalformedCsprojXml_ReturnsUnknownAsync()
    {
        var csprojPath = Path.Combine(_tempDir, "Malformed.csproj");
        await File.WriteAllTextAsync(csprojPath, "<Project><Unclosed>");
        var docPath = Path.Combine(_tempDir, "Foo.cs");

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "Malformed",
            csprojPath,
            new[] { ("Foo.cs", "public class Foo { }\n", docPath) });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.ListProjectFrameworkTargets();

        Assert.That(result.Success, Is.True);
        var data = (List<ProjectFrameworkSummary>)result.Data!;
        Assert.That(data[0].TargetFramework, Is.EqualTo("unknown"));
    }
}
