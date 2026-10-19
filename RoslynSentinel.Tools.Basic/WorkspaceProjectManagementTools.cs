using System.ComponentModel;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

[McpServerToolType]
public class WorkspaceProjectManagementTools
{
    private readonly WorkspaceProjectManagementImpl _impl;

    public WorkspaceProjectManagementTools(WorkspaceProjectManagementImpl impl)
    {
        _impl = impl;
    }

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ListSolutionItems")]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.ProjectList)]
    [Produces(DataTag.DependencyList)]
    [Description("Lists projects, files, dependencies, or solution-folder items in the loaded solution.")]
    public Task<SentinelCallToolResult<object>> ListSolutionItemsTool(
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
    public SentinelCallToolResult<List<SolutionFileInfo>, ResultError> ListWorkspaceSolutionsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Your workspace root - a real project/repo directory, not a drive root or '/'.")] string workspacePath,
        CancellationToken cancellationToken = default)
        => _impl.ListWorkspaceSolutions(reason, workspacePath, cancellationToken);

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
        => _impl.LoadSolution(reason, solutionPath, baseRepoDir, forceReload, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "CreateProject")]
    [Produces(DataTag.ResultOnly)]
    [Description("Creates a new project and adds it to the current solution. projectType defaults to console.")]
    public Task<SentinelCallToolResult<object>> CreateProjectTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string projectName,
        [ExternalInputRequired(DataTag.ProjectType)] string projectType = "console",
        CancellationToken cancellationToken = default)
        => _impl.CreateProject(reason, projectName, projectType, cancellationToken);

    [McpServerTool(Name = "SplitProjectByFolder")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves all files under a specific folder from a source project to a new target project, preserving folder structure.")]
    public Task<SentinelCallToolResult<object>> SplitProjectByFolderTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ProjectName, required: true)] string sourceProjectName,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string folderName,
        [ExternalInputRequired(DataTag.ProjectName, required: true)] string targetProjectName,
        CancellationToken cancellationToken = default)
        => _impl.SplitProjectByFolder(reason, sourceProjectName, folderName, targetProjectName, cancellationToken);

    [McpServerTool(Name = "ListProjectFrameworkTargets")]
    [Produces(DataTag.Report)]
    [Description("Returns each project's TargetFramework value. No parameters.")]
    public Task<SentinelCallToolResult<List<ProjectFrameworkSummary>, ResultError>> ListProjectFrameworkTargetsTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        CancellationToken cancellationToken = default)
        => _impl.ListProjectFrameworkTargets(reason, cancellationToken);

    [McpServerTool(Name = "DeleteUnusedSymbol")]
    [Produces(DataTag.ResultOnly)]
    [Description("Deletes a symbol only if it has zero usages in the entire codebase. Distinction from RemoveMember: this tool refuses if ANY usage is found; RemoveMember checks for callers/implementations but allows skipPrecheck. Returns changeId.")]
    public Task<SentinelCallToolResult<object>> DeleteUnusedSymbolTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("Preferred resolution path, together with docCommentId - as returned by Search (mode: symbol or references). The most reliable and accurate way to identify the target.")] string projectName = "",
        [Description("Preferred resolution path, together with projectName - as returned by Search (mode: symbol or references).")] string docCommentId = "",
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
        => _impl.DeleteUnusedSymbol(reason, filePath, projectName, docCommentId, symbolName, contextSnippet, lineBefore, lineAfter, line, column, cancellationToken);
}
