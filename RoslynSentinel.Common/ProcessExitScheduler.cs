namespace RoslynSentinel.Common;

/// <summary>
/// Exits the server process on a detached continuation after a short delay, so the response to the tool call that asked
/// for the exit can be flushed first. Shared by the two callers that exit the process on purpose: the
/// <c>McpServerControl</c> StopServer operation and the idle-restart monitor.
/// </summary>
public static class ProcessExitScheduler
{
    /// <summary>
    /// Schedules <c>Environment.Exit(0)</c> to run after <paramref name="delay"/> (default 1000 ms) on a detached task.
    /// Returns immediately.
    /// </summary>
    public static void Schedule(TimeSpan? delay = null)
    {
        // The MCP SDK's request-handling path awaits the tool handler and then awaits flushing
        // the response to the stdio transport before this call completes - so by the time this
        // method returns, the caller is guaranteed to already have the response in flight. Exiting
        // synchronously here would still be safe by that reasoning, but scheduling it on a
        // detached continuation with a delay is cheap defense-in-depth against being wrong
        // about that ordering, for what is otherwise an irreversible action. The delay is 1000 ms
        // (was 250 ms) because callers intermittently saw only "Connection closed" instead of the
        // acknowledgement - consistent with the exit occasionally winning the race against the
        // response flush on a busy server. Not reproducible on demand; if it still recurs, the
        // fix is to exit from an outgoing-message filter after the response is actually written.
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay ?? TimeSpan.FromMilliseconds(1000));
            Environment.Exit(0);
        });
    }
}
