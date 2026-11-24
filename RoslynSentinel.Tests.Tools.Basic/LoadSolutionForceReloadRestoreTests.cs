// LoadSolution(forceReload) restores the solution before loading (punchlist #24). The seam-based tests pass a fake
// restore delegate so no dotnet process is spawned; they load the ContosoOrders fixture for real (seconds), like
// LoadSolutionWaitTests. The ordering assertion compares LoadState.LastLoadedUtc seen inside the fake with the value
// after the call: the fake runs before the reload, so it still sees the first load's timestamp.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
public class LoadSolutionForceReloadRestoreTests
{
    private static SolutionRestoreResult Ok() => new(true, true, false, 0, "", TimeSpan.FromSeconds(1.5));

    [Test]
    public async Task LoadSolution_ForceReload_RestoresBeforeReloadingAndSaysSo()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var firstLoadedAt = workspaceManager.LoadState.LastLoadedUtc;
        var calls = new List<(string Path, DateTime? LoadedAtDuringRestore)>();
        var tool = CreateImpl(workspaceManager, (path, _, _) =>
        {
            calls.Add((path, workspaceManager.LoadState.LastLoadedUtc));
            return Task.FromResult(Ok());
        });

        var result = await tool.LoadSolution(new ToolCallReason("forces a reload so the restore runs first"), fixture.SolutionPath, forceReload: true, timeoutSeconds: 120);

        Assert.That(result.IsError, Is.False);
        Assert.That(calls, Has.Count.EqualTo(1), "forceReload must restore exactly once");
        Assert.That(calls[0].Path, Is.EqualTo(fixture.SolutionPath));
        Assert.That(calls[0].LoadedAtDuringRestore, Is.EqualTo(firstLoadedAt), "the restore must run before the reload replaces the workspace");
        Assert.That(workspaceManager.LoadState.LastLoadedUtc, Is.GreaterThan(firstLoadedAt), "the reload must still happen after the restore");
        Assert.That(result.SuccessData?.ToString(), Does.Contain("reloaded").And.Contain("Restored the solution first"));
    }

    [Test]
    public async Task LoadSolution_FirstLoadOfUnrestoredSolutionRestoresOnceAndAlreadyLoadedWithoutForceReloadNeverRestores()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var restoreCalls = 0;
        var tool = CreateImpl(workspaceManager, (_, _, _) =>
        {
            restoreCalls++;
            return Task.FromResult(Ok());
        });

        var first = await tool.LoadSolution(new ToolCallReason("first load without forceReload"), fixture.SolutionPath, timeoutSeconds: 120);
        var again = await tool.LoadSolution(new ToolCallReason("repeat load without forceReload"), fixture.SolutionPath, timeoutSeconds: 120);

        Assert.That(first.IsError, Is.False);
        Assert.That(again.IsError, Is.False);
        Assert.That(again.SuccessData?.ToString(), Does.Contain("already loaded"));
        // The TestSolutionFixture copy has no obj/, so the first load restores automatically; the repeat is the already-loaded no-op.
        Assert.That(restoreCalls, Is.EqualTo(1), "only the first load of an unrestored solution restores; the already-loaded no-op must not");
    }

    [Test]
    public async Task LoadSolution_ForceReloadRestoreFails_StillLoadsAndReportsTheFact()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var firstLoadedAt = workspaceManager.LoadState.LastLoadedUtc;
        var tool = CreateImpl(workspaceManager, (_, _, _) =>
            Task.FromResult(new SolutionRestoreResult(false, true, false, 1, "error NU1101: Unable to find package Foo", TimeSpan.FromSeconds(2))));

        var result = await tool.LoadSolution(new ToolCallReason("restore fails but the load goes ahead"), fixture.SolutionPath, forceReload: true, timeoutSeconds: 120);

        Assert.That(result.IsError, Is.False, "a restore failure must not block the load");
        Assert.That(workspaceManager.LoadState.LastLoadedUtc, Is.GreaterThan(firstLoadedAt));
        var text = result.SuccessData?.ToString();
        Assert.That(text, Does.Contain("exit code 1").And.Contain("NU1101").And.Contain("existing restore state"));
    }

    [Test]
    public async Task LoadSolution_ForceReloadRestoreThrows_StillLoadsWithoutRawExceptionText()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var tool = CreateImpl(workspaceManager, (_, _, _) => throw new InvalidOperationException("secret internal detail C:\\internal\\path"));

        var result = await tool.LoadSolution(new ToolCallReason("restore throws but the load goes ahead"), fixture.SolutionPath, forceReload: true, timeoutSeconds: 120);

        Assert.That(result.IsError, Is.False, "an unexpected restore exception must not block the load");
        var text = result.SuccessData?.ToString();
        Assert.That(text, Does.Contain("could not run"));
        Assert.That(text, Does.Not.Contain("secret internal detail").And.Not.Contain("C:\\internal"));
    }

    [Test]
    public async Task LoadSolution_ForceReloadCancelledDuringRestore_DoesNotLoad()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var firstLoadedAt = workspaceManager.LoadState.LastLoadedUtc;
        using var cts = new CancellationTokenSource();
        CancellationToken? seen = null;
        var tool = CreateImpl(workspaceManager, (_, _, token) =>
        {
            seen = token;
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Ok());
        });

        var result = await tool.LoadSolution(new ToolCallReason("cancelled while restoring"), fixture.SolutionPath, forceReload: true, timeoutSeconds: 120, cancellationToken: cts.Token);

        Assert.That(seen, Is.EqualTo(cts.Token), "the caller's token must reach the restore");
        Assert.That(result.IsError, Is.True, "a cancelled call is not reported as a success");
        Assert.That(workspaceManager.LoadState.LastLoadedUtc, Is.EqualTo(firstLoadedAt), "no reload may start after cancellation");
    }

    [Test]
    public void DescribeRestoreResult_EachOutcome_StatesFactsWithoutPolicyWords()
    {
        var timeout = TimeSpan.FromMinutes(5);
        var notStarted = WorkspaceProjectManagementImpl.DescribeRestoreResult(new SolutionRestoreResult(false, false, false, null, "", TimeSpan.Zero), timeout);
        var timedOut = WorkspaceProjectManagementImpl.DescribeRestoreResult(new SolutionRestoreResult(false, true, true, null, "", TimeSpan.FromMinutes(5)), timeout);
        var failed = WorkspaceProjectManagementImpl.DescribeRestoreResult(new SolutionRestoreResult(false, true, false, 1, "", TimeSpan.FromSeconds(3)), timeout);
        var succeeded = WorkspaceProjectManagementImpl.DescribeRestoreResult(Ok(), timeout);

        Assert.That(notStarted, Does.Contain("could not be started"));
        Assert.That(timedOut, Does.Contain("300 s").And.Contain("terminated"));
        Assert.That(failed, Does.Contain("exit code 1").And.Not.Contain("Last restore output"));
        Assert.That(succeeded, Does.Contain("Restored the solution first").And.Contain("1.5 s"));
        foreach (var text in new[] { notStarted, timedOut, failed, succeeded })
        {
            Assert.That(text, Does.Not.Contain("MUST").And.Not.Contain("stop").And.Not.Contain("do not"));
        }
    }

    [Test]
    public async Task LoadSolution_FirstLoadRestoreFails_StillLoadsAndAppendsTheForceReloadHintWithoutBuildAdvice()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tool = CreateImpl(workspaceManager, (_, _, _) =>
            Task.FromResult(new SolutionRestoreResult(false, true, false, 1, "", TimeSpan.FromSeconds(2))));

        var result = await tool.LoadSolution(new ToolCallReason("first load of an unrestored solution, restore fails"), fixture.SolutionPath, timeoutSeconds: 120);

        Assert.That(result.IsError, Is.False, "a restore failure must not block the load");
        var text = result.SuccessData?.ToString();
        Assert.That(text, Does.Contain("ended with exit code 1").And.Contain("the existing restore state was kept"));
        Assert.That(text, Does.Contain("Unrestored project(s)").And.Contain("forceReload"));
        Assert.That(text, Does.Not.Contain("Build(").And.Not.Contain("dotnet restore"));
    }

    [Test]
    public async Task LoadSolution_ProjectsAlreadyRestored_NoForceReload_NeverRestores()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        foreach (var csproj in SolutionProjectPaths.Read(fixture.SolutionPath))
        {
            var objDir = Path.Combine(Path.GetDirectoryName(csproj)!, "obj");
            Directory.CreateDirectory(objDir);
            var assets = Path.Combine(objDir, "project.assets.json");
            File.WriteAllText(assets, "{}");
            File.SetLastWriteTimeUtc(assets, DateTime.UtcNow.AddMinutes(5));
        }
        var restoreCalls = 0;
        var tool = CreateImpl(workspaceManager, (_, _, _) =>
        {
            restoreCalls++;
            return Task.FromResult(Ok());
        });

        var result = await tool.LoadSolution(new ToolCallReason("load with every project already restored"), fixture.SolutionPath, timeoutSeconds: 120);

        Assert.That(result.IsError, Is.False);
        Assert.That(restoreCalls, Is.EqualTo(0), "a solution whose projects all have a current assets file must not restore");
        Assert.That(result.SuccessData?.ToString(), Does.Not.Contain("Restored"));
    }

    [Test]
    public async Task AmbientDefault_UnderATestAssembly_IsTheNoOpRestore()
    {
        Assert.That(SolutionRestore.AmbientRestore, Is.Not.Null);
        var restore = SolutionRestore.ResolveDefault();
        Assert.That(restore.Method.Name, Is.EqualTo(nameof(SolutionRestore.NoOpRestoreForTests)));

        var result = await restore(Path.Combine(Path.GetTempPath(), "does-not-exist.sln"), TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Duration, Is.EqualTo(TimeSpan.Zero), "the test default spawns no process, so it takes no time");
    }

    private static WorkspaceProjectManagementImpl CreateImpl(PersistentWorkspaceManager workspaceManager, Func<string, TimeSpan, CancellationToken, Task<SolutionRestoreResult>> restore)
    {
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        return new WorkspaceProjectManagementImpl(
            workspaceManager, Battery.Basic.TestEngines.StructuralRefinementEngine(workspaceManager, config), NullLogger<WorkspaceProjectManagementImpl>.Instance, Battery.Basic.TestEngines.SolutionStructureEngine(workspaceManager, config), restore);
    }
}
