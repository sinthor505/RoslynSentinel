using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests;

// Load-tracking tests for the post-write background reload (plan_multi_file_movefile_and_namespace_change_tool, Step 14).
// They use the REAL PersistentWorkspaceManager on purpose: the reload path (RunTrackedBackgroundReload ->
// ReloadWorkspaceFromDiskAsync -> SwapInFreshWorkspaceLocked) and its status tracking are the behaviour under test, and a
// fake manager would skip exactly that. Only the MSBuild open is replaced, through the OpenSolutionOverride seam, and the
// reload's open is gated on a TaskCompletionSource so each test decides exactly when the reload finishes. No test sleeps
// and no test asserts elapsed time; the only Delay is an upper bound that fails a test which would otherwise hang.
[TestFixture]
public class WorkspaceReloadTrackingTests
{
    private const string OriginalCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";
    private const string UpdatedCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
    private static readonly TimeSpan WaitBound = TimeSpan.FromSeconds(30);

    private PersistentWorkspaceManager _manager = null!;
    private string _projectDir = null!;
    private string _csprojPath = null!;
    private TaskCompletionSource _gate = null!;
    private TaskCompletionSource _reloadEntered = null!;

    [SetUp]
    public void Setup()
    {
        _manager = (PersistentWorkspaceManager)Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _projectDir = Path.Combine(Path.GetTempPath(), $"WsReloadTracking_{Guid.NewGuid()}");
        Directory.CreateDirectory(_projectDir);
        _csprojPath = Path.Combine(_projectDir, "TestProj.csproj");
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reloadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Keeps the cross-process write lock file inside this test's temp directory, never the repo root.
        _manager.SetTestSolutionRoot(_projectDir);
    }

    [TearDown]
    public void TearDown()
    {
        // Open the gate first so a reload that is still parked cannot outlive the fixture.
        _gate.TrySetResult();
        _manager.Dispose();
        if (Directory.Exists(_projectDir))
        {
            Directory.Delete(_projectDir, true);
        }
    }

    [Test]
    public async Task CsprojWrite_StatusIsLoadingWhenTheApplyReturns_ThenLoaded()
    {
        await LoadFixtureSolutionAsync();
        ArmGatedReload(failReload: false);
        try
        {
            var result = await _manager.ApplyProposedChangesAsync(
                new Dictionary<FilePathWrapper, string> { [_csprojPath] = UpdatedCsproj });
            await _reloadEntered.Task.WaitAsync(WaitBound);

            Assert.That(result.NeedsFullReload, Is.True, "a .csproj in the write set must queue a full reload");
            Assert.That(_manager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loading),
                "the reload is tracked as a load while it is parked on the gate");

            _gate.TrySetResult();
            var status = await _manager.WaitForLoadAsync(WaitBound, CancellationToken.None);

            Assert.That(status, Is.EqualTo(SolutionLoadStatus.Loaded));
            Assert.That(_manager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loaded));
        }
        finally
        {
            _gate.TrySetResult();
        }
    }

    [Test]
    public async Task ReadDuringReload_WaitsAndReturnsTheSwappedSolution()
    {
        await LoadFixtureSolutionAsync();
        var preReload = _manager.CurrentSolution;
        ArmGatedReload(failReload: false);
        try
        {
            await _manager.ApplyProposedChangesAsync(
                new Dictionary<FilePathWrapper, string> { [_csprojPath] = UpdatedCsproj });
            await _reloadEntered.Task.WaitAsync(WaitBound);

            var read = _manager.GetCurrentSolutionAsync(CancellationToken.None);
            Assert.That(read.IsCompleted, Is.False,
                "a read while the reload is gated must wait for the load, not return the pre-reload solution");

            _gate.TrySetResult();
            var solution = await read;

            Assert.That(solution, Is.Not.SameAs(preReload), "the read must return the swapped-in solution");
            Assert.That(solution, Is.SameAs(_manager.CurrentSolution));
        }
        finally
        {
            _gate.TrySetResult();
        }
    }

    [Test]
    public async Task ReloadFailsWithOldWorkspaceKept_TerminalStatusLoaded_LastLoadFailureUnchanged()
    {
        await LoadFixtureSolutionAsync();

        // Prime a non-null LastLoadFailure with an explicit load that fails while the usable solution is kept.
        _manager.OpenSolutionOverride = (_, _, _) =>
            Task.FromException<Solution>(new InvalidOperationException("primed load failure"));
        var primeFailure = await Assert.CatchAsync(() => _manager.LoadSolutionAsync(_csprojPath, CancellationToken.None));
        Assert.That(primeFailure, Is.Not.Null, "the priming load must fail");
        var lastLoadFailureBefore = _manager.LoadState.LastLoadFailure;
        Assert.That(lastLoadFailureBefore, Is.Not.Null, "the priming failure must be recorded as LastLoadFailure");

        ArmGatedReload(failReload: true);
        try
        {
            await _manager.ApplyProposedChangesAsync(
                new Dictionary<FilePathWrapper, string> { [_csprojPath] = UpdatedCsproj });
            await _reloadEntered.Task.WaitAsync(WaitBound);

            _gate.TrySetResult();
            var status = await _manager.WaitForLoadAsync(WaitBound, CancellationToken.None);

            Assert.That(status, Is.EqualTo(SolutionLoadStatus.Loaded),
                "a reload that fails with the old workspace kept ends Loaded, not Failed");
            Assert.That(_manager.ReloadFailure, Is.Not.Null, "the reload failure must be recorded");
            Assert.That(_manager.ReloadFailure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.PostWriteReload));
            Assert.That(_manager.LoadState.LastLoadFailure, Is.EqualTo(lastLoadFailureBefore),
                "a background reload failure must not overwrite the last explicit load failure");
        }
        finally
        {
            _gate.TrySetResult();
        }
    }

    [Test]
    public async Task ApplyOutcomeReloadPending_TrueForCsprojWrite_FalseForCsWrite()
    {
        File.WriteAllText(_csprojPath, OriginalCsproj);
        var csPath = Path.Combine(_projectDir, "Foo.cs");
        const string originalCs = "public class Foo { }";
        await File.WriteAllTextAsync(csPath, originalCs);

        // No SolutionPath is set, so no background reload is fired; only the ReloadPending flag is under test.
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            _csprojPath,
            [("Foo.cs", originalCs, csPath)]);
        _manager.SetTestSolution(solution);
        var validationEngine = new ValidationEngine(_manager);
        var logger = NullLogger<IWorkspaceManager>.Instance;

        var csprojOutcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            validationEngine,
            _manager,
            logger,
            new Dictionary<FilePathWrapper, string> { [_csprojPath] = UpdatedCsproj },
            "test csproj write");

        Assert.That(csprojOutcome.ReloadPending, Is.True, "a .csproj write needed a full reload");

        var csOutcome = await ValidateAndApplyHelper.ValidateAndApplyAsync(
            validationEngine,
            _manager,
            logger,
            new Dictionary<FilePathWrapper, string> { [csPath] = "public class Foo { public int X { get; set; } }" },
            "test cs write");

        Assert.That(csOutcome.ReloadPending, Is.False, "an ordinary .cs write takes the per-document fold");
    }

    [Test]
    public async Task WritingCallReturnsWhileReloadIsStillGated()
    {
        await LoadFixtureSolutionAsync();
        ArmGatedReload(failReload: false);
        try
        {
            var applyTask = _manager.ApplyProposedChangesAsync(
                new Dictionary<FilePathWrapper, string> { [_csprojPath] = UpdatedCsproj });

            // The gate is still closed here: an apply that waited for the reload would not finish before the bound.
            var finished = await Task.WhenAny(applyTask, Task.Delay(WaitBound));

            Assert.That(finished, Is.SameAs(applyTask), "the writing call must not wait for the gated reload");
            Assert.That(applyTask.IsCompleted, Is.True);
            var result = await applyTask;
            Assert.That(result.NeedsFullReload, Is.True);

            await _reloadEntered.Task.WaitAsync(WaitBound);
            Assert.That(_manager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loading),
                "the reload that the writing call queued is still in progress");
        }
        finally
        {
            _gate.TrySetResult();
        }
    }

    // Loads the fixture csproj through the real load path with an ungated open, so the manager has a workspace and a
    // SolutionPath and the post-write reload is armed.
    private async Task LoadFixtureSolutionAsync()
    {
        File.WriteAllText(_csprojPath, OriginalCsproj);
        _manager.OpenSolutionOverride = (_, _, _) => Task.FromResult(BuildSolution());
        await _manager.LoadSolutionAsync(_csprojPath, CancellationToken.None);
    }

    // The reload's open announces itself, then waits for the gate; failReload makes it throw once the gate opens.
    private void ArmGatedReload(bool failReload)
    {
        _manager.OpenSolutionOverride = async (_, _, _) =>
        {
            _reloadEntered.TrySetResult();
            await _gate.Task;
            if (failReload)
            {
                throw new InvalidOperationException("gated reload failure");
            }

            return BuildSolution();
        };
    }

    private Solution BuildSolution() =>
        TestSolutionBuilder.CreateSolutionWithProject("TestProj", _csprojPath, []);
}
