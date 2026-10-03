using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

// Restricted/operator-only tools, gated behind the "Admin" mode (deliberately excluded from
// AllModes in ServerStdio.cs/ServerHttp.cs, so it's off by default and only reachable via an
// explicit --mode=Admin or --mode=<...>,Admin). See
// docs/current/ideas/external-drift-hard-blocker.md -> these two tools used to live in
// WorkspaceTools (model-visible by default under the "Workspace" mode), but letting the
// in-task model reconcile external drift itself only works for a genuinely concurrent-editing
// scenario this server doesn't target; under the single-session/no-concurrent-actors assumption a
// real drift hit should stop the session, not be something the model talks its way past. This
// class is also the intended home for any future restricted/operator-only tool, not a one-off.
[McpServerToolType]
public class AdminTools
{
    private readonly IWorkspaceManager _workspaceManager;

    public AdminTools(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    [McpServerTool(Name = "ListExternalDiskChanges")]
    [Produces(DataTag.FileList)]
    [Description("Returns files modified on disk since the agent last synced.")]
    public List<string> ListExternalDiskChanges(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return _workspaceManager.GetExternalFileChanges();
    }

    [McpServerTool(Name = "IsSessionHalted")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns whether the session-wide fatal drift latch is set.")]
    public bool IsSessionHalted(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return _workspaceManager.IsSessionHalted();
    }

    [McpServerTool(Name = "AcknowledgeExternalFileChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Clears the external-change list and fatal drift latch after disk changes are reviewed.")]
    public string AcknowledgeExternalFileChanges(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var count = _workspaceManager.GetExternalFileChanges().Count;
        var wasHalted = _workspaceManager.IsSessionHalted();
        _workspaceManager.ClearExternalFileChanges();
        _workspaceManager.ClearSessionHalt();
        return wasHalted
            ? $"Cleared {count} tracked external file change(s) and the session-wide fatal drift latch."
            : $"Cleared {count} tracked external file change(s).";
    }

    public enum McpServerControlOperation
    {
        GetServerStatus,
        StopServer
    }

    /// <summary>
    /// Single-member enum used as a schema-visible confirmation token: the emitted JSON schema lists
    /// <c>ConfirmServerStop</c> as the only accepted value, so a caller can see the guard in the
    /// tool listing instead of discovering a magic string from a refusal.
    /// </summary>
    public enum McpServerStopConfirmation
    {
        ConfirmServerStop
    }

    [McpServerTool(Name = "McpServerControl")]
    [Produces(DataTag.ResultOnly)]
    [Description("Operator-only control of this server process. operation=GetServerStatus reports the running process. operation=StopServer exits the process and is refused unless confirmServerStop=ConfirmServerStop is also passed.")]
    public string McpServerControl(
     [Description(ToolParams.Reason)] ToolCallReason reason,
     McpServerControlOperation operation,
     [Description("Required only with operation=StopServer; the single accepted value is ConfirmServerStop. Guards against an accidental stop.")]
     McpServerStopConfirmation? confirmServerStop = null,
     CancellationToken cancellationToken = default)
    {
        return ControlServer(operation, confirmServerStop, ScheduleProcessExit, _workspaceManager.SolutionPath, cancellationToken);
    }

    /// <summary>
    /// Core of <see cref="McpServerControl"/>, with the process-exit side effect injected so tests can
    /// verify the confirmation guard without terminating the test host.
    /// </summary>
    public static string ControlServer(
        McpServerControlOperation operation,
        McpServerStopConfirmation? confirmServerStop,
        Action scheduleExit,
        string? loadedSolutionPath = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (operation == McpServerControlOperation.GetServerStatus)
        {
            return $"Running. PID={Environment.ProcessId}, path={System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName}";
        }

        if (operation == McpServerControlOperation.StopServer)
        {
            if (confirmServerStop != McpServerStopConfirmation.ConfirmServerStop)
            {
                return "Refused: operation=StopServer requires confirmServerStop=ConfirmServerStop. "
                    + "Re-issue the call with that parameter to stop the server; nothing was stopped.";
            }

            scheduleExit();
            var loadHint = string.IsNullOrWhiteSpace(loadedSolutionPath)
                ? "call LoadSolution with your solution path"
                : $"call LoadSolution(solutionPath: \"{loadedSolutionPath}\")";
            return "Stopping. VS Code will rebuild the server binary and spawn a fresh instance on your next tool call. "
                + $"The fresh instance starts with no solution loaded: {loadHint} before any other tool.";
        }

        return $"Unknown operation '{operation}'. Valid operations: GetServerStatus, StopServer.";
    }

    private static void ScheduleProcessExit()
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
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            Environment.Exit(0);
        });
    }
}
