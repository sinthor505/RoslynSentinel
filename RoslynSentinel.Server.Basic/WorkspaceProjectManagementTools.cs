using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

[McpServerToolType]
public class WorkspaceProjectManagementTools
{
    private readonly WorkspaceProjectManagementImpl _impl;

    public WorkspaceProjectManagementTools(WorkspaceProjectManagementImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "ListSolutionItems")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.ProjectList)]
    [Produces(DataTag.DependencyList)]
    [Description("Lists projects, files, dependencies, or solution-folder items in the loaded solution.")]
    public Task<SentinelCallToolResult<object>> ListSolutionItems(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("files/dependencies: requires projectName. projects/solutionItems: ignore projectName, list every project or every solution-folder item respectively - solutionItems are files attached via the .sln's Solution Folders (e.g. plan/handoff docs), never part of any project's compiled Documents, so SearchSolutionText and kind=files won't find them; read their content with ProjectDoc. all: ignores projectName and returns everything in one call (every project, every solution-folder item, and every project's files and dependencies) - use this for a complete, guaranteed-non-empty view instead of guessing which project or kind to ask for.")]
        [ExternalInputRequired(DataTag.Scope)] SolutionItemsKind kind,
        [Description("Required for kind=files/dependencies. Not used for projects/solutionItems/all.")]
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _impl.ListSolutionItems(reason, kind, projectName, cancellationToken);

    [McpServerTool(Name = "ListWorkspaceSolutions")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.SolutionList)]
    [Description("Lists all *.sln and *.slnx files under a directory. Returns absolute paths for use with LoadSolution.")]
    public SentinelCallToolResult<List<SolutionFileInfo>, ResultError> ListWorkspaceSolutions(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Your workspace root - a real project/repo directory, not a drive root or '/'.")] string workspacePath,
        CancellationToken cancellationToken = default)
        => _impl.ListWorkspaceSolutions(reason, workspacePath, cancellationToken);

    [McpServerTool(Name = "LoadSolution")]
    [Produces(DataTag.ResultOnly)]
    [Description("Loads a .NET solution file into memory for persistent analysis. Must be called before any operation that returns ErrorCode=\"SolutionNotLoaded\". Accepts absolute paths. For relative paths, omit baseRepoDir and let the server resolve it against its configured base directory - only pass baseRepoDir if you have independently confirmed that exact directory exists on this host; a fabricated/guessed baseRepoDir is rejected with an error rather than silently ignored. If this exact solution is already loaded, this is a no-op by default (no re-read from disk) - pass forceReload:true to discard in-memory state and re-open it from disk.")]
    public Task<SentinelCallToolResult<object>> LoadSolution(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SolutionFilepath, required: true)] string solutionPath,
        [ToolOption(ToolOptionTag.RepoDirectory)][Description("Optional base directory used to resolve a relative solutionPath (e.g. the repo root). Overrides the server's configured base-repo-dir for this call. Must exist on this host - omit this entirely rather than guessing a value.")] string? baseRepoDir = null,
        [Description("If the given solutionPath is already loaded, false (default) returns immediately without touching the workspace. true forces a full reload from disk, discarding any in-memory state (equivalent to today's unconditional LoadSolution behavior). Has no effect when a different or no solution is currently loaded - that always loads normally regardless of this flag.")] bool forceReload = false,
        CancellationToken cancellationToken = default)
        => _impl.LoadSolution(reason, solutionPath, baseRepoDir, forceReload, cancellationToken);

    [McpServerTool(Name = "CreateProject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Creates a new project and adds it to the current solution. projectType defaults to console.")]
    public Task<SentinelCallToolResult<object>> CreateProject(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName,
        [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console",
        CancellationToken cancellationToken = default)
        => _impl.CreateProject(reason, projectName, projectType, cancellationToken);

    [McpServerTool(Name = "SplitProjectByFolder")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves all files under a specific folder from a source project to a new target project, preserving folder structure.")]
    public Task<SentinelCallToolResult<object>> SplitProjectByFolder(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ProjectName, required: true)] string sourceProjectName,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName,
        CancellationToken cancellationToken = default)
        => _impl.SplitProjectByFolder(reason, sourceProjectName, folderName, targetProjectName, cancellationToken);

    [McpServerTool(Name = "ListProjectFrameworkTargets")]
    [Produces(DataTag.Report)]
    [Description("Returns each project's TargetFramework value. No parameters.")]
    public Task<SentinelCallToolResult<List<ProjectFrameworkSummary>, ResultError>> ListProjectFrameworkTargets(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        CancellationToken cancellationToken = default)
        => _impl.ListProjectFrameworkTargets(reason, cancellationToken);

    [McpServerTool(Name = "SafeDeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages in the entire codebase. Distinction from RemoveMember: this tool refuses if ANY usage is found; RemoveMember checks for callers/implementations but allows skipPrecheck. Returns changeId.")]
    public Task<SentinelCallToolResult<object>> SafeDeleteUnusedSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filePath,
        [Description("Preferred resolution path, together with docCommentId - as returned by LocateSymbol/FindReferences. The most reliable and accurate way to identify the target.")] string projectName = "",
        [Description("Preferred resolution path, together with projectName - as returned by LocateSymbol/FindReferences.")] string docCommentId = "",
        [Description("Fallback resolution path if projectName/docCommentId aren't available. Combine with contextSnippet/lineBefore/lineAfter to disambiguate; symbolName alone is enough if there's only one declaration with that name.")]
        [Consumes(DataTag.SymbolName, required: false)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description("Legacy fallback resolution path if neither of the above is available - 1-based line of the declaration site. Both line and column are required together.")]
        [Consumes(DataTag.StartLine, required: false)] int line = 0,
        [Description("Legacy fallback resolution path - 1-based column of the declaration site. Both line and column are required together.")]
        [Consumes(DataTag.Offset, required: false)] int column = 0,
        CancellationToken cancellationToken = default)
        => _impl.SafeDeleteUnusedSymbol(reason, filePath, projectName, docCommentId, symbolName, contextSnippet, lineBefore, lineAfter, line, column, cancellationToken);
}
