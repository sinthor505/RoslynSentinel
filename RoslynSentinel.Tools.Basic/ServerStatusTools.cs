using System.ComponentModel;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tools.Basic;

// Carries [McpServerToolType] so every reflection scanner sees it, but is registered explicitly as an always-on class
// (ServiceRegistrationExtensionsBasic) and deliberately kept out of ToolClassRegistry, so it is never counted or excludable.
[McpServerToolType]
public class ServerStatusTools
{
    public const string ToolName = "McpServerStatus";

    private readonly IWorkspaceManager _workspaceManager;
    private readonly ActiveToolSurface _activeToolSurface;
    private readonly StoppedByScriptMarker _stoppedByScriptMarker;
    private readonly ToolsetService? _toolsets;
    private readonly SentinelHostOptions? _hostOptions;
    private readonly ToolTelemetry? _toolTelemetry;
    private readonly GitBackendWarmup _gitBackend;
    private readonly ToolLoopLatch? _loopLatch;
    private readonly ToolSurfaceController? _surface;

    /// <param name="toolsets">Present only in claude-lean, where McpToolsetControlTool can switch tools on at runtime.</param>
    /// <param name="toolTelemetry">Per-tool call counters; null (tests, hosts without the telemetry filter) omits the toolTelemetry field.</param>
    /// <param name="gitBackend">Source of the gitBackend block; null uses the process-wide <see cref="GitBackendWarmup.Shared"/> (tests inject one). Not DI-registered, so the optional default applies.</param>
    /// <param name="loopLatch">Per-agent loop latch state (proposal_loop_latch.md); reported as loopLatch only while --loop-latch is on. Null (tests, hosts without it) omits the field.</param>
    /// <param name="surface">Live tool collection snapshot; null (tests, modes without it) keeps the declared-versus-allowed answer.</param>
    public ServerStatusTools(
        IWorkspaceManager workspaceManager,
        ActiveToolSurface activeToolSurface,
        StoppedByScriptMarker stoppedByScriptMarker,
        ToolsetService? toolsets = null,
        SentinelHostOptions? hostOptions = null,
        ToolTelemetry? toolTelemetry = null,
        GitBackendWarmup? gitBackend = null,
        ToolLoopLatch? loopLatch = null,
        ToolSurfaceController? surface = null)
    {
        _loopLatch = loopLatch;
        _surface = surface;
        _gitBackend = gitBackend ?? GitBackendWarmup.Shared;
        _toolTelemetry = toolTelemetry;
        _hostOptions = hostOptions;
        _workspaceManager = workspaceManager;
        _activeToolSurface = activeToolSurface;
        _stoppedByScriptMarker = stoppedByScriptMarker;
        _toolsets = toolsets;
    }

    [McpServerTool(Name = ToolName, ReadOnly = true)]
    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)] // read-only diagnostic an operator or agent needs while halted (breaker filter refuses every tool not on the allow-list)
    [InlineBudget(InlineBudgetClass.Whole)] // listing/settings calls (22-34K chars) are needed whole; see plan_inline_budget_classes
    [Produces(DataTag.ResultOnly)]
    [Description("Diagnostic snapshot: server build identity (serverVersion, serverBuildTimeUtc, serverBinaryPath, serverPid, buildSha, binaryStaleness = commits since buildSha touching server source, else loaded assemblies with a newer build on disk), session-halt state, circuit breaker, loaded workspace (isFreshStartup = this server process has not loaded a solution yet, e.g. just restarted), active tool-mode resolution. " +
        "Tools are gated per mode: before concluding a tool does not exist, call with toolListing=inactive." +
        " solutionLoadStatus = NotLoaded | Loading | Loaded | Failed; tool calls wait up to 30 s for a Loading solution." +
        " workspaceStale = a workspace reload failed and the loaded solution may not match disk (see reloadFailure; recover with LoadSolution forceReload: true); lastLoadFailure = text of the last failed load." +
        " lastLaunch = the launcher's last build of HEAD (buildFailed = fell back to the older servedSha; firstErrors and hint say why)." +
        " operatingMode/operatingModeSource = SingleAgent or MultiAgent and why; callerAgentId = agent id of this call (null if none)." +
        " eolMode = line-ending policy for writes (--eol / ROSLYNSENTINEL_EOL): auto keeps each file's endings (else .editorconfig), crlf or lf forces one." +
        " clientProtocolVersion = this session's negotiated MCP version; toolListChangedRaised = tool-list changes applied (not proof of delivery); toolListSubscriptionRequests = tool-list subscriptions (2026-07-28 clients)." +
        " toolSurface.surfaceVersion counts changes to the live tool surface (surfaceTrigger and schemaPatches say why).")]
    public SentinelCallToolResult<McpServerStatusResult> McpServerStatusTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("inactive: declared tools not active in this mode, with an enabledBy hint. all: every declared tool.")]
        McpServerStatusToolListing toolListing = McpServerStatusToolListing.none,
        [Description("Case-insensitive substring of tool/class name; narrows toolListing.")]
        string? toolNameFilter = null,
        [Description("Startup settings to list with undeclared env names: nonDefault or all.")]
        McpServerStatusSettingsListing settings = McpServerStatusSettingsListing.none,
        CancellationToken cancellationToken = default,
        RequestContext<CallToolRequestParams>? requestContext = null)
    {
        _ = cancellationToken;

        // One snapshot per response: activeForThisMode, the declared-tool rows and toolSurface all read the same build.
        var surfaceSnapshot = _surface?.Current;
        var useSnapshot = surfaceSnapshot is { Version: > 0 };

        var manual = (IManualCircuitBreaker)_workspaceManager;
        var automatic = (IAutomaticCircuitBreaker)_workspaceManager;
        var unrecoverable = (IUnrecoverableBreaker)_workspaceManager;

        var declaredToolAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel", StringComparison.Ordinal) == true)
            .ToArray();
        // ServerStatusTools is registered outside ActiveToolClasses (always on), so it must be
        // special-cased or McpServerStatusTool would report itself as inactive.
        // A tool is active when its class is active and, under a per-tool allow-list (claude-lean),
        // its name is on it too.
        // A tool McpToolsetControlTool switched on is active too, whatever its class or the allow-list say.
        var dynamicToolNames = _toolsets?.DynamicToolNames.ToHashSet(StringComparer.Ordinal) ?? [];
        // With a live snapshot, active means exposed in it (startup registration plus runtime toolsets).
        // Without one, the class and allow-list check applies as before.
        bool IsActive(string className, string toolName) =>
            useSnapshot
                ? surfaceSnapshot!.IsExposed(toolName)
                : (dynamicToolNames.Contains(toolName)
                    || ((className == nameof(ServerStatusTools) || _activeToolSurface.ActiveToolClasses.Contains(className))
                        && _activeToolSurface.IsToolAllowed(toolName)));

        // Each tool name is declared by exactly one class (guarded by McpServerStatusToolListingTests.EveryToolName_IsDeclaredByExactlyOneClass).
        var declaredTools = McpToolSchemaPatcher.DiscoverAllDeclaredTools(declaredToolAssemblies)
            .Select(t =>
            {
                var row = useSnapshot ? surfaceSnapshot!.Find(t.ToolName) : null;
                return new McpServerStatusDeclaredTool(
                    Name: t.ToolName,
                    ClassName: t.ClassName,
                    ActiveForThisMode: IsActive(t.ClassName, t.ToolName),
                    EnabledBy: IsActive(t.ClassName, t.ToolName) ? null : DescribeHowToEnable(t.ClassName, t.ToolName, useSnapshot),
                    ExposedBy: row?.Origin switch
                    {
                        ToolSurfaceOrigin.Startup => "startup",
                        ToolSurfaceOrigin.RuntimeToolset => "toolset:" + row!.ToolSet,
                        _ => null,
                    },
                    HiddenParameters: row is { HiddenParameters.Count: > 0 } ? row.HiddenParameters : null);
            })
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();
        var allDeclaredTools = declaredTools
            .Where(t => toolListing == McpServerStatusToolListing.all
                || (toolListing == McpServerStatusToolListing.inactive && !t.ActiveForThisMode))
            .Where(t => string.IsNullOrWhiteSpace(toolNameFilter)
                || t.Name.Contains(toolNameFilter, StringComparison.OrdinalIgnoreCase)
                || t.ClassName.Contains(toolNameFilter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var reloadFailure = (_workspaceManager as IWorkspaceReloadFailureProvider)?.ReloadFailure;

        return new SentinelCallToolResult<McpServerStatusResult>
        {
            IsError = false,
            StatusMessage = "McpServerStatus executed successfully.",
            SuccessData = new McpServerStatusResult(
                ServerPid: Environment.ProcessId,
                ServerVersion: ServerBuildInfo.Version,
                // On-disk DLL mtime, not the loaded assembly's identity: compare against the last source edit.
                ServerBuildTimeUtc: ServerBuildInfo.BuildTimeUtc,
                ServerBinaryPath: ServerBuildInfo.BinaryPath,
                BinaryStaleness: ServerBinaryStaleness.CheckNow(),
                SessionHalted: _workspaceManager.IsSessionHalted(),
                IsFreshStartup: _workspaceManager.LoadState.IsFreshStartup,
                SolutionLoadStatus: _workspaceManager.SolutionLoadStatus,
                ServerStartedUtc: _workspaceManager.LoadState.ServerStartedUtc,
                SolutionPath: _workspaceManager.SolutionPath,
                ProjectCount: _workspaceManager.ProjectCount,
                WorkspaceVersion: _workspaceManager.WorkspaceVersion,
            Breakers: new McpServerStatusBreakers(

                new McpServerStatusBreakerState(
                    Tripped: manual.IsTripped(),
                    Message: manual.StateMessage()
                ),
                new McpServerStatusBreakerState(
                    Tripped: automatic.IsTripped(),
                    Message: automatic.StateMessage()
                ),
                new McpServerStatusBreakerState(
                    Tripped: unrecoverable.IsTripped(),
                    Message: unrecoverable.StateMessage()
                )
            ),
            ToolSurface: new McpServerStatusToolSurface(
                ModeArg: _activeToolSurface.ModeArg,
                ActiveModes: _activeToolSurface.ActiveModes,
                IncludeTools: _activeToolSurface.IncludeTools,
                ExcludeTools: _activeToolSurface.ExcludeTools,
                ActiveToolClassCount: _activeToolSurface.ActiveToolClasses.Count,
                ActiveToolClasses: _activeToolSurface.ActiveToolClasses,
                SurfaceVersion: useSnapshot ? surfaceSnapshot!.Version : null,
                SurfaceBuiltUtc: useSnapshot ? surfaceSnapshot!.BuiltUtc : null,
                SurfaceTrigger: useSnapshot ? surfaceSnapshot!.Trigger : null,
                SchemaPatches: useSnapshot ? surfaceSnapshot!.SchemaPatches : null,
                // Parameter provenance rides with a tool listing only, and only when non-empty (null = omitted from the wire).
                HiddenParameterGroups: useSnapshot && toolListing != McpServerStatusToolListing.none
                    && surfaceSnapshot!.HiddenParameterGroups is { Count: > 0 } hiddenGroups ? hiddenGroups : null,
                UnmatchedHideRules: useSnapshot && toolListing != McpServerStatusToolListing.none
                    && surfaceSnapshot!.UnmatchedHideRules is { Count: > 0 } unmatchedRules ? unmatchedRules : null
            ),
            StoppedByScript: new McpServerStatusStoppedByScript(
                WasFound: _stoppedByScriptMarker.WasFound,
                Details: _stoppedByScriptMarker.Details
            ),
            DeclaredToolCount: declaredTools.Length,
            InactiveToolCount: declaredTools.Count(t => !t.ActiveForThisMode),
            AllDeclaredTools: allDeclaredTools,
            WorkspaceStale: reloadFailure is not null,
            ReloadFailure: reloadFailure,
            LastLoadFailure: _workspaceManager.LoadState.LastLoadFailure,
            // Where this process writes logs/crash.log/tool_list*.json (--working-dir, else the binary directory).
            WorkingDir: ServerInstancePaths.Current.WorkingDir,
            WorkingDirExplicit: ServerInstancePaths.Current.IsExplicit,
            // Commit this binary was built from (null for an unstamped local build).
            BuildSha: ServerBuildInfo.BuildSha,
            // Effective --respawn (auto | none): whether anything restarts this process after StopServer.
            Respawn: (_hostOptions?.Respawn ?? RespawnPolicy.Auto).ToString().ToLowerInvariant(),
            // The launcher's last build outcome (null when no marker: legacy layout, HTTP, no launcher).
            LastLaunch: LaunchBuildMarker.Current,
            // Only once a call has been recorded; the call that produces this status is not counted yet (the telemetry
            // filter records after the call completes), so the very first status of a process omits the field.
            ToolTelemetry: _toolTelemetry is { HasCalls: true } ? _toolTelemetry.Snapshot() : null,
            // The mode is fixed per process (see AgentOperatingModeOptions), so the main session can read it here.
            OperatingMode: AgentOperatingModeOptions.Effective.ToString(),
            OperatingModeSource: AgentOperatingModeOptions.Source,
            CallerAgentId: ToolCallContext.Current?.AgentId,
            // The stdio server's own idle restart; LastUnmetGuard and LastCheckedUtc come from the monitor's last tick.
            IdleRestart: new McpServerStatusIdleRestart(
                Enabled: IdleRestartOptions.Enabled,
                Minutes: IdleRestartOptions.Minutes,
                LastActivityUtc: ServerActivityTracker.Shared.LastActivityUtc,
                InFlightCount: ServerActivityTracker.Shared.InFlightCount,
                LastUnmetGuard: IdleRestartState.LastUnmetGuard,
                LastCheckedUtc: IdleRestartState.LastCheckedUtc),
            // The only waiter on the git backend warmup: bounded (about 2 s), "warming" on timeout, never throws.
            GitBackend: _gitBackend.Report(GitBackendWarmup.StatusWait),
            // Beside the orientation breaker's state, but only while the latch is switched on (null = field omitted from the wire).
            LoopLatch: LoopLatchOptions.Enabled && _loopLatch is not null ? _loopLatch.Snapshot() : null,
            // Process-wide policy; PersistentWorkspaceManager.EolModeOverride is a test seam and is not reported.
            EolMode: EolPolicyOptions.Mode.ToString().ToLowerInvariant(),
            // Negotiated MCP version of the calling session; null when the call carries no request context (tests, in-process callers).
            ClientProtocolVersion: requestContext?.JsonRpcRequest.Context?.ProtocolVersion ?? requestContext?.Server.NegotiatedProtocolVersion,
            // Tool-list change scopes this process applied; not proof a client received notifications/tools/list_changed.
            ToolListChangedRaised: _toolsets?.ToolListChangedRaised,
            // subscriptions/listen requests asking for tool-list notifications (2026-07-28 clients); null without ToolsetService.
            ToolListSubscriptionRequests: _toolsets?.ToolListSubscriptionRequests,
            // Always present: problems, halt state and the undeclared-env count; the lists ride with settings != none.
            Configuration: BuildConfiguration(settings)
        )
        };
    }

    private static McpServerStatusConfiguration BuildConfiguration(McpServerStatusSettingsListing settings)
    {
        var report = StartupSettings.LastReport;
        var listed = settings != McpServerStatusSettingsListing.none;

        IReadOnlyList<McpServerStatusSettingGroup>? groups = null;
        if (listed)
        {
            groups = report.Settings
                .Where(s => settings == McpServerStatusSettingsListing.all || s.Source != SettingSource.Default)
                .GroupBy(s => s.Scope)
                .OrderBy(g => (int)g.Key)
                .Select(g => new McpServerStatusSettingGroup(
                    // One entry per set flag so the wire never carries a combined flags string.
                    Scopes: Enum.GetValues<SettingScope>().Where(f => f != SettingScope.None && g.Key.HasFlag(f)).ToArray(),
                    Settings: g.Select(s => new McpServerStatusSetting(
                        Flag: s.Flag,
                        Env: s.EnvName,
                        Value: s.EffectiveText,
                        Source: s.Source)).ToArray()))
                .ToArray();
        }

        return new McpServerStatusConfiguration(
            Problems: report.Problems,
            Halted: report.Halted,
            UndeclaredEnvCount: report.UndeclaredEnv.Count,
            UndeclaredEnv: listed ? report.UndeclaredEnv : null,
            UndeclaredEnvHint: !listed && report.UndeclaredEnv.Count > 0 ? StartupSettings.UndeclaredEnvHint : null,
            SettingGroups: groups);
    }

    private string DescribeHowToEnable(string className, string toolName, bool useSnapshot)
    {
        // Snapshot does not expose the tool although its class is active and the allow-list passes: registration is missing.
        if (useSnapshot
            && (className == nameof(ServerStatusTools) || _activeToolSurface.ActiveToolClasses.Contains(className))
            && _activeToolSurface.IsToolAllowed(toolName))
        {
            return "declared and in an active class, but not registered with the server (ToolClassRegistry entry or DI block missing)";
        }

        if (_activeToolSurface.ExcludeTools.Contains(className))
        {
            return $"excluded by --exclude-tools {className}; remove it from --exclude-tools";
        }

        // claude-lean with McpToolsetControlTool: a tool in an on-demand set is enabled at runtime, not by a restart.
        if (_toolsets is not null && ToolsetCatalog.FindSet(toolName) is { } toolSet)
        {
            return ToolCallHint.Call("McpToolsetControl", ("toolSet", toolSet.ToString()), ("enabled", "true"));
        }

        // A per-tool allow-list (claude-lean) filters the tool out whether or not its class is active.
        // Point at the wider modes that carry the class, never at --include-tools, which cannot widen
        // an allow-list.
        if (!_activeToolSurface.IsToolAllowed(toolName))
        {
            var widerModes = _activeToolSurface.ClassModes.TryGetValue(className, out var carrying)
                ? carrying.Where(m => !_activeToolSurface.ActiveModes.Contains(m, StringComparer.OrdinalIgnoreCase)).ToArray()
                : [];
            return widerModes.Length > 0
                ? $"not in the claude-lean core toolset; restart with --mode {string.Join(" or ", widerModes)} instead of claude-lean"
                : "not in the claude-lean core toolset; restart without --mode claude-lean";
        }

        return _activeToolSurface.ClassModes.TryGetValue(className, out var modes) && modes.Count > 0
            ? $"--mode {string.Join(" or ", modes)}, or --include-tools {className}"
            : $"--include-tools {className}";
    }
}
/// <summary>Which declared tools McpServerStatusTool lists in <c>AllDeclaredTools</c>.</summary>
public enum McpServerStatusToolListing
{
    none,
    inactive,
    all
}
/// <summary>Whether McpServerStatusTool lists the effective startup settings (and undeclared env names) in <c>Configuration</c>.</summary>
public enum McpServerStatusSettingsListing
{
    none,
    nonDefault,
    all
}

/// <summary>
/// Startup configuration state from <c>StartupSettings.LastReport</c>. Problems, Halted and UndeclaredEnvCount are always
/// present; UndeclaredEnv and SettingGroups only when a settings listing was requested; UndeclaredEnvHint only when the
/// list is omitted and undeclared names exist.
/// </summary>
public sealed record McpServerStatusConfiguration(
    IReadOnlyList<ConfigurationProblem> Problems,
    bool Halted,
    int UndeclaredEnvCount,
    IReadOnlyList<string>? UndeclaredEnv,
    string? UndeclaredEnvHint,
    IReadOnlyList<McpServerStatusSettingGroup>? SettingGroups);

/// <summary>One startup setting row: Value is the effective text (an External setting reports "set" or "unset").</summary>
public sealed record McpServerStatusSetting(string? Flag, string? Env, string Value, SettingSource Source);

/// <summary>Startup setting rows that share one scope set; Scopes lists each applicable host.</summary>
public sealed record McpServerStatusSettingGroup(IReadOnlyList<SettingScope> Scopes, IReadOnlyList<McpServerStatusSetting> Settings);

// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>A single circuit breaker's tripped state and message, as reported by McpServerStatusTool.</summary>
public sealed record McpServerStatusBreakerState(bool Tripped, string? Message);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>Circuit breaker states reported by <see cref="McpServerStatusResult"/>.</summary>
public sealed record McpServerStatusBreakers(
    McpServerStatusBreakerState Manual,
    McpServerStatusBreakerState Automatic,
    McpServerStatusBreakerState Unrecoverable);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>Active --mode/--include-tools/--exclude-tools resolution, as reported by McpServerStatusTool.</summary>
public sealed record McpServerStatusToolSurface(
    string ModeArg,
    IReadOnlyCollection<string> ActiveModes,
    IReadOnlyCollection<string> IncludeTools,
    IReadOnlyCollection<string> ExcludeTools,
    int ActiveToolClassCount,
    IReadOnlyCollection<string> ActiveToolClasses,
    // Live snapshot fields: null without a ToolSurfaceController (tests, modes without one).
    long? SurfaceVersion = null,
    DateTime? SurfaceBuiltUtc = null,
    string? SurfaceTrigger = null,
    IReadOnlyList<string>? SchemaPatches = null,
    // Parameter provenance (stage D): null unless a tool listing was requested and the snapshot has entries.
    IReadOnlyList<HiddenParameterGroup>? HiddenParameterGroups = null,
    IReadOnlyList<ParameterVisibilityRule>? UnmatchedHideRules = null);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>Whether this instance's predecessor was deliberately stopped by
/// roslynsentinel-vscode-control.ps1's stopallstdio/stopallhttp/stopalltypes actions, as reported by
/// McpServerStatusTool.</summary>
public sealed record McpServerStatusStoppedByScript(bool WasFound, string? Details);
/// <summary>
/// The stdio server's own idle restart (IdleRestartMonitor). When Enabled, the server exits once it has been
/// idle for Minutes and the launcher has a newer ready build for HEAD. LastUnmetGuard names what currently keeps
/// it running; values are the IdleRestartPolicy.Guard* constants. LastCheckedUtc is null until the monitor has
/// ticked. InFlightCount includes the McpServerStatus call that produces this record, since that call runs inside
/// the tool-telemetry filter.
/// </summary>
public sealed record McpServerStatusIdleRestart(bool Enabled, int Minutes, DateTime LastActivityUtc, int InFlightCount, string? LastUnmetGuard, DateTime? LastCheckedUtc);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Success payload of <see cref="ServerStatusTools.McpServerStatusTool"/>.
/// </summary>
public sealed record McpServerStatusResult(
    int ServerPid,
    string ServerVersion,
    DateTime ServerBuildTimeUtc,
    string ServerBinaryPath,
    ServerBinaryStalenessReport BinaryStaleness,
    bool SessionHalted,
    bool IsFreshStartup,
    SolutionLoadStatus SolutionLoadStatus,
    DateTime ServerStartedUtc,
    string? SolutionPath,
    int ProjectCount,
    int WorkspaceVersion,
    McpServerStatusBreakers Breakers,
    McpServerStatusToolSurface ToolSurface,
    McpServerStatusStoppedByScript StoppedByScript,
    int DeclaredToolCount,
    int InactiveToolCount,
    IReadOnlyCollection<McpServerStatusDeclaredTool> AllDeclaredTools,
    bool WorkspaceStale,
    WorkspaceReloadFailure? ReloadFailure,
    string? LastLoadFailure,
    string? WorkingDir = null,
    bool WorkingDirExplicit = false,
    string? BuildSha = null,
    string? Respawn = null,
    LaunchBuildResult? LastLaunch = null,
    ToolTelemetrySnapshot? ToolTelemetry = null,
    // OperatingMode: effective agent operating mode (Auto, SingleAgent, MultiAgent).
    // OperatingModeSource: where it came from. CallerAgentId: agent id of the call that asked (null when the host supplies none).
    string? OperatingMode = null,
    string? OperatingModeSource = null,
    string? CallerAgentId = null,
    McpServerStatusIdleRestart? IdleRestart = null,
    // Libgit2 backend health: active backend, native load, repo probe and the seam's fallback counters (GitBackendWarmup).
    GitBackendSnapshot? GitBackend = null,
    // Latched tools per agent (empty = none latched); present only while --loop-latch is on.
    IReadOnlyList<LoopLatchAgentState>? LoopLatch = null,
    // Effective --eol write policy (auto | crlf | lf), fixed per process by EolPolicyOptions.
    string? EolMode = null,
    // Negotiated MCP protocol version of the calling session (null without a request context).
    string? ClientProtocolVersion = null,
    // Tool-list change scopes ToolsetService applied in this process (null when no ToolsetService is injected).
    int? ToolListChangedRaised = null,
    int? ToolListSubscriptionRequests = null,
    // Startup configuration registry state (problems, halt, undeclared env); lists only with settings != none.
    McpServerStatusConfiguration? Configuration = null);
/// <summary>
/// One <see cref="McpServerToolAttribute"/>-carrying method discovered via reflection, as reported
/// by McpServerStatusTool's <c>AllDeclaredTools</c>. Ground truth for "does this tool exist in this
/// process" independent of whether its class is currently active for this session's mode.
/// </summary>
// ExposedBy (startup | toolset:<name>) and HiddenParameters come from the live snapshot row; null when not exposed or no snapshot.
public sealed record McpServerStatusDeclaredTool(string Name, string ClassName, bool ActiveForThisMode, string? EnabledBy = null, string? ExposedBy = null, IReadOnlyList<string>? HiddenParameters = null);
