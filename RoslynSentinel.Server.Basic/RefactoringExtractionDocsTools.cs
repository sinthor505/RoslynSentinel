using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

// Decision 7 step 3 (plan_split_workspace_refactoring_tools_for_di.md): MCP-surface half of the
// RefactoringExtractionDocsTools/Impl pair. All method bodies delegate one-line to _impl; all original
// [McpServerTool]/[Produces]/[Description] attributes are preserved verbatim from SentinelRefactoringTools.cs.
[McpServerToolType]
public class RefactoringExtractionDocsTools
{
    private readonly RefactoringExtractionDocsImpl _impl;

    public RefactoringExtractionDocsTools(
        RefactoringEngine refactoringEngine,
        MsToolAugmentEngine msToolAugmentEngine,
        MappingEngine mappingEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        ILogger logger)
    {
        _impl = new RefactoringExtractionDocsImpl(refactoringEngine, msToolAugmentEngine, mappingEngine, symbolNavigationEngine, workspaceManager, validationEngine, logger);
    }

    [McpServerTool(Name = "GenerateMapping")]
    [Produces(DataTag.ChangeId)]
    [Description("Generates a mapping method between fromType and toType. Returns changeId.")]
    public Task<ToolResult<object>> GenerateMapping(
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [ExternalInputRequired(DataTag.DataType, required: true)] string fromType,
        [ExternalInputRequired(DataTag.DataType)] string toType,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default) =>
        _impl.GenerateMapping(filepath, fromType, toType, dryRun, returnDiff, requestParams, cancellationToken);

    [McpServerTool(Name = "UsingDirective")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view using directives in a file.")]
    public Task<ToolResult<object>> UsingDirective(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: inserts a using; no-op if already present. remove: deletes the matching using directive. view: lists current using directives (name, isStatic, alias); makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add/remove, unused for operation=view.
        [Description("Required for add/remove. For static usings, prefix with \"static \" (e.g. \"static System.Math\"). Not required for view.")]
        [Consumes(DataTag.SymbolName, required: false)] string? namespaceName = null,
        [Description("add only. After inserting, runs Roslyn's Simplifier (semantic-model-based, not text find/replace) over the file to shorten now-redundant fully-qualified references - only reduces a name when doing so introduces no ambiguity.")] bool simplifyExisting = false,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.UsingDirective(reason, filepath, operation, namespaceName, simplifyExisting, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "SummaryComment")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view a /// <summary> XML doc comment on a type or member. For overloaded targets, combine targetName with contextSnippet/lineBefore/lineAfter to disambiguate.")]
    public Task<ToolResult<object>> SummaryComment(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: adds or replaces the summary, overwriting any existing one. remove: deletes the summary comment if present; no-op if none exists. view: returns the current summary text (or null if none); makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add, unused for remove/view.
        [Description("Required for add - the new summary text. Not used for remove/view.")][Consumes(DataTag.SourceCode, required: false)] string? summaryText = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ContainingTypeName)] string? containingTypeName = null,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.SummaryComment(reason, filepath, operation, targetName, summaryText, contextSnippet, lineBefore, lineAfter, containingTypeName, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ExtractLocalVariable")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts an inline expression into a named local variable declaration. exactExpressionText is NOT a search fragment (unlike contextSnippet on other tools) - it must be the WHOLE expression to extract, copied verbatim.")]
    public Task<ToolResult<object>> ExtractLocalVariable(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("The exact expression to extract, copied VERBATIM character-for-character from a prior ReadFile/GetMethodSource result - the whole expression, not a shortened/unique fragment. This is NOT a search anchor like contextSnippet on other tools: it must match the target expression's full text exactly (whitespace differences are tolerated, but the expression itself must be complete). A partial expression may still resolve to the nearest enclosing expression rather than the one you intended, silently extracting the wrong span - if in doubt, include the whole expression, not less.")]
        [Consumes(DataTag.ContextSnippet, required: true)] string exactExpressionText,
        [Consumes(DataTag.SymbolName)] string variableName,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ExtractLocalVariable(reason, filepath, exactExpressionText, variableName, lineBefore, lineAfter, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ExtractMethodSafe")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts selected statements into a new method with the correct return type inferred from the selection. newMethodName must be a valid C# identifier. exactSourceBlock is NOT a search fragment (unlike contextSnippet on other tools) - the entire range you want extracted must appear in it verbatim, since its matched span IS the extraction boundary; a too-short excerpt silently extracts only that narrower range, not the whole intended block. Written to disk (or staged, per autoStage) like other refactoring tools - not preview-only. Returns changeId.")]
    // Fixes MS BUG: where selections ending with "return <expression>" are extracted into a method declared "private void MethodName(...)", causing a compile error. This tool uses Roslyn's SemanticModel to determine the actual type of the returned expression, and DataFlowAnalysis to find the correct parameter list. Requires a loaded solution (via set_solution_path or equivalent).
    public Task<ToolResult<object>> ExtractMethodSafe(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [ExternalInputRequired(DataTag.MethodName, required: true)] string newMethodName,
        [Description("The exact statements to extract, copied VERBATIM character-for-character from a prior ReadFile/GetMethodSource result - not retyped from memory, not a shortened/unique fragment. This is NOT a search anchor like contextSnippet on other tools: the whole extracted range (every statement, including blank lines/comments within it, exactly as they appear in the file) must be present here, because the matched span directly becomes the extraction boundary. Passing only part of the intended range (e.g. just the first statement) will silently extract only that part, stranding the rest - some ambiguous narrow selections are refused with an error, but do not rely on that guard catching every case; when in doubt, include more of the surrounding block, not less.")]
        [Consumes(DataTag.ContextSnippet, required: true)] string exactSourceBlock,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ExtractMethodSafe(reason, filepath, newMethodName, exactSourceBlock, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);
}
