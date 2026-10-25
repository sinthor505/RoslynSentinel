using System.ComponentModel;

using RoslynSentinel.Common;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

// Operator-adjacent tools: ExternalFileDriftTool (status/list/acknowledge external-change state and
// the session-halt latch) and McpServerControlTool (process lifecycle). Always visible in claude-lean
// (ToolClassRegistry.ClaudeLeanToolNames).
[McpServerToolType]
public class AdminTools
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly RespawnPolicy _respawn;
    private readonly TestInstanceLauncher _testInstances;
    private readonly ActiveToolSurface? _activeToolSurface;

    // Property names are the wire contract: camelCase, nulls omitted.
    private static readonly System.Text.Json.JsonSerializerOptions TestInstanceJson = new(SharedJsonOptions.Default)
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="hostOptions">Supplies the effective --respawn value for the StopServer text; null (tests) means Auto.</param>
    /// <param name="testInstances">Owns the test-instance registry; null (tests) builds a private one.</param>
    /// <param name="activeToolSurface">Supplies this server's --mode string, the default mode of a launched test instance; null means claude-lean.</param>
    public AdminTools(IWorkspaceManager workspaceManager, SentinelHostOptions? hostOptions = null, TestInstanceLauncher? testInstances = null, ActiveToolSurface? activeToolSurface = null)
    {
        _workspaceManager = workspaceManager;
        _respawn = hostOptions?.Respawn ?? RespawnPolicy.Auto;
        _testInstances = testInstances ?? new TestInstanceLauncher(workspaceManager, new BuildEngine(workspaceManager));
        _activeToolSurface = activeToolSurface;
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
            return $"Nothing cleared: operation Acknowledge needs one of two things. (1) files = the files you reviewed with {ToolCallHint.Call("ExternalFileDrift", ("operation", "List"))}, keeping acknowledgeScope at its default ConfirmWithListedFiles, to clear only those files; or (2) acknowledgeScope = ConfirmAll with no files, to clear every tracked change. "
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
            return $"Nothing cleared: '{string.Join("', '", unmatched)}' match no tracked change. Tracked changes: {DriftMessages.SummarizeFiles(current)}. Call {ToolCallHint.Call("ExternalFileDrift", ("operation", "List"))} for full paths.";
        }

        if (matched.Count == 0)
        {
            return $"Nothing cleared: files named no entries. Tracked changes: {DriftMessages.SummarizeFiles(current)}. Call {ToolCallHint.Call("ExternalFileDrift", ("operation", "List"))} for full paths.";
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
        StopServer,
        LaunchTestInstance,
        StopTestInstance,
        ListTestInstances
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

    private static string TestInstanceError(ResultError error) =>
    System.Text.Json.JsonSerializer.Serialize(new
    {
        isError = true,
        errorData = error
    }, TestInstanceJson);

    /// <summary>
    /// LaunchTestInstance / StopTestInstance / ListTestInstances. Returns JSON text: the launch payload, a
    /// stopped/instances list, or <c>{ isError, errorData }</c> carrying a <see cref="ResultError"/>. Public so tests
    /// can drive it without a tool-call round trip.
    /// </summary>
    public async Task<string> ControlTestInstancesAsync(
        McpServerControlOperation operation, TestInstanceBuildMode? build, string? mode, string? includeTools, int? pid, CancellationToken cancellationToken)
    {
        try
        {
            switch (operation)
            {
                case McpServerControlOperation.LaunchTestInstance:
                    var effectiveMode = !string.IsNullOrWhiteSpace(mode)
                        ? mode.Trim()
                        : string.IsNullOrWhiteSpace(_activeToolSurface?.ModeArg) ? "claude-lean" : _activeToolSurface!.ModeArg;
                    var launched = await _testInstances.LaunchAsync(build ?? TestInstanceBuildMode.Auto, effectiveMode, includeTools, cancellationToken);
                    return System.Text.Json.JsonSerializer.Serialize(launched, TestInstanceJson);

                case McpServerControlOperation.StopTestInstance:
                    var stopped = _testInstances.Stop(pid);
                    return System.Text.Json.JsonSerializer.Serialize(new
                    {
                        stopped,
                        note = stopped.Count == 0 ? "No test instances were running." : $"Stopped {stopped.Count} test instance(s).",
                    }, TestInstanceJson);

                case McpServerControlOperation.ListTestInstances:
                    var instances = _testInstances.List();
                    return System.Text.Json.JsonSerializer.Serialize(new
                    {
                        instances,
                        note = instances.Count == 0 ? $"No test instances are running. Start one with {ToolCallHint.Call("McpServerControl", ("operation", "LaunchTestInstance"))}." : null,
                    }, TestInstanceJson);

                default:
                    return $"Unknown operation '{operation}'.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ToolException ex)
        {
            return TestInstanceError(ToolErrorMapper.ToResultError(ex, _workspaceManager, $"McpServerControl {operation}"));
        }
        catch (Exception ex)
        {
            // Never leak the raw exception text or a stack trace: name the type only.
            return TestInstanceError(new ResultError(ToolErrorCode.Exception, $"McpServerControl {operation} failed unexpectedly ({ex.GetType().Name}). Check the server log for details."));
        }
    }

    [McpServerTool(Name = "McpServerControl")]
    [Produces(DataTag.ResultOnly)]
    [Description("Operator control. StopServer needs confirmServerStop. LaunchTestInstance starts an HTTP copy of the working tree, returns url and request snippets (build Auto rebuilds only if sources changed). StopTestInstance stops pid or all. ListTestInstances lists.")]
    public async Task<string> McpServerControlTool(
     [Description(ToolParams.Reason)] ToolCallReason reason,
     McpServerControlOperation operation,
     McpServerStopConfirmation? confirmServerStop = null,
     TestInstanceBuildMode? build = null,
     string? mode = null,
     string? includeTools = null,
     int? pid = null,
     CancellationToken cancellationToken = default)
    {
        if (operation is McpServerControlOperation.LaunchTestInstance or McpServerControlOperation.StopTestInstance or McpServerControlOperation.ListTestInstances)
        {
            return await ControlTestInstancesAsync(operation, build, mode, includeTools, pid, cancellationToken);
        }

        // Children are killed before the delayed exit so a stopped server never leaves test instances behind.
        return ControlServer(operation, confirmServerStop, () =>
        {
            _testInstances.KillAll();
            ScheduleProcessExit();
        }, _workspaceManager.SolutionPath, _respawn, cancellationToken);
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
                return $"Stopping. This instance stays down: nothing will restart it, so there is no fresh instance to call {ToolCallHint.Call("LoadSolution")} on. "
                    + $"To get another test instance, call {ToolCallHint.Call("McpServerControl", ("operation", "LaunchTestInstance"))} from your own session.";
            }

            var loadHint = string.IsNullOrWhiteSpace(loadedSolutionPath)
                ? $"call {ToolCallHint.Call("LoadSolution")}"
                : $"call {ToolCallHint.Call("LoadSolution", ("solutionPath", loadedSolutionPath))}";
            var stopping = "Stopping. VS Code will rebuild the server binary and spawn a fresh instance on your next tool call. "
                + $"The fresh instance starts with no solution loaded: {loadHint} before any other tool.";
            // The respawn builds committed HEAD only, so flag server source that has uncommitted edits.
            var dirty = string.IsNullOrWhiteSpace(loadedSolutionPath)
                ? null
                : ServerBinaryStaleness.DescribeUncommittedServerSource(Path.GetDirectoryName(loadedSolutionPath) ?? string.Empty);
            return dirty is null ? stopping : $"{stopping} Warning: {dirty}";
        }

        return $"Unknown operation '{operation}'. Valid operations: GetServerStatus, StopServer, LaunchTestInstance, StopTestInstance, ListTestInstances.";
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
