#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Support;

namespace RoslynSentinel.Tests.Battery.Advanced;

/// <summary>
/// Battery #7 -> Tests for four engines at 6-mention coverage:
///   A. BasicRefactoringEngine (4 tests) -> InvertBoolean, InvertBoolean (stub), InvertBoolean (stub), InvertBoolean (stub)
///   B. SemanticSearchEngine      (4 tests) -> FindMethodsByReturnType, FindTypesByAttribute
///   C. AdvancedTypeEngine        (5 tests) -> ConvertTupleToClass, ChangePropertyType, ConvertAnonymousToNamed
///   D. MemberRefactoringEngine (5 tests) -> ConvertMethodToProperty, MakeMethodStatic, ExtractMethod, AddRemoveParams, SortMembers
///
/// Total: 16 tests. All workspace-based (SetSource / SetMultipleFiles).
/// </summary>
/// 
[TestFixture]
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
[Category("BasicRefactoringEngine")] // sentinel:auto-category
public class BasicRefactoringEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private BasicRefactoringEngine _basicRefactoringEngine;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _basicRefactoringEngine = new BasicRefactoringEngine(_workspaceManager);
        var navEngine = Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = Basic.TestEngines.ValidationEngine(_workspaceManager, Basic.TestEngines.DiffEngine(), NullLogger<ValidationEngine>.Instance);
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
    public async Task InvertBoolean_AnyInput_ThrowsNotImplemented()
    {
        // InvertBoolean is a documented stub -> requires solution-wide reference tracking
        SetSource("public class C { public bool IsEnabled { get; set; } }");
        var ex = await Assert.ThrowsAsync<ToolNotImplementedException>(async () =>
        {
            await _basicRefactoringEngine.InvertBooleanAsync("Test.cs", "IsEnabled");
        });
        Assert.That(ex!.Message, Does.Contain("not implemented"), "InvertBoolean stub should throw ToolNotImplementedException");
    }
}
