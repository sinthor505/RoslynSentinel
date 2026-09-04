using System.ComponentModel;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

// Restricted/operator-only tools, gated behind the "Admin" mode (deliberately excluded from
// AllModes in ServerStdio.cs/ServerHttp.cs, so it's off by default and only reachable via an
// explicit --mode=Admin or --mode=<...>,Admin). See
// docs/current/ideas/external-drift-hard-blocker.md — these two tools used to live in
// SentinelWorkspaceTools (model-visible by default under the "Workspace" mode), but letting the
// in-task model reconcile external drift itself only works for a genuinely concurrent-editing
// scenario this server doesn't target; under the single-session/no-concurrent-actors assumption a
// real drift hit should stop the session, not be something the model talks its way past. This
// class is also the intended home for any future restricted/operator-only tool, not a one-off.
[McpServerToolType]
public class SentinelAdminTools
{
    private readonly IWorkspaceManager _workspaceManager;

    public SentinelAdminTools(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    [McpServerTool(Name = "ListExternalDiskChanges")]
    [Produces(DataTag.FileList)]
    [Description("Returns files modified on disk since the AI last synced. No parameters.")]
    public List<string> ListExternalDiskChanges(
    [Description(ToolParams.Reason)] string reason,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return _workspaceManager.GetExternalFileChanges();
    }

    [McpServerTool(Name = "IsSessionHalted")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns whether the session-wide fatal drift latch is currently set. No parameters.")]
    public bool IsSessionHalted(
    [Description(ToolParams.Reason)] string reason,
    CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return _workspaceManager.IsSessionHalted();
    }

    [McpServerTool(Name = "AcknowledgeExternalFileChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Clears the external-change list and, if set, the session-wide fatal drift latch, after an operator has reviewed the disk changes. No parameters.")]
    public string AcknowledgeExternalFileChanges(
    [Description(ToolParams.Reason)] string reason,
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
}
