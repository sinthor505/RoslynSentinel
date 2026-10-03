using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

public class ServerStatusTools
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ActiveToolSurface _activeToolSurface;
    private readonly StoppedByScriptMarker _stoppedByScriptMarker;
    private readonly ToolsetService? _toolsets;

    /// <param name="toolsets">Present only in claude-lean, where McpToolsetControl can switch tools on at runtime.</param>
    public ServerStatusTools(
        IWorkspaceManager workspaceManager,
        ActiveToolSurface activeToolSurface,
        StoppedByScriptMarker stoppedByScriptMarker,
        ToolsetService? toolsets = null)
    {
        _workspaceManager = workspaceManager;
        _activeToolSurface = activeToolSurface;
        _stoppedByScriptMarker = stoppedByScriptMarker;
        _toolsets = toolsets;
    }

    [McpServerTool(Name = "McpServerStatus")]
    [Produces(DataTag.ResultOnly)]
    [Description("Diagnostic snapshot: server build identity (serverVersion, serverBuildTimeUtc, serverBinaryPath, serverPid, binaryStaleness = loaded assemblies with a newer build on disk), session-halt state, circuit breaker, loaded workspace, active tool-mode resolution. " +
        "Tools are gated per mode: before concluding a tool does not exist, call with toolListing=inactive.")]
    public object McpServerStatus(
        [Description("inactive: declared tools not active in this mode, with an enabledBy hint. all: every declared tool.")]
        McpServerStatusToolListing toolListing = McpServerStatusToolListing.none,
        [Description("Case-insensitive substring of tool/class name; narrows toolListing.")]
        string? toolNameFilter = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var manual = (IManualCircuitBreaker)_workspaceManager;
        var automatic = (IAutomaticCircuitBreaker)_workspaceManager;
        var unrecoverable = (IUnrecoverableBreaker)_workspaceManager;

        var declaredToolAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("RoslynSentinel", StringComparison.Ordinal) == true)
            .ToArray();
        // ServerStatusTools is registered outside ActiveToolClasses (always on), so it must be
        // special-cased or McpServerStatus would report itself as inactive.
        // A tool is active when its class is active and, under a per-tool allow-list (claude-lean),
        // its name is on it too.
        // A tool McpToolsetControl switched on is active too, whatever its class or the allow-list say.
        var dynamicToolNames = _toolsets?.DynamicToolNames.ToHashSet(StringComparer.Ordinal) ?? [];
        bool IsActive(string className, string toolName) =>
            dynamicToolNames.Contains(toolName)
            || ((className == nameof(ServerStatusTools) || _activeToolSurface.ActiveToolClasses.Contains(className))
                && _activeToolSurface.IsToolAllowed(toolName));

        // The same tool name can be declared by more than one class (a facade and the split class
        // behind it); report one entry per name, preferring the class that is actually active.
        var declaredTools = McpToolSchemaPatcher.DiscoverAllDeclaredTools(declaredToolAssemblies)
            .GroupBy(t => t.ToolName, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(t => IsActive(t.ClassName, t.ToolName)).ThenBy(t => t.ClassName, StringComparer.Ordinal).First())
            .Select(t => new McpServerStatusDeclaredTool(
                Name: t.ToolName,
                ClassName: t.ClassName,
                ActiveForThisMode: IsActive(t.ClassName, t.ToolName),
                EnabledBy: IsActive(t.ClassName, t.ToolName) ? null : DescribeHowToEnable(t.ClassName, t.ToolName)))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();
        var allDeclaredTools = declaredTools
            .Where(t => toolListing == McpServerStatusToolListing.all
                || (toolListing == McpServerStatusToolListing.inactive && !t.ActiveForThisMode))
            .Where(t => string.IsNullOrWhiteSpace(toolNameFilter)
                || t.Name.Contains(toolNameFilter, StringComparison.OrdinalIgnoreCase)
                || t.ClassName.Contains(toolNameFilter, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new SentinelCallToolResult<McpServerStatusResult>
        {
            IsSuccess = true,
            StatusMessage = "McpServerStatus executed successfully.",
            SuccessData = new McpServerStatusResult(
                ServerPid: Environment.ProcessId,
                ServerVersion: ServerBuildInfo.Version,
                // On-disk DLL mtime, not the loaded assembly's identity: compare against the last source edit.
                ServerBuildTimeUtc: ServerBuildInfo.BuildTimeUtc,
                ServerBinaryPath: ServerBuildInfo.BinaryPath,
                BinaryStaleness: ServerBinaryStaleness.CheckNow(),
                SessionHalted: _workspaceManager.IsSessionHalted(),
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
                ActiveToolClasses: _activeToolSurface.ActiveToolClasses
            ),
            StoppedByScript: new McpServerStatusStoppedByScript(
                WasFound: _stoppedByScriptMarker.WasFound,
                Details: _stoppedByScriptMarker.Details
            ),
            DeclaredToolCount: declaredTools.Length,
            InactiveToolCount: declaredTools.Count(t => !t.ActiveForThisMode),
            AllDeclaredTools: allDeclaredTools
        )
        };
    }

    private string DescribeHowToEnable(string className, string toolName)
    {
        if (_activeToolSurface.ExcludeTools.Contains(className))
        {
            return $"excluded by --exclude-tools {className}; remove it from --exclude-tools";
        }

        // claude-lean with McpToolsetControl: a tool in an on-demand set is enabled at runtime, not by a restart.
        if (_toolsets is not null && ToolsetCatalog.FindSet(toolName) is { } toolSet)
        {
            return $"McpToolsetControl(toolSet: {toolSet}, enabled: true)";
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
/// <summary>Which declared tools McpServerStatus lists in <c>AllDeclaredTools</c>.</summary>
public enum McpServerStatusToolListing
{
    none,
    inactive,
    all
}
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>A single circuit breaker's tripped state and message, as reported by McpServerStatus.</summary>
public sealed record McpServerStatusBreakerState(bool Tripped, string? Message);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>Circuit breaker states reported by <see cref="McpServerStatusResult"/>.</summary>
public sealed record McpServerStatusBreakers(
    McpServerStatusBreakerState Manual,
    McpServerStatusBreakerState Automatic,
    McpServerStatusBreakerState Unrecoverable);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>Active --mode/--include-tools/--exclude-tools resolution, as reported by McpServerStatus.</summary>
public sealed record McpServerStatusToolSurface(
    string ModeArg,
    IReadOnlyCollection<string> ActiveModes,
    IReadOnlyCollection<string> IncludeTools,
    IReadOnlyCollection<string> ExcludeTools,
    int ActiveToolClassCount,
    IReadOnlyCollection<string> ActiveToolClasses);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>Whether this instance's predecessor was deliberately stopped by
/// roslynsentinel-vscode-control.ps1's stopallstdio/stopallhttp/stopalltypes actions, as reported by
/// McpServerStatus.</summary>
public sealed record McpServerStatusStoppedByScript(bool WasFound, string? Details);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Named shape mirroring <see cref="ServerStatusTools.McpServerStatus"/>'s anonymous return
/// object, used only as <c>OutputSchemaType</c> so the tool can advertise a real MCP
/// <c>outputSchema</c>/<c>StructuredContent</c> shape (2026-07-28 protocol) without changing the
/// method's actual return type.
/// </summary>
public sealed record McpServerStatusResult(
    int ServerPid,
    string ServerVersion,
    DateTime ServerBuildTimeUtc,
    string ServerBinaryPath,
    ServerBinaryStalenessReport BinaryStaleness,
    bool SessionHalted,
    string? SolutionPath,
    int ProjectCount,
    int WorkspaceVersion,
    McpServerStatusBreakers Breakers,
    McpServerStatusToolSurface ToolSurface,
    McpServerStatusStoppedByScript StoppedByScript,
    int DeclaredToolCount,
    int InactiveToolCount,
    IReadOnlyCollection<McpServerStatusDeclaredTool> AllDeclaredTools);
/// <summary>
/// One <see cref="McpServerToolAttribute"/>-carrying method discovered via reflection, as reported
/// by McpServerStatus's <c>AllDeclaredTools</c>. Ground truth for "does this tool exist in this
/// process" independent of whether its class is currently active for this session's mode.
/// </summary>
public sealed record McpServerStatusDeclaredTool(string Name, string ClassName, bool ActiveForThisMode, string? EnabledBy = null);
