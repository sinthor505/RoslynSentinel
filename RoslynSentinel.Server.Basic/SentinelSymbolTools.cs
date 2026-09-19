using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

[McpServerToolType]
public class SentinelSymbolTools
{
    private readonly SymbolNavigationTools _navigation;
    private readonly SymbolRelationshipTools _relationship;

    public SentinelSymbolTools(
        ImpactAnalyzer impactAnalyzer,
        SemanticSearchEngine semanticSearchEngine,
        // MetricsEngine metricsEngine,
        InventoryEngine inventoryEngine,
        // DeadCodeEngine deadCodeEngine,
        AnalysisEngine analysisEngine,
        // DocumentationEngine documentationEngine,
        DependencyEngine dependencyEngine,
        ProjectStructureEngine projectStructureEngine,
        // AsyncSafetyEngine asyncSafetyEngine,
        // HealthOrchestrationEngine healthOrchestrationEngine,
        // ArchitecturalEngine architecturalEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        // DependencyInjectionEngine dependencyInjectionEngine,
        DiscoveryEngine discoveryEngine,
        ProjectConsistencyEngine projectConsistencyEngine,
        ISolutionProvider workspaceManager,
        SentinelConfiguration config,
        ILogger<SentinelSymbolTools> logger)
    {
        _ = inventoryEngine;
        _ = analysisEngine;
        _ = dependencyEngine;
        _ = projectStructureEngine;
        _ = projectConsistencyEngine;
        _ = config;
        _navigation = new SymbolNavigationTools(symbolNavigationEngine, impactAnalyzer, workspaceManager, logger);
        _relationship = new SymbolRelationshipTools(discoveryEngine, semanticSearchEngine, symbolNavigationEngine, workspaceManager, logger);
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
        _navigation.LocateSymbol(reason, symbolName, symbolKind, containingType, containingNamespace, projectName, filepath, exactMatch, cancellationToken);

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
        _navigation.InspectSymbol(reason, filepath, contextSnippet, aspect, lineBefore, lineAfter, cancellationToken);

    [McpServerTool(Name = "QuerySymbolRelationships")]
    [Produces(DataTag.Report)]
    [Description("Queries type-relationship facts by name: implementors of an interface, attribute usages, object-creation sites, extension methods, types carrying an attribute, or methods by return type. If the targeted searchKind returns zero results, automatically broadens to all kinds and reports whatever is found. For call-site/override queries on a method or property, use FindReferences instead.")]
    public Task<ToolResult<object>> QuerySymbolRelationships(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string name,
        [Description("Which relationship to query.")]
        [ExternalInputRequired(DataTag.SymbolKind)] FindUsagesSearchKind searchKind,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description("Ranks results by frequency. Only affects objectCreations.")]
        [ToolOption(ToolOptionTag.Sort)] bool sortByFrequency = false,
        CancellationToken cancellationToken = default) =>
        _relationship.QuerySymbolRelationships(reason, name, searchKind, projectName, filepath, sortByFrequency, cancellationToken);

    [McpServerTool(Name = "GetBestInsertionPoint")]
    [Produces(DataTag.StartLine)]
    [Description("Returns the best 1-based line number for inserting a new member in a type, following standard C# ordering (fields -> constructors -> destructors -> properties -> events -> methods -> nested types).")]
    public Task<ToolResult<object>> GetBestInsertionPoint(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.ContainerName)] string containerName,
        [Description("The kind of member being inserted.")]
        [ExternalInputRequired(DataTag.MemberKind)] InsertionMemberKind memberKind,
        CancellationToken cancellationToken = default) =>
        _relationship.GetBestInsertionPoint(reason, filepath, containerName, memberKind, cancellationToken);

    [McpServerTool(Name = "PreviewRenameImpact")]
    [Produces(DataTag.Report)]
    [Description("Previews the impact of renaming a symbol across the solution without applying changes. Returns affected files and location count, plus whether any affected file is a test file. For the full per-location list, use FindReferences.")]
    public Task<ToolResult<object>> PreviewRenameImpact(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Together with symbolName, resolves the target when docCommentId isn't known. Use contextSnippet/lineBefore/lineAfter to disambiguate if the name appears more than once.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Consumes(DataTag.SymbolName)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description("Preferred way to identify the target, together with projectName - as returned by LocateSymbol. Unambiguous; no filepath needed.")]
        string? docCommentId = null,
        [Description(ToolParams.ProjectName)] string? projectName = null,
        CancellationToken cancellationToken = default) =>
        _relationship.PreviewRenameImpact(reason, filepath, symbolName, contextSnippet, lineBefore, lineAfter, docCommentId, projectName, cancellationToken);

    [McpServerTool(Name = "FindReferences")]
    [Produces(DataTag.Report)]
    [Description("Finds call sites and/or implementations for a symbol. This is a single-level, flat lookup - for a multi-level call tree use GetCallGraph, for a local variable's read/write/capture sites use TraceVariableLifetime, for a rename-impact summary use PreviewRenameImpact, and for type-relationship queries (implementors, attribute usage, object creation, etc.) use QuerySymbolRelationships.")]
    public Task<ToolResult<object>> FindReferences(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SymbolName, required: true)] string symbolName,
        [Description("callers: call sites only. implementations: overrides/interface implementations only. all: both, clearly labeled.")]
        [Consumes(DataTag.SymbolKind)] FindReferencesKind kind,
        [Description("Optional - omit to search by name across the solution; supply to pin resolution when the name is ambiguous across files.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        CancellationToken cancellationToken = default) =>
        _relationship.FindReferences(reason, symbolName, kind, filepath, contextSnippet, lineBefore, lineAfter, cancellationToken);

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
        _navigation.GetTypeInfo(reason, typeName, include, projectName, includeInherited, cancellationToken);
}
