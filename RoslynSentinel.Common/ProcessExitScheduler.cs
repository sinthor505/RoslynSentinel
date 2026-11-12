namespace RoslynSentinel.Common;

/// <summary>
/// Exits the server process on a detached continuation after a fixed delay (a blind timer). Used where no response is
/// involved: the idle-restart monitor. The <c>McpServerControl</c> StopServer operation uses
/// <see cref="ResponseGatedProcessExit"/>, which waits for its response to be written.
/// </summary>
public static class ProcessExitScheduler
{
    /// <summary>
    /// Schedules <c>Environment.Exit(0)</c> to run after <paramref name="delay"/> (default 1000 ms) on a detached task.
    /// Returns immediately. A blind timer: it knows nothing about any response, so a caller whose own response must
    /// reach the client first uses <see cref="ResponseGatedProcessExit"/> instead.
    /// </summary>
    public static void Schedule(TimeSpan? delay = null)
    {
        // History: StopServer used to call this before it had built its response (the dirty-source check runs
        // `git status`, up to 5 s) and before the call-tool filters and serialization ran, so on a slow git the 1000 ms
        // timer could win the race against the response write and the caller saw only "Connection closed". Raising the
        // delay (250 ms -> 1000 ms) only hid it. StopServer now builds its full response first and then exits through
        // ResponseGatedProcessExit, an outgoing-message filter that exits after the response is actually written. What is
        // left here is the no-response case (IdleRestartMonitor), where a short delay is only defense in depth.
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay ?? TimeSpan.FromMilliseconds(1000));
            Environment.Exit(0);
        });
    }
}
