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
    private readonly ResponseGatedProcessExit? _gatedExit;

    // Property names are the wire contract: camelCase, nulls omitted.
    private static readonly System.Text.Json.JsonSerializerOptions TestInstanceJson = new(SharedJsonOptions.Default)
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="hostOptions">Supplies the effective --respawn value for the StopServer text; null (tests) means Auto.</param>
    /// <param name="testInstances">Owns the test-instance registry; null (tests) builds a private one.</param>
    /// <param name="activeToolSurface">Supplies this server's --mode string, the default mode of a launched test instance; null means claude-lean.</param>
    /// <param name="gatedExit">Exits the process after StopServer's response is written (the outgoing-message filter drives it); null (tests, hosts without the filter) falls back to the blind-timer <see cref="ProcessExitScheduler"/>.</param>
    public AdminTools(IWorkspaceManager workspaceManager, SentinelHostOptions? hostOptions = null, TestInstanceLauncher? testInstances = null, ActiveToolSurface? activeToolSurface = null, ResponseGatedProcessExit? gatedExit = null)
    {
        _gatedExit = gatedExit;
        _workspaceManager = workspaceManager;
        _respawn = hostOptions?.Respawn ?? RespawnPolicy.Auto;
        _testInstances = testInstances ?? new TestInstanceLauncher(workspaceManager, new BuildEngine(workspaceManager));
        _activeToolSurface = activeToolSurface;
    }

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ExternalFileDrift")]
    [Produces(DataTag.ResultOnly)]
    [Description("Inspect and clear external file drift: a tracked file changed on disk outside this server. Drift is listed for visibility and does not halt writes; a file you read or edited that drifted is refused with StaleRead until you re-read it with ReadFile. operation Status = whether the session is halted (true only after an unrecoverable breaker trip) plus the tracked count. List = tracked file paths. Acknowledge = remove files from the visibility list: pass files (from List) with acknowledgeScope ConfirmWithListedFiles (default), or acknowledgeScope ConfirmAll with no files; it does not change whether a write is allowed. Files you did not name stay listed. LedgerList/LedgerFix/LedgerRelease resolve an open MoveMember ledger that refuses unrelated writes.")]
    public SentinelCallToolResult<string> ExternalFileDriftTool(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    [Description("Status, List, Acknowledge, LedgerList, LedgerFix or LedgerRelease. List shows the files drift detection has listed.")] ExternalFileDriftOperation operation,
    [Description("Only for Acknowledge with ConfirmWithListedFiles: names or relative paths (CSV or JSON array) from List.")] string? files = null,
    [Description("Only for Acknowledge. ConfirmWithListedFiles (default): remove only the named files from the visibility list; refused when files is empty. ConfirmAll: remove every listed file; pass no files.")] ExternalFileDriftAcknowledgeScope acknowledgeScope = ExternalFileDriftAcknowledgeScope.ConfirmWithListedFiles,
    [Description("Only for LedgerFix: entry ids (CSV or JSON array) from LedgerList.")] string? entryIds = null,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (operation != ExternalFileDriftOperation.LedgerFix && !string.IsNullOrWhiteSpace(entryIds))
        {
            return Refused(ToolErrorCode.InvalidArgument, "entryIds is only used with operation: LedgerFix; nothing changed.");
        }

        if (operation == ExternalFileDriftOperation.Acknowledge)
        {
            return AcknowledgeDriftResult(_workspaceManager, files, acknowledgeScope);
        }

        if (!string.IsNullOrWhiteSpace(files) || acknowledgeScope == ExternalFileDriftAcknowledgeScope.ConfirmAll)
        {
            return Refused(ToolErrorCode.InvalidArgument, "files and acknowledgeScope are only used with operation: Acknowledge; nothing changed.");
        }

        if (operation is ExternalFileDriftOperation.LedgerList or ExternalFileDriftOperation.LedgerFix or ExternalFileDriftOperation.LedgerRelease)
        {
            // Same instance MoveMember opens the ledger on; the ScopedOperationLedgerEngine registered in DI is a different object.
            return LedgerResult(_workspaceManager as IScopedOperationLedger, operation, entryIds);
        }

        var current = _workspaceManager.GetExternalFileChanges();

        if (operation == ExternalFileDriftOperation.Status)
        {
            var summary = current.Count > 0 ? $" ({DriftMessages.SummarizeFiles(current)})" : string.Empty;
            // TODO ExternalFileDrift Status prints SessionHalted=IsSessionHalted(), which is always False now (dead-latch cleanup), while the tool description says it is true only after an unrecoverable breaker trip; drop the field or fix the description.
            return Ok($"SessionHalted={_workspaceManager.IsSessionHalted()}; tracked external changes: {current.Count}{summary}.");
        }

        if (operation == ExternalFileDriftOperation.List)
        {
            return Ok(current.Count == 0
                ? "No tracked external file changes."
                : string.Join(Environment.NewLine, current));
        }

        return Refused(ToolErrorCode.InvalidArgument, $"Unknown operation '{operation}'. Valid operations: Status, List, Acknowledge, LedgerList, LedgerFix, LedgerRelease.");
    }

    /// <summary>
    /// Core of <c>ExternalFileDriftTool(operation: Acknowledge)</c>, taking the health reporter as a
    /// parameter so tests can seed drift with a stub instead of a real file-watcher round trip.
    /// </summary>
    public static string AcknowledgeDrift(IWorkspaceHealthReporter health, string? files, ExternalFileDriftAcknowledgeScope scope)
    {
        // TODO remove AcknowledgeDrift once its direct-call tests move to AcknowledgeDriftResult; kept for its plain-text contract.
        var r = AcknowledgeDriftResult(health, files, scope);
        return r.IsError ? r.ErrorData!.Message : r.SuccessData!;
    }

    private static SentinelCallToolResult<string> Ok(string text) => new() { IsError = false, SuccessData = text };

    private static SentinelCallToolResult<string> Refused(string errorCode, string message) =>
        new() { IsError = true, ErrorData = new ResultError(errorCode, message) };

    /// <summary>
    /// Envelope form of <see cref="AcknowledgeDrift"/>: success carries the cleared-summary text; every refusal
    /// carries an error code (InvalidArgument, or NotFound when a named file matches no tracked change) and the same message text.
    /// </summary>
    public static SentinelCallToolResult<string> AcknowledgeDriftResult(IWorkspaceHealthReporter health, string? files, ExternalFileDriftAcknowledgeScope scope)
    {
        var hasFiles = !string.IsNullOrWhiteSpace(files);

        if (scope == ExternalFileDriftAcknowledgeScope.ConfirmAll)
        {
            if (hasFiles)
            {
                return Refused(ToolErrorCode.InvalidArgument, "Nothing cleared: acknowledgeScope ConfirmAll clears every tracked external change and cannot be combined with files. Either omit files (to clear all) or pass files with acknowledgeScope ConfirmWithListedFiles (to clear only those).");
            }

            var all = new List<string>(health.GetExternalFileChanges());
            health.ClearExternalFileChanges();
            health.ClearSessionHalt();

            var cleared = $"Cleared {all.Count} tracked external file change(s)";
            if (all.Count > 0)
            {
                cleared += $": {DriftMessages.SummarizeFiles(all)}";
            }
            cleared += " from the drift list.";
            return Ok(cleared);
        }

        if (!hasFiles)
        {
            var tracked = health.GetExternalFileChanges();
            return Refused(ToolErrorCode.InvalidArgument, $"Nothing cleared: operation Acknowledge needs one of two things. (1) files = the files you reviewed with {ToolCallHint.Call("ExternalFileDrift", ("operation", "List"))}, keeping acknowledgeScope at its default ConfirmWithListedFiles, to clear only those files; or (2) acknowledgeScope = ConfirmAll with no files, to clear every tracked change. "
                + $"Tracked changes: {DriftMessages.SummarizeFiles(tracked)}.");
        }

        var requested = DelimitedListParser.ParseStringOrJsonArrayToList(files, out var parseError);
        if (parseError != null)
        {
            return Refused(ToolErrorCode.InvalidArgument, parseError);
        }

        if (requested == null)
        {
            return Refused(ToolErrorCode.InvalidArgument, parseError ?? "Failed to parse files list.");
        }

        var current = health.GetExternalFileChanges();
        DriftMessages.ResolveSelection(current, requested, out var matched, out var unmatched);

        if (unmatched.Count > 0)
        {
            return Refused(ToolErrorCode.NotFound, $"Nothing cleared: '{string.Join("', '", unmatched)}' match no tracked change. Tracked changes: {DriftMessages.SummarizeFiles(current)}. Call {ToolCallHint.Call("ExternalFileDrift", ("operation", "List"))} for full paths.");
        }

        if (matched.Count == 0)
        {
            return Refused(ToolErrorCode.NotFound, $"Nothing cleared: files named no entries. Tracked changes: {DriftMessages.SummarizeFiles(current)}. Call {ToolCallHint.Call("ExternalFileDrift", ("operation", "List"))} for full paths.");
        }

        health.ClearExternalFileChanges(matched);
        health.ClearSessionHalt();
        var remaining = health.GetExternalFileChanges();

        var message = $"Cleared {matched.Count}: {DriftMessages.SummarizeFiles(matched)} from the drift list.";
        if (remaining.Count > 0)
        {
            message += $" {remaining.Count} file(s) remain listed: {DriftMessages.SummarizeFiles(remaining)}.";
        }

        return Ok(message);
    }

    private const int LedgerListCap = 50;
    private const int LedgerIdListCap = 10;

    /// <summary>
    /// Core of <c>ExternalFileDriftTool(operation: LedgerList | LedgerFix | LedgerRelease)</c>. <paramref name="ledger"/> is the
    /// live ledger (the workspace manager MoveMember opens it on); null means this workspace manager has none.
    /// </summary>
    public static SentinelCallToolResult<string> LedgerResult(IScopedOperationLedger? ledger, ExternalFileDriftOperation operation, string? entryIds)
    {
        if (ledger is null)
        {
            return Refused(ToolErrorCode.InvalidArgument, "ledger not available on this workspace manager; nothing changed.");
        }

        return operation switch
        {
            ExternalFileDriftOperation.LedgerList => LedgerListResult(ledger),
            ExternalFileDriftOperation.LedgerFix => LedgerFixResult(ledger, entryIds),
            ExternalFileDriftOperation.LedgerRelease => LedgerReleaseResult(ledger),
            _ => Refused(ToolErrorCode.InvalidArgument, $"'{operation}' is not a ledger operation; nothing changed.")
        };
    }

    private static SentinelCallToolResult<string> LedgerListResult(IScopedOperationLedger ledger)
    {
        var entries = ledger.GetOpenEntries();
        if (entries.Count == 0)
        {
            return Ok("Ledger: none open.");
        }

        var unresolved = entries.Where(e => !e.IsFixed).ToList();
        // Unresolved first so the cap never hides the entries that still block writes.
        var ordered = unresolved.Concat(entries.Where(e => e.IsFixed)).ToList();

        var lines = new List<string>
        {
            $"Ledger open for '{ledger.OpenOperationName}' (opened by change {ledger.OpeningChangeId ?? "unknown"}): {entries.Count} entries, {unresolved.Count} unresolved."
        };
        lines.AddRange(ordered.Take(LedgerListCap).Select(LedgerEntryLine));
        if (ordered.Count > LedgerListCap)
        {
            lines.Add($"... {ordered.Count - LedgerListCap} more");
        }

        var release = LedgerMessages.ReleaseCall();
        lines.Add(unresolved.Count > 0
            ? $"Next: after fixing the call site(s), {LedgerMessages.FixCall(unresolved[0].EntryId)}; or abandon the ledger with {release}."
            : $"Next: every entry is fixed; {release} to close the ledger.");
        return Ok(string.Join(Environment.NewLine, lines));
    }

    private static string LedgerEntryLine(LedgerEntryBase entry)
    {
        var parts = new List<string?> { entry.EntryId, $"{entry.FilePath}:{entry.Line}", entry.IsFixed ? "fixed" : "unresolved" };
        if (entry is CallSiteLedgerEntry callSite)
        {
            parts.Add(callSite.BrokenExpression);
            parts.Add(callSite.Status.ToString());
            parts.Add(callSite.BlockReason);
            parts.Add(callSite.SuggestedFix);
        }

        // One line per entry: collapse any line breaks inside an expression or reason.
        return string.Join(" | ", parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => string.Join(' ', p!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
    }

    private static string BoundedIds(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        var shown = string.Join(", ", list.Take(LedgerIdListCap));
        return list.Count > LedgerIdListCap ? $"{shown} (+{list.Count - LedgerIdListCap} more)" : shown;
    }

    private static SentinelCallToolResult<string> LedgerFixResult(IScopedOperationLedger ledger, string? entryIds)
    {
        var listCall = LedgerMessages.ListCall();
        if (string.IsNullOrWhiteSpace(entryIds))
        {
            return Refused(ToolErrorCode.InvalidArgument, $"Nothing changed: operation LedgerFix needs entryIds (CSV or JSON array). Get them with {listCall}.");
        }

        var requested = DelimitedListParser.ParseStringOrJsonArrayToList(entryIds, out var parseError);
        if (parseError != null || requested == null)
        {
            return Refused(ToolErrorCode.InvalidArgument, parseError ?? "Failed to parse entryIds list.");
        }

        var entries = ledger.GetOpenEntries();
        if (entries.Count == 0)
        {
            return Refused(ToolErrorCode.NotFound, "Nothing changed: no ledger is open.");
        }

        var known = entries.ToDictionary(e => e.EntryId, e => e.EntryId, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<string>();
        var unknown = new List<string>();
        foreach (var token in requested.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            if (known.TryGetValue(token, out var id))
            {
                resolved.Add(id);
            }
            else
            {
                unknown.Add(token);
            }
        }

        if (unknown.Count > 0)
        {
            var unresolvedIds = entries.Where(e => !e.IsFixed).Select(e => e.EntryId);
            return Refused(ToolErrorCode.NotFound, $"Nothing changed: unknown entry id(s) {BoundedIds(unknown)}. Unresolved ids: {BoundedIds(unresolvedIds)}. Call {listCall} for details.");
        }

        if (resolved.Count == 0)
        {
            return Refused(ToolErrorCode.InvalidArgument, $"Nothing changed: entryIds named no entries. Get them with {listCall}.");
        }

        ledger.RecordFix(resolved, "operator");
        var released = ledger.TryRelease();
        if (released)
        {
            return Ok($"Marked {resolved.Count} entry(ies) fixed (not re-verified against the source). 0 unresolved remain; ledger released, unrelated writes are allowed again.");
        }

        var remaining = ledger.GetOpenEntries().Where(e => !e.IsFixed).ToList();
        return Ok($"Marked {resolved.Count} entry(ies) fixed (not re-verified against the source). {remaining.Count} unresolved remain; ledger still open. Next: {listCall}.");
    }

    private static SentinelCallToolResult<string> LedgerReleaseResult(IScopedOperationLedger ledger)
    {
        var operationName = ledger.OpenOperationName;
        if (operationName is null && ledger.GetOpenEntries().Count == 0)
        {
            return Ok("Ledger: none open; nothing changed.");
        }

        var abandoned = ledger.ForceRelease();
        return Ok(abandoned == 0
            ? $"Released the ledger for '{operationName}'; it had no unresolved entries."
            : $"Released the ledger for '{operationName}': {abandoned} unresolved entry(ies) were abandoned and the affected files may not compile.");
    }

    public enum ExternalFileDriftOperation
    {
        Status,
        List,
        Acknowledge,
        LedgerList,
        LedgerFix,
        LedgerRelease
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

    private static readonly System.Text.Json.JsonSerializerOptions ResultErrorJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps the text <see cref="ControlTestInstancesAsync"/> returns onto the envelope: non-JSON text (the
    /// unknown-operation message) is InvalidArgument; <c>{ isError: true, errorData }</c> becomes the error;
    /// any other JSON payload is success data, with the payload shape unchanged.
    /// </summary>
    private static SentinelCallToolResult<object> FromTestInstanceText(string text)
    {
        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, text) };
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("isError", out var isError)
                && isError.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                ResultError? error = null;
                if (root.TryGetProperty("errorData", out var errorData))
                {
                    try
                    {
                        error = System.Text.Json.JsonSerializer.Deserialize<ResultError>(errorData, ResultErrorJson);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // Fall through to the fixed message below; never surface the parser's exception text.
                    }
                }

                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = error ?? new ResultError(ToolErrorCode.Exception, "McpServerControl returned an error that could not be read. Check the server log for details."),
                };
            }

            return new SentinelCallToolResult<object> { IsError = false, SuccessData = root.Clone() };
        }
    }

    [McpServerTool(Name = "McpServerControl")]
    [Produces(DataTag.ResultOnly)]
    [Description("Operator control. StopServer needs confirmServerStop. LaunchTestInstance starts an HTTP copy of the working tree, returns url and request snippets (build Auto rebuilds only if sources changed). StopTestInstance stops pid or all. ListTestInstances lists.")]
    public async Task<SentinelCallToolResult<object>> McpServerControlTool(
     [Description(ToolParams.Reason)] ToolCallReason reason,
     McpServerControlOperation operation,
     McpServerStopConfirmation? confirmServerStop = null,
     TestInstanceBuildMode? build = null,
     string? mode = null,
     string? includeTools = null,
     int? pid = null,
     RequestContext<CallToolRequestParams>? requestContext = null,
     CancellationToken cancellationToken = default)
    {
        if (operation is McpServerControlOperation.LaunchTestInstance or McpServerControlOperation.StopTestInstance or McpServerControlOperation.ListTestInstances)
        {
            return FromTestInstanceText(await ControlTestInstancesAsync(operation, build, mode, includeTools, pid, cancellationToken));
        }

        // Children are killed before the exit so a stopped server never leaves test instances behind. requestContext is
        // the SDK-injected request (excluded from the schema, see SentinelToolFactory.AugmentedServiceTypes); its JSON-RPC id
        // is what the outgoing-message filter waits for. Without it (direct calls in tests) or without the gate, the exit
        // falls back to the blind timer. McpServerControl is not task-eligible (TaskEnabledToolsHelper), so the id is always
        // the one answered with this call's own response; if it ever became task-augmented the response would be a task
        // handle for that id and the gate's fallback timer would end the process instead.
        var requestId = requestContext?.JsonRpcRequest.Id;
        var result = ControlServerResult(operation, confirmServerStop, () =>
        {
            _testInstances.KillAll();
            if (_gatedExit is not null && requestId is { } id)
            {
                _gatedExit.ExpectResponseTo(id);
            }
            else
            {
                ProcessExitScheduler.Schedule();
            }
        }, _workspaceManager.SolutionPath, _respawn, cancellationToken);

        return result.IsError
            ? new SentinelCallToolResult<object> { IsError = true, ErrorData = result.ErrorData }
            : new SentinelCallToolResult<object> { IsError = false, SuccessData = result.SuccessData };
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
        // TODO remove ControlServer once its direct-call tests move to ControlServerResult; kept for its plain-text contract.
        var r = ControlServerResult(operation, confirmServerStop, scheduleExit, loadedSolutionPath, respawn, cancellationToken);
        return r.IsError ? r.ErrorData!.Message : r.SuccessData!;
    }

    /// <summary>
    /// Envelope form of <see cref="ControlServer"/>: status and stop texts are success data; the unconfirmed-stop
    /// refusal is ConfirmationRequired (and never calls <c>scheduleExit</c>); an unknown operation is InvalidArgument.
    /// </summary>
    public static SentinelCallToolResult<string> ControlServerResult(
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
            return Ok($"Running. PID={Environment.ProcessId}, path={System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName}");
        }

        if (operation == McpServerControlOperation.StopServer)
        {
            if (confirmServerStop != McpServerStopConfirmation.ConfirmServerStop)
            {
                return Refused(ToolErrorCode.ConfirmationRequired, "Refused: operation=StopServer requires confirmServerStop=ConfirmServerStop. "
                    + "Re-issue the call with that parameter to stop the server; nothing was stopped.");
            }

            // scheduleExit is the LAST step before returning: the full response text, including the dirty-source check
            // (`git status`, up to 5 s), is built first, so a slow git can no longer delay the response past the exit.
            if (respawn == RespawnPolicy.None)
            {
                // --respawn=none (the HTTP default): no launcher or client restarts this process.
                var stays = $"Stopping. This instance stays down: nothing will restart it, so there is no fresh instance to call {ToolCallHint.Call("LoadSolution")} on. "
                    + $"To get another test instance, call {ToolCallHint.Call("McpServerControl", ("operation", "LaunchTestInstance"))} from your own session.";
                scheduleExit();
                return Ok(stays);
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
            var text = dirty is null ? stopping : $"{stopping} Warning: {dirty}";
            scheduleExit();
            return Ok(text);
        }

        return Refused(ToolErrorCode.InvalidArgument, $"Unknown operation '{operation}'. Valid operations: GetServerStatus, StopServer, LaunchTestInstance, StopTestInstance, ListTestInstances.");
    }
}
