using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// MCP surface for the read/navigation slice of workspace tools (GetMethodSource, GetFileOutline, ListAll, Search,
/// GetOperationDetail, GetLargeResult). Holds the [McpServerTool] attributes and delegates to
/// <see cref="WorkspaceReadNavigationImpl"/> (Search to the symbol engines). Registered in ToolClassRegistry and DI;
/// WorkspaceTools keeps attribute-less delegators for source compatibility. See
/// docs/current/plans/closed/plan_split_workspace_refactoring_tools_for_di.md (Decision 1-Amendment).
/// </summary>
[McpServerToolType]
public class WorkspaceReadNavigationTools
{
    private readonly WorkspaceReadNavigationImpl _impl;
    // Needed by the Search members moved here from WorkspaceTools.
    private readonly SymbolNavigationImpl _symbolNavigation;
    private readonly SymbolRelationshipImpl _symbolRelationship;

    public WorkspaceReadNavigationTools(
        WorkspaceReadNavigationImpl impl,
        SymbolNavigationImpl symbolNavigation,
        SymbolRelationshipImpl symbolRelationship)
    {
        _impl = impl;
        _symbolNavigation = symbolNavigation;
        _symbolRelationship = symbolRelationship;
    }
    [McpServerTool(Name = "GetMethodSource")]
    [Produces(DataTag.SourceCode)]
    [Description("Returns a method's or constructor's full source text and attributes. For a constructor, pass the class name.")]
    public Task<SentinelCallToolResult<MethodSourceResult, ResultError>> GetMethodSourceTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.MethodName, required: true)] string methodName,
        // Optional on purpose: every existing call names a method that is unique in its file and passes no container.
        [Consumes(DataTag.SymbolName, required: false)][Description("Optional. Narrows the search to the type that declares the method (generic arguments optional: 'Box' or 'Box<T>'; 'Outer.Inner' means Inner). A name that is still declared more than once returns an Ambiguous error listing every candidate; the first match is never returned.")] string? containerName = null,
        CancellationToken cancellationToken = default)
        => _impl.GetMethodSource(reason, filePath, methodName, containerName, cancellationToken);

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "GetFileOutline")]
    [Produces(DataTag.Report)]
    [Description("Returns a structural outline of a file's types and members with 1-based line ranges (no bodies).")]
    public Task<SentinelCallToolResult<FileOutlineResult, ResultError>> GetFileOutlineTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        CancellationToken cancellationToken = default)
        => _impl.GetFileOutline(reason, filePath, cancellationToken);

    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "ListAll")]
    [Produces(DataTag.Report)]
    [Description("Lists every declared symbol in the loaded solution with file, kind, name, container, and line range. Call this first if you don't know an exact symbol name.")]
    public Task<SentinelCallToolResult<object>> ListAllTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.ListAllKindValues)][ExternalInputRequired(DataTag.SymbolKind, required: false)] ListAllKind kind = ListAllKind.all,
        [Description("Restricts to one project. Omit for the whole solution.")]
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
        [Description("false (default) = skip test projects; true = include them. Naming a test project in projectName/filePath always includes it.")] bool includeTests = false,
        CancellationToken cancellationToken = default)
        => _impl.ListAll(reason, kind, projectName, includeTests: includeTests, cancellationToken: cancellationToken);
    // Deliberately NOT an [McpServerTool]: WorkspaceTools never exposed SearchSolutionText (Search(mode: text) replaced it),
    // and registering this class beside WorkspaceTools must not add a tool to the Workspace/Claude surfaces.
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Description("Searches source files in the loaded solution for a pattern, evaluated both as a literal substring and (if it compiles) as a regex in one pass. Returns literalResults (all literal-substring matches) and regexResults (additional regex-only matches), plus regexOverlapCount and regexPatternValid. Each match has file, line, column, a preview, and the enclosing member name. For a known symbol name, Search(mode: symbol) is more precise. If you don't know the exact name, call ListAll first.")]
    public Task<SentinelCallToolResult<object>> SearchSolutionTextTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Text to search for, matched both as a literal substring and as a regex.")]
        [ToolOption(ToolOptionTag.Pattern, required: true)] string pattern,
        [Description("Restricts the search to files whose path matches this glob. Supports *, **, ?, " +
            "{a,b,...} alternation, and [abc]/[!abc] character classes; other special characters are " +
            "rejected with an error naming the unsupported construct. Matches the solution-relative " +
            "path (with / separators) when the glob contains '/', otherwise matches the bare filename. Test projects are scanned too but their hits are hidden unless includeTests: true (or the glob's first path segment names a test project); hidden hits are counted in the status message.")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
        [Description("Maximum number of matches to scan.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200,
        [Description("false (default) = test projects are scanned but their matches are hidden and only counted in the status message; true = include them. Naming a test project as the glob's first path segment always includes it.")] bool includeTests = false,
        CancellationToken cancellationToken = default)
        => _impl.SearchSolutionText(reason, pattern, fileGlob, maxResults, includeTests, cancellationToken: cancellationToken);
    [UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]
    [McpServerTool(Name = "GetOperationDetail")]
    [Produces(DataTag.ResultOnly)]
    [Description("Returns a filtered, paged slice of an operation result blob by changeId.")]
    public Task<SentinelCallToolResult<object>> GetOperationDetailTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ChangeId, required: true)] string changeId,
        [Description("Filters items by outcome (failures/skipped/succeeded/rolledback/NeedsManualReview, with common synonyms) or by file:<path>. Omit for all.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] string? filter = null,
        [Description("Caps how many filtered items this page returns.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxItems = 50,
        [Description("Filtered items to skip. Pass NextOffset from the previous response to continue paging.")]
        [ToolOptionAttribute(ToolOptionTag.Offset)] int offset = 0,
        CancellationToken cancellationToken = default)
        => _impl.GetOperationDetail(reason, changeId, filter, maxItems, offset, cancellationToken);
    [McpServerTool(Name = "GetLargeResult")]
    [Produces(DataTag.Report)]
    [Description("Pages a stored large result (the largeResult pointer). offset counts units of its sequence (list records interleaved, or lines); follow nextOffset until hasMoreData is false. path is only for results whose largeResult lists omitted.")]
    public Task<SentinelCallToolResult<object>> GetLargeResultTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of resultId/filePath must be supplied;
        // neither is individually required but the tool fails if both are omitted.
        [Description("The resultId from the original truncated result. Required if filePath is omitted.")]
        [Consumes(DataTag.ResultId)] string? resultId = null,
        [Description("Path to a largeresult_*.json file. Required if resultId is omitted.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Description("Max units (records or lines) per page; omit to fill the size budget.")]
        [ToolOption(ToolOptionTag.ResultLimit)] int? limit = null,
        [Description("Units (records or lines) to skip: the previous nextOffset.")]
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        [Description("Only for results whose largeResult lists omitted: the omitted[].path to page (default first); ignored otherwise.")]
        string? path = null,
        CancellationToken cancellationToken = default)
        => _impl.GetLargeResult(reason, resultId, filePath, limit, offset, path: path, cancellationToken: cancellationToken);

    [McpServerTool(Name = "Search")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.DocCommentId)]
    [Produces(DataTag.ProjectName)]
    [Description("Search; mode picks the target. text: regex/literal scan of .cs files (restrict with fileGlob). symbol: declaration lookup by name. references/callers/implementations: usages of a symbol (callers = call sites, implementations = overrides/implementers). attribute: where an attribute is applied. Declaration-kind modes (all, namespace, class, interface, method, property, struct, record, enum, enum member, constructor, field) list up to 30 rows; totalRecords/hasMoreData show truncation - narrow with query or projectName. Zero matches is not an error.")]
    public Task<SentinelCallToolResult<object>> SearchSolutionTool(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    SearchMode mode,
    [Description("text: regex/literal (| alternates). Symbol modes: required name. Declaration-kind modes: optional name filter.")]
        string? query = null,
    [Description("Text mode glob, e.g. 'MyProj/**/*.cs'.")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
    [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200,
    [ExternalInputRequired(DataTag.SymbolKind)] SymbolKindFilter symbolKind = SymbolKindFilter.any,
    [Description("symbol, references, callers, implementations modes: the type that declares the name.")]
        [ExternalInputRequired(DataTag.ContainingType)] string? containingType = null,
    [Description("symbol mode.")]
        [ExternalInputRequired(DataTag.ContainingNamespace)] string? containingNamespace = null,
    [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
    [Description("false = substring.")]
        [ToolOption(ToolOptionTag.MatchType)] bool exactMatch = true,
    [Description("Pins the symbol when its name is ambiguous across files.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
    [Description("Verbatim source line near the symbol, to disambiguate.")]
        [Consumes(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
    [Description("Adjacent line.")][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
    [Description("Adjacent line.")][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
    [Description("true = include test projects.")] bool includeTests = false,
    [Description("attribute mode: owner also carries this attribute.")] string? hasAttribute = null,
    [Description("text mode: 0-10 lines around each match.")] int contextLines = 0,
    CancellationToken cancellationToken = default)
    => DispatchSearch(reason, mode, query, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch, filePath, contextSnippet, lineBefore, lineAfter, includeTests, hasAttribute, contextLines, cancellationToken);

    /// <summary>
    /// Hard cap on rows returned by Search in declaration-kind modes; extra matches are counted in totalRecords and flagged via hasMoreData/warningDetails.
    /// </summary>
    public const int KindListingMaxItems = 30;

    private static ListAllKind SearchModeToListAllKind(SearchMode mode) => mode switch
    {
        SearchMode.all => ListAllKind.all,
        SearchMode.@namespace => ListAllKind.@namespace,
        SearchMode.@class => ListAllKind.@class,
        SearchMode.@interface => ListAllKind.@interface,
        SearchMode.method => ListAllKind.method,
        SearchMode.property => ListAllKind.property,
        SearchMode.@struct => ListAllKind.@struct,
        SearchMode.record => ListAllKind.record,
        SearchMode.@enum => ListAllKind.@enum,
        SearchMode.enumMember => ListAllKind.enumMember,
        SearchMode.constructor => ListAllKind.constructor,
        SearchMode.field => ListAllKind.field,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unhandled SearchMode in declaration-listing dispatch.")
    };

    private Task<SentinelCallToolResult<object>> DispatchSearchCore(
    ToolCallReason reason, SearchMode mode, string? query, string? fileGlob, int maxResults,
    SymbolKindFilter symbolKind, string? containingType, string? containingNamespace, string? projectName,
    bool exactMatch, string? filePath, string? contextSnippet,
    string? lineBefore, string? lineAfter, bool includeTests, string? hasAttribute, int contextLines, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case SearchMode.text:
                return _impl.SearchSolutionText(reason, query ?? string.Empty, fileGlob, maxResults, includeTests, contextLines, cancellationToken);
            case SearchMode.symbol:
                return _symbolNavigation.LocateSymbol(reason, query ?? string.Empty, symbolKind, containingType, containingNamespace, projectName, filePath, exactMatch, includeTests: includeTests, cancellationToken: cancellationToken);
            case SearchMode.references:
                return _symbolRelationship.FindReferences(reason, query ?? string.Empty, FindReferencesKind.all, filePath, contextSnippet, lineBefore, lineAfter, containingType: containingType, includeTests: includeTests, cancellationToken: cancellationToken);
            case SearchMode.callers:
                return _symbolRelationship.FindReferences(reason, query ?? string.Empty, FindReferencesKind.callers, filePath, contextSnippet, lineBefore, lineAfter, containingType: containingType, includeTests: includeTests, cancellationToken: cancellationToken);
            case SearchMode.implementations:
                return _symbolRelationship.FindReferences(reason, query ?? string.Empty, FindReferencesKind.implementations, filePath, contextSnippet, lineBefore, lineAfter, containingType: containingType, includeTests: includeTests, cancellationToken: cancellationToken);
            case SearchMode.attribute:
                return _symbolRelationship.QuerySymbolRelationships(reason, query ?? string.Empty, FindUsagesSearchKind.attributeUsages, projectName, filePath, includeTests: includeTests, hasAttribute: hasAttribute, cancellationToken: cancellationToken);
            default:
                return _impl.ListAll(reason, SearchModeToListAllKind(mode), projectName, query, exactMatch, KindListingMaxItems, includeTests: includeTests, cancellationToken: cancellationToken);
        }
    }

    private async Task<SentinelCallToolResult<object>> DispatchSearch(
    ToolCallReason reason, SearchMode mode, string? query, string? fileGlob, int maxResults,
    SymbolKindFilter symbolKind, string? containingType, string? containingNamespace, string? projectName,
    bool exactMatch, string? filePath, string? contextSnippet,
    string? lineBefore, string? lineAfter, bool includeTests, string? hasAttribute, int contextLines, CancellationToken cancellationToken)
    {
        if (mode is SearchMode.text or SearchMode.symbol or SearchMode.references or SearchMode.callers or SearchMode.implementations or SearchMode.attribute
            && string.IsNullOrWhiteSpace(query))
        {
            var what = mode switch
            {
                SearchMode.text => "text or regex pattern",
                SearchMode.attribute => "attribute name (with or without the Attribute suffix, e.g. TestFixture)",
                _ => "symbol name"
            };
            var example = mode switch
            {
                SearchMode.text => "TODO",
                SearchMode.attribute => "TestFixture",
                _ => "MyType"
            };
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"query is required for mode: {mode}. Pass the {what} in query, for example {RoslynSentinel.Common.ToolCallHint.Call("Search", ("mode", mode.ToString()), ("query", example))}.")
            };
        }

        var misplaced = FindMisplacedSearchParameters(mode, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch, filePath, contextSnippet, lineBefore, lineAfter, hasAttribute, contextLines);
        if (misplaced is not null)
        {
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, misplaced)
            };
        }

        var searchResult = await DispatchSearchCore(reason, mode, query, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch, filePath, contextSnippet, lineBefore, lineAfter, includeTests, hasAttribute, contextLines, cancellationToken);
        return _impl.AddZeroHitEvidence(mode, query, searchResult);
    }

    /// <summary>
    /// Returns a one-line InvalidArgument message when a parameter that the chosen mode ignores was explicitly supplied
    /// with a non-default value, or null when nothing is misplaced. Null, blank and default values (maxResults 200,
    /// contextLines 0, symbolKind any, exactMatch true) count as "not supplied" because clients send defaults.
    /// </summary>
    private static string? FindMisplacedSearchParameters(SearchMode mode, string? fileGlob, int maxResults, SymbolKindFilter symbolKind, string? containingType, string? containingNamespace, string? projectName, bool exactMatch, string? filePath, string? contextSnippet, string? lineBefore, string? lineAfter, string? hasAttribute, int contextLines)
    {
        var isUsageMode = mode is SearchMode.references or SearchMode.callers or SearchMode.implementations;
        var isDeclarationKind = mode is not (SearchMode.text or SearchMode.symbol or SearchMode.attribute) && !isUsageMode;
        var misplaced = new List<string>();

        void Check(string name, bool supplied, bool applies, string appliesTo)
        {
            if (supplied && !applies)
            {
                misplaced.Add($"{name} ({appliesTo})");
            }
        }

        Check("fileGlob", !string.IsNullOrWhiteSpace(fileGlob), mode == SearchMode.text, "mode text only");
        // maxResults is a cap, so ignoring it outside text mode is harmless; an error only costs a weak model a turn.
        Check("contextLines", contextLines != 0, mode == SearchMode.text, "mode text only");
        Check("symbolKind", symbolKind != SymbolKindFilter.any, mode == SearchMode.symbol, "mode symbol only");
        Check("containingType", !string.IsNullOrWhiteSpace(containingType), mode == SearchMode.symbol || isUsageMode, "modes symbol, references, callers, implementations only");
        Check("containingNamespace", !string.IsNullOrWhiteSpace(containingNamespace), mode == SearchMode.symbol, "mode symbol only");
        Check("hasAttribute", !string.IsNullOrWhiteSpace(hasAttribute), mode == SearchMode.attribute, "mode attribute only");
        Check("contextSnippet", !string.IsNullOrWhiteSpace(contextSnippet), isUsageMode, "modes references, callers, implementations only");
        Check("lineBefore", !string.IsNullOrWhiteSpace(lineBefore), isUsageMode, "modes references, callers, implementations only");
        Check("lineAfter", !string.IsNullOrWhiteSpace(lineAfter), isUsageMode, "modes references, callers, implementations only");
        Check("exactMatch", !exactMatch, mode == SearchMode.symbol || isDeclarationKind, "mode symbol and declaration-kind modes only");
        Check("filePath", !string.IsNullOrWhiteSpace(filePath), mode is SearchMode.symbol or SearchMode.attribute || isUsageMode, "modes symbol, references, callers, implementations, attribute only");
        Check("projectName", !string.IsNullOrWhiteSpace(projectName), mode is SearchMode.symbol or SearchMode.attribute || isDeclarationKind, "modes symbol, attribute and declaration-kind only");

        if (misplaced.Count == 0)
        {
            return null;
        }

        var fix = string.Empty;
        if (!string.IsNullOrWhiteSpace(fileGlob) && mode != SearchMode.text)
        {
            fix = " To restrict by path use filePath in the symbol, references, callers, implementations and attribute modes, or projectName in the symbol, attribute and declaration-kind modes.";
        }
        else if (!string.IsNullOrWhiteSpace(filePath) && mode == SearchMode.text)
        {
            fix = " To restrict text mode by path use fileGlob.";
        }

        var example = mode == SearchMode.text
            ? RoslynSentinel.Common.ToolCallHint.Call("Search", ("mode", "text"), ("query", "TODO"), ("fileGlob", "MyProj/**/*.cs"))
            : RoslynSentinel.Common.ToolCallHint.Call("Search", ("mode", mode.ToString()), ("query", "MyType"));
        return $"Search mode {mode} ignores: {string.Join(", ", misplaced)}. Remove it or change mode.{fix} For example {example}.";
    }
}
