using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Edge-gate lease (EdgeGateLease, plan_multi_file_movefile_and_namespace_change_tool Steps 20 and 23).
// The rows that need no workspace (AcquireAsync, Holder.Dispose) use a bare EdgeGateLease. The chokepoint rows T23.1-T23.5
// need the REAL PersistentWorkspaceManager.ApplyProposedChangesAsync, because the lease wait sits at the top of its try and
// a fake manager never reaches it. That path also needs a loaded solution, so these rows copy the ContosoOrders sample
// through TestSolutionFixture, the same way MoveTypeToFileTests does. The fixture is heavy (a temp copy plus an MSBuild
// load per test), which is why it is used only for the chokepoint rows.
[TestFixture]
public class EdgeGateLeaseTests
{
    // Upper bound on any wait that must finish. A wrong implementation fails here instead of hanging the run.
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(10);

    // Negative check only: a writer that must still be waiting is observed as not complete after this window.
    // Nothing waits for a result on this timer.
    private static readonly TimeSpan StillWaitingWindow = TimeSpan.FromMilliseconds(100);

    [SetUp]
    public void ResetIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearIdentity() => ToolCallContext.Set(null);

    // Runs the work on a thread-pool task whose ExecutionContext has flow suppressed. Call it AFTER the holder has been
    // acquired. AcquireAsync stores the fallback holder token in the caller's AsyncLocal, and a plain Task.Run started after
    // that would copy the token and pass through as if it were the holder. With flow suppressed the task starts with an empty
    // context, so its identity is null, which never matches the holder: it waits like any other writer.
    private static Task<T> StartFromOtherFlow<T>(Func<Task<T>> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    private static string Probe(int value) => $"public class EdgeGateProbe\r\n{{\r\n    public int X() => {value};\r\n}}\r\n";

    private static string WriteProbeFile(TestSolutionFixture fixture)
    {
        var projectFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).First();
        var file = Path.Combine(Path.GetDirectoryName(projectFile)!, "EdgeGateProbe.cs");
        File.WriteAllText(file, Probe(1));
        return file;
    }

    private static Dictionary<FilePathWrapper, string> Change(string file, int value) => new() { [file] = Probe(value) };

    private static async Task<PersistentWorkspaceManager> LoadManagerAsync(TestSolutionFixture fixture)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        await manager.LoadSolutionAsync(fixture.SolutionPath, null, TestSolutionFixture.LoadTimeout);
        return manager;
    }

    private static Finding? LeaseFinding(ApplyChangesResult result) =>
        result.Findings?.FirstOrDefault(f => f.Source == EdgeGateWaitResult.FindingSource);

    [Test]
    [CancelAfter(60000)]
    public async Task SecondWriter_WaitsWhileLeaseHeld_ThenProceedsOnTimeout()
    {
        using var fixture = new TestSolutionFixture();
        var file = WriteProbeFile(fixture);
        using var manager = await LoadManagerAsync(fixture);
        manager.Lease.MaxWait = TimeSpan.FromMilliseconds(300);
        using var holder = await manager.Lease.AcquireAsync(CancellationToken.None);

        var competing = StartFromOtherFlow(() => manager.ApplyProposedChangesAsync(Change(file, 3), retryCount: 0, validateChanges: false));
        await Task.Delay(StillWaitingWindow);
        Assert.That(competing.IsCompleted, Is.False, "A writer must keep waiting while another flow holds the lease, before MaxWait.");

        // Bounded by MaxWait (300 ms): the write then proceeds while the lease is still held.
        var result = await competing.WaitAsync(GuardTimeout);

        Assert.That(result.IsError, Is.False, result.Summary);
        Assert.That(await File.ReadAllTextAsync(file), Does.Contain("=> 3"), "The waiting write must reach disk after the bound.");
        Assert.That(LeaseFinding(result), Is.Not.Null, "A writer that waited must report an EdgeGateLease finding.");
        Assert.That(LeaseFinding(result)!.Severity, Is.EqualTo(FindingSeverity.Caution), "A writer that proceeded at the bound reports Caution.");
    }

    [Test]
    [CancelAfter(60000)]
    public async Task Holder_PassesThroughItsOwnApply()
    {
        using var fixture = new TestSolutionFixture();
        var file = WriteProbeFile(fixture);
        using var manager = await LoadManagerAsync(fixture);
        // MaxWait is left at its default (30 s). A wrong implementation would wait the full bound; the guard fails first.
        Assert.That(manager.Lease.MaxWait, Is.GreaterThan(GuardTimeout), "Test setup: MaxWait must exceed the guard so a wrong wait is caught by the guard.");
        using var holder = await manager.Lease.AcquireAsync(CancellationToken.None);

        // Same flow as the acquirer (no StartFromOtherFlow): the flow token equals the holder identity, so there is no wait.
        var result = await manager.ApplyProposedChangesAsync(Change(file, 2), retryCount: 0, validateChanges: false).WaitAsync(GuardTimeout);

        Assert.That(result.IsError, Is.False, result.Summary);
        Assert.That(manager.Lease.IsHeld, Is.True, "The holder's own apply must complete while the lease is still held.");
        Assert.That(LeaseFinding(result), Is.Null, "The holder passes through without waiting, so there is no EdgeGateLease finding.");
        Assert.That(await File.ReadAllTextAsync(file), Does.Contain("=> 2"));
    }

    [Test]
    [CancelAfter(60000)]
    public async Task SecondWriter_ProceedsAtOnceWhenLeaseReleased()
    {
        using var fixture = new TestSolutionFixture();
        var file = WriteProbeFile(fixture);
        using var manager = await LoadManagerAsync(fixture);
        Assert.That(manager.Lease.MaxWait, Is.GreaterThan(GuardTimeout), "Test setup: MaxWait must exceed the guard so an unreleased wait is caught by the guard.");
        using var holder = await manager.Lease.AcquireAsync(CancellationToken.None);

        var competing = StartFromOtherFlow(() => manager.ApplyProposedChangesAsync(Change(file, 3), retryCount: 0, validateChanges: false));
        await Task.Delay(StillWaitingWindow);
        Assert.That(competing.IsCompleted, Is.False, "A writer must keep waiting while another flow holds the lease.");

        holder.Dispose();
        var result = await competing.WaitAsync(GuardTimeout);

        Assert.That(result.IsError, Is.False, result.Summary);
        Assert.That(LeaseFinding(result), Is.Not.Null, "A writer that waited must report an EdgeGateLease finding.");
        Assert.That(LeaseFinding(result)!.Severity, Is.EqualTo(FindingSeverity.Info), "Released before MaxWait: the finding reports a release, not a timeout.");
        Assert.That(manager.Lease.IsHeld, Is.False);
    }

    [Test]
    [CancelAfter(60000)]
    public async Task LeaseDoesNotHoldSolutionLock_ReadsProceedWhileHeld()
    {
        using var fixture = new TestSolutionFixture();
        WriteProbeFile(fixture);
        using var manager = await LoadManagerAsync(fixture);
        using var holder = await manager.Lease.AcquireAsync(CancellationToken.None);

        // A read from another flow. The read path never consults the lease, so it returns while the lease is held.
        var solution = await StartFromOtherFlow(() => manager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None)).WaitAsync(GuardTimeout);

        Assert.That(solution, Is.Not.Null);
        Assert.That(manager.Lease.IsHeld, Is.True, "Reading must neither release nor wait on the lease.");
    }

    [Test]
    [CancelAfter(60000)]
    public async Task NoLeaseHeld_ApplyHasNoWait()
    {
        using var fixture = new TestSolutionFixture();
        var file = WriteProbeFile(fixture);
        using var manager = await LoadManagerAsync(fixture);
        Assert.That(manager.Lease.IsHeld, Is.False, "Test setup: no lease is held.");

        var result = await manager.ApplyProposedChangesAsync(Change(file, 2), retryCount: 0, validateChanges: false).WaitAsync(GuardTimeout);

        Assert.That(result.IsError, Is.False, result.Summary);
        Assert.That(LeaseFinding(result), Is.Null, "With no lease held the apply does not wait and reports no EdgeGateLease finding.");
    }

    [Test]
    [CancelAfter(60000)]
    public async Task AcquireAsync_SecondAcquirer_WaitsForRelease()
    {
        var lease = new EdgeGateLease();
        using var first = await lease.AcquireAsync(CancellationToken.None);

        var second = StartFromOtherFlow(() => lease.AcquireAsync(CancellationToken.None));
        await Task.Delay(StillWaitingWindow);
        Assert.That(second.IsCompleted, Is.False, "An acquirer from another flow must wait for the holder to release.");

        first.Dispose();
        using var holder = await second.WaitAsync(GuardTimeout);

        Assert.That(holder.Owning, Is.True, "The second acquirer gets the owning holder once the first releases.");
        Assert.That(lease.IsHeld, Is.True);
    }

    [Test]
    [CancelAfter(60000)]
    public async Task Holder_Dispose_IsIdempotent()
    {
        var lease = new EdgeGateLease();
        var stale = await lease.AcquireAsync(CancellationToken.None);
        stale.Dispose();

        using var current = await lease.AcquireAsync(CancellationToken.None);
        stale.Dispose();

        Assert.That(lease.IsHeld, Is.True, "Disposing an already released holder must not release a newer holder.");
        Assert.That(current.Owning, Is.True);

        current.Dispose();
        Assert.That(lease.IsHeld, Is.False);
    }
}
