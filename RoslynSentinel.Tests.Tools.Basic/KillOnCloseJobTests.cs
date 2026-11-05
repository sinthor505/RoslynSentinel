// KillOnCloseJob: a Windows kill-on-close job object. Isolated jobs are disposed in tests so the assigned child is
// killed by the OS; the process-wide job is never disposed (no way to do so by design), so its test kills its child.
using System.Diagnostics;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[NonParallelizable]
[Category("AdminTools")]
[Category("KillOnCloseJob")]
public class KillOnCloseJobTests
{
    private static Process StartSleepingChild()
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 60");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null.");
    }

    private static void KillIfRunning(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Process ended while being killed; nothing left to clean up.
        }
    }

    [Test]
    [Platform("Win")]
    public void KillOnCloseJob_DisposingJob_KillsAssignedChild()
    {
        KillOnCloseJob? job = null;
        Process? child = null;
        try
        {
            job = KillOnCloseJob.CreateIsolated(out var createError);
            Assert.That(job, Is.Not.Null, $"CreateIsolated failed: {createError}");

            child = StartSleepingChild();
            var assigned = job!.TryAssign(child, out var assignError);
            Assert.That(assigned, Is.True, $"TryAssign failed: {assignError}");
            Assert.That(child.HasExited, Is.False, "Child exited before the job was disposed.");

            job.Dispose();

            Assert.That(child.WaitForExit(10000), Is.True,
                "Child was still running 10 seconds after the kill-on-close job was disposed.");
        }
        finally
        {
            KillIfRunning(child);
            child?.Dispose();
            job?.Dispose();
        }
    }

    [Test]
    [Platform("Win")]
    public void KillOnCloseJob_ProcessWideAssign_ReturnsTrueAndChildSurvivesUntilKilledByTest()
    {
        Process? child = null;
        try
        {
            child = StartSleepingChild();

            var assigned = KillOnCloseJob.TryAssignToProcessWideJob(child, out var warning);
            // A CI box that is itself inside a restrictive job can refuse nested assignment; that is an
            // environment limit, not a regression, so skip with the warning instead of failing.
            Assume.That(assigned, Is.True, $"Process-wide assign refused by this environment: {warning}");

            Assert.That(warning, Is.Null);
            Assert.That(child.HasExited, Is.False, "Child exited after process-wide assignment.");
        }
        finally
        {
            // The process-wide job is intentionally never disposed; only the child is killed here.
            KillIfRunning(child);
            child?.Dispose();
        }
    }

    [Test]
    public void KillOnCloseJob_NonWindows_ProcessWideAssignIsNoOp()
    {
        Assume.That(!OperatingSystem.IsWindows(), "Non-Windows only: Windows runs are covered by the Platform(\"Win\") tests.");

        using var child = Process.Start(new ProcessStartInfo("sleep", "1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Process.Start returned null.");

        var assigned = KillOnCloseJob.TryAssignToProcessWideJob(child, out var warning);

        Assert.That(assigned, Is.True);
        Assert.That(warning, Is.Null);
        child.WaitForExit(5000);
    }
}
