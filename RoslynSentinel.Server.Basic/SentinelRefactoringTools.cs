using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

// LEGACY FACADE — add new tools to the split classes (RefactoringSignatureTools,
// RefactoringStructuralTools, RefactoringExtractionDocsTools), not here. Preserves the original
// public constructor signature and all original [McpServerTool] method signatures for the existing
// 18 direct-construction test call sites and the Advanced typeof() smoke-check, per Decision 3 of
// docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md. Bodies delegate to internally
// -constructed instances of the 3 new split classes.
[McpServerToolType]
public class SentinelRefactoringTools
{
    private readonly RefactoringSignatureTools _signature;
    private readonly RefactoringStructuralTools _structural;
    private readonly RefactoringExtractionDocsTools _extractionDocs;

    public SentinelRefactoringTools(
        RefactoringEngine refactoringEngine,
        //StandardRefactoringEngine standardRefactoringEngine,
        MappingEngine mappingEngine,
    //    SemanticRefactoringLibrary semanticRefactoringLibrary,
    //    GranularRefactoringEngine granularRefactoringEngine,
        // AdvancedLogicEngine advancedLogicEngine,
        // RefinementEngine refinementEngine,
        // AdvancedTypeEngine advancedTypeEngine,
        StructuralRefinementEngine structuralRefinementEngine,
        //CodeStyleEngine codeStyleEngine,
        //    CodeFlowEngine codeFlowEngine,
        // AdvancedRefactoringEngine advancedRefactoringEngine,
        // LogicOptimizationEngine logicOptimizationEngine,
        // ModernizationEngine modernizationEngine,
        // OutParamRefactoringEngine outParamRefactoringEngine,
        MsToolAugmentEngine augmentEngine,
    //    CodeGenerationEngine codeGenerationEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        SentinelConfiguration config,
        ILogger<SentinelRefactoringTools> logger)
    {
        _signature = new RefactoringSignatureTools(refactoringEngine, workspaceManager, validationEngine, symbolNavigationEngine, logger);
        _structural = new RefactoringStructuralTools(refactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, logger);
        _extractionDocs = new RefactoringExtractionDocsTools(refactoringEngine, augmentEngine, mappingEngine, symbolNavigationEngine, workspaceManager, validationEngine, logger);
    }

    [McpServerTool(Name = "RenameSymbol")]
    public Task<ToolResult<object>> RenameSymbol(
        ToolCallReason reason,
        string projectName,
        string docCommentId,
        string newName,
        string sessionId = "",
        bool dryRun = false,
        bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default) =>
        _signature.RenameSymbol(reason, projectName, docCommentId, newName, sessionId, dryRun, returnDiff, requestParams, cancellationToken);

    [McpServerTool(Name = "MethodSignature")]
    public Task<ToolResult<object>> MethodSignature(
        ToolCallReason reason,
        FilePathWrapper filepath,
        AddRemoveViewAction operation,
        string methodName,
        string? paramName = null,
        string? paramType = null,
        string? defaultValue = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default,
        bool nullDefault = false) =>
        _signature.MethodSignature(reason, filepath, operation, methodName, paramName, paramType, defaultValue, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken, nullDefault);

    [McpServerTool(Name = "ChangeAccessibility")]
    public Task<ToolResult<object>> ChangeAccessibility(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string targetName,
        AccessibilityLevel accessibility,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _signature.ChangeAccessibility(reason, filepath, targetName, accessibility, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ConstructorParameter")]
    public Task<ToolResult<object>> ConstructorParameter(
        ToolCallReason reason,
        FilePathWrapper filepath,
        AddRemoveViewAction operation,
        string className,
        string? paramName = null,
        string? paramType = null,
        string? fieldName = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _signature.ConstructorParameter(reason, filepath, operation, className, paramName, paramType, fieldName, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "GenerateMapping")]
    public Task<ToolResult<object>> GenerateMapping(
        FilePathWrapper filepath,
        string fromType,
        string toType,
        bool dryRun = false,
        bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default) =>
        _extractionDocs.GenerateMapping(filepath, fromType, toType, dryRun, returnDiff, requestParams, cancellationToken);

    [McpServerTool(Name = "Member")]
    public Task<ToolResult<object>> Member(
        ToolCallReason reason,
        FilePathWrapper filepath,
        MemberAction operation,
        string? containerName = null,
        string? namespaceName = null,
        string? memberName = null,
        string? newMemberSource = null,
        string? position = null,
        TypedMemberKind? typedKind = null,
        string? typedName = null,
        string? typedType = null,
        string accessibility = "public",
        bool hasSetter = true,
        bool isInit = false,
        bool isReadonly = false,
        bool isStatic = false,
        string? initializer = null,
        bool skipPrecheck = false,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default) =>
        _structural.Member(reason, filepath, operation, containerName, namespaceName, memberName, newMemberSource, position, typedKind, typedName, typedType,
            accessibility, hasSetter, isInit, isReadonly, isStatic, initializer, skipPrecheck, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff,
            requestParams, cancellationToken);

    [McpServerTool(Name = "UsingDirective")]
    public Task<ToolResult<object>> UsingDirective(
        ToolCallReason reason,
        FilePathWrapper filepath,
        AddRemoveViewAction operation,
        string? namespaceName = null,
        bool simplifyExisting = false,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _extractionDocs.UsingDirective(reason, filepath, operation, namespaceName, simplifyExisting, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyEnum")]
    public Task<ToolResult<object>> ModifyEnum(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string enumName,
        string values,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _structural.ModifyEnum(reason, filepath, enumName, values, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "SummaryComment")]
    public Task<ToolResult<object>> SummaryComment(
        ToolCallReason reason,
        FilePathWrapper filepath,
        AddRemoveViewAction operation,
        string targetName,
        string? summaryText = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        string? containingTypeName = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _extractionDocs.SummaryComment(reason, filepath, operation, targetName, summaryText, contextSnippet, lineBefore, lineAfter, containingTypeName, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ExtractLocalVariable")]
    public Task<ToolResult<object>> ExtractLocalVariable(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string exactExpressionText,
        string variableName,
        string? lineBefore = null,
        string? lineAfter = null,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _extractionDocs.ExtractLocalVariable(reason, filepath, exactExpressionText, variableName, lineBefore, lineAfter, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ExtractMethodSafe")]
    public Task<ToolResult<object>> ExtractMethodSafe(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string newMethodName,
        string exactSourceBlock,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _extractionDocs.ExtractMethodSafe(reason, filepath, newMethodName, exactSourceBlock, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyAttribute")]
    public Task<ToolResult<object>> ModifyAttribute(
        ToolCallReason reason,
        FilePathWrapper? filepath = null,
        string? targetName = null,
        string? existingAttribute = null,
        AttributeModifyAction? action = null,
        string? newAttribute = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<AttributeEdit>? edits = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _structural.ModifyAttribute(reason, filepath, targetName, existingAttribute, action, newAttribute, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyModifier", UseStructuredContent = true, OutputSchemaType = typeof(ModifyModifierResultEnvelope))]
    public Task<ToolResult<object>> ModifyModifier(
        ToolCallReason reason,
        FilePathWrapper? filepath = null,
        string? targetName = null,
        NonAccessibilityModifier? modifier = null,
        AddRemoveAction? action = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<ModifierEdit>? edits = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _structural.ModifyModifier(reason, filepath, targetName, modifier, action, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyBaseType")]
    public Task<ToolResult<object>> ModifyBaseType(
        ToolCallReason reason,
        FilePathWrapper? filepath = null,
        string? typeName = null,
        string? baseTypeName = null,
        AddRemoveAction? action = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<BaseTypeEdit>? edits = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _structural.ModifyBaseType(reason, filepath, typeName, baseTypeName, action, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "SyncTypeAndFilename")]
    public Task<ToolResult<object>> SyncTypeAndFilename(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string? targetTypeName = null,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _structural.SyncTypeAndFilename(reason, filepath, targetTypeName, dryRun, returnDiff, cancellationToken);


    // Added by InsertMemberAfter (expected - used for diagnostics)
    // Thin static wrapper preserved for SentinelWorkspaceTools.cs and SentinelWholeFileWriteTools.cs,
    // which still call SentinelRefactoringTools.BuildDiffFromPreImages(...) directly - confirmed via
    // LocateSymbol/ReadFile this is NOT dead code (unlike BuildDiffAsync, which had zero callers
    // anywhere in the solution). A future cleanup could point those 2 callers at
    // ValidateAndApplyHelper.BuildDiffFromPreImages directly (per this plan's Decision 1 cross-class
    // coupling note) and drop this wrapper, but that touches Workspace-side files outside Step 3's scope.
    internal static string BuildDiffFromPreImages(Dictionary<FilePathWrapper, string> changes, IReadOnlyDictionary<string, string?>? preImages) =>
        ValidateAndApplyHelper.BuildDiffFromPreImages(changes, preImages);
}

// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Named shape mirroring the <c>updatedHandle</c> anonymous object inside
/// <see cref="RenameSymbolResultEnvelope"/>'s <c>Data</c>, built from <see cref="SymbolHandle"/>.
/// Primary path only - see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record RenameSymbolUpdatedHandle(
    [property: Produces(DataTag.SessionId)] string SessionId,
    [property: Produces(DataTag.ProjectName)] string ProjectName,
    [property: Produces(DataTag.DocCommentId)] string DocCommentId);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Named shape mirroring the anonymous object <see cref="SentinelRefactoringTools.RenameSymbol"/>
/// assigns to <c>ToolResult&lt;object&gt;.Data</c> on its applied success path. Primary path only
/// (the resolution-failed / no-pending-changes / apply-failed error paths return a different,
/// error-shaped envelope with no Data) - see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record RenameSymbolData(
    [property: Produces(DataTag.ChangeId)] string? ChangeId,
    bool DryRun,
    string? Diff,
    [property: Produces(DataTag.SymbolName)] string OldName,
    [property: Produces(DataTag.SymbolName)] string NewName,
    int FilesChanged,
    RenameSymbolUpdatedHandle? UpdatedHandle,
    IReadOnlyList<ResidualMention>? ResidualMentions,
    string? ResidualMentionsNote);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Envelope shape mirroring <c>ToolResult&lt;object&gt;</c> as actually populated on
/// <see cref="SentinelRefactoringTools.RenameSymbol"/>'s primary success path, which sets only
/// <c>Success</c> and <c>Data</c> (not TotalRecords/WorkspaceVersion/etc). Primary path only -
/// see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record RenameSymbolResultEnvelope(
    bool Success,
    RenameSymbolData? Data);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Envelope shape mirroring <c>ToolResult&lt;object&gt;</c> as actually populated on
/// <see cref="SentinelRefactoringTools.ModifyModifier"/>'s primary (autoStage=true, singular-edit,
/// non-batch) success path, which sets only <c>Success</c> and <c>Data</c>. Deliberately does not
/// cover the batch (edits != null), autoStage=false, or error branches - see
/// proposal_structuredcontent_rollout.md. AppliedChangeSummary lives in RoslynSentinel.Common and
/// is reused by several other tools (e.g. ChangeAccessibility); it is intentionally left
/// undecorated here rather than adding DataTag attributes to a shared type outside this POC's
/// file scope - ChangeId tagging on ModifyModifier's own output is expressed at the method level
/// via the existing [Produces(DataTag.ChangeId)] instead.
/// </summary>
public sealed record ModifyModifierResultEnvelope(
    bool Success,
    AppliedChangeSummary? Data);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Named shape mirroring the engine-layer <c>MethodParameterInfo</c> (RoslynSentinel.Basic,
/// RefactoringEngine.cs) as surfaced by MethodSignature's view branch. Declared here (rather than
/// tagging MethodParameterInfo itself) because that engine type lives outside this POC's file
/// scope - see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record MethodSignatureParameterInfo(
    [property: Produces(DataTag.SymbolName)] string ParamName,
    [property: Produces(DataTag.DataType)] string ParamType,
    string? DefaultValue);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Mirrors the anonymous <c>new { Parameters = parameters }</c> object assigned to Data on
/// MethodSignature's view branch.
/// </summary>
public sealed record MethodSignatureViewData(
    IReadOnlyList<MethodSignatureParameterInfo> Parameters);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Envelope shape mirroring <c>ToolResult&lt;object&gt;</c> as actually populated on
/// <see cref="SentinelRefactoringTools.MethodSignature"/>'s "view" branch only (operation=view),
/// which sets only <c>Success</c> and <c>Data = new { Parameters }</c>. The add/remove branches
/// (both the non-autoStage ToJsonSummary shape and the autoStage applied-with-offload
/// MemberChangedContentResult/AppliedChangeSummary shape) are intentionally NOT covered by this
/// POC - see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record MethodSignatureViewResultEnvelope(
    bool Success,
    MethodSignatureViewData? Data);
