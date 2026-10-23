using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

// Operator-adjacent tools: ExternalFileDriftTool (status/list/acknowledge external-change state and
// the session-halt latch) and McpServerControlTool (process lifecycle). Always visible in claude-lean
// (ToolClassRegistry.ClaudeLeanToolNames).
[McpServerToolType]
public class AdminTools
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly RespawnPolicy _respawn;

    /// <param name="hostOptions">Supplies the effective --respawn value for the StopServer text; null (tests) means Auto.</param>
    public AdminTools(IWorkspaceManager workspaceManager, SentinelHostOptions? hostOptions = null)
    {
        _workspaceManager = workspaceManager;
        _respawn = hostOptions?.Respawn ?? RespawnPolicy.Auto;
    }

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ExternalFileDrift")]
    [Produces(DataTag.ResultOnly)]
    [Description("Inspect and clear external file drift: a tracked file changed on disk outside this server. A write touching such a file is refused and halts the session; read-only tools keep working. operation Status = halted or not, plus the tracked count. List = tracked file paths; review these first. Acknowledge = clear the halt: pass files (from List) with acknowledgeScope ConfirmWithListedFiles (default), or acknowledgeScope ConfirmAll with no files. Files you did not name stay flagged.")]
    public string ExternalFileDriftTool(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    [Description("Status, List or Acknowledge. After a 'Session halted' refusal, call List first.")] ExternalFileDriftOperation operation,
    [Description("Only for Acknowledge with ConfirmWithListedFiles: names or relative paths (CSV or JSON array) from List.")] string? files = null,
    [Description("Only for Acknowledge. ConfirmWithListedFiles (default): clear only the named files; refused when files is empty. ConfirmAll: clear every tracked change; pass no files.")] ExternalFileDriftAcknowledgeScope acknowledgeScope = ExternalFileDriftAcknowledgeScope.ConfirmWithListedFiles,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (operation == ExternalFileDriftOperation.Acknowledge)
        {
            return AcknowledgeDrift(_workspaceManager, files, acknowledgeScope);
        }

        if (!string.IsNullOrWhiteSpace(files) || acknowledgeScope == ExternalFileDriftAcknowledgeScope.ConfirmAll)
        {
            return "files and acknowledgeScope are only used with operation: Acknowledge; nothing changed.";
        }

        var current = _workspaceManager.GetExternalFileChanges();

        if (operation == ExternalFileDriftOperation.Status)
        {
            var summary = current.Count > 0 ? $" ({DriftMessages.SummarizeFiles(current)})" : string.Empty;
            return $"SessionHalted={_workspaceManager.IsSessionHalted()}; tracked external changes: {current.Count}{summary}.";
        }

        if (operation == ExternalFileDriftOperation.List)
        {
            return current.Count == 0
                ? "No tracked external file changes."
                : string.Join(Environment.NewLine, current);
        }

        return $"Unknown operation '{operation}'. Valid operations: Status, List, Acknowledge.";
    }

    /// <summary>
    /// Core of <c>ExternalFileDriftTool(operation: Acknowledge)</c>, taking the health reporter as a
    /// parameter so tests can seed drift with a stub instead of a real file-watcher round trip.
    /// </summary>
    public static string AcknowledgeDrift(IWorkspaceHealthReporter health, string? files, ExternalFileDriftAcknowledgeScope scope)
    {
        var hasFiles = !string.IsNullOrWhiteSpace(files);

        if (scope == ExternalFileDriftAcknowledgeScope.ConfirmAll)
        {
            if (hasFiles)
            {
                return "Nothing cleared: acknowledgeScope ConfirmAll clears every tracked external change and cannot be combined with files. Either omit files (to clear all) or pass files with acknowledgeScope ConfirmWithListedFiles (to clear only those).";
            }

            var all = new List<string>(health.GetExternalFileChanges());
            health.ClearExternalFileChanges();
            health.ClearSessionHalt();

            var cleared = $"Cleared {all.Count} tracked external file change(s)";
            if (all.Count > 0)
            {
                cleared += $": {DriftMessages.SummarizeFiles(all)}";
            }
            cleared += ". Session-halt latch cleared.";
            return cleared;
        }

        if (!hasFiles)
        {
            var tracked = health.GetExternalFileChanges();
            return "Nothing cleared: operation Acknowledge needs one of two things. (1) files = the files you reviewed with ExternalFileDrift(operation: List), keeping acknowledgeScope at its default ConfirmWithListedFiles, to clear only those files; or (2) acknowledgeScope = ConfirmAll with no files, to clear every tracked change. "
                + $"Tracked changes: {DriftMessages.SummarizeFiles(tracked)}.";
        }

        var requested = DelimitedListParser.ParseStringOrJsonArrayToList(files, out var parseError);
        if (parseError != null)
        {
            return parseError;
        }

        if (requested == null)
        {
            return parseError ?? "Failed to parse files list.";
        }

        var current = health.GetExternalFileChanges();
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);

        if (unmatched.Count > 0)
        {
            return $"Nothing cleared: '{string.Join("', '", unmatched)}' match no tracked change. Tracked changes: {DriftMessages.SummarizeFiles(current)}. Call ExternalFileDrift(operation: List) for full paths.";
        }

        if (matched.Count == 0)
        {
            return $"Nothing cleared: files named no entries. Tracked changes: {DriftMessages.SummarizeFiles(current)}. Call ExternalFileDrift(operation: List) for full paths.";
        }

        health.ClearExternalFileChanges(matched);
        health.ClearSessionHalt();
        var remaining = health.GetExternalFileChanges();

        var message = $"Cleared {matched.Count}: {DriftMessages.SummarizeFiles(matched)}. Session-halt latch cleared.";
        if (remaining.Count > 0)
        {
            message += $" {remaining.Count} file(s) remain flagged: {DriftMessages.SummarizeFiles(remaining)}. A write that touches one of them will halt the session again; acknowledge them too once reviewed.";
        }

        return message;
    }

    public enum ExternalFileDriftOperation
    {
        Status,
        List,
        Acknowledge
    }

    public enum ExternalFileDriftAcknowledgeScope
    {
        ConfirmWithListedFiles,
        ConfirmAll
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
    public string McpServerControlTool(
     [Description(ToolParams.Reason)] ToolCallReason reason,
     McpServerControlOperation operation,
     [Description("Required with operation=StopServer; only value: ConfirmServerStop.")]
     McpServerStopConfirmation? confirmServerStop = null,
     CancellationToken cancellationToken = default)
    {
        return ControlServer(operation, confirmServerStop, ScheduleProcessExit, _workspaceManager.SolutionPath, _respawn, cancellationToken);
    }

    /// <summary>
    /// Core of <see cref="McpServerControlTool"/>, with the process-exit side effect injected so tests can
    /// verify the confirmation guard without terminating the test host.
    /// </summary>
    public static string ControlServer(
        McpServerControlOperation operation,
        McpServerStopConfirmation? confirmServerStop,
        Action scheduleExit,
        string? loadedSolutionPath = null,
        RespawnPolicy respawn = RespawnPolicy.Auto,
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
            if (respawn == RespawnPolicy.None)
            {
                // --respawn=none (the HTTP default): no launcher or client restarts this process.
                return "Stopping. This instance stays down: nothing will restart it, so there is no fresh instance to call LoadSolution on. "
                    + "To get another test instance, call McpServerControl(operation: LaunchTestInstance) from your own session.";
            }

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
