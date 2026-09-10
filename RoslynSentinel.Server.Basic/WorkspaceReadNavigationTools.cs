using System.ComponentModel;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Thin MCP surface for the read/navigation slice of workspace tools. Holds all [McpServerTool]
/// attributes and delegates every call to <see cref="WorkspaceReadNavigationImpl"/>, which carries
/// the actual implementation. See docs/current/plan_split_workspace_refactoring_tools_for_di.md
/// (Decision 1-Amendment).
/// </summary>
[McpServerToolType]
public class WorkspaceReadNavigationTools
{
    private readonly WorkspaceReadNavigationImpl _impl;

    public WorkspaceReadNavigationTools(WorkspaceReadNavigationImpl impl)
    {
        _impl = impl;
    }

    // ── Phase 1 — Low-level fallback tools ──────────────────────────
    [McpServerTool(Name = "GetMethodSource")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns the full source text of a named method or constructor, plus a structured list of its attributes. For a constructor, pass the containing class's name (e.g. methodName: \"OrderService\" for `public OrderService(...)`). Case-sensitive match with case-insensitive fallback. Returns the first match for overloaded names.")]
    public Task<ToolResult<object>> GetMethodSource(
        [Description(ToolParams.Reason)] string reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath, [Consumes(DataTag.MethodName, required: true)] string methodName,
        CancellationToken cancellationToken = default)
        => _impl.GetMethodSource(reason, filepath, methodName, cancellationToken);

    [McpServerTool(Name = "GetFileOutline")]
    [Produces(DataTag.Report)]
    [Description("Returns a structural outline of a file — namespaces, classes, structs, records, interfaces, enums (and their members), methods, properties, constructors, and fields, with 1-based line ranges. Member bodies are not included.")]
    public Task<ToolResult<object>> GetFileOutline(
        [Description(ToolParams.Reason)] string reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        CancellationToken cancellationToken = default)
        => _impl.GetFileOutline(reason, filepath, cancellationToken);

    [McpServerTool(Name = "ListAll")]
    [Produces(DataTag.Report)]
    [Description("Lists every namespace/class/interface/struct/record/enum/enum member/constructor/field/method/property declared anywhere in the loaded solution, one row per symbol with its file, kind, name, container, and line range — the solution-wide equivalent of GetFileOutline. Call this FIRST when you don't already know the exact name of the type/method/field you need — it is cheaper and more reliable than guessing plausible-sounding names and searching for each one individually with SearchSolutionText. kind filters to one symbol kind (default: all — every kind). Optional projectName restricts to one project. Can return a lot of rows on a large solution; narrow with kind and/or projectName first.")]
    public Task<ToolResult<object>> ListAll(
        [Description(ToolParams.Reason)] string reason,
        [Description(ToolParams.ListAllKindValues)][ExternalInputRequired(DataTag.SymbolKind, required: false)] ListAllKind kind = ListAllKind.all,
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
        CancellationToken cancellationToken = default)
        => _impl.ListAll(reason, kind, projectName, cancellationToken);

    [McpServerTool(Name = "SearchSolutionText")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches all source files in the loaded solution for a text pattern or regex. Only searches documents that are part of a loaded project's source code (e.g. .cs files). For a known symbol (class/method/field/etc. by name), use LocateSymbol instead — it's semantic, not text-based, so it won't false-positive on comments/strings or miss partial-line matches. If you don't know the exact name you're looking for, call ListAll first — it's cheaper and more reliable than guessing plausible-sounding names and searching for each one individually here. Use ListSolutionItems(kind: solutionItems) to see files attached via the .sln's Solution Folders and other non-project files, use ProjectDoc to read plan/handoff/documentation files directly, and use GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file. Returns file path, 1-based line and column, a preview, and enclosingMember (the name of the method/property/constructor/field/etc. containing the match, or null if the match isn't inside any member) per match. searchMode is required and never inferred: pass literal for an exact substring, regex for a pattern. A pattern containing regex metacharacters is still searched literally under searchMode: literal, so an unstated mode used to guarantee zero results. fileGlob restricts to matching file paths. maxResults caps total matches (default 200).")]
    public Task<ToolResult<object>> SearchSolutionText(
        [Description(ToolParams.Reason)] string reason,
        [ToolOption(ToolOptionTag.Pattern, required: true)] string pattern, [ToolOption(ToolOptionTag.SearchMode, required: true)] TextSearchMode searchMode, [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null, [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200,
        CancellationToken cancellationToken = default)
        => _impl.SearchSolutionText(reason, pattern, searchMode, fileGlob, maxResults, cancellationToken);

    // ── Phase 2 — Blob persistence query + undo tools ──────────────────
    [McpServerTool(Name = "GetOperationDetail")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns a filtered slice of an operation result blob by changeId. filter accepts prefix synonyms: fail/err → failures, warn/skip → skipped, ok/pass/info/success → succeeded, roll/revert/undo → rolledback, manual/manual_review/needs_manual_review → NeedsManualReview (bridge compiler-error skips), file:<path> to filter by path, or omit for all items. Unrecognised prefixes return an error. offset skips that many filtered items before taking maxItems; pass NextOffset from the previous response to page through the rest. TotalItems reflects the filtered count; HasMorePages is true when more items remain past this page.")]
    public Task<ToolResult<object>> GetOperationDetail(
        [Description(ToolParams.Reason)] string reason,
        [Consumes(DataTag.ChangeId, required: true)] string changeId, [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null, [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50, [ToolOptionAttribute(ToolOptionTag.Offset)] int offset = 0,
        CancellationToken cancellationToken = default)
        => _impl.GetOperationDetail(reason, changeId, filter, maxItems, offset, cancellationToken);

    // ── get_large_result ─────────────────────────────────────────────
    [McpServerTool(Name = "GetLargeResult")]
    [Produces(DataTag.Report)]
    [Description("""
        Pages through a large result written to disk when output result payload exceeded the inline size threshold. Supply either resultId (resolves to .roslynsentinel/largeresults/largeresult_*_{resultId}.json) or filePath (must match the largeresult_*.json pattern). Returns ToolResult<object> with TotalRecords and HasMore.
        """)]
    public Task<ToolResult<object>> GetLargeResult(
        [Description(ToolParams.Reason)] string reason,
        [Consumes(DataTag.ResultId)] string? resultId = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        CancellationToken cancellationToken = default)
        => _impl.GetLargeResult(reason, resultId, filepath, limit, offset, cancellationToken);
}
