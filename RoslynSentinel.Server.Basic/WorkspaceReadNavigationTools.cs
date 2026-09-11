using System.ComponentModel;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Thin MCP surface for the read/navigation slice of workspace tools. Holds all [McpServerTool]
/// attributes and delegates every call to <see cref="WorkspaceReadNavigationImpl"/>, which carries
/// the actual implementation. See docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md
/// (Decision 1-Amendment).
///
/// This class is a trial slice of that plan's DI split, landed ahead of the rest: today it is
/// registered in DI only as a plain singleton consumed internally by <c>SentinelWorkspaceTools</c>
/// (field <c>_readNav</c>) — the plan's Decision 4 mode strings ("WorkspaceReadNav"/"WorkspaceFileIO")
/// that would call <c>mcpBuilder.WithTools&lt;WorkspaceReadNavigationTools&gt;()</c> and make the
/// [McpServerTool] attributes below live have not been added to
/// ServiceRegistrationExtensionsBasic.cs yet. So GetMethodSource/GetFileOutline/GetLargeResult here
/// are currently NOT reachable over MCP — the real, registered versions of those three tool names
/// are the ones in SentinelWorkspaceTools.cs, which is what MCP clients actually call. This is not
/// dead code or an accidental duplicate; it is the intended shape once Decision 4/Decision 7 step 4
/// finish wiring the fine-grained mode strings.
/// </summary>
[McpServerToolType]
public class WorkspaceReadNavigationTools
{
    private readonly WorkspaceReadNavigationImpl _impl;

    public WorkspaceReadNavigationTools(WorkspaceReadNavigationImpl impl)
    {
        _impl = impl;
    }
    [McpServerTool(Name = "GetMethodSource")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the full source text of a named method or constructor, plus a structured list of its attributes.")]
    public Task<ToolResult<object>> GetMethodSource(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Method or constructor name. For a constructor, pass the containing class's name (e.g. \"OrderService\" for `public OrderService(...)`). Case-sensitive with case-insensitive fallback; returns the first match for overloaded names.")]
        [Consumes(DataTag.MethodName, required: true)] string methodName,
        CancellationToken cancellationToken = default)
        => _impl.GetMethodSource(reason, filepath, methodName, cancellationToken);

    [McpServerTool(Name = "GetFileOutline")]
    [Produces(DataTag.Report)]
    [Description("Returns a structural outline of a file — namespaces, classes, structs, records, interfaces, enums (and their members), methods, properties, constructors, and fields, with 1-based line ranges. Member bodies are not included.")]
    public Task<ToolResult<object>> GetFileOutline(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        CancellationToken cancellationToken = default)
        => _impl.GetFileOutline(reason, filepath, cancellationToken);

    [McpServerTool(Name = "ListAll")]
    [Produces(DataTag.Report)]
    [Description("Lists every namespace/class/interface/struct/record/enum/enum member/constructor/field/method/property declared in the loaded solution, one row per symbol with its file, kind, name, container, and line range. Call this first when you don't already know the exact name of a type/method/field.")]
    public Task<ToolResult<object>> ListAll(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Restricts results to one symbol kind. Defaults to all kinds.")]
        [ExternalInputRequired(DataTag.SymbolKind, required: false)] ListAllKind kind = ListAllKind.all,
        [Description("Restricts results to one project. Omit to search the whole solution.")]
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _impl.ListAll(reason, kind, projectName, cancellationToken);
    [McpServerTool(Name = "SearchSolutionText")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches source files in the loaded solution for a pattern, evaluated both as a literal substring and (if it compiles) as a regex in one pass. Returns literalResults (all literal-substring matches) and regexResults (additional regex-only matches), plus regexOverlapCount and regexPatternValid. Each match has file, line, column, a preview, and the enclosing member name. For a known symbol name, LocateSymbol is more precise. If you don't know the exact name, call ListAll first.")]
    public Task<ToolResult<object>> SearchSolutionText(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Text to search for, matched both as a literal substring and as a regex.")]
        [ToolOption(ToolOptionTag.Pattern, required: true)] string pattern,
        [Description("Restricts the search to files whose path matches this glob.")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
        [Description("Maximum number of matches to scan.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200,
        CancellationToken cancellationToken = default)
        => _impl.SearchSolutionText(reason, pattern, fileGlob, maxResults, cancellationToken);
    [McpServerTool(Name = "GetOperationDetail")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns a filtered, paged slice of an operation result blob by changeId.")]
    public Task<ToolResult<object>> GetOperationDetail(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ChangeId, required: true)] string changeId,
        [Description("Filters items by outcome or path. Accepts prefixes fail/err (failures), warn/skip (skipped), ok/pass/info/success (succeeded), roll/revert/undo (rolled back), manual (needs manual review); or file:<path> to filter by path. Omit for all items.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null,
        [Description("Maximum number of filtered items to return.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50,
        [Description("Number of filtered items to skip before taking maxItems. Pass the previous response's NextOffset to page through the rest.")]
        [ToolOptionAttribute(ToolOptionTag.Offset)] int offset = 0,
        CancellationToken cancellationToken = default)
        => _impl.GetOperationDetail(reason, changeId, filter, maxItems, offset, cancellationToken);
    [McpServerTool(Name = "GetLargeResult")]
    [Produces(DataTag.Report)]
    [Description("Pages through a large result that was written to disk because it exceeded the inline size threshold.")]
    public Task<ToolResult<object>> GetLargeResult(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of resultId/filepath must be supplied;
        // neither is individually required but the tool fails if both are omitted.
        [Description("The result's resultId, as returned alongside the original truncated result. Required if filepath is omitted.")]
        [Consumes(DataTag.ResultId)] string? resultId = null,
        [Description("Path to a largeresult_*.json file under .roslynsentinel/largeresults. Required if resultId is omitted.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description("Maximum number of records to return.")]
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [Description("Number of records to skip before taking limit.")]
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        CancellationToken cancellationToken = default)
        => _impl.GetLargeResult(reason, resultId, filepath, limit, offset, cancellationToken);
}
