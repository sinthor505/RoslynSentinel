// Covers the sticky workspace reload failure record and its exposure on McpServerStatus (workspaceStale,
// reloadFailure, lastLoadFailure). The debounce and apply failure paths have no fault seam yet, so they are
// exercised only through RecordReloadFailure here.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
public class WorkspaceReloadFailureStatusTests
{
    private const string MissingSolutionFileName = "Missing.sln";

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

    private static string MissingSolutionPath() =>
        Path.Combine(Path.GetTempPath(), "does-not-exist", MissingSolutionFileName);

    private static McpServerStatusResult StatusFor(PersistentWorkspaceManager manager)
    {
        var tools = new ServerStatusTools(
            manager,
            new ActiveToolSurface("test", new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), null),
            new StoppedByScriptMarker(WasFound: false, Details: null));

        var result = (SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatusTool("test reason");
        return result.SuccessData!;
    }

    [Test]
    public async Task LoadSolutionAsync_MissingAbsolutePath_RecordsExplicitLoadFailureWithoutPaths()
    {
        var thrown = await Assert.CatchAsync(async () => await _manager.LoadSolutionAsync(MissingSolutionPath()));

        Assert.That(thrown, Is.Not.Null, "a missing solution must surface as a load failure");
        var failure = _manager.ReloadFailure;
        Assert.That(failure, Is.Not.Null, "the failed explicit load must be recorded");
        Assert.That(failure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.ExplicitLoad));
        Assert.That(failure.TriggerFile, Is.EqualTo(MissingSolutionFileName));
        Assert.That(failure.Attempts, Is.EqualTo(1));
        Assert.That(failure.OldWorkspaceKept, Is.False);
        // Bare directory names in .NET messages are not absolute paths; the sanitizer strips rooted paths only.
        Assert.That(failure.Cause, Is.Not.Null.And.Not.Empty);
        Assert.That(failure.Cause, Does.Not.Contain(Path.GetTempPath()), "the cause must not carry the directory path");

        var lastLoadFailure = _manager.LoadState.LastLoadFailure;
        Assert.That(lastLoadFailure, Is.Not.Null.And.Not.Empty);
        Assert.That(lastLoadFailure, Does.Not.Contain(Path.GetTempPath()), "LastLoadFailure must not carry the directory path");
    }

    [Test]
    public async Task LoadSolutionAsync_GoodLoadAfterFailedLoad_ClearsReloadFailure()
    {
        await Assert.CatchAsync(async () => await _manager.LoadSolutionAsync(MissingSolutionPath()));
        Assert.That(_manager.ReloadFailure, Is.Not.Null, "precondition: the bad load must record a failure");

        await _manager.LoadSolutionAsync(_fixture.SolutionPath);

        Assert.That(_manager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loaded));
        Assert.That(_manager.ReloadFailure, Is.Null, "a full successful load clears the sticky failure");
    }

    [Test]
    public void McpServerStatus_NoFailure_ReportsNotStaleAndNoReloadFailure()
    {
        var status = StatusFor(_manager);

        Assert.That(status.WorkspaceStale, Is.False);
        Assert.That(status.ReloadFailure, Is.Null);
    }

    [Test]
    public void McpServerStatus_AfterRecordedReloadFailure_ReportsStaleAndTheManagerRecord()
    {
        // Debounce and apply failure paths have no fault seam yet, so the failure is recorded directly.
        _manager.RecordReloadFailure(WorkspaceReloadTriggerKind.PostWriteReload, "X.sln", new InvalidOperationException("boom"), true);
        Assert.That(_manager.ReloadFailure, Is.Not.Null, "precondition: the recorded failure must be visible on the manager");

        var status = StatusFor(_manager);

        Assert.That(status.WorkspaceStale, Is.True);
        Assert.That(status.ReloadFailure, Is.EqualTo(_manager.ReloadFailure));
        Assert.That(status.ReloadFailure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.PostWriteReload));
        Assert.That(status.ReloadFailure.TriggerFile, Is.EqualTo("X.sln"));
        Assert.That(status.ReloadFailure.Cause, Is.EqualTo("InvalidOperationException: boom"));
    }

    [Test]
    public async Task McpServerStatus_AfterBadPathLoad_ReportsLastLoadFailureWithoutAbsoluteDirectory()
    {
        await Assert.CatchAsync(async () => await _manager.LoadSolutionAsync(MissingSolutionPath()));

        var status = StatusFor(_manager);

        Assert.That(status.LastLoadFailure, Is.Not.Null.And.Not.Empty);
        Assert.That(status.LastLoadFailure, Does.Not.Contain(Path.GetTempPath()), "the status must not expose the absolute directory");
    }

    [Test]
    public async Task LoadSolutionAsync_ReloadOpenThrows_KeepsPreviousSolutionAndRecordsFailure()
    {
        await _manager.LoadSolutionAsync(_fixture.SolutionPath);
        var before = _manager.CurrentSolution;
        var versionBefore = _manager.WorkspaceVersion;
        _manager.OpenSolutionOverride = (ws, path, ct) => throw new IOException("boom");

        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _manager.LoadSolutionAsync(_fixture.SolutionPath));

        Assert.That(ReferenceEquals(_manager.CurrentSolution, before), Is.True, "a failed reload must keep the previous solution instance");
        Assert.That(_manager.WorkspaceVersion, Is.EqualTo(versionBefore), "a failed reload must not bump the workspace version");
        var failure = _manager.ReloadFailure;
        Assert.That(failure, Is.Not.Null, "the failed reload must be recorded");
        Assert.That(failure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.ExplicitLoad));
        Assert.That(failure.OldWorkspaceKept, Is.True);
        Assert.That(failure.Cause, Does.Contain("IOException"));
        Assert.That(_manager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loaded), "the previous solution is still loaded");
    }

    [Test]
    public async Task LoadSolutionAsync_ReloadSucceedsAfterFailedReload_ClearsFailureAndSwapsSolution()
    {
        await _manager.LoadSolutionAsync(_fixture.SolutionPath);
        var before = _manager.CurrentSolution;
        var projectCountBefore = before!.Projects.Count();
        // Capture the default open seam so it can be restored exactly after the failing override.
        var defaultOpen = _manager.OpenSolutionOverride;
        _manager.OpenSolutionOverride = (ws, path, ct) => throw new IOException("boom");
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _manager.LoadSolutionAsync(_fixture.SolutionPath));
        Assert.That(_manager.ReloadFailure, Is.Not.Null, "precondition: the failed reload must be recorded");

        var versionBeforeRetry = _manager.WorkspaceVersion;
        _manager.OpenSolutionOverride = defaultOpen;
        await _manager.LoadSolutionAsync(_fixture.SolutionPath);

        Assert.That(_manager.ReloadFailure, Is.Null, "a successful reload clears the sticky failure");
        Assert.That(_manager.CurrentSolution, Is.Not.SameAs(before), "a successful reload swaps in a new solution instance");
        Assert.That(_manager.CurrentSolution!.Projects.Count(), Is.EqualTo(projectCountBefore));
        Assert.That(_manager.WorkspaceVersion, Is.GreaterThan(versionBeforeRetry));
    }
}
