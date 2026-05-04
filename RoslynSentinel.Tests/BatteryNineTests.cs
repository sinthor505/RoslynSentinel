#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

/// <summary>
/// Battery #9 — Dedicated test fixtures for four engines lacking named coverage:
///   A. TestingEngine              (3 tests) — CalculateComplexity, GenerateTestSkeleton, GenerateTestScaffold
///   B. PerformanceEngine          (3 tests) — StringConcatInLoop detected, clean code, unknown file
///   C. StructuralRefinementEngine (3 tests) — SyncTypeAndFilename match/mismatch/unknown
///   D. SolutionManagementEngine   (2 tests) — CreateProject null path, SplitProject propagates
///
/// Total: 11 tests.
/// </summary>

// ════════════════════════════════════════════════════════════════════════════════
// A. TestingEngineTests
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class TestingEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private TestingEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new TestingEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task CalculateComplexity_IfAndForLoop_ReturnsThree()
    {
        SetSource(@"
public class Calculator
{
    public int Calculate(int x)
    {
        if (x > 0)
        {
            for (int i = 0; i < x; i++) { }
        }
        return x;
    }
}");
        var report = await _engine.CalculateComplexityAsync("Test.cs", "Calculate");

        // base(1) + if(1) + for(1) = 3
        Assert.That(report.CyclomaticComplexity, Is.EqualTo(3));
        Assert.That(report.MethodName, Is.EqualTo("Calculate"));
        Assert.That(report.ConditionalsToTest, Has.Count.GreaterThan(0), "Should list conditionals for test guidance");
    }

    [Test]
    public async Task GenerateTestSkeleton_ClassWithPublicMethods_GeneratesTestClassCode()
    {
        SetSource(@"
namespace MyApp
{
    public class UserService
    {
        public string GetName() => ""test"";
        public void Save() { }
    }
}");
        var report = await _engine.GenerateTestSkeletonAsync("Test.cs", "UserService");

        Assert.That(report.Content, Does.Contain("UserServiceTests"), "Test class name should be derived from class name");
        Assert.That(report.Content, Does.Contain("GetName_Should_ReturnExpectedResult_When_ValidInput"), "Each public method should get a test stub");
    }

    [Test]
    public async Task GenerateTestScaffold_ClassWithInterfaceConstructor_GeneratesMockSetup()
    {
        SetSource(@"
namespace MyApp
{
    public interface IOrderRepository { }
    public class OrderService
    {
        private readonly IOrderRepository _repo;
        public OrderService(IOrderRepository repo) { _repo = repo; }
        public void PlaceOrder() { }
    }
}");
        var result = await _engine.GenerateTestScaffoldAsync("Test.cs", "OrderService");

        Assert.That(result.Error, Is.Null, "Should succeed without error");
        Assert.That(result.Code, Does.Contain("OrderServiceTests"), "Test class should be named correctly");
        Assert.That(result.Code, Does.Contain("Mock<IOrderRepository>"), "Interface constructor params should become mocks");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. SolutionManagementEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SolutionManagementEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SolutionManagementEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SolutionManagementEngine(_workspaceManager);
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Source", [("Foo.cs", "public class Foo { }")]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public void CreateProject_NullSolutionPath_ThrowsException()
    {
        // AdhocWorkspace has no FilePath; SolutionPath is also null → "Solution path not found."
        var ex = Assert.ThrowsAsync<Exception>(async () =>
            await _engine.CreateProjectAsync("NewProject", "classlib"));

        Assert.That(ex.Message, Does.Contain("Solution path not found"),
            "Should throw when no solution file path is available");
    }

    [Test]
    public void SplitProjectByFolder_NullSolutionPath_ThrowsViaCreateProject()
    {
        // SplitProject internally calls CreateProjectAsync first, which propagates the null-path error
        var ex = Assert.ThrowsAsync<Exception>(async () =>
            await _engine.SplitProjectByFolderAsync("Source", "Services", "Source.Services"));

        Assert.That(ex.Message, Does.Contain("Solution path not found"),
            "SplitProject should propagate the null solution path exception");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. PerformanceEngineTests
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class PerformanceEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private PerformanceEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new PerformanceEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task AnalyzePerformance_StringLiteralConcatInForeach_DetectsIssue()
    {
        SetSource(@"
public class Builder
{
    public string Build(string[] items)
    {
        string result = """";
        foreach (var item in items)
        {
            result = result + "", "";
        }
        return result;
    }
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");

        Assert.That(issues, Is.Not.Empty, "String concat with literal in loop should be flagged");
        Assert.That(issues.Any(i => i.IssueType == "StringConcatenationInLoop"), Is.True,
            "Issue type must be StringConcatenationInLoop");
    }

    [Test]
    public async Task AnalyzePerformance_CleanMethod_ReturnsNoIssues()
    {
        SetSource(@"
public class Clean
{
    public int Add(int a, int b) => a + b;
}");
        var issues = await _engine.AnalyzePerformanceAsync("Test.cs");

        Assert.That(issues, Is.Empty, "No performance problems should yield empty list");
    }

    [Test]
    public async Task AnalyzePerformance_UnknownFile_ReturnsEmptyNotThrows()
    {
        SetSource("public class C {}"); // workspace contains Test.cs, not Unknown.cs

        var issues = await _engine.AnalyzePerformanceAsync("Unknown.cs");

        Assert.That(issues, Is.Empty, "Unknown file should return empty list, not throw");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// D. StructuralRefinementEngineTests
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class StructuralRefinementEngineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private StructuralRefinementEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new StructuralRefinementEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task SyncTypeAndFilename_WhenFilenameMatchesType_ReturnsNoChangeMessage()
    {
        // File "MyService.cs" contains class MyService → names already match
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("MyService.cs", "public class MyService {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.SyncTypeAndFilenameAsync("MyService.cs");

        Assert.That(result, Is.EqualTo("Filename matches primary type."));
    }

    [Test]
    public async Task SyncTypeAndFilename_WhenFilenameDoesNotMatchType_ReturnsChangeDescriptor()
    {
        // File "Wrong.cs" but class is MyService → mismatch, engine proposes rename
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Wrong.cs", "public class MyService {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.SyncTypeAndFilenameAsync("Wrong.cs");

        Assert.That(result, Does.StartWith("CHANGE_"), "Should propose a rename via staging change ID");
        Assert.That(result, Does.Contain("MyService.cs"), "Target filename should be the type name");
    }

    [Test]
    public void SyncTypeAndFilename_UnknownFile_ThrowsException()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", "public class C {}")]);
        _workspaceManager.SetTestSolution(solution);

        Assert.ThrowsAsync<Exception>(() => _engine.SyncTypeAndFilenameAsync("DoesNotExist.cs"));
    }
}
