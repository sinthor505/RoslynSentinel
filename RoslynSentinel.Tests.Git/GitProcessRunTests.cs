using RoslynSentinel.Common;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// <see cref="GitProcess.Run"/> / <see cref="GitProcess.RunAsync"/>: completion, byte-exact stdout, prefix args, start
/// failure, timeout versus caller cancellation. No repository is needed (every command runs in the temp directory).
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// The two hang tests (timeout, mid-run cancel) depend on a shell alias (`!sleep`), which needs git's bundled sh (Git for Windows).
[TestFixture]
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitProcessRunTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    private static readonly string Cwd = Path.GetTempPath();

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }
    }

    [Test]
    public async Task RunAsync_GitVersion_Completes()
    {
        var run = await GitProcess.RunAsync(Cwd, ["--version"], Generous);

        Assert.That(run.Outcome, Is.EqualTo(GitRunOutcome.Completed), run.FailureDetail);
        Assert.That(run.ExitCode, Is.EqualTo(0));
        Assert.That(run.StdOut, Does.StartWith("git version"));
        Assert.That(run.StdOutBytes, Is.Empty);
    }

    [Test]
    public void Run_GitVersion_Completes()
    {
        var run = GitProcess.Run(Cwd, ["--version"], Generous);

        Assert.That(run.Outcome, Is.EqualTo(GitRunOutcome.Completed), run.FailureDetail);
        Assert.That(run.StdOut, Does.StartWith("git version"));
    }

    [Test]
    public async Task RunAsync_BytesMode_ReturnsRawStdoutBytes()
    {
        var run = await GitProcess.RunAsync(Cwd, ["--version"], Generous, options: new GitRunOptions { StdOutAsBytes = true });

        Assert.That(run.Outcome, Is.EqualTo(GitRunOutcome.Completed), run.FailureDetail);
        Assert.That(run.StdOut, Is.Empty);
        Assert.That(System.Text.Encoding.ASCII.GetString(run.StdOutBytes), Does.StartWith("git version"));
    }

    [Test]
    public async Task RunAsync_PrefixArgs_PrecedeTheCallerArgs()
    {
        var run = await GitProcess.RunAsync(
            Cwd,
            ["config", "--get", "rs.probe"],
            Generous,
            options: new GitRunOptions { PrefixArgs = ["-c", "rs.probe=hello"] });

        Assert.That(run.Outcome, Is.EqualTo(GitRunOutcome.Completed), run.FailureDetail);
        Assert.That(run.StdOut.Trim(), Is.EqualTo("hello"));
    }

    [Test]
    public async Task RunAsync_NonZeroExit_IsStillCompleted()
    {
        var run = await GitProcess.RunAsync(Cwd, ["config", "--get", "rs.no-such-key"], Generous);

        Assert.That(run.Outcome, Is.EqualTo(GitRunOutcome.Completed), run.FailureDetail);
        Assert.That(run.ExitCode, Is.Not.EqualTo(0));
    }

    [Test]
    public async Task RunAsync_MissingExecutable_ReportsFailed()
    {
        var run = await GitProcess.RunAsync(
            Cwd,
            ["--version"],
            Generous,
            options: new GitRunOptions { GitExecutable = Path.Combine(Cwd, "rs-no-such-git-" + Guid.NewGuid().ToString("N")) });

        Assert.That(run.Outcome, Is.EqualTo(GitRunOutcome.Failed));
        Assert.That(run.FailureDetail, Is.Not.Empty);
    }

    [Test]
    public async Task RunAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await GitProcess.RunAsync(Cwd, ["--version"], Generous, cts.Token));
    }

    [Test]
    public async Task RunAsync_Timeout_ReturnsTimedOutNotCancellation()
    {
        var run = await GitProcess.RunAsync(Cwd, ["-c", "alias.rshang=!sleep 30", "rshang"], TimeSpan.FromSeconds(1));

        Assert.That(run.Outcome, Is.EqualTo(GitRunOutcome.TimedOut), run.FailureDetail);
    }

    [Test]
    public async Task RunAsync_CallerCancelMidRun_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await GitProcess.RunAsync(Cwd, ["-c", "alias.rshang=!sleep 30", "rshang"], Generous, cts.Token));
    }

    [Test]
    public void Run_Timeout_ReturnsTimedOut()
    {
        var run = GitProcess.Run(Cwd, ["-c", "alias.rshang=!sleep 30", "rshang"], TimeSpan.FromSeconds(1));

        Assert.That(run.Outcome, Is.EqualTo(GitRunOutcome.TimedOut), run.FailureDetail);
    }
}
