// Battery #13 -> CodeGenerationEngine / ControlFlowEngine / SymbolNavigationEngine
// CodeGenerationEngine is sync/JSON-only (no workspace needed for its primary methods).
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("ReadonlyFieldCandidate")] // sentinel:auto-category
[Category("SymbolNavigationEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class SymbolNavigationEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private SymbolNavigationEngine _engine = null!;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    [Test]
    public async Task FindReadonlyFieldCandidates_NonReadonlyPrivateField_ReportsIt()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", "public class Service { private int _count = 0; }")]);
        _workspaceManager.SetTestSolution(solution);
        var candidates = await _engine.FindReadonlyFieldCandidatesAsync("Service.cs");
        Assert.That(candidates.Any(c => c.FieldName == "_count"), Is.True, "Non-readonly private field should be reported as readonly candidate");
    }

    [Test]
    public async Task FindReadonlyFieldCandidates_ReadonlyField_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", "public class Service { private readonly int _count = 0; }")]);
        _workspaceManager.SetTestSolution(solution);
        var candidates = await _engine.FindReadonlyFieldCandidatesAsync("Service.cs");
        Assert.That(candidates.Any(c => c.FieldName == "_count"), Is.False, "Already-readonly field should NOT be reported");
    }

    [Test]
    public async Task FindReadonlyFieldCandidates_UnknownFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", "public class Service { }")]);
        _workspaceManager.SetTestSolution(solution);
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.FindReadonlyFieldCandidatesAsync("DoesNotExist.cs"));
    }
}
