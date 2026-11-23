// Battery #25 -> Gap Coverage: 40 untested engine methods across 14 engines
// Each group covers the specific methods that had zero test references.
// All tests use in-memory AdhocWorkspace via TestSolutionBuilder (no MSBuild/project loading).
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("BasicRefactoringEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class RefactoringEngineGapTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private BasicRefactoringEngine _basicRefactoringEngine = null!;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _basicRefactoringEngine = Basic.TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task ConvertIndexerToMethodAsync_SimpleIndexer_ReturnsNonNull()
    {
        var source = @"
public class Cache {
    private string[] _data = new string[10];
    public string this[int key] => _data[key];
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Cache.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _basicRefactoringEngine.ConvertIndexerToMethodAsync("Cache.cs");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified)); Assert.That(result.UpdatedText, Does.Contain("Get("));
    }

    [Test]
    public async Task ConvertIndexerToMethodAsync_MissingFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _basicRefactoringEngine.ConvertIndexerToMethodAsync("NoFile.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }


    [Test]
    [Category("ControlFlowSummary")] // sentinel:auto-category
    public async Task AnalyzeControlFlowAsync_SimpleMethod_ReturnsSummary()
    {
        var source = @"
public class Service {
    public int Compute(int x) {
        if (x > 0) return x;
        return -x;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _basicRefactoringEngine.AnalyzeControlFlowAsync("Service.cs", "Compute");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.MethodName, Is.EqualTo("Compute"));
    }

    [Test]
    public async Task AnalyzeControlFlowAsync_UnknownFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
        {
            await _basicRefactoringEngine.AnalyzeControlFlowAsync("NoFile.cs", "Foo");
        });
        Assert.That(ex.MissingTargetName, Is.EqualTo("NoFile.cs"));
    }

    [Test]
    public async Task AnalyzeControlFlowAsync_UnknownMethod_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", "public class Calc { public int Add(int a, int b) => a + b; }")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
        {
            await _basicRefactoringEngine.AnalyzeControlFlowAsync("Calc.cs", "Missing");
        });
        Assert.That(ex.MissingTargetKind, Is.EqualTo("method"));
    }

    [Test]
    public async Task AnalyzeControlFlowAsync_AbstractMethod_ThrowsTargetIneligible()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Shape.cs", "public abstract class Shape { public abstract int Area(); }")]);
        _workspaceManager.SetTestSolution(solution);
        await Assert.ThrowsAsync<ToolTargetIneligibleException>(async () =>
        {
            await _basicRefactoringEngine.AnalyzeControlFlowAsync("Shape.cs", "Area");
        });
    }

    [Test]
    [Category("DataFlowSummary")] // sentinel:auto-category
    public async Task AnalyzeDataFlowAsync_SimpleMethod_ReturnsSummary()
    {
        var source = @"
public class Service {
    public int Sum(int a, int b) {
        int total = a + b;
        return total;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _basicRefactoringEngine.AnalyzeDataFlowAsync("Service.cs", "Sum");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.MethodName, Is.EqualTo("Sum"));
    }

    [Test]
    public async Task AnalyzeDataFlowAsync_UnknownFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
        {
            await _basicRefactoringEngine.AnalyzeDataFlowAsync("NoFile.cs", "Foo");
        });
        Assert.That(ex.MissingTargetName, Is.EqualTo("NoFile.cs"));
    }

    [Test]
    public async Task AnalyzeDataFlowAsync_UnknownMethod_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", "public class Calc { public int Add(int a, int b) => a + b; }")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
        {
            await _basicRefactoringEngine.AnalyzeDataFlowAsync("Calc.cs", "Missing");
        });
        Assert.That(ex.MissingTargetKind, Is.EqualTo("method"));
    }
}
