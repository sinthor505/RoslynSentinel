using System.Collections.Concurrent;
using System.Diagnostics;
using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The gitBackend block of McpServerStatus and the <see cref="GitBackendWarmup"/> behind it: the normal environment reports
/// Libgit2, a failing native probe degrades to CliFallback without faulting and logs once, a slow probe makes the status
/// report "warming" within its bound, and the repo probe never blocks its starter.
/// </summary>
[TestFixture]
// The slow-probe tests hold a thread-pool thread until released and one waits out the full 2 s status bound; they are
// not order- or parallelism-sensitive (each owns its warmup instance), the attribute only keeps their timing assertions
// away from a saturated pool on full parallel runs.
[NonParallelizable]
public class McpServerStatusGitBackendTests
{
    private PersistentWorkspaceManager _manager = null!;

    [SetUp]
    public void SetUp() => _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<IWorkspaceManager>.Instance);

    [TearDown]
    public void TearDown() => _manager.Dispose();

    private static FallbackGitReader NewReader() => new(new Libgit2GitReader(), new CliGitReader(), _ => { });

    private GitBackendSnapshot StatusBlock(GitBackendWarmup? warmup)
    {
        var none = new HashSet<string>();
        var tools = new ServerStatusTools(
            _manager,
            new ActiveToolSurface("test", none, none, new HashSet<string>(), new HashSet<string>(), null),
            new StoppedByScriptMarker(WasFound: false, Details: null),
            gitBackend: warmup);

        var result = (SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatusTool("test reason", McpServerStatusToolListing.none);
        return result.SuccessData!.GitBackend!;
    }

    [Test]
    public void GitBackendBlock_InTheNormalEnvironment_ReportsLibgit2WithAVersion()
    {
        // The shared instance starts lazily; give a cold native load room beyond the status wait so this is not a race.
        GitBackendWarmup.Shared.Start();
        Assert.That(GitBackendWarmup.Shared.NativeStatus.Wait(TimeSpan.FromSeconds(30)), Is.True, "native probe did not finish");

        var block = StatusBlock(warmup: null);

        Assert.That(block.ActiveBackend, Is.EqualTo("Libgit2"), "libgit2 natives did not load in the test environment: " + block.NativeLoad);
        Assert.That(block.NativeLoad, Is.EqualTo("ok"));
        Assert.That(block.Libgit2Version, Is.Not.Null.And.Not.Empty);
        Assert.That(block.RepoProbe, Is.Not.Empty);
    }

    [Test]
    public void FailingNativeProbe_ReportsCliFallback_LogsOnce_AndNeverFaults()
    {
        var logged = new ConcurrentQueue<string>();
        var warmup = new GitBackendWarmup(
            () => throw new DllNotFoundException("git2-native missing"),
            _ => "ok",
            NewReader(),
            logged.Enqueue);

        warmup.Start();
        warmup.Start();
        Assert.That(warmup.NativeStatus.Wait(TimeSpan.FromSeconds(10)), Is.True);
        warmup.StartRepoProbe("C:\\anywhere");
        var first = warmup.Report(TimeSpan.FromSeconds(5));
        var second = warmup.Report(TimeSpan.FromSeconds(5));

        Assert.That(warmup.NativeStatus.IsFaulted, Is.False, "the native status task must never fault");
        Assert.That(warmup.RepoStatus!.IsFaulted, Is.False, "the repo status task must never fault");
        Assert.That(warmup.NativeStatus.Result.Backend, Is.EqualTo("CliFallback"));
        Assert.That(first.ActiveBackend, Is.EqualTo("CliFallback"));
        Assert.That(first.Libgit2Version, Is.Null);
        Assert.That(first.NativeLoad, Does.StartWith("failed: DllNotFoundException: git2-native missing"));
        Assert.That(first.RepoProbe, Does.StartWith("fallback:"));
        Assert.That(second, Is.EqualTo(first));
        Assert.That(logged, Has.Count.EqualTo(1), "a native-load failure is logged exactly once: " + string.Join(" | ", logged));
        Assert.That(logged.Single(), Does.Contain("DllNotFoundException"));
    }

    [Test]
    public void RepoProbeFailure_IsRecordedAsFallback_AndNeverFaults()
    {
        var logged = new ConcurrentQueue<string>();
        var warmup = new GitBackendWarmup(() => "v1", _ => throw new NotSupportedException("extension x"), NewReader(), logged.Enqueue);

        warmup.StartRepoProbe("C:\\anywhere");
        warmup.StartRepoProbe("C:\\anywhere");
        var block = warmup.Report(TimeSpan.FromSeconds(5));

        Assert.That(warmup.RepoStatus!.IsFaulted, Is.False);
        Assert.That(block.ActiveBackend, Is.EqualTo("Libgit2"));
        Assert.That(block.RepoProbe, Is.EqualTo("fallback: NotSupportedException: extension x"));
        Assert.That(logged, Has.Count.LessThanOrEqualTo(1), "one log line per exception type: " + string.Join(" | ", logged));
    }

    [Test]
    public void SlowNativeProbe_StatusReturnsWarmingWithinTheBound_ThenTheRealResult()
    {
        var gate = new TaskCompletionSource();
        var warmup = new GitBackendWarmup(() => { gate.Task.Wait(); return "v-slow"; }, _ => "ok", NewReader());
        try
        {
            warmup.Start();
            var timer = Stopwatch.StartNew();

            var block = StatusBlock(warmup);

            timer.Stop();
            Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)), "McpServerStatus must not block past its bound");
            Assert.That(timer.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1)), "the status waits for the warmup before it gives up");
            Assert.That(block.ActiveBackend, Is.EqualTo("warming"));
            Assert.That(block.NativeLoad, Is.EqualTo("warming"));
            Assert.That(block.Libgit2Version, Is.Null);
        }
        finally
        {
            gate.SetResult();
        }

        var done = warmup.Report(TimeSpan.FromSeconds(5));
        Assert.That(done.ActiveBackend, Is.EqualTo("Libgit2"));
        Assert.That(done.Libgit2Version, Is.EqualTo("v-slow"));
    }

    [Test]
    public void SlowRepoProbe_NeverBlocksItsStarter_AndReportsWarmingUntilDone()
    {
        // Starting the repo probe is what PersistentWorkspaceManager.LoadSolutionAsync does after a successful load, so a
        // starter that returns at once means the load never waits for it. The manager hook itself calls the process-wide
        // instance (not injectable), so that wiring is asserted by design: the call is a plain StartRepoProbe, nothing awaits it.
        var gate = new TaskCompletionSource();
        var warmup = new GitBackendWarmup(() => "v1", _ => { gate.Task.Wait(); return "ok"; }, NewReader());
        try
        {
            var timer = Stopwatch.StartNew();
            warmup.StartRepoProbe("C:\\anywhere");
            timer.Stop();

            Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(1)), "StartRepoProbe must return without waiting for the probe");
            Assert.That(warmup.Report(TimeSpan.Zero).RepoProbe, Is.EqualTo("warming"));
        }
        finally
        {
            gate.SetResult();
        }

        Assert.That(warmup.Report(TimeSpan.FromSeconds(5)).RepoProbe, Is.EqualTo("ok"));
    }

    [Test]
    public void RepoProbe_BeforeAnySolutionLoad_ReportsNotStarted()
    {
        var warmup = new GitBackendWarmup(() => "v1", _ => "ok", NewReader());

        Assert.That(warmup.Report(TimeSpan.FromSeconds(5)).RepoProbe, Is.EqualTo(GitBackendWarmup.RepoNotStarted));
    }

    [Test]
    public void GitBackendBlock_ShowsTheSolutionRepositorysShortHashLength_OrNullUntilProbed()
    {
        var lengths = new ShortHashLengths((_, _) => 9);
        var warmup = new GitBackendWarmup(() => "v1", _ => "ok", NewReader(), log: null, lengths);

        var before = StatusBlock(warmup).ShortHashLength;
        lengths.Refresh(Path.Combine(Path.GetTempPath(), "rs-status-shl", ".git"), "wd", TimeSpan.FromSeconds(1));
        var after = StatusBlock(warmup).ShortHashLength;

        Assert.That(before, Is.Null);
        Assert.That(after, Is.EqualTo(9));
    }

    [Test]
    public void EachStartRepoProbe_ReplacesTheCapturedTask()
    {
        var warmup = new GitBackendWarmup(() => "v1", dir => dir, NewReader());

        warmup.StartRepoProbe("first");
        var firstTask = warmup.RepoStatus;
        warmup.StartRepoProbe("second");

        Assert.That(warmup.RepoStatus, Is.Not.SameAs(firstTask));
        Assert.That(warmup.Report(TimeSpan.FromSeconds(5)).RepoProbe, Is.EqualTo("second"));
    }
}
