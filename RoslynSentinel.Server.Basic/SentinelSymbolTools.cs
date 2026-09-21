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
    [Description("Locates declaration sites for a symbol by name (declared symbols only, not free text).")]
    public Task<SentinelCallToolResult<object>> LocateSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string symbolName,
        [Description("Restricts the search to one kind of symbol.")]
        [ExternalInputRequired(DataTag.SymbolKind)] SymbolKindFilter symbolKind = SymbolKindFilter.any,
        [Description("Restricts to symbols declared inside this type.")]
        [ExternalInputRequired(DataTag.ContainingType)] string? containingType = null,
        [Description("Restricts results to symbols declared inside this namespace.")]
        [ExternalInputRequired(DataTag.ContainingNamespace)] string? containingNamespace = null,
        [ExternalInputRequired(DataTag.ProjectName)] string? projectName = null,
        [ExternalInputRequired(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description("false enables prefix/contains search instead of exact match.")]
        [ToolOption(ToolOptionTag.MatchType)] bool exactMatch = true,
        CancellationToken cancellationToken = default) =>
        _navigation.LocateSymbol(reason, symbolName, symbolKind, containingType, containingNamespace, projectName, filepath, exactMatch, cancellationToken);

    [McpServerTool(Name = "InspectSymbol")]
    [Produces(DataTag.DocCommentId)]
    [Description("Inspects a symbol in depth; requires a file and context snippet to resolve it.")]
    public Task<SentinelCallToolResult<object>> InspectSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Description("info: type/kind/accessibility/attributes/docs. blastRadius: call sites and affected projects (summary; use FindReferences for full breakdown).")]
        [ToolOption(ToolOptionTag.Aspect)] InspectSymbolAspect aspect,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        CancellationToken cancellationToken = default
        ) =>
        _navigation.InspectSymbol(reason, filepath, contextSnippet, aspect, lineBefore, lineAfter, cancellationToken);

    [McpServerTool(Name = "QuerySymbolRelationships")]
    [Produces(DataTag.Report)]
    [Description("Queries type-relationship facts: implementors, attribute usages, object-creation sites, extension methods, or methods by return type.")]
    public Task<SentinelCallToolResult<object>> QuerySymbolRelationships(
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
    [Description("Returns the best 1-based line number to insert a new member, following standard C# member ordering.")]
    public Task<SentinelCallToolResult<BestInsertionResult, ResultError>> GetBestInsertionPoint(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.ContainerName)] string containerName,
        [Description("The kind of member being inserted.")]
        [ExternalInputRequired(DataTag.MemberKind)] InsertionMemberKind memberKind,
        CancellationToken cancellationToken = default) =>
        _relationship.GetBestInsertionPoint(reason, filepath, containerName, memberKind, cancellationToken);

    [McpServerTool(Name = "PreviewRenameImpact")]
    [Produces(DataTag.Report)]
    [Description("Previews the impact of renaming a symbol solution-wide without applying changes.")]
    public Task<SentinelCallToolResult<object>> PreviewRenameImpact(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("With symbolName, resolves the target when docCommentId isn't known.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Consumes(DataTag.SymbolName)] string? symbolName = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description("Preferred, with projectName: unambiguous target id from LocateSymbol.")]
        string? docCommentId = null,
        [Description(ToolParams.ProjectName)] string? projectName = null,
        CancellationToken cancellationToken = default) =>
        _relationship.PreviewRenameImpact(reason, filepath, symbolName, contextSnippet, lineBefore, lineAfter, docCommentId, projectName, cancellationToken);

    [McpServerTool(Name = "FindReferences")]
    [Produces(DataTag.Report)]
    [Description("Finds call sites and/or implementations for a symbol (flat, single-level).")]
    public Task<SentinelCallToolResult<object>> FindReferences(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SymbolName, required: true)] string symbolName,
        [Description("callers: call sites only. implementations: overrides/interface impls only. all: both.")]
        [Consumes(DataTag.SymbolKind)] FindReferencesKind kind,
        [Description("Optional: pins resolution when the name is ambiguous across files.")]
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        CancellationToken cancellationToken = default) =>
        _relationship.FindReferences(reason, symbolName, kind, filepath, contextSnippet, lineBefore, lineAfter, cancellationToken);

    [McpServerTool(Name = "GetTypeInfo")]
    [Produces(DataTag.Report)]
    [Description("Returns a known type's hierarchy, members, or both. Use LocateSymbol first if unsure the type exists.")]
    public Task<SentinelCallToolResult<object>> GetTypeInfo(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.DataType)] string typeName,
        [Description("hierarchy: base chain, interfaces, derived types. members: public/protected members (enum values shown as Field with their value). both (default): both.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] TypeInfoInclude include = TypeInfoInclude.both,
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Description("Excludes inherited members when false. Applies only to include=members/both.")]
        [ToolOptionAttribute(ToolOptionTag.Filter)] bool includeInherited = true,
        CancellationToken cancellationToken = default) =>
        _navigation.GetTypeInfo(reason, typeName, include, projectName, includeInherited, cancellationToken);
}
