// Regression tests for solution load status, WaitForLoadAsync, the wait in GetCurrentSolutionAsync and
// ResolveFromWire, and the timeout overload that cancels a load (plan Step A2a/A2b, covered by Step A3).
//
// Timing assumptions: the mid-load cases (status Loading while a load is running, a 1 ms wait that is still
// Loading) assume MSBuild takes longer than a few milliseconds to open the fixture solution. A real load
// takes seconds, so this holds in practice. The 1 ms timeout case is deterministic because the token is
// already cancelled when OpenSolutionAsync starts.

using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

#pragma warning disable CS0618, CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
public class SolutionLoadStatusTests
{
    [Test]
    public void SolutionLoadStatus_BeforeAnyLoad_IsNotLoaded()
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        Assert.That(workspaceManager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.NotLoaded));
    }

    [Test]
    public async Task SolutionLoadStatus_WhileLoadRunning_IsLoadingThenLoaded()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        var load = workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        Assert.That(workspaceManager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loading));
        await load;
        Assert.That(workspaceManager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loaded));
    }

    [Test]
    public async Task GetCurrentSolutionAsync_MidLoad_WaitsAndReturnsSolution()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        var load = workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var solution = await workspaceManager.GetCurrentSolutionAsync(CancellationToken.None);
        await load;

        Assert.That(solution.ProjectIds.Count, Is.GreaterThan(0), "the call must return the loaded solution, not throw NotLoaded");
        Assert.That(workspaceManager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loaded));
    }

    [Test]
    public async Task WaitForLoadAsync_NoLoad_ReturnsCurrentStatusImmediately()
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        var stopwatch = Stopwatch.StartNew();
        var status = await workspaceManager.WaitForLoadAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        stopwatch.Stop();

        Assert.That(status, Is.EqualTo(SolutionLoadStatus.NotLoaded));
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "no load is running, so the wait must not block for its timeout");
    }

    [Test]
    public async Task WaitForLoadAsync_ShortTimeoutMidLoad_ReturnsLoadingAndLoadStillFinishes()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        var load = workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var status = await workspaceManager.WaitForLoadAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.That(status, Is.EqualTo(SolutionLoadStatus.Loading), "a wait that times out reports Loading");
        await load;
        Assert.That(workspaceManager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loaded), "a wait timeout must not cancel the load");
    }

    [Test]
    public async Task ResolveFromWire_MidLoad_WaitsAndResolves()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var relativePath = Path.GetRelativePath(fixture.SolutionDirectory, targetFile);

        var load = workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        // ResolveFromWire blocks synchronously by design, so run it off the test's own context.
        var wrapper = await Task.Run(() => workspaceManager.ResolveFromWire(relativePath));
        await load;

        Assert.That(wrapper.Validated, Is.True, "a mid-load path must resolve once the load finishes");
        Assert.That(wrapper.FailureReason, Is.Not.EqualTo(FilePathFailureReason.NoSolutionLoaded));
    }

    [Test]
    public async Task LoadSolutionAsync_TimeoutElapsed_CancelsAndThrowsSolutionLoadTimeoutException()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        await Assert.ThrowsAsync<SolutionLoadTimeoutException>(
            async () => await workspaceManager.LoadSolutionAsync(fixture.SolutionPath, null, TimeSpan.FromMilliseconds(1), CancellationToken.None));

        Assert.That(workspaceManager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Failed));
        Assert.That(workspaceManager.LoadState.LastLoadFailure, Does.Contain("timeoutSeconds"));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SolutionNotLoadedException>(
            async () => await workspaceManager.GetCurrentSolutionAsync(CancellationToken.None));
        stopwatch.Stop();
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "a failed load must not make GetCurrentSolutionAsync hang");
    }

    [Test]
    public async Task LoadSolutionAsync_TimeoutOnReloadOfLoadedSolution_KeepsLoadedStatus()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        await Assert.ThrowsAsync<SolutionLoadTimeoutException>(
            async () => await workspaceManager.LoadSolutionAsync(fixture.SolutionPath, null, TimeSpan.FromMilliseconds(1), CancellationToken.None));

        Assert.That(workspaceManager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Loaded), "a failed reload keeps the earlier usable snapshot reported as Loaded");
        var solution = await workspaceManager.GetCurrentSolutionAsync(CancellationToken.None);
        Assert.That(solution.ProjectIds.Count, Is.GreaterThan(0));
    }

    [Test]
    public async Task LoadSolutionAsync_BadPath_StatusFailedWithFailureText()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var missingPath = Path.Combine(fixture.SolutionDirectory, "DoesNotExist", "Missing.sln");

        var thrown = await Assert.CatchAsync(async () => await workspaceManager.LoadSolutionAsync(missingPath));

        Assert.That(thrown, Is.Not.Null, "a missing solution must surface as a load failure");
        Assert.That(workspaceManager.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.Failed));
        Assert.That(workspaceManager.LoadState.LastLoadFailure, Is.Not.Null.And.Not.Empty);
    }
}
