#pragma warning disable CS8618
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("StructuralRefinementEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class StructuralRefinementEngineTests
{
    private IWorkspaceManager _workspaceManager;
    private StructuralRefinementEngine _engine;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.StructuralRefinementEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task SyncTypeAndFilename_WhenFilenameMatchesType_ReturnsAlreadyInTargetState()
    {
        // File "MyService.cs" contains class MyService -> names already match
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("MyService.cs", "public class MyService {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.SyncTypeAndFilenameAsync("MyService.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.AlreadyInTargetState));
    }

    [Test]
    public async Task SyncTypeAndFilename_WhenFilenameDoesNotMatchType_ReturnsChangeDescriptor()
    {
        // File "Wrong.cs" but class is MyService -> mismatch, engine proposes rename
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Wrong.cs", "public class MyService {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.SyncTypeAndFilenameAsync("Wrong.cs");

        // The engine proposes the rename via Changes (new file path -> content) and Message,
        // not UpdatedText -> there is no "CHANGE_" staging id anywhere in the implementation.
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), "Should propose a rename.");
        var changeKey = result.Changes?.Keys.FirstOrDefault(k => k.Contains("MyService.cs"));
        Assert.That(changeKey?.Absolute, Is.Not.Null.And.Not.Empty, "Target filename should be the type name");
        Assert.That(result.Message, Does.Contain("MyService.cs"), "Message should describe the rename target");
        Assert.That(result.UpdatedText, Is.EqualTo(result.Changes?.Values.Single()), "UpdatedText should equal the single Changes value");
    }

    [Test]
    public async Task SyncTypeAndFilename_UnknownFile_ThrowsFileNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", "public class C {}")]);
        _workspaceManager.SetTestSolution(solution);

        await Assert.ThrowsAsync<ToolNotFoundException>(
            () => _engine.SyncTypeAndFilenameAsync("DoesNotExist.cs"));
    }
}
