using NUnit.Framework;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Cross-process write lock (CrossProcessWriteLock): a FileShare.None lock file plus a holder sidecar under
// <root>/.roslynsentinel. Every test gets its own temp root, never the repo root, and disposes what it acquires.
// Also covers WriteRefusal's WriteLockBusy mapping.
[TestFixture]
public class CrossProcessWriteLockTests
{
    private string _root = string.Empty;

    [SetUp]
    public void NewRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void DeleteRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string LockPath => Path.Combine(_root, ".roslynsentinel", "write.lock");

    private string SidecarPath => Path.Combine(_root, ".roslynsentinel", "write.lock.holder.json");

    private static WriteLockHolder Holder(string agentId) =>
        new(SessionId: "session-test", AgentId: agentId, ToolName: "WriteFile", Reason: "unit test", SinceUtc: DateTime.UtcNow);

    [Test]
    [CancelAfter(30000)]
    public async Task Acquire_Free_ReturnsLeaseAndCreatesLockFileAndSidecar()
    {
        var result = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-A"), TimeSpan.FromSeconds(1), CancellationToken.None);
        await using var lease = result.Lease!;

        Assert.That(result.Acquired, Is.True);
        Assert.That(File.Exists(LockPath), Is.True);
        Assert.That(File.Exists(SidecarPath), Is.True);
        Assert.That(File.ReadAllText(SidecarPath), Does.Contain("agent-A"));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task SecondAcquire_WhileHeld_TimesOutBusyNamingHolder()
    {
        var first = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-A"), TimeSpan.FromSeconds(1), CancellationToken.None);
        await using var lease = first.Lease!;

        var second = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-B"), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.That(second.Acquired, Is.False);
        Assert.That(second.Lease, Is.Null);
        Assert.That(second.BusyHolder, Is.Not.Null);
        Assert.That(second.BusyHolder!.AgentId, Is.EqualTo("agent-A"));
        Assert.That(second.BusyMessage(), Does.Contain("agent-A").And.Contain("WriteFile"));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Dispose_ReleasesLock_SecondAcquireSucceeds()
    {
        var first = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-A"), TimeSpan.FromSeconds(1), CancellationToken.None);
        await first.Lease!.DisposeAsync();

        Assert.That(File.Exists(SidecarPath), Is.False);

        var second = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-B"), TimeSpan.FromSeconds(1), CancellationToken.None);
        await using var lease = second.Lease!;

        Assert.That(second.Acquired, Is.True);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task DisposeTwice_IsIdempotent()
    {
        var first = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-A"), TimeSpan.FromSeconds(1), CancellationToken.None);
        var lease = first.Lease!;

        await Assert.DoesNotThrowAsync(async () =>
        {
            await lease.DisposeAsync();
            await lease.DisposeAsync();
        });

        var second = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-B"), TimeSpan.FromSeconds(1), CancellationToken.None);
        await using var secondLease = second.Lease!;

        Assert.That(second.Acquired, Is.True);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task ReleaseOnException_LockIsFreeAfterThrowingHolder()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var held = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-A"), TimeSpan.FromSeconds(1), CancellationToken.None);
            await using var lease = held.Lease!;
            throw new InvalidOperationException("simulated failure while holding the lock");
        });

        var second = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-B"), TimeSpan.FromSeconds(1), CancellationToken.None);
        await using var secondLease = second.Lease!;

        Assert.That(second.Acquired, Is.True);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Waiter_AcquiresAfterHolderReleases()
    {
        var first = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-A"), TimeSpan.FromSeconds(1), CancellationToken.None);
        var firstLease = first.Lease!;

        var waiter = CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-B"), TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(150);
        await firstLease.DisposeAsync();

        var second = await waiter;
        await using var secondLease = second.Lease!;

        Assert.That(second.Acquired, Is.True);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Cancellation_ThrowsOperationCanceled()
    {
        var first = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-A"), TimeSpan.FromSeconds(1), CancellationToken.None);
        await using var lease = first.Lease!;

        using var cts = new CancellationTokenSource();
        var waiter = CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-B"), TimeSpan.FromSeconds(5), cts.Token);
        cts.CancelAfter(150);

        // Task.Delay surfaces TaskCanceledException, a subtype of the OperationCanceledException the contract names;
        // NUnit's ThrowsAsync<T> matches the exact type, so this checks the instance instead.
        var caught = await Assert.CatchAsync(async () => await waiter);
        Assert.That(caught, Is.InstanceOf<OperationCanceledException>());
    }

    [Test]
    [CancelAfter(30000)]
    public async Task MissingSidecar_BusyMessageSaysHolderUnknown()
    {
        var first = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-A"), TimeSpan.FromSeconds(1), CancellationToken.None);
        await using var lease = first.Lease!;
        File.Delete(SidecarPath);

        var second = await CrossProcessWriteLock.AcquireAsync(_root, Holder("agent-B"), TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.That(second.Acquired, Is.False);
        Assert.That(second.BusyHolder, Is.Null);
        Assert.That(second.BusyMessage(), Does.Contain("holder unknown"));
    }

    [Test]
    public void WriteRefusal_TryMap_WriteLockBusy_MapsToWriteLockBusyCode()
    {
        var result = new ApplyChangesResult(
            IsError: true,
            SucceededFiles: new List<string>(),
            FailedFiles: new Dictionary<FilePathWrapper, string>(),
            Summary: "WriteLockBusy",
            RefusalCode: ToolErrorCode.WriteLockBusy);

        var mapped = WriteRefusal.TryMap("WriteFile", result, out var error);

        Assert.That(mapped, Is.True);
        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.WriteLockBusy));
    }
}
