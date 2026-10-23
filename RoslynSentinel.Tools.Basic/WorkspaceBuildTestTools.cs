using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

[McpServerToolType]
public class WorkspaceBuildTestTools
{
    private readonly WorkspaceBuildTestImpl _impl;

    public WorkspaceBuildTestTools(WorkspaceBuildTestImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "GetDiagnostics")]
    [Produces(DataTag.Report)]
    [Description("Gets compiler diagnostics for a file, project, or the whole solution.")]
    public Task<SentinelCallToolResult<object>> GetDiagnosticsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("file/project: also pass scopeName. solution: scopeName is ignored.")]
        [Consumes(DataTag.ProjectName, required: true)][Consumes(DataTag.SourceFilepath, required: false)] ToolScope scope = ToolScope.solution,
        [Description("Required for scope=file (a filePath) or scope=project (a projectName). Ignored for scope=solution.")]
        string? scopeName = null,
        [Description("Groups results by diagnostic ID and returns counts instead of the raw list.")]
        bool summarize = false,
        [Description("Caps the raw diagnostics list. Ignored when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        [Description("Caps the number of groups returned. Only used when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.TopN)] int topN = 20,
        [Description("noBuild (default): diagnostics only. quickBuild/fullBuild/fullBuildThrowaway: additionally runs a build check (see Build tool) and attaches it as BuildVerification.")]
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _impl.GetDiagnostics(reason, scope, scopeName, summarize, maxDetails, topN, verify, cancellationToken);

    [McpServerTool(Name = "Build")]
    [Produces(DataTag.Report)]
    [Description(ToolParams.BuildDescription)]
    public Task<SentinelCallToolResult<object>> BuildTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.BuildLevel)] BuildVerifyLevel level = BuildVerifyLevel.fullBuildThrowaway,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 20,
        [Description(ToolParams.UseScratchDir)] bool useScratchDir = false,
        [Description("true = also return the build's stdout/stderr tail. Default false.")] bool includeOutput = false,
        [Description("true = also return warnings, capped by maxDetails. Default false: count only.")] bool includeWarnings = false,
        CancellationToken cancellationToken = default)
        => _impl.Build(reason, level, scope, scopeName, maxDetails, useScratchDir, includeOutput, includeWarnings, cancellationToken);

    [McpServerTool(Name = "RunTest")]
    [Produces(DataTag.Report)]
    [Description(ToolParams.RunTestDescription)]
    public Task<SentinelCallToolResult<object>> RunTestTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("solution (default) runs every test project one after another; project runs only the project named in scopeName; file is not supported.")] ToolScope scope = ToolScope.solution,
        [Description("Project name (for example RoslynSentinel.Tests.Basic) when scope=project; not a parameter called projectName.")] string? scopeName = null,
        [Description("Passed to `dotnet test --filter`. With scope=solution it still builds every test project (about 1.5 minutes); prefer scope=project with scopeName.")] string? filter = null,
        TestResultsFilter resultsType = TestResultsFilter.failed,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        int timeoutSeconds = 600,
        [Description("true = counts and FailureSummary only, no Results list.")] bool summary = false,
        [Description(ToolParams.UseScratchDir)] bool useScratchDir = false,
        [Description("true = keep failed tests' Output and the stdout/stderr tails. Default false: compact digest, full result behind FullResultsId.")] bool includeOutput = false,
        [Description("Extra env vars for the test process (name -> value).")] Dictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
        => _impl.RunTest(reason, scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, useScratchDir, includeOutput, environment, cancellationToken);
}
