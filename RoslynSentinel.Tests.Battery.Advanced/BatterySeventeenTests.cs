using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

// ────────────────────────────────────────────────────────────────────────────
// Battery #17 -> LogicSimplificationEngine,
//               MsToolAugmentEngine and SolutionStructureEngine.
// ────────────────────────────────────────────────────────────────────────────

[TestFixture]
[Category("LogicSimplificationEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class LogicOptimizationEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private LogicSimplificationEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _mgr = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.LogicSimplificationEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown()
    {
        _mgr?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task ConvertToNullCoalescing_MissingFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ConvertToNullCoalescingAsync("NoSuchFile.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ConvertToNullCoalescing_IfNullAssignment_ConvertsToModernSyntax()
    {
        const string source = @"
public class Guard
{
    public string Ensure(string value)
    {
        if (value == null)
        {
            value = string.Empty;
        }
        return value;
    }
}";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Guard.cs", source)]));

        var result = await _engine.ConvertToNullCoalescingAsync("Guard.cs");

        Assert.That(result.Outcome, Is.EqualTo(RoslynSentinel.Common.EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("??"));
    }

    [Test]
    public async Task AddGuardClauses_MissingFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.AddGuardClausesAsync("NoSuchFile.cs", "DoWork"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task SimplifyBooleanExpressions_MissingFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.SimplifyBooleanExpressionsAsync("NoSuchFile.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}

