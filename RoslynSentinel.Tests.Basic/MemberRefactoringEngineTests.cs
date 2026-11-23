#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Support;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
[Category("MemberRefactoringEngine")] // sentinel:auto-category
public class MemberRefactoringEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private MemberRefactoringEngine _memberRefactoringEngine;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var navEngine = Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = Basic.TestEngines.ValidationEngine(_workspaceManager, Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
        _memberRefactoringEngine = Basic.TestEngines.MemberRefactoringEngine(_workspaceManager, navEngine, validationEngine);
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
    public async Task ConvertMethodToProperty_SingleReturnNoParams_ConvertsToExpressionProperty()
    {
        SetSource(@"
public class Counter
{
    private int _count;
    public int GetCount() { return _count; }
}");
        var result = await _memberRefactoringEngine.ConvertMethodToPropertyAsync("Test.cs", "GetCount");
        // Method becomes an expression-bodied property -> no parameter list
        Assert.That(result.UpdatedText, Does.Contain("GetCount"), "Property name should be preserved");
        Assert.That(result.UpdatedText, Does.Contain("=>"), "Should produce expression-bodied property");
        Assert.That(result.UpdatedText, Does.Not.Contain("GetCount()"), "Should not have method parameter parens");
    }

    [Test]
    public async Task ConvertMethodToProperty_MethodWithParameters_ThrowsTargetIneligible()
    {
        const string source = @"
public class Calculator
{
    public int Add(int a, int b) { return a + b; }
}";
        SetSource(source);
        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(async () =>
        {
            await _memberRefactoringEngine.ConvertMethodToPropertyAsync("Test.cs", "Add");
        });
        Assert.That(ex.Message, Does.Contain("methods with parameters cannot become properties"));
    }

    [Test]
    public async Task MakeMethodStatic_MethodWithNoInstanceAccess_AddsStaticKeyword()
    {
        SetSource(@"
public class MathHelper
{
    public int Multiply(int a, int b) { return a * b; }
}");
        var result = await _memberRefactoringEngine.MakeMethodStaticAsync("Test.cs", "Multiply");
        Assert.That(result.UpdatedText, Does.Contain("static"), "Method with no instance access should receive static keyword");
        Assert.That(result.UpdatedText, Does.Contain("Multiply"), "Method name should be preserved");
    }


    [Test]
    [Category("ExtractMethodResult")] // sentinel:auto-category
    public async Task ExtractMethodAsync_ValidRange_ReturnsResult()
    {
        var source = @"public class Calc {
    public int Process(int a, int b) {
        int sum = a + b;
        return sum;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        // Line 3 = "        int sum = a + b;"
        var result = await _memberRefactoringEngine.ExtractMethodAsync("Calc.cs", 3, "int sum = a + b;", 3, "int sum = a + b;", "ComputeSum");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsError, Is.False, result.ErrorMessage);
        Assert.That(result.ExtractedMethodText, Does.Contain("ComputeSum("));
        Assert.That(result.CallSiteReplacement, Does.Contain("ComputeSum("));
        Assert.That(result.UpdatedSourceContent, Does.Contain("ComputeSum("));
    }

    [Test]
    [Category("ExtractMethodResult")] // sentinel:auto-category
    public async Task ExtractMethodAsync_UnknownFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
        {
            await _memberRefactoringEngine.ExtractMethodAsync("NoFile.cs", 1, "x", 1, "x", "NewMethod");
        });
        Assert.That(ex.MissingTargetName, Is.EqualTo("NoFile.cs"));
    }

    [Test]
    public async Task AddRemoveParamsAsync_MethodWithParams_ReturnsNonNull()
    {
        var source = "public class Service { public void Go(int a, int b) {} }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _memberRefactoringEngine.AddRemoveParamsAsync("Service.cs", "Go");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("Go("));
    }

    [Test]
    public async Task AddRemoveParamsAsync_MissingFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _memberRefactoringEngine.AddRemoveParamsAsync("NoFile.cs", "Foo"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    private static readonly string SimpleSource = Sources.Order;

    // --- SortMembers ---
    [Test]
    public async Task SortMembers_AutoStageTrue_ReturnsNotNull()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _memberRefactoringEngine.SortMembersAsync("Order.cs", "Order");
        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        // TODO assert member order in UpdatedText once the sort key for Order.cs members is confirmed
        Assert.That(result.UpdatedText, Is.Not.Empty);
    }

    // --- ExtractMethod ---
    [Test]
    public async Task ExtractMethod_ValidLineRange_ReturnsResult()
    {
        SetSource(SimpleSource, "Order.cs");
        var result = await _memberRefactoringEngine.ExtractMethodAsync("Order.cs", 17, "return string.Format(\"{0}: {1}\", OrderId, CustomerName);", 17, "return string.Format(\"{0}: {1}\", OrderId, CustomerName);", "FormatLabel");
        Assert.That(result.IsError, Is.False, result.ErrorMessage);
        Assert.That(result.ExtractedMethodText, Does.Contain("FormatLabel("));
    }
}
