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
    [Description("Compiles the loaded solution and reports the result. level=quickBuild uses in-memory Roslyn diagnostics (fast, same check GetDiagnostics does). level=fullBuild shells out to `dotnet build` (slower, catches MSBuild-only failures - NuGet restore, resource copy, post-build events - that quickBuild can't see). A green build returns the outcome, the projects compiled and the error/warning counts only. A failed build returns the error counts by project, by file and by diagnostic code, the first maxDetails (default 20) errors with the root-cause project's errors first, and SuppressedDownstreamErrorCount (errors in projects that failed only because a project they depend on failed). OmittedErrorCount says how many errors are not listed; FullDiagnosticsResultId holds every error and warning - read it with GetLargeResult. The stdout/stderr tails and the warnings list are left out unless you pass includeOutput / includeWarnings.")]
    public Task<SentinelCallToolResult<object>> Build(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel level = BuildVerifyLevel.fullBuild,
        ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 20,
        [Description("Only used by level=fullBuild. " + ToolParams.UseScratchDir)] bool useScratchDir = false,
        [Description("true = also return the stdout/stderr tail of the build, even when it succeeded or its errors were parsed. Default false: counts and errors only.")] bool includeOutput = false,
        [Description("true = also return the warnings list and warning summary, capped by maxDetails. Default false: only the warning count.")] bool includeWarnings = false,
        CancellationToken cancellationToken = default)
        => _impl.Build(reason, level, scope, scopeName, maxDetails, useScratchDir, includeOutput, includeWarnings, cancellationToken);

    [McpServerTool(Name = "RunTest")]
    [Produces(DataTag.Report)]
    [Description("Runs `dotnet test` against the loaded solution (or a single project) and reports structured results. Returns TotalCount/PassedCount/FailedCount/SkippedCount, a FailureSummary grouping failures by message signature (e.g. \"45 of 50 failures share one cause\") so an agent doesn't have to paginate to notice a pattern, and a capped Results list (filtered by resultsType, then capped by maxDetails). Each failed test entry also carries Output: the test's captured output (for example NUnit TestContext.Out / Console.Out or xUnit ITestOutputHelper), the last 2000 characters, present only for failed tests. resultsType defaults to \"failed\" so a clean run stays a short summary with no per-test list; pass \"all\" to see every test's outcome. Set summary=true to omit the Results list entirely (just counts + FailureSummary), regardless of resultsType. filter is passed through to `dotnet test --filter` - an unresolvable filter expression is a distinct error from a filter that resolves but matches zero tests. With scope=solution a filter still builds and probes every test project (about 1.5 minutes for 13 projects); pass scope=project and scopeName to run one project in seconds.")]
    public Task<SentinelCallToolResult<object>> RunTest(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("solution (default) runs every test project one after another; project runs only the project named in scopeName; file is not supported.")] ToolScope scope = ToolScope.solution,
        [Description("Project name (for example RoslynSentinel.Tests.Basic) when scope=project; not a parameter called projectName.")] string? scopeName = null,
        [Description("Passed through to `dotnet test --filter`. With scope=solution a filter still builds and probes every test project (about 1.5 minutes for 13 projects); pass scope=project and scopeName to run one project in seconds.")] string? filter = null,
        TestResultsFilter resultsType = TestResultsFilter.failed,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        int timeoutSeconds = 600,
        [Description("If true, omit the per-test Results list from the response entirely - only counts and FailureSummary are returned, independent of resultsType.")] bool summary = false,
        [Description(ToolParams.UseScratchDir)] bool useScratchDir = false,
        CancellationToken cancellationToken = default)
        => _impl.RunTest(reason, scope, scopeName, filter, resultsType, maxDetails, timeoutSeconds, summary, useScratchDir, cancellationToken);
}
