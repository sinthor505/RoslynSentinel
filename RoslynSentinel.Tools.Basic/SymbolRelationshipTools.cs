using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

[McpServerToolType]
public class SymbolRelationshipTools
{
    private readonly SymbolRelationshipImpl _impl;

    public SymbolRelationshipTools(SymbolRelationshipImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "QuerySymbolRelationships")]
    [Produces(DataTag.Report)]
    [Description("Queries type-relationship facts by name: implementors of an interface, attribute usages, object-creation sites, extension methods, types carrying an attribute, or methods by return type. Zero results for the given searchKind broadens to all kinds. For call-site/override queries on a method or property, use FindReferences instead.")]
    public Task<SentinelCallToolResult<object>> QuerySymbolRelationships(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string name,
        [Description("Which relationship to query.")]
        [ExternalInputRequired(DataTag.SymbolKind)] FindUsagesSearchKind searchKind,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Description("Ranks results by frequency. Only affects objectCreations.")]
        [ToolOption(ToolOptionTag.Sort)] bool sortByFrequency = false,
        CancellationToken cancellationToken = default) =>
        _impl.QuerySymbolRelationships(reason, name, searchKind, projectName, filePath, sortByFrequency, cancellationToken);

    [McpServerTool(Name = "GetBestInsertionPoint")]
    [Produces(DataTag.StartLine)]
    [Description("Returns the best 1-based line number for inserting a new member in a type, following standard C# ordering (fields -> constructors -> destructors -> properties -> events -> methods -> nested types).")]
    public Task<SentinelCallToolResult<BestInsertionResult, ResultError>> GetBestInsertionPoint(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.ContainerName)] string containerName,
        [Description("The kind of member being inserted.")]
        [ExternalInputRequired(DataTag.MemberKind)] InsertionMemberKind memberKind,
        CancellationToken cancellationToken = default) =>
        _impl.GetBestInsertionPoint(reason, filePath, containerName, memberKind, cancellationToken);

    [McpServerTool(Name = "PreviewRenameImpact")]
    [Produces(DataTag.Report)]
    [Description("Previews the impact of renaming a symbol across the solution without applying changes. Returns affected files, location count and whether any is a test file. For per-location detail use FindReferences.")]
    public Task<SentinelCallToolResult<object>> PreviewRenameImpact(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("With symbolName, resolves the target when docCommentId is unknown.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Consumes(DataTag.SymbolName)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description("Preferred target id, with projectName (from LocateSymbol); no filePath needed.")]
        string? docCommentId = null,
        [Description(ToolParams.ProjectName)] string? projectName = null,
        CancellationToken cancellationToken = default) =>
        _impl.PreviewRenameImpact(reason, filePath, symbolName, contextSnippet, lineBefore, lineAfter, docCommentId, projectName, cancellationToken);

    [McpServerTool(Name = "FindReferences")]
    [Produces(DataTag.Report)]
    [Description("Finds call sites and/or implementations for a symbol (single-level, flat lookup). For a call tree use GetCallGraph, for a local variable's read/write/capture sites TraceVariableLifetime, for a rename-impact summary PreviewRenameImpact, for type-relationship queries (implementors, attribute usage, object creation) QuerySymbolRelationships.")]
    public Task<SentinelCallToolResult<object>> FindReferences(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SymbolName, required: true)] string symbolName,
        [Description("callers: call sites. implementations: overrides/interface implementations. all: both.")]
        [Consumes(DataTag.SymbolKind)] FindReferencesKind kind,
        [Description("Pins resolution when the name is ambiguous across files.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        CancellationToken cancellationToken = default) =>
        _impl.FindReferences(reason, symbolName, kind, filePath, contextSnippet, lineBefore, lineAfter, cancellationToken);
}
