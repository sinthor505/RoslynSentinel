using System.ComponentModel;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Basic;

public class SentinelServerStatusTools
{
    // Added by AddMember (expected - used for diagnostics)
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ActiveToolSurface _activeToolSurface;
    private readonly StoppedByScriptMarker _stoppedByScriptMarker;

    public SentinelServerStatusTools(
        IWorkspaceManager workspaceManager,
        ActiveToolSurface activeToolSurface,
        StoppedByScriptMarker stoppedByScriptMarker)
    {
        _workspaceManager = workspaceManager;
        _activeToolSurface = activeToolSurface;
        _stoppedByScriptMarker = stoppedByScriptMarker;
    }

    [McpServerTool(Name = "McpServerStatus", UseStructuredContent = true, OutputSchemaType = typeof(McpServerStatusResult))]
    [Produces(DataTag.ResultOnly)]
    [Description("Always-available diagnostic snapshot: session-halt state, circuit breaker status, the loaded workspace, and today's active --mode/--include-tools/--exclude-tools resolution. Call this first when another tool is missing, a call fails unexpectedly, or the session seems stuck. No parameters.")]
    public object McpServerStatus(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var manual = (IManualCircuitBreaker)_workspaceManager;
        var automatic = (IAutomaticCircuitBreaker)_workspaceManager;
        var unrecoverable = (IUnrecoverableBreaker)_workspaceManager;

        return new
        {
            sessionHalted = _workspaceManager.IsSessionHalted(),
            solutionPath = _workspaceManager.SolutionPath,
            projectCount = _workspaceManager.ProjectCount,
            workspaceVersion = _workspaceManager.WorkspaceVersion,
            breakers = new
            {
                manual = new
                {
                    tripped = manual.IsTripped(),
                    message = manual.StateMessage()
                },
                automatic = new
                {
                    tripped = automatic.IsTripped(),
                    message = automatic.StateMessage()
                },
                unrecoverable = new
                {
                    tripped = unrecoverable.IsTripped(),
                    message = unrecoverable.StateMessage()
                },
            },
            toolSurface = new
            {
                modeArg = _activeToolSurface.ModeArg,
                activeModes = _activeToolSurface.ActiveModes,
                includeTools = _activeToolSurface.IncludeTools,
                excludeTools = _activeToolSurface.ExcludeTools,
                activeToolClassCount = _activeToolSurface.ActiveToolClasses.Count,
                activeToolClasses = _activeToolSurface.ActiveToolClasses,
            },
            stoppedByScript = new
            {
                wasFound = _stoppedByScriptMarker.WasFound,
                details = _stoppedByScriptMarker.Details,
            },
        };
    }
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
/// Named shape mirroring <see cref="SentinelServerStatusTools.McpServerStatus"/>'s anonymous return
/// object, used only as <c>OutputSchemaType</c> so the tool can advertise a real MCP
/// <c>outputSchema</c>/<c>StructuredContent</c> shape (2026-07-28 protocol) without changing the
/// method's actual return type.
/// </summary>
public sealed record McpServerStatusResult(
    bool SessionHalted,
    string? SolutionPath,
    int ProjectCount,
    int WorkspaceVersion,
    McpServerStatusBreakers Breakers,
    McpServerStatusToolSurface ToolSurface,
    McpServerStatusStoppedByScript StoppedByScript);
