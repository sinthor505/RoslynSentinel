using System.ComponentModel;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;
/// <summary>
/// MCP surface for the workspace tools. The tool methods here carry the [McpServerTool] attributes and call the
/// <c>Workspace*Impl</c> classes directly (project management, build/test, file edit, health/misc).
/// GetMethodSourceTool, GetFileOutlineTool, ListAllTool, SearchSolutionTool, GetOperationDetailTool and
/// GetLargeResultTool are the exception: attribute-less delegators to <see cref="WorkspaceReadNavigationTools"/>
/// (field <c>_readNavTools</c>), which carries their [McpServerTool] attributes and delegates to
/// <see cref="WorkspaceReadNavigationImpl"/>. They exist so existing callers keep compiling and no tool name is
/// registered twice. See docs/current/plans/closed/plan_split_workspace_refactoring_tools_for_di.md.
/// </summary>
[McpServerToolType]
public class WorkspaceTools
{
    private readonly SymbolNavigationEngine _symbolNavigationEngine;    // Added by AddConstructorParameter
    private readonly BuildEngine _buildEngine;
    private readonly TestRunEngine _testRunEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<WorkspaceTools> _logger;
    private readonly WorkspaceReadNavigationImpl _readNav;
    private readonly WriteToolAdviceHelper _writeAdvice;

    // Which tools this server can serve, for the edit-impact next step; null (unit tests, hosts without a surface) = every tool callable.
    private readonly ActiveToolSurface? _activeToolSurface;

    private readonly WorkspaceProjectManagementImpl _projectManagement;

    private readonly WorkspaceBuildTestImpl _buildTest;

    private readonly WorkspaceFileEditImpl _fileEdit;

    private readonly WorkspaceHealthMiscImpl _healthMisc;

    private readonly WorkspaceReadNavigationTools _readNavTools;

    public WorkspaceTools(IWorkspaceManager workspaceManager, ValidationEngine validationEngine, DiffEngine diffEngine, DiagnosticEngine diagnosticEngine, StructuralRefinementEngine structuralRefinementEngine, SentinelConfiguration config, ILogger<WorkspaceTools> logger, BuildEngine buildEngine, SymbolNavigationEngine symbolNavigationEngine, TestRunEngine testRunEngine, WorkspaceReadNavigationImpl readNav, WriteToolAdviceHelper writeAdvice, ActiveToolSurface? activeToolSurface = null)
    {
        _activeToolSurface = activeToolSurface;
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _diagnosticEngine = diagnosticEngine;
        _structuralRefinementEngine = structuralRefinementEngine;
        _config = config;
        _logger = logger;
        _buildEngine = buildEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _testRunEngine = testRunEngine;
        _readNav = readNav;
        _writeAdvice = writeAdvice;

        // The Impl classes carry the tool bodies; the tool methods below call them directly. Built here
        // (not taken as ctor params) so the many `new WorkspaceTools(...)` call sites need no update.
        _projectManagement = new WorkspaceProjectManagementImpl(workspaceManager, structuralRefinementEngine, logger,
            // Built internally (not a WorkspaceTools ctor param) so the many `new WorkspaceTools(...)` test call sites need no update.
            new SolutionStructureEngine(workspaceManager, config));
        _buildTest = new WorkspaceBuildTestImpl(workspaceManager, diagnosticEngine, buildEngine, testRunEngine, logger);
        _fileEdit = new WorkspaceFileEditImpl(workspaceManager, readNav, logger, validationEngine, symbolNavigationEngine, writeAdvice, activeToolSurface);
        _healthMisc = new WorkspaceHealthMiscImpl(workspaceManager, config, buildEngine, logger);

        // Search's mode:symbol/references dispatch targets. Built internally (not taken as ctor
        // params) so the many existing `new WorkspaceTools(...)` call sites across the test suite
        // don't all need updating for a dependency only Search uses.
        _symbolNavigation = new SymbolNavigationImpl(symbolNavigationEngine, new ImpactAnalyzer(workspaceManager), workspaceManager, logger);
        _symbolRelationship = new SymbolRelationshipImpl(new DiscoveryEngine(workspaceManager), symbolNavigationEngine, workspaceManager, logger);
        _readNavTools = new WorkspaceReadNavigationTools(readNav, _symbolNavigation, _symbolRelationship);
    }
    [McpServerTool(Name = "Features")]
    [Produces(DataTag.Report)]
    [Description("Queries or updates feature flags.")]
    public Task<SentinelCallToolResult<object>> McpServerFeaturesTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("list: all flags. get: flags named in names. update: batch-updates flags in enabled.")]
        FeaturesAction action,
        [Description("Required for action=get: flag names to look up.")]
        List<string>? names = null,
        [Description("Required for action=update: [{Key, Value}] pairs to apply.")]
        List<KeyValuePair<string, bool>>? enabled = null,
        [Description("Test-only: delay before acting.")]
        int delaySeconds = 0,
        CancellationToken cancellationToken = default)
        => _healthMisc.Features(reason, action, names, enabled, delaySeconds, cancellationToken);
    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ListSolutionItems")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.ProjectList)]
    [Produces(DataTag.DependencyList)]
    [Description("Lists projects, files, dependencies, or solution-folder items in the loaded solution.")]
    public Task<SentinelCallToolResult<object>> ListSolutionItemsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("files/dependencies: of projectName, or of every project when omitted. projects/solutionItems: list every project or solution-folder item (solutionItems aren't searchable via SearchSolutionText - use ProjectDoc). all: everything in one call.")]
        [ExternalInputRequired(DataTag.Scope)] SolutionItemsKind kind,
        [Description("kind=files/dependencies: limit to this project (omit for all projects).")]
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListSolutionItems(reason, kind, projectName, cancellationToken);

    /// <summary>
    /// Hard cap on how many files ListWorkspaceSolutionsTool will walk before giving up -> protects
    /// against a caller passing an overly broad root (see the drive-root guard below) that isn't
    /// caught by that check but still turns out to contain far more than any real workspace would
    /// (e.g. a node_modules-style tree, or a root one level above the intended one).
    /// </summary>
    private const int ListWorkspaceSolutionsMaxFilesWalked = 200_000;
    [McpServerTool(Name = "ListWorkspaceSolutions")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.SolutionList)]
    [Description("Lists .sln/.slnx files under a directory, for use with LoadSolution.")]
    public SentinelCallToolResult<List<SolutionFileInfo>, ResultError> ListWorkspaceSolutionsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("A real project/repo directory, not a drive root.")] string workspacePath,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListWorkspaceSolutions(reason, workspacePath, cancellationToken);
    // current directory, --base-repo-dir (if set), or the server's install directory.
    [McpServerTool(Name = "LoadSolution")]
    [Produces(DataTag.ResultOnly)]
    [Description("Loads a .NET solution into memory; required before any tool that needs a loaded solution. Absolute paths work; for a relative path omit baseRepoDir. A load already in progress (for example the start-up auto-load) is awaited, not repeated.")]
    public Task<SentinelCallToolResult<object>> LoadSolutionTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SolutionFilepath, required: true)] string solutionPath,
        [ToolOption(ToolOptionTag.RepoDirectory)][Description("Base directory for a relative solutionPath. Must exist on this host - omit rather than guess.")] string? baseRepoDir = null,
        [Description("true = discard in-memory state and reload from disk even if this solution is already loaded. Default false: no-op when already loaded.")] bool forceReload = false,
        [Description("Seconds to wait (1-3600, default 30) for a load in progress or one this call starts; a load this call starts is cancelled on expiry and the error names timeoutSeconds.")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
        => _projectManagement.LoadSolution(reason, solutionPath, baseRepoDir, forceReload, timeoutSeconds, cancellationToken);

    // ListExternalDiskChanges/AcknowledgeExternalFileChanges moved to AdminTools.cs,
    // gated behind the "Admin" mode -> see docs/current/ideas/closed/idea_external_drift_hard_blocker.md.
    // Not model-visible by default anymore; reconciliation is an out-of-band operator action now.

    // ApplyUnifiedDiffTool moved to WholeFileWriteTools.cs (gated off the default MCP surface,
    // alongside ApplyDiffTool) -> see docs/current/design_applyunifieddiff_replace_snippet_v1.md.
    // ReplaceSnippetTool (below, on this default surface) replaces it for small, exact-text batchEdits.

    [McpServerTool(Name = "ReplaceSnippet")]
    [SupportsBatching]
    [Produces(DataTag.ChangeId)]
    // Deliberately names no whole-file-write tool. Attribute arguments must be compile-time
    // constants, so this text can't be generated per-session from the tool registry the way the
    // over-cap error can (see WriteToolAdviceHelper) -> and a hardcoded name here would be shown to
    // the model on every single call even when that tool is gated off, which is the run-398 failure
    // in its most persistent form. The error path is where the redirect is actually needed.
    [Description("Replaces one exact text block with another in a file, for small localized edits. Prefer a structural Roslyn tool for structural changes. By default the edited project(s) and their dependents are delta-compiled BEFORE writing, and the change is rejected if it introduces a new compiler error. For edits larger than the size limit or whole-member rewrites, use Member(operation: replace) instead.")]
    public Task<SentinelCallToolResult<ReplaceSnippetResult>> ReplaceSnippetTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("apply: writes the change. validate: checks it would apply cleanly without writing.")]
        [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'batchEdits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)][Description("File to edit. Required unless batchEdits is given. Do not combine with batchEdits; each batch edit carries its own filePath.")] string? filePath = null,
        [ToolOption(ToolOptionTag.OldContent, required: false)][Description(ToolParams.OldContent)] string? oldContent = null,
        [ToolOption(ToolOptionTag.NewContent, required: false)][Description(ToolParams.NewContent)] string? newContent = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ReplaceSnippetWithinMember)] string? withinMember = null,
        [Description(ToolParams.ReplaceSnippetMatchLine)] int? matchLine = null,
        [Description(ToolParams.SnippetEdits)] List<SnippetEdit>? batchEdits = null,
        [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
        => _fileEdit.ReplaceSnippet(reason, action, filePath, oldContent, newContent, lineBefore, lineAfter, withinMember, matchLine, batchEdits, validateOnApply, returnDiff, cancellationToken);

    [McpServerTool(Name = "CreateFile")]
    [Produces(DataTag.ChangeId)]
    [Description("Creates a new file; fails if it already exists (never overwrites). Pass content to write the full body in one call, any file type. For a .cs file you may instead seed an empty type with typeKind and typeName, then add members with Member. Parent directories are created if missing.")]
    public Task<SentinelCallToolResult<object>> CreateFileTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("For .cs files: namespace to seed the file with; omit it for the global namespace.")] string? namespaceName = null,
        [Description("Alternative to content. Required for .cs files: kind of top-level type to seed (class/interface/etc). Use staticClass for a static utility class.")] NewTypeKind? typeKind = null,
        [Description("Alternative to content. Required for .cs files: name of the seeded top-level type.")] string? typeName = null,
        [Description("Full file body, written in one call (any file type; for .cs it is validated and rejected if it adds compiler errors). Use instead of typeKind/typeName/namespaceName, not with them.")] string? content = null,
        CancellationToken cancellationToken = default)
        => _fileEdit.CreateFile(reason, filePath, namespaceName, typeKind, typeName, content, cancellationToken);

    [McpServerTool(Name = "RetryFailedChanges")]
    [Produces(DataTag.ResultOnly)]
    [Description("Retries failed file writes using server-cached content.")]
    public Task<SentinelCallToolResult<object>> RetryFailedChangesTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: false)] List<string>? specificFiles = null,
        [ToolOption(ToolOptionTag.RetryCount)] int retryCount = 3,
        CancellationToken cancellationToken = default)
        => _fileEdit.RetryFailedChanges(reason, specificFiles, retryCount, cancellationToken);

    // WriteBlobForApplyAsync moved to OperationBlobHelper.WriteBlobForApplyAsync (Decision 7 step 1).
    [McpServerTool(Name = "GetDiagnostics")]
    [Produces(DataTag.Report)]
    [Description("Gets compiler diagnostics for a file, project, or the whole solution.")]
    public Task<SentinelCallToolResult<object>> GetDiagnosticsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("file/project: also pass scopeName. solution: scopeName ignored.")]
        [Consumes(DataTag.ProjectName, required: true)][Consumes(DataTag.SourceFilepath, required: false)] ToolScope scope = ToolScope.solution,
        [Description("Required for scope=file/project (filePath or projectName). Ignored for solution.")]
        string? scopeName = null,
        [Description("Groups results by diagnostic ID with counts instead of a raw list.")]
        bool summarize = false,
        [Description("Caps the raw diagnostics list. Ignored when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        [Description("Caps groups returned. Only used when summarize=true.")]
        [ToolOptionAttribute(ToolOptionTag.TopN)] int topN = 20,
        [Description("quickBuild/fullBuild/fullBuildThrowaway also run a build check.")]
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _buildTest.GetDiagnostics(reason, scope, scopeName, summarize, maxDetails, topN, verify, cancellationToken);
    [McpServerTool(Name = "Build")]
    [Produces(DataTag.Report)]
    [Description(ToolParams.BuildDescription)]
    public Task<SentinelCallToolResult<object>> BuildTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.BuildLevel)] BuildVerifyLevel level = BuildVerifyLevel.fullBuildThrowaway,
        [Description("solution (default), project (scopeName = project name) or file (scopeName = file path; full builds build its project).")] ToolScope scope = ToolScope.solution,
        string? scopeName = null,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 20,
        [Description(ToolParams.UseScratchDir)] bool useScratchDir = false,
        [Description("true = also return the build's stdout/stderr tail. Default false.")] bool includeOutput = false,
        [Description("true = also return warnings, capped by maxDetails. Default false: count only.")] bool includeWarnings = false,
        CancellationToken cancellationToken = default)
        => _buildTest.Build(reason, level, scope, scopeName, maxDetails, useScratchDir, includeOutput, includeWarnings, cancellationToken);

    [McpServerTool(Name = "RunTest")]
    [Produces(DataTag.Report)]
    [Description(ToolParams.RunTestDescription)]
    public Task<SentinelCallToolResult<object>> RunTestTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("solution (default) runs every test project one after another; project runs only the project named in scopeName; file is not supported.")] ToolScope scope = ToolScope.solution,
        [Description("Project name (for example RoslynSentinel.Tests.Basic) when scope=project; not a parameter called projectName.")] string? scopeName = null,
        [Description("Passed to `dotnet test --filter`. With scope=solution it still builds every test project (about 1.5 minutes); prefer scope=project with scopeName.")] string? filter = null,
        [Description(ToolParams.RunTestSuite)] TestSuite suite = TestSuite.fast,
        TestResultsFilter resultsType = TestResultsFilter.failed,
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxDetails = 50,
        int timeoutSeconds = 600,
        [Description("true = counts and FailureSummary only, no Results list.")] bool summary = false,
        [Description(ToolParams.UseScratchDir)] bool useScratchDir = false,
        [Description("true = keep failed tests' Output and the stdout/stderr tails. Default false: compact digest.")] bool includeOutput = false,
        [Description("Extra env vars for the test process (name -> value).")] Dictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
        => _buildTest.RunTest(reason, scope, scopeName, filter, suite, resultsType, maxDetails, timeoutSeconds, summary, useScratchDir, includeOutput, environment, cancellationToken);
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: none of projectName/docCommentId/symbolName/line/column is
    // individually required -> the tool needs exactly one full resolution strategy: (projectName +
    // docCommentId), or symbolName (optionally with contextSnippet/lineBefore/lineAfter), or
    // (line + column). Supplying an incomplete subset of any one strategy (e.g. line without column)
    // is accepted by the schema but rejected at runtime with InvalidArgument.
    [McpServerTool(Name = "DeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages anywhere in the codebase.")]
    public Task<SentinelCallToolResult<object>> DeleteUnusedSymbolTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("File that declares the symbol; still required with docCommentId.")][Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("From the same Search result as docCommentId.")] string projectName = "",
        [Description("From Search(mode: symbol), pass exactly as returned, do not construct.")] string docCommentId = "",
        [Description("Fallback; add contextSnippet to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: false)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description("Fallback: 1-based declaration line; needs column.")]
        [Consumes(DataTag.StartLine, required: false)] int line = 0,
        [Description("Fallback: 1-based declaration column; needs line.")]
        [Consumes(DataTag.Offset, required: false)] int column = 0,
        CancellationToken cancellationToken = default)
        => _projectManagement.DeleteUnusedSymbol(reason, filePath, projectName, docCommentId, symbolName, contextSnippet, lineBefore, lineAfter, line, column, cancellationToken);

    [McpServerTool(Name = "CreateProject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Creates a new project and adds it to the loaded solution.")]
    public Task<SentinelCallToolResult<object>> CreateProjectTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName,
        [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console",
        CancellationToken cancellationToken = default)
        => _projectManagement.CreateProject(reason, projectName, projectType, cancellationToken);

    [McpServerTool(Name = "SplitProjectByFolder")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves all files under a folder from one project to a new target project, preserving structure.")]
    public Task<SentinelCallToolResult<object>> SplitProjectByFolderTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ProjectName, required: true)] string sourceProjectName,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName,
        CancellationToken cancellationToken = default)
        => _projectManagement.SplitProjectByFolder(reason, sourceProjectName, folderName, targetProjectName, cancellationToken);

    // ── Phase 1 -> Low-level fallback tools ──────────────────────────────────
    // Non-tool delegator: the GetMethodSource [McpServerTool] lives on WorkspaceReadNavigationTools.
    public Task<SentinelCallToolResult<MethodSourceResult, ResultError>> GetMethodSourceTool(
        ToolCallReason reason, string filePath, string methodName, string? containerName = null, CancellationToken cancellationToken = default)
        => _readNavTools.GetMethodSourceTool(reason, filePath, methodName, containerName, cancellationToken);

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ReadFile")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns a file's raw text verbatim, or a 1-based line-range slice via startLine/endLine. To read a single member, pass memberName (optionally containerName).")]
    public Task<SentinelCallToolResult<object>> ReadFileTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("Omit to start from the first line.")] int? startLine = null,
        [Description("Omit to read through the last line.")] int? endLine = null,
        [Consumes(DataTag.SymbolName, required: false)][Description("Read just this member's source (method, property, field, constructor, or a type name for the whole type) instead of the whole file. Cannot be combined with startLine/endLine.")] string? memberName = null,
        [Consumes(DataTag.SymbolName, required: false)][Description("Only with memberName: the type that holds the member, to disambiguate same-named members.")] string? containerName = null,
        [Description("None (default) = unchanged; Summary = adds a lineEndings field: counts per style, dominant style, final-newline flag and the line numbers that differ; All = like Summary, and the returned source is rendered with <CRLF>, <LF>, <CR> or <EOF> at the end of every line. In All mode the source is NOT verbatim - never paste it into ReplaceSnippet.")] LineEndingDisplay showLineEndings = LineEndingDisplay.None,
        CancellationToken cancellationToken = default)
        => _fileEdit.ReadFile(reason, filePath, startLine, endLine, memberName, containerName, showLineEndings, cancellationToken);

    // Non-tool delegator: the GetFileOutline [McpServerTool] lives on WorkspaceReadNavigationTools.
    public Task<SentinelCallToolResult<FileOutlineResult, ResultError>> GetFileOutlineTool(
        ToolCallReason reason, string filePath, CancellationToken cancellationToken = default)
        => _readNavTools.GetFileOutlineTool(reason, filePath, cancellationToken);

    // Non-tool delegator: the ListAll [McpServerTool] lives on WorkspaceReadNavigationTools.
    public Task<SentinelCallToolResult<object>> ListAllTool(
        ToolCallReason reason, ListAllKind kind = ListAllKind.all, string? projectName = null, bool includeTests = false,
        CancellationToken cancellationToken = default)
        => _readNavTools.ListAllTool(reason, kind, projectName, includeTests, cancellationToken);

    /// <summary>
    /// Non-tool delegator kept so existing callers of <c>WorkspaceTools.SearchSolutionTool</c> keep compiling.
    /// The Search tool itself (the [McpServerTool] attribute) lives on <see cref="WorkspaceReadNavigationTools"/>.
    /// </summary>
    public Task<SentinelCallToolResult<object>> SearchSolutionTool(
        ToolCallReason reason, SearchMode mode, string? query = null, string? fileGlob = null, int maxResults = 200,
        SymbolKindFilter symbolKind = SymbolKindFilter.any, string? containingType = null, string? containingNamespace = null,
        string? projectName = null, bool exactMatch = true, string? filePath = null, string? contextSnippet = null,
        string? lineBefore = null, string? lineAfter = null, bool includeTests = false, string? hasAttribute = null,
        int contextLines = 0,
        CancellationToken cancellationToken = default)
        => _readNavTools.SearchSolutionTool(reason, mode, query, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch, filePath, contextSnippet, lineBefore, lineAfter, includeTests, hasAttribute, contextLines, cancellationToken);

    // Non-tool delegator: the GetOperationDetail [McpServerTool] lives on WorkspaceReadNavigationTools.
    public Task<SentinelCallToolResult<object>> GetOperationDetailTool(
        ToolCallReason reason, string changeId, string? filter = null, int maxItems = 50, int offset = 0,
        CancellationToken cancellationToken = default)
        => _readNavTools.GetOperationDetailTool(reason, changeId, filter, maxItems, offset, cancellationToken);

    [McpServerTool(Name = "UndoLastApply")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reverts files from a previous apply back to their pre-apply state.")]
    public Task<SentinelCallToolResult<object>> UndoLastApplyTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.OperationId, required: true)] string changeId,
        CancellationToken cancellationToken = default)
        => _fileEdit.UndoLastApply(reason, changeId, cancellationToken);

    // ── 8. GetWorkspaceHealthTool ─────────────────────────────────────────────────
    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "GetWorkspaceHealth")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reports live workspace/solution health: loaded state, project/document counts, and staleness.")]
    public Task<SentinelCallToolResult<WorkspaceHealthReport, ResultError>> GetWorkspaceHealthTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        BuildVerifyLevel verify = BuildVerifyLevel.noBuild,
        CancellationToken cancellationToken = default)
        => _healthMisc.GetWorkspaceHealth(reason, verify, cancellationToken);

    [McpServerTool(Name = "ListProjectFrameworkTargets")]
    [Produces(DataTag.Report)]
    [Description("Returns each project's TargetFramework value.")]
    public Task<SentinelCallToolResult<List<ProjectFrameworkSummary>, ResultError>> ListProjectFrameworkTargetsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        CancellationToken cancellationToken = default)
        => _projectManagement.ListProjectFrameworkTargets(reason, cancellationToken);

    // ── get_large_result ────────────────────────────────────────────────────────

    // Non-tool delegator: the GetLargeResult [McpServerTool] lives on WorkspaceReadNavigationTools.
    // limit null = fill the size budget (see GetLargeResult); a non-null value is an upper bound.
    public Task<SentinelCallToolResult<object>> GetLargeResultTool(
        ToolCallReason reason, string? resultId = null, string? filePath = null, int? limit = null, int offset = 0,
        string? path = null, CancellationToken cancellationToken = default)
        => _readNavTools.GetLargeResultTool(reason, resultId, filePath, limit, offset, path, cancellationToken);

    private readonly SymbolNavigationImpl _symbolNavigation;

    private readonly SymbolRelationshipImpl _symbolRelationship;
}
