#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

/// <summary>
/// Battery #9 -> Dedicated test fixtures for four engines lacking named coverage:
///   A. TestingEngine              (3 tests) -> CalculateComplexity, GenerateTestSkeleton, GenerateTestScaffold
///   B. PerformanceEngine          (3 tests) -> StringConcatInLoop detected, clean code, unknown file
///   C. StructuralRefinementEngine (3 tests) -> SyncTypeAndFilename match/mismatch/unknown
///   D. SolutionManagementEngineTests (2 tests; exercise SolutionStructureEngine) -> CreateProject null path, SplitProject propagates
///
/// Total: 11 tests.
/// </summary>

// ════════════════════════════════════════════════════════════════════════════════
// A. TestingEngineTests
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("TestingEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class TestingEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private TestingEngine _engine;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.TestingEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

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
    [Category("TestScaffoldResult")] // sentinel:auto-category
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

    [Test]
    public async Task GenerateTestScaffold_ClassNotInFile_ThrowsToolNotFoundWithKindAndNearestNameHint()
    {
        SetSource(@"
public class Order
{
    public string CustomerId { get; set; }
}");

        var ex = await Assert.ThrowsAsync<RoslynSentinel.Common.ToolNotFoundException>(async () => await _engine.GenerateTestScaffoldAsync("Test.cs", "Ordr"));
        Assert.That(ex!.MissingTargetKind, Is.EqualTo(RoslynSentinel.Common.MissingTargetKinds.Class));
        Assert.That(ex.MissingTargetName, Is.EqualTo("Ordr"));
        Assert.That(ex.Message, Does.Contain("Did you mean 'Order'?"));
        Assert.That(ex.Message, Does.Contain("Retry with className: 'Order'."));
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. PerformanceEngineTests
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("PerformanceEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class PerformanceEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private PerformanceEngine _engine;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.PerformanceEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

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
