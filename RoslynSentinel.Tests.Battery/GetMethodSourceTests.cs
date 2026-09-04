// GetMethodSource — SentinelWorkspaceTools. Zero coverage before this file (GetTestCoverageMap
// flagged branches: document == null, method == null, methodBytes > threshold, plus happy path).
// root == null is not exercised here — GetSyntaxRootAsync only returns null for non-source
// documents, which TestSolutionBuilder never produces, so that branch is effectively unreachable
// from these fixtures.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class GetMethodSourceTests
{
    private FakeWorkspaceManager _workspaceManager;
    private SentinelWorkspaceTools _tools;
    private string _documentPath;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new FakeWorkspaceManager();
        _documentPath = Path.Combine(Path.GetTempPath(), "GetMethodSourceTests_" + Guid.NewGuid().ToString("N"), "Foo.cs");

        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("Foo.cs", "public class Foo\n{\n    public int Bar(int x)\n    {\n        return x + 1;\n    }\n}\n", _documentPath),
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
            new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            new TestRunEngine(_workspaceManager));
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task GetMethodSource_ExistingMethod_ReturnsSourceAndSignatureAsync()
    {
        var result = await _tools.GetMethodSource(reason: "test", _documentPath, "Bar");

        Assert.That(result.Success, Is.True);
        var data = (MethodSourceResult)result.Data!;
        Assert.That(data.Source, Does.Contain("return x + 1;"));
        Assert.That(data.Signature, Does.Contain("Bar"));
        Assert.That(data.Envelope, Is.Not.Null);
        Assert.That(data.Envelope.LineCount, Is.EqualTo(8));
        Assert.That(data.Envelope.ReturnedFromLine, Is.EqualTo(3));
        Assert.That(data.Envelope.ReturnedToLine, Is.EqualTo(6));
    }

    [Test]
    public async Task GetMethodSource_ConstructorRequestedByClassName_ReturnsConstructorSourceAsync()
    {
        var ctorDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "WithCtor.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[]
            {
                ("WithCtor.cs", "public class WithCtor\n{\n    public WithCtor(int x)\n    {\n    }\n}\n", ctorDocPath),
            });
        _workspaceManager.SetTestSolution(solution);

        var result = await _tools.GetMethodSource(reason: "test", ctorDocPath, "WithCtor");

        Assert.That(result.Success, Is.True);
        var data = (MethodSourceResult)result.Data!;
        Assert.That(data.Source, Does.Contain("public WithCtor(int x)"));
    }

    [Test]
    public async Task GetMethodSource_FileNotInSolution_ReturnsFileNotFoundAsync()
    {
        var missingPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "DoesNotExist.cs");

        var result = await _tools.GetMethodSource(reason: "test", missingPath, "Bar");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("FileNotFound"));
    }

    [Test]
    public async Task GetMethodSource_MethodNameNotInFile_ReturnsMethodNotFoundAsync()
    {
        var result = await _tools.GetMethodSource(reason: "test", _documentPath, "NoSuchMethod");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Error!.ErrorCode, Is.EqualTo("MethodNotFound"));
    }

    [Test]
    public async Task GetMethodSource_MethodNameCaseMismatch_FallsBackToCaseInsensitiveMatchAsync()
    {
        var result = await _tools.GetMethodSource(reason: "test", _documentPath, "bar");

        Assert.That(result.Success, Is.True);
        var data = (MethodSourceResult)result.Data!;
        Assert.That(data.Signature, Does.Contain("Bar"));
    }

    [Test]
    public async Task GetMethodSource_MethodLargerThanThreshold_OffloadsAndReturnsLargeResultInfoAsync()
    {
        var bigDocPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Big.cs");
        var bigBody = string.Concat(Enumerable.Range(0, 2000).Select(i => $"        var line{i} = {i};\n"));
        var bigSource = $"public class Big\n{{\n    public void Huge()\n    {{\n{bigBody}    }}\n}}\n";
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(Path.GetDirectoryName(_documentPath)!, "TestProj.csproj"),
            new[] { ("Big.cs", bigSource, bigDocPath) });
        _workspaceManager.SetTestSolution(solution);
        // GetMethodSource only offloads when GetSolutionRoot() is non-empty; the fake derives that
        // from SolutionPath since the AdhocWorkspace solution here has no FilePath of its own.
        _workspaceManager.SolutionPath = Path.Combine(Path.GetDirectoryName(_documentPath)!, "Test.sln");

        var result = await _tools.GetMethodSource(reason: "test", bigDocPath, "Huge");

        Assert.That(result.Success, Is.True);
        Assert.That(result.LargeResult, Is.Not.Null);
        Assert.That(result.LargeResult!.ResultType, Is.EqualTo("MethodSource"));
    }
}
