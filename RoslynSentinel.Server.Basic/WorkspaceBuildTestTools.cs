using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

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
    public Task<SentinelCallToolResult<object>> GetDiagnostics(
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
        [Description("noBuild (default): diagnostics only. quickBuild/fullBuild: additionally runs a build check (see Build tool) and attaches it as BuildVerification.")]
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _impl.GetDiagnostics(reason, scope, scopeName, summarize, maxDetails, topN, verify, cancellationToken);

    [McpServerTool(Name = "Build")]
    [Produces(DataTag.Report)]
    [Description("Compiles the loaded solution and reports errors/warnings. level=quickBuild uses in-memory Roslyn diagnostics (fast, same check GetDiagnostics does). level=fullBuild shells out to `dotnet build` (slower, catches MSBuild-only failures - NuGet restore, resource copy, post-build events - that quickBuild can't see). Returns BuildSucceeded, ExitCode, ErrorCount/WarningCount, capped Errors/Warnings lists, ErrorSummary/WarningSummary (grouped by diagnostic Id, uncapped, for spotting one cause behind many errors), Duration.")]
    public Task<SentinelCallToolResult<object>> Build(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel level = BuildVerifyLevel.fullBuild,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        CancellationToken cancellationToken = default)
        => _impl.Build(reason, level, scope, scopeName, maxDetails, cancellationToken);

    [McpServerTool(Name = "RunTest")]
    [Produces(DataTag.Report)]
    [Description("Runs `dotnet test` against the loaded solution (or a single project) and reports structured results. Returns TotalCount/PassedCount/FailedCount/SkippedCount, a FailureSummary grouping failures by message signature (e.g. \"45 of 50 failures share one cause\") so an agent doesn't have to paginate to notice a pattern, and a capped Results list (filtered by resultsType, then capped by maxDetails). resultsType defaults to \"failed\" so a clean run stays a short summary with no per-test list; pass \"all\" to see every test's outcome. Set summary=true to omit the Results list entirely (just counts + FailureSummary), regardless of resultsType. filter is passed through to `dotnet test --filter` - an unresolvable filter expression is a distinct error from a filter that resolves but matches zero tests.")]
    public Task<SentinelCallToolResult<object>> RunTest(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        string? filter = null,
        TestResultsFilter resultsType = TestResultsFilter.failed,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        int timeoutSeconds = 600,
        [Description("If true, omit the per-test Results list from the response entirely - only counts and FailureSummary are returned, independent of resultsType.")] bool summary = false,
        CancellationToken cancellationToken = default)
        => _impl.RunTest(reason, scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, cancellationToken);
}
