// Battery #12 -> CodeStyleEngine / SyntaxUpgradeEngine / ModernizationEngine / BasicRefactoringEngine
// All engines in this battery require SentinelConfiguration, which defaults all features to enabled.

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("SyntaxUpgradeEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class SyntaxUpgradeEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private SyntaxUpgradeEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.SyntaxUpgradeEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task AddBraces_IfWithoutBraces_AddsBraces()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { void M(int x) { if (x > 0) System.Console.WriteLine(x); } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddBracesAsync("Code.cs");

        Assert.That(result.UpdatedText, Does.Contain("{"), "Braces should be added around if body");
        Assert.That(result.UpdatedText, Does.Contain("System.Console.WriteLine"), "Original statement should be preserved");
    }

    [Test]
    public async Task AddBraces_UnknownFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { }")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.AddBracesAsync("DoesNotExist.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UpgradeToModernGuards_NoGuardPatterns_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { public void Run() { var x = 1; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.UpgradeToModernGuardsAsync("Code.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}
