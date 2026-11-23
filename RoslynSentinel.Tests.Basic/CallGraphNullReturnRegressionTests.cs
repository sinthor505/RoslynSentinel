using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Regression tests for call graph null-return bug:
/// GetCallGraphTool/GetReverseCallGraph returned null (serialized as empty output)
/// when the method was not found. Now throws InvalidOperationException with
/// actionable guidance.
/// </summary>
[TestFixture]
[Category("SymbolNavigationEngine")] // sentinel:auto-category
public class CallGraphNullReturnRegressionTests
{
    private IWorkspaceManager _workspaceManager;
    private SymbolNavigationEngine _symbolNavigationEngine;
    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task GetCallGraph_ExistingMethod_ReturnsNode()
    {
        const string code = @"
public class Calc
{
    public int Add(int a, int b) => a + b;
    public int Double(int x) => Add(x, x);
}";
        SetSource(code, "Calc.cs");
        var result = await _symbolNavigationEngine.GetCallGraphAsync("Calc.cs", "Double", maxDepth: 2);
        Assert.That(result, Is.Not.Null, "Should return a CallGraphNode for an existing method");
        Assert.That(result!.MethodName, Is.EqualTo("Double"));
    }

    [Test]
    public async Task GetCallGraph_NonExistentMethod_ThrowsNotFound()
    {
        const string code = @"public class Calc { public int Add(int a, int b) => a + b; }";
        SetSource(code, "Calc.cs");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
        {
            await _symbolNavigationEngine.GetCallGraphAsync("Calc.cs", "NonExistent", maxDepth: 2);
        });
        Assert.That(ex.MissingTargetName, Is.EqualTo("NonExistent"));
    }

    [Test]
    public async Task GetReverseCallGraph_ExistingMethod_ReturnsNode()
    {
        const string code = @"
public class Svc
{
    public void Helper() { }
    public void Caller() { Helper(); }
}";
        SetSource(code, "Svc.cs");
        var result = await _symbolNavigationEngine.GetReverseCallGraphAsync("Svc.cs", "Helper", maxDepth: 2);
        Assert.That(result, Is.Not.Null, "Should return a ReverseCallGraphNode for an existing method");
        Assert.That(result!.MethodName, Is.EqualTo("Helper"));
    }

    [Test]
    public async Task GetReverseCallGraph_NonExistentMethod_ThrowsNotFound()
    {
        const string code = @"public class Svc { public void Helper() { } }";
        SetSource(code, "Svc.cs");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
        {
            await _symbolNavigationEngine.GetReverseCallGraphAsync("Svc.cs", "Ghost", maxDepth: 2);
        });
        Assert.That(ex.MissingTargetName, Is.EqualTo("Ghost"));
    }
}
