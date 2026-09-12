using System.ComponentModel;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Basic;

public class SentinelServerStatusTools
{
    // Added by AddMember (expected - used for diagnostics)
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ActiveToolSurface _activeToolSurface;

    public SentinelServerStatusTools(IWorkspaceManager workspaceManager, ActiveToolSurface activeToolSurface)
    {
        _workspaceManager = workspaceManager;
        _activeToolSurface = activeToolSurface;
    }

    [McpServerTool(Name = "McpServerStatus")]
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
                manual = new { tripped = manual.IsTripped(), message = manual.StateMessage() },
                automatic = new { tripped = automatic.IsTripped(), message = automatic.StateMessage() },
                unrecoverable = new { tripped = unrecoverable.IsTripped(), message = unrecoverable.StateMessage() },
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
        };
    }}
