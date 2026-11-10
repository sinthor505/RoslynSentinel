using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RoslynSentinel.Common;

/// <summary>What <see cref="ChildProcessCapture.RunAsync"/> observed. ExitCode is null when the root process never exited on its own (timeout kill).</summary>
public sealed record ChildProcessCaptureResult(
    string StdOut,
    string StdErr,
    bool TimedOut,
    bool RootExited,
    int? ExitCode,
    bool DrainTimedOut);

/// <summary>
/// Captures stdout/stderr of a started child process and waits for the PROCESS to exit, not for pipe EOF. A grandchild
/// that inherited the redirected pipes (an MSBuild worker node with /nodeReuse:true) keeps the pipes open after the root
/// exited, so an EOF-based wait (ReadToEndAsync, Process.WaitForExitAsync with BeginOutputReadLine) lasts until the
/// timeout. Here the reads are chunked loops into StringBuilders, so partial text survives cancellation.
/// </summary>
/// <remarks>
/// Differs from <see cref="GitProcess"/>, which is synchronous, returns null on a held pipe and discards the text: this
/// helper is async, gives the reads a drain grace after exit, then cancels them and keeps what was read, and reports
/// DrainTimedOut so the caller can continue with the real exit code and possibly incomplete output.
/// </remarks>
public static class ChildProcessCapture
{
    /// <summary>How long the reads get to reach EOF after the process exited (or was killed) before they are cancelled.</summary>
    public static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(4);

    /// <summary>
    /// <paramref name="process"/> must already be started with RedirectStandardOutput and RedirectStandardError.
    /// Caller cancellation kills the process tree and rethrows <see cref="OperationCanceledException"/>. A timeout kills
    /// the tree and returns TimedOut with RootExited reporting whether the root had exited before the kill.
    /// </summary>
    public static async Task<ChildProcessCaptureResult> RunAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? drainGrace = null)
    {
        var grace = drainGrace ?? DefaultDrainGrace;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var readCts = new CancellationTokenSource();
        var reads = Task.WhenAll(
            PumpAsync(process.StandardOutput, stdout, readCts.Token),
            PumpAsync(process.StandardError, stderr, readCts.Token));

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillTree(process);
            await readCts.CancelAsync().ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            var rootExited = HasExited(process);
            TryKillTree(process);
            var drainTimedOutAfterKill = await DrainAsync(reads, grace, readCts).ConfigureAwait(false);
            return new ChildProcessCaptureResult(
                Snapshot(stdout), Snapshot(stderr), TimedOut: true, RootExited: rootExited,
                ExitCode: rootExited ? process.ExitCode : null, DrainTimedOut: drainTimedOutAfterKill);
        }

        var drainTimedOut = await DrainAsync(reads, grace, readCts).ConfigureAwait(false);
        return new ChildProcessCaptureResult(
            Snapshot(stdout), Snapshot(stderr), TimedOut: false, RootExited: true,
            ExitCode: process.ExitCode, DrainTimedOut: drainTimedOut);
    }

    /// <summary>Waits up to <paramref name="grace"/> for the reads; on expiry cancels them. True when the grace expired.</summary>
    private static async Task<bool> DrainAsync(Task reads, TimeSpan grace, CancellationTokenSource readCts)
    {
        using var graceCts = new CancellationTokenSource();
        var finished = await Task.WhenAny(reads, Task.Delay(grace, graceCts.Token)).ConfigureAwait(false) == reads;
        await graceCts.CancelAsync().ConfigureAwait(false);
        if (!finished)
        {
            // The pump swallows its own cancellation/disposal errors, so the abandoned task cannot fault unobserved.
            await readCts.CancelAsync().ConfigureAwait(false);
        }

        return !finished;
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder sink, CancellationToken token)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                lock (sink)
                {
                    sink.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Cancelled after the drain grace, or the stream was disposed with the process: keep the partial text.
        }
    }

    private static string Snapshot(StringBuilder sink)
    {
        lock (sink)
        {
            return sink.ToString();
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Not guarded by HasExited on purpose: after the root exited, the kill must still reap a grandchild that holds the pipes.</summary>
    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already exited or not killable: nothing more to do.
        }
    }
}
