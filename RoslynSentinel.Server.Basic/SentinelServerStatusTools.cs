using System.ComponentModel;

using ModelContextProtocol.Server;

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

    [McpServerTool(Name = "McpServerStatus")]
    [Produces(DataTag.ResultOnly)]
    [Description("Diagnostic snapshot: session-halt state, circuit breaker, loaded workspace, active tool-mode resolution.")]
    public object McpServerStatus(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var manual = (IManualCircuitBreaker)_workspaceManager;
        var automatic = (IAutomaticCircuitBreaker)_workspaceManager;
        var unrecoverable = (IUnrecoverableBreaker)_workspaceManager;


        return new SentinelCallToolResult<McpServerStatusResult>
        {
            IsSuccess = true,
            StatusMessage = "McpServerStatus executed successfully.",
            SuccessData = new McpServerStatusResult(
                ServerPid: Environment.ProcessId,
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
            )
        )
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
    int ServerPid,
    bool SessionHalted,
    string? SolutionPath,
    int ProjectCount,
    int WorkspaceVersion,
    McpServerStatusBreakers Breakers,
    McpServerStatusToolSurface ToolSurface,
    McpServerStatusStoppedByScript StoppedByScript);
