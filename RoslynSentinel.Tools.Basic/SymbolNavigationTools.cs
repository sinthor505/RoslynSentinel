using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

[McpServerToolType]
public class SymbolNavigationTools
{
    private readonly SymbolNavigationImpl _impl;

    public SymbolNavigationTools(SymbolNavigationImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "LocateSymbol", ReadOnly = true)]
    [Produces(DataTag.DocCommentId)]
    [Produces(DataTag.ProjectName)]
    [Description("Locates declaration sites for a symbol by name. Only matches declared symbols, not arbitrary text - use Search for free text. Returns SymbolHandles containing projectName, docCommentId, and filePath.")]
    public Task<SentinelCallToolResult<object>> LocateSymbolTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string symbolName,
        [Description("Restricts the search to one kind of symbol.")]
        [ExternalInputRequired(DataTag.SymbolKind)] SymbolKindFilter symbolKind = SymbolKindFilter.any,
        [Description("Restricts results to symbols declared inside this type, to disambiguate a common member name.")]
        [ExternalInputRequired(DataTag.ContainingType)] string? containingType = null,
        [Description("Restricts results to symbols declared inside this namespace.")]
        [ExternalInputRequired(DataTag.ContainingNamespace)] string? containingNamespace = null,
        [ExternalInputRequired(DataTag.ProjectName)] string? projectName = null,
        [ExternalInputRequired(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Description("false enables a prefix/contains search instead of an exact name match.")]
        [ToolOption(ToolOptionTag.MatchType)] bool exactMatch = true,
        CancellationToken cancellationToken = default) =>
        _impl.LocateSymbol(reason, symbolName, symbolKind, containingType, containingNamespace, projectName, filePath, exactMatch, cancellationToken: cancellationToken);

    [McpServerTool(Name = "InspectSymbol", ReadOnly = true)]
    [Produces(DataTag.DocCommentId)]
    [Description("Inspects a symbol in depth. Requires a file and a context snippet to resolve the symbol - if you only have a name, use Search(mode: symbol) first to find the declaring file.")]
    public Task<SentinelCallToolResult<object>> InspectSymbolTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Description("info returns type, kind, accessibility, attributes, and documentation. blastRadius returns all call sites and affected projects - for a full caller/override breakdown instead of a summary, use Search(mode: references).")]
        [ToolOption(ToolOptionTag.Aspect)] InspectSymbolAspect aspect,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        CancellationToken cancellationToken = default
        ) =>
        _impl.InspectSymbol(reason, filePath, contextSnippet, aspect, lineBefore, lineAfter, cancellationToken);

    [McpServerTool(Name = "GetTypeInfo", ReadOnly = true)]
    [Produces(DataTag.Report)]
    [Description("Returns type information for a type you already know the name of - hierarchy, members, or both. If unsure it exists or the name is ambiguous, use Search(mode: symbol) first. To change an enum's values, use ModifyEnum.")]
    public Task<SentinelCallToolResult<object>> GetTypeInfoTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.DataType)] string typeName,
        [Description("hierarchy: base class chain, interfaces, derived types. members: all public/protected members (an enum's values appear as Field members with their value in Signature, e.g. \"Status.Active = 1\"). both: hierarchy and members.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] TypeInfoInclude include = TypeInfoInclude.both,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Description("Excludes inherited members when false. Applies only to include=members or include=both.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] bool includeInherited = true,
        CancellationToken cancellationToken = default) =>
        _impl.GetTypeInfo(reason, typeName, include, projectName, includeInherited, cancellationToken);
}
