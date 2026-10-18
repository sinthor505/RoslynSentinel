using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// MCP surface for the read/navigation slice of workspace tools (GetMethodSource, GetFileOutline, ListAll, Search,
/// GetOperationDetail, GetLargeResult). Holds the [McpServerTool] attributes and delegates to
/// <see cref="WorkspaceReadNavigationImpl"/> (Search to the symbol engines). Registered in ToolClassRegistry and DI;
/// WorkspaceTools keeps attribute-less delegators for source compatibility. See
/// docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md (Decision 1-Amendment).
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
        CancellationToken cancellationToken = default)
        => _impl.GetMethodSource(reason, filePath, methodName, cancellationToken);

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
            "path (with / separators) when the glob contains '/', otherwise matches the bare filename.")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
        [Description("Maximum number of matches to scan.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200,
        [Description("false (default) = skip test projects; true = include them. Naming a test project in projectName/filePath always includes it.")] bool includeTests = false,
        CancellationToken cancellationToken = default)
        => _impl.SearchSolutionText(reason, pattern, fileGlob, maxResults, includeTests, cancellationToken);
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
    [Description("Pages through a large result written to disk after exceeding the inline size threshold. Page with offset; the response carries nextOffset and hasMoreData.")]
    public Task<SentinelCallToolResult<object>> GetLargeResultTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of resultId/filePath must be supplied;
        // neither is individually required but the tool fails if both are omitted.
        [Description("The resultId from the original truncated result. Required if filePath is omitted.")]
        [Consumes(DataTag.ResultId)] string? resultId = null,
        [Description("Path to a largeresult_*.json file. Required if resultId is omitted.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Description("Max records to return. Ignored for Raw/text results - use charLimit. A page may return fewer rows than limit when the result exceeds the inline size backstop; always continue from nextOffset while hasMoreData is true.")]
        [ToolOption(ToolOptionTag.ResultLimit)] int limit = 50,
        [Description("Records (or characters, for text results) to skip before the next page.")]
        [ToolOption(ToolOptionTag.Offset)] int offset = 0,
        [Description("Max characters to return for a Raw/text result. Other result types use limit instead.")]
        [ToolOption(ToolOptionTag.CharLimit)] int? charLimit = null,
        CancellationToken cancellationToken = default)
        => _impl.GetLargeResult(reason, resultId, filePath, limit, offset, charLimit: charLimit, cancellationToken: cancellationToken);

    [McpServerTool(Name = "Search")]
    [Produces(DataTag.Report)]
    [Produces(DataTag.FileList)]
    [Produces(DataTag.DocCommentId)]
    [Produces(DataTag.ProjectName)]
    [Description("Unified search. mode selects what's searched: text (free-text/regex scan), symbol (declaration lookup by name), references (callers and/or implementations of a symbol: use mode callers for call sites, implementations for overrides/interface implementers, references for both), attribute (every place an attribute is applied, by name e.g. TestFixture or TestFixtureAttribute; returns decorated symbol, target kind, arguments, file, line), or a declaration-kind listing (all, namespace, class, interface, method, property, struct, record, enum, enum member, constructor, field). query: mode text = pattern matched as both a literal and a regex (use fileGlob, not a path parameter, to restrict files); symbol/references = symbol name; declaration-kind modes = optional case-insensitive name filter (exact name unless exactMatch is false). Declaration-kind listings return at most 30 rows: when more match, totalRecords holds the full match count, hasMoreData is true, listSummary gives per-project (byProject) and top-10 per-file (byFile, truncatedFileCount) match counts over ALL matches, and warningDetails says the list was truncated - narrow with query or projectName, or use mode: symbol. Zero matches is not an error: it returns an empty result (isError false, totalRecords 0) whose statusMessage says what to try next.")]
    public Task<SentinelCallToolResult<object>> SearchSolutionTool(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    SearchMode mode,
    [Description("mode: text = pattern matched as both a literal and a regex; symbol/references/callers/implementations = symbol name; attribute = attribute name; declaration-kind modes = optional case-insensitive name filter (see exactMatch). Omit in declaration-kind modes only if you want up to 30 rows of that kind. Text mode is case-insensitive and per line, .cs files only; use | for alternation. Required for modes text, symbol, references, callers, implementations and attribute.")]
        string? query = null,
    [Description("mode: text only. Glob restricting file paths (omit for all files). " +
        "Supports '*' (within one path segment), '**' (any depth), '?' (one char), " +
        "'{a,b,...}' alternation, and '[abc]'/'[!abc]' character classes. A glob " +
        "containing '/' matches the solution-relative path (e.g. '**/Foo.cs', 'MyProj/*.cs'); a " +
        "glob with no '/' matches the bare filename only (e.g. '*.cs', 'Foo.cs').")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? fileGlob = null,
    [Description("mode: text only. Caps total matches scanned.")]
        [ToolOptionAttribute(ToolOptionTag.ResultLimit)] int maxResults = 200,
    [Description("mode: symbol only. Restricts to one kind of symbol.")]
        [ExternalInputRequired(DataTag.SymbolKind)] SymbolKindFilter symbolKind = SymbolKindFilter.any,
    [Description("mode: symbol only. Restricts to symbols declared inside this type.")]
        [ExternalInputRequired(DataTag.ContainingType)] string? containingType = null,
    [Description("mode: symbol only. Restricts to symbols declared inside this namespace.")]
        [ExternalInputRequired(DataTag.ContainingNamespace)] string? containingNamespace = null,
    [Description("Restricts to one project (symbol/all/declaration-kind/attribute modes).")]
        [Consumes(DataTag.ProjectName, required: false)] string? projectName = null,
    [Description("mode: symbol and declaration-kind modes. true (default) = exact name match; false = substring (contains) match.")]
        [ToolOption(ToolOptionTag.MatchType)] bool exactMatch = true,
    [Description("mode: symbol/references/callers/implementations/attribute only. Pins resolution when the name is ambiguous across files. Ignored in text and declaration-kind modes - use fileGlob in text mode.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
    [Description("mode: references/callers/implementations only. Optional; use when the name is ambiguous. " + ToolParams.ContextSnippet)]
        [Consumes(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
    [Description("mode: references/callers/implementations only. " + ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
    [Description("mode: references/callers/implementations only. " + ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
    [Description("false (default) = skip test projects in every mode, including symbol; true = include them. Naming a test project in projectName/filePath always includes it.")] bool includeTests = false,
    [Description("mode: attribute only. Owner must also carry this attribute.")] string? hasAttribute = null,
    CancellationToken cancellationToken = default)
    => DispatchSearch(reason, mode, query, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch, filePath, contextSnippet, lineBefore, lineAfter, includeTests, hasAttribute, cancellationToken);

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
    string? lineBefore, string? lineAfter, bool includeTests, string? hasAttribute, CancellationToken cancellationToken)
    {
        switch (mode)
        {
            case SearchMode.text:
                return _impl.SearchSolutionText(reason, query ?? string.Empty, fileGlob, maxResults, includeTests, cancellationToken);
            case SearchMode.symbol:
                return _symbolNavigation.LocateSymbol(reason, query ?? string.Empty, symbolKind, containingType, containingNamespace, projectName, filePath, exactMatch, includeTests: includeTests, cancellationToken: cancellationToken);
            case SearchMode.references:
                return _symbolRelationship.FindReferences(reason, query ?? string.Empty, FindReferencesKind.all, filePath, contextSnippet, lineBefore, lineAfter, includeTests: includeTests, cancellationToken: cancellationToken);
            case SearchMode.callers:
                return _symbolRelationship.FindReferences(reason, query ?? string.Empty, FindReferencesKind.callers, filePath, contextSnippet, lineBefore, lineAfter, includeTests: includeTests, cancellationToken: cancellationToken);
            case SearchMode.implementations:
                return _symbolRelationship.FindReferences(reason, query ?? string.Empty, FindReferencesKind.implementations, filePath, contextSnippet, lineBefore, lineAfter, includeTests: includeTests, cancellationToken: cancellationToken);
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
    string? lineBefore, string? lineAfter, bool includeTests, string? hasAttribute, CancellationToken cancellationToken)
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
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"query is required for mode: {mode}. Pass the {what} in query.")
            };
        }

        var result = await DispatchSearchCore(reason, mode, query, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch, filePath, contextSnippet, lineBefore, lineAfter, includeTests, hasAttribute, cancellationToken);

        var ignored = FindIgnoredSearchParameters(mode, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch, filePath, contextSnippet, lineBefore, lineAfter, hasAttribute);
        if (ignored.Count == 0)
        {
            return result;
        }

        var priorWarning = string.IsNullOrEmpty(result.WarningDetails) ? string.Empty : result.WarningDetails + " ";
        return result with { WarningDetails = priorWarning + $"Ignored parameter(s) for mode {mode}: {string.Join(", ", ignored)}." };
    }

    private static List<string> FindIgnoredSearchParameters(SearchMode mode, string? fileGlob, int maxResults, SymbolKindFilter symbolKind, string? containingType, string? containingNamespace, string? projectName, bool exactMatch, string? filePath, string? contextSnippet, string? lineBefore, string? lineAfter, string? hasAttribute)
    {
        var used = mode switch
        {
            SearchMode.text => new[] { "fileGlob", "maxResults" },
            SearchMode.symbol => new[] { "symbolKind", "containingType", "containingNamespace", "projectName", "exactMatch", "filePath" },
            SearchMode.references or SearchMode.callers or SearchMode.implementations => new[] { "filePath", "contextSnippet", "lineBefore", "lineAfter" },
            SearchMode.attribute => new[] { "projectName", "filePath", "hasAttribute" },
            _ => new[] { "projectName", "exactMatch" }
        };

        var ignored = new List<string>();
        void Check(string name, bool supplied)
        {
            if (supplied && !used.Contains(name))
            {
                ignored.Add(name);
            }
        }

        Check("fileGlob", fileGlob is not null);
        Check("maxResults", maxResults != 200);
        Check("symbolKind", symbolKind != SymbolKindFilter.any);
        Check("containingType", containingType is not null);
        Check("containingNamespace", containingNamespace is not null);
        Check("projectName", projectName is not null);
        Check("exactMatch", !exactMatch);
        Check("filePath", filePath is not null);
        Check("contextSnippet", contextSnippet is not null);
        Check("lineBefore", lineBefore is not null);
        Check("lineAfter", lineAfter is not null);
        Check("hasAttribute", hasAttribute is not null);
        return ignored;
    }
}
