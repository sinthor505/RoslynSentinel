using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

[McpServerToolType]
public class SymbolNavigationTools
{
    private readonly SymbolNavigationImpl _impl;

    public SymbolNavigationTools(
        SymbolNavigationEngine symbolNavigationEngine,
        ImpactAnalyzer impactAnalyzer,
        ISolutionProvider workspaceManager,
        ILogger logger)
    {
        _impl = new SymbolNavigationImpl(symbolNavigationEngine, impactAnalyzer, workspaceManager, logger);
    }

    [McpServerTool(Name = "LocateSymbol", UseStructuredContent = true, OutputSchemaType = typeof(LocateSymbolResult))]
    [Produces(DataTag.DocCommentId)]
    [Produces(DataTag.ProjectName)]
    [Description("Locates declaration sites for a symbol by name. Only matches declared symbols, not arbitrary text - use SearchSolutionText for free text. Returns SymbolHandles containing projectName, docCommentId, and filePath.")]
    public Task<ToolResult<object>> LocateSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string symbolName,
        [Description("Restricts the search to one kind of symbol.")]
        [ExternalInputRequired(DataTag.SymbolKind)] SymbolKindFilter symbolKind = SymbolKindFilter.any,
        [Description("Restricts results to symbols declared inside this type, to disambiguate a common member name.")]
        [ExternalInputRequired(DataTag.ContainingType)] string? containingType = null,
        [Description("Restricts results to symbols declared inside this namespace.")]
        [ExternalInputRequired(DataTag.ContainingNamespace)] string? containingNamespace = null,
        [ExternalInputRequired(DataTag.ProjectName)] string? projectName = null,
        [ExternalInputRequired(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description("false enables a prefix/contains search instead of an exact name match.")]
        [ToolOption(ToolOptionTag.MatchType)] bool exactMatch = true,
        CancellationToken cancellationToken = default) =>
        _impl.LocateSymbol(reason, symbolName, symbolKind, containingType, containingNamespace, projectName, filepath, exactMatch, cancellationToken);

    [McpServerTool(Name = "InspectSymbol")]
    [Produces(DataTag.DocCommentId)]
    [Description("Inspects a symbol in depth. Requires a file and a context snippet to resolve the symbol - if you only have a name, use LocateSymbol first to find the declaring file.")]
    public Task<ToolResult<object>> InspectSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Description("info returns type, kind, accessibility, attributes, and documentation. blastRadius returns all call sites and affected projects - for a full caller/override breakdown instead of a summary, use FindReferences.")]
        [ToolOption(ToolOptionTag.Aspect)] InspectSymbolAspect aspect,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        CancellationToken cancellationToken = default
        ) =>
        _impl.InspectSymbol(reason, filepath, contextSnippet, aspect, lineBefore, lineAfter, cancellationToken);

    [McpServerTool(Name = "GetTypeInfo")]
    [Produces(DataTag.Report)]
    [Description("Returns type information for a type you already know the name of - hierarchy, members, or both. If you're not sure the type exists or need to disambiguate a common name, use LocateSymbol first. To change an enum's values, use ModifyEnum.")]
    public Task<ToolResult<object>> GetTypeInfo(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.DataType)] string typeName,
        [Description("hierarchy: base class chain, interfaces, derived types. members: all public/protected members - for an enum, each value appears as a Field member with its explicit/ordinal value inline in Signature (e.g. \"Status.Active = 1\"), and inherited System.Enum/ValueType noise is excluded automatically. both: hierarchy and members together (default).")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] TypeInfoInclude include = TypeInfoInclude.both,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Description("Excludes inherited members when false. Applies only to include=members or include=both.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] bool includeInherited = true,
        CancellationToken cancellationToken = default) =>
        _impl.GetTypeInfo(reason, typeName, include, projectName, includeInherited, cancellationToken);
}

/// <summary>
/// Named shape mirroring <c>SymbolLocation</c> (the real per-item type
/// <c>SymbolNavigationEngine.LocateSymbolAsync</c> returns), used only as part of
/// <c>LocateSymbolResult</c>'s <c>OutputSchemaType</c> so <see cref="SymbolNavigationTools.LocateSymbol"/>
/// can advertise a real MCP <c>outputSchema</c>/<c>StructuredContent</c> shape without changing the
/// method's actual return type. Primary path only - see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record LocatedSymbolInfo(
    [property: Produces(DataTag.SymbolName)] string SymbolName,
    [property: Produces(DataTag.DocCommentId)] string? DocCommentId,
    [property: Produces(DataTag.ProjectName)] string ProjectName,
    string FullyQualifiedName,
    [property: Produces(DataTag.SymbolKind)] string SymbolKind,
    [property: Produces(DataTag.Signature)] string Signature,
    [property: Produces(DataTag.ContainingType)] string? ContainingType,
    [property: Produces(DataTag.ContainingNamespace)] string? ContainingNamespace,
    [property: Produces(DataTag.SourceFilepath)] string? FilePath,
    int? Line,
    [property: Produces(DataTag.ContextSnippet)] string? ContextSnippet,
    [property: Produces(DataTag.Accessibility)] string Accessibility);

/// <summary>
/// Named shape mirroring the actual <c>ToolResult&lt;object&gt;</c> envelope
/// <see cref="SymbolNavigationTools.LocateSymbol"/> returns on its primary (match-found) success path
/// - StructuredContent is populated from the whole method return value, not just its inner
/// <c>Data</c>, since LocateSymbol (unlike McpServerStatus) returns
/// <c>Task&lt;ToolResult&lt;object&gt;&gt;</c> rather than a bare object. Used only as
/// <c>OutputSchemaType</c> so the tool can advertise a real MCP <c>outputSchema</c>/
/// <c>StructuredContent</c> shape (2026-07-28 protocol) without changing the method's actual return
/// type. Primary path only (the not-found and exception error paths return a different, error-shaped
/// envelope with no Data) - see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record LocateSymbolResult(
    bool Success,
    IReadOnlyList<LocatedSymbolInfo>? Data,
    int? TotalRecords,
    int? WorkspaceVersion);
