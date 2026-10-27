using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Tools.Basic;

// Covers the stale-workspace write refusal in ApplyProposedChangesAsync: while ReloadFailure is set, targets
// that are not .csproj/.sln/.slnx are refused with nothing written, and a successful reload clears the latch.
// Not parallelizable: each test loads a real solution through PersistentWorkspaceManager, like the other
// workspace reload fixtures.
[TestFixture]
[NonParallelizable]
public class WorkspaceStaleRefusalTests
{
    private TestSolutionFixture _fixture = null!;
    private PersistentWorkspaceManager _manager = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp() => _fixture = new TestSolutionFixture();

    [OneTimeTearDown]
    public void OneTimeTearDown() => _fixture.Dispose();

    [SetUp]
    public void SetUp() =>
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

    [TearDown]
    public void TearDown() => _manager.Dispose();

    private string ProjectDirectory() =>
        Path.GetDirectoryName(_manager.CurrentSolution!.Projects
            .Select(p => p.FilePath)
            .First(f => f is not null && File.Exists(f) && f.StartsWith(_fixture.SolutionDirectory, StringComparison.OrdinalIgnoreCase))!)!;

    private static Dictionary<FilePathWrapper, string> NewFileChange(string path) =>
        new() { [new FilePathWrapper(path)] = "class StaleProbe { }\n" };

    [Test]
    public async Task StaleWorkspace_CsWrite_IsRefusedAndNothingWritten()
    {
        await _manager.LoadSolutionAsync(_fixture.SolutionPath);
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ProjectFileChange, "Test.csproj", new InvalidOperationException("boom"), oldWorkspaceKept: true);
        var target = Path.Combine(ProjectDirectory(), "StaleProbe_Refused.cs");

        var result = await _manager.ApplyProposedChangesAsync(NewFileChange(target));

        Assert.That(result.IsError, Is.True, "a .cs write against a stale workspace must be refused");
        Assert.That(result.Summary, Does.Contain("stale").And.Contain("forceReload: true"));
        Assert.That(File.Exists(target), Is.False, "a refused write must not touch disk");
        Assert.That(_manager.ReloadFailure, Is.Not.Null, "the latch stays set until a reload succeeds");
    }

    [Test]
    public async Task StaleWorkspace_MixedCsAndCsprojTargets_IsRefused()
    {
        await _manager.LoadSolutionAsync(_fixture.SolutionPath);
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ProjectFileChange, "Test.csproj", new InvalidOperationException("boom"), oldWorkspaceKept: true);
        var dir = ProjectDirectory();
        var csTarget = Path.Combine(dir, "StaleProbe_Mixed.cs");
        var projTarget = Path.Combine(dir, "StaleProbe_Mixed.csproj");
        var changes = new Dictionary<FilePathWrapper, string>
        {
            [new FilePathWrapper(csTarget)] = "class StaleProbe { }\n",
            [new FilePathWrapper(projTarget)] = "<Project />\n",
        };

        var result = await _manager.ApplyProposedChangesAsync(changes);

        Assert.That(result.IsError, Is.True, "a batch containing a .cs target must be refused as a whole");
        Assert.That(File.Exists(csTarget), Is.False, "the .cs target must not be written");
        Assert.That(File.Exists(projTarget), Is.False, "the .csproj target must not be written when the batch is refused");
    }

    [Test]
    public async Task StaleWorkspace_AfterSuccessfulReload_WriteIsAllowed()
    {
        await _manager.LoadSolutionAsync(_fixture.SolutionPath);
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ProjectFileChange, "Test.csproj", new InvalidOperationException("boom"), oldWorkspaceKept: true);
        Assert.That(_manager.ReloadFailure, Is.Not.Null, "precondition: the failure must be recorded");

        await _manager.LoadSolutionAsync(_fixture.SolutionPath);

        Assert.That(_manager.ReloadFailure, Is.Null, "a successful reload must clear the failure");
        var target = Path.Combine(ProjectDirectory(), "StaleProbe_Recovered.cs");

        var result = await _manager.ApplyProposedChangesAsync(NewFileChange(target));

        Assert.That(result.IsError, Is.False, result.Summary);
        Assert.That(File.Exists(target), Is.True, "the write must land once the workspace is fresh");
    }

    [Test]
    public async Task NoFailure_CsWrite_IsNotRefused()
    {
        await _manager.LoadSolutionAsync(_fixture.SolutionPath);
        Assert.That(_manager.ReloadFailure, Is.Null, "precondition: no failure recorded");
        var target = Path.Combine(ProjectDirectory(), "StaleProbe_NoFailure.cs");

        var result = await _manager.ApplyProposedChangesAsync(NewFileChange(target));

        Assert.That(result.IsError, Is.False, result.Summary);
        Assert.That(File.Exists(target), Is.True, "the write must land when no failure is recorded");
    }

    // Pins the current undo behaviour: exactRestore (UndoLastApply) is refused while stale because
    // AllowExactRestoreWhenWorkspaceStale is false. This test must change if that constant is flipped.
    [Test]
    public async Task StaleWorkspace_ExactRestoreWrite_IsRefusedWhileExemptionIsOff()
    {
        await _manager.LoadSolutionAsync(_fixture.SolutionPath);
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.ProjectFileChange, "Test.csproj", new InvalidOperationException("boom"), oldWorkspaceKept: true);
        var target = Path.Combine(ProjectDirectory(), "StaleProbe_ExactRestore.cs");

        var result = await _manager.ApplyProposedChangesAsync(NewFileChange(target), exactRestore: true);

        Assert.That(result.IsError, Is.True, "undo writes are refused while the exemption is off");
        Assert.That(result.Summary, Does.Contain("stale"));
        Assert.That(File.Exists(target), Is.False, "a refused undo write must not touch disk");
    }

    [TestCase("a.csproj", true)]
    [TestCase("A.CSPROJ", true)]
    [TestCase("x\\y.sln", true)]
    [TestCase("y.slnx", true)]
    [TestCase("a.cs", false)]
    [TestCase("a.props", false)]
    [TestCase("a.targets", false)]
    [TestCase("a", false)]
    public void IsStructuralTarget_ClassifiesByExtension(string path, bool expected) =>
        Assert.That(WorkspaceStaleText.IsStructuralTarget(path), Is.EqualTo(expected));

    [Test]
    public void Reason_NamesTriggerAndCauseAndRecoveryNamesForceReload()
    {
        var failure = new WorkspaceReloadFailure(WorkspaceReloadTriggerKind.SolutionFileChange, "My.sln", DateTime.UtcNow, 1, true, "IOException: locked");

        var reason = WorkspaceStaleText.Reason(failure);

        Assert.That(reason, Does.Contain("SolutionFileChange"));
        Assert.That(reason, Does.Contain("My.sln"));
        Assert.That(reason, Does.Contain("locked"));
        Assert.That(WorkspaceStaleText.Recovery, Does.Contain("forceReload: true"));
    }
}
