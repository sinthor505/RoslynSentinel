using System.Diagnostics;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("OutOfProcess")] // starts cmd/ping processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class ChildProcessCaptureTests
{
    private static Process StartCmd(string arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        return process;
    }

    private static HashSet<int> PingIds()
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("ping"))
        {
            ids.Add(p.Id);
            p.Dispose();
        }

        return ids;
    }

    /// <summary>Kills only the pings this test started (ids that did not exist before it), never other sessions' pings.</summary>
    private static void KillPingsStartedSince(HashSet<int> before)
    {
        foreach (var p in Process.GetProcessesByName("ping"))
        {
            try
            {
                if (!before.Contains(p.Id))
                {
                    p.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    [SetUp]
    public void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("These fakes use cmd.exe and ping.exe.");
        }
    }

    [Test]
    public async Task RunAsync_RootExitsWhileGrandchildHoldsPipe_ReturnsAfterDrainGraceWithExitCode()
    {
        var before = PingIds();
        try
        {
            // cmd exits at once; the backgrounded ping inherits stdout/stderr and holds them for ~19 s.
            using var process = StartCmd("/c start /b ping -n 20 127.0.0.1");
            var sw = Stopwatch.StartNew();

            var result = await ChildProcessCapture.RunAsync(process, TimeSpan.FromSeconds(60), CancellationToken.None);

            sw.Stop();
            Assert.That(result.TimedOut, Is.False);
            Assert.That(result.RootExited, Is.True);
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(result.DrainTimedOut, Is.True);
            Assert.That(sw.Elapsed, Is.LessThan(ChildProcessCapture.DefaultDrainGrace + TimeSpan.FromSeconds(8)),
                "the helper must wait for process exit plus the drain grace, not for the grandchild's pipe EOF");
        }
        finally
        {
            KillPingsStartedSince(before);
        }
    }

    [Test]
    public async Task RunAsync_QuickChild_ReturnsTextAndExitCodeWithoutDrainTimeout()
    {
        using var process = StartCmd("/c echo hello-out& echo hello-err 1>&2& exit /b 3");

        var result = await ChildProcessCapture.RunAsync(process, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.That(result.TimedOut, Is.False);
        Assert.That(result.RootExited, Is.True);
        Assert.That(result.ExitCode, Is.EqualTo(3));
        Assert.That(result.DrainTimedOut, Is.False);
        Assert.That(result.StdOut, Does.Contain("hello-out"));
        Assert.That(result.StdErr, Does.Contain("hello-err"));
    }

    [Test]
    public async Task RunAsync_ChildOutlivesTimeout_KillsTreeAndReportsRootNotExited()
    {
        var before = PingIds();
        try
        {
            using var process = StartCmd("/c ping -n 30 127.0.0.1");

            var result = await ChildProcessCapture.RunAsync(process, TimeSpan.FromSeconds(2), CancellationToken.None);

            Assert.That(result.TimedOut, Is.True);
            Assert.That(result.RootExited, Is.False);
            Assert.That(result.ExitCode, Is.Null);
        }
        finally
        {
            // The tree kill should already have reaped ping; this only backstops a failed kill.
            KillPingsStartedSince(before);
        }
    }

    [Test]
    public async Task RunAsync_CallerCancellation_KillsTreeAndThrows()
    {
        var before = PingIds();
        try
        {
            using var process = StartCmd("/c ping -n 30 127.0.0.1");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            await Assert.ThatAsync(
                async () => await ChildProcessCapture.RunAsync(process, TimeSpan.FromSeconds(60), cts.Token),
                Throws.InstanceOf<OperationCanceledException>());
        }
        finally
        {
            KillPingsStartedSince(before);
        }
    }
}
