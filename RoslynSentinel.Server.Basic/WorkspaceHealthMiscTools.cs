using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

[McpServerToolType]
public class WorkspaceHealthMiscTools
{
    private readonly WorkspaceHealthMiscImpl _impl;

    public WorkspaceHealthMiscTools(WorkspaceHealthMiscImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "Features")]
    [Produces(DataTag.Report)]
    [Description("Queries or updates feature flags.")]
    public Task<SentinelCallToolResult<object>> Features(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("list: returns all feature flags. get: returns only the flags named in names. update: batch-updates the flags named in enabled.")]
        FeaturesAction action,
        [Description("Required for action=get - the feature names to look up. Not used for list/update.")]
        List<string>? names = null,
        [Description("Required for action=update - [{Key: featureName, Value: bool}] pairs to apply. Not used for list/get.")]
        List<KeyValuePair<string, bool>>? enabled = null,
        [Description("Test-only: waits this many seconds before acting, to exercise MCP task polling/cancellation.")]
        int delaySeconds = 0,
        CancellationToken cancellationToken = default)
        => _impl.Features(reason, action, names, enabled, delaySeconds, cancellationToken);

    [McpServerTool(Name = "GetWorkspaceHealth")]
    [Produces(DataTag.ResultOnly)]
    [Description("Targeted workspace health check - reads actual workspace/solution state directly rather than environment probes. Returns IsOperational, HasLoadedSolution, LoadedSolutionPath, ProjectCount, DocumentCount, LoadErrors, Summary, StaleDocumentCount, RequiresReload, SampleStaleFiles. IsOperational=true + HasLoadedSolution=false means no solution loaded yet - not an error. RequiresReload=true means files changed on disk since the last LoadSolution call. verify=quickBuild/fullBuild additionally runs a build check and attaches it as BuildVerification.")]
    public Task<SentinelCallToolResult<WorkspaceHealthReport, ResultError>> GetWorkspaceHealth(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _impl.GetWorkspaceHealth(reason, verify, cancellationToken);
}
