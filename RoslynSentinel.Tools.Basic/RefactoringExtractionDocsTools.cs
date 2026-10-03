using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

// Decision 7 step 3 (plan_split_workspace_refactoring_tools_for_di.md): MCP-surface half of the
// RefactoringExtractionDocsTools/Impl pair. All method bodies delegate one-line to _impl; all original
// [McpServerTool]/[Produces]/[Description] attributes are preserved verbatim from RefactoringTools.cs.
[McpServerToolType]
public class RefactoringExtractionDocsTools
{
    private readonly RefactoringExtractionDocsImpl _impl;

    public RefactoringExtractionDocsTools(RefactoringExtractionDocsImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "UsingDirective")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view using directives in a file.")]
    public Task<SentinelCallToolResult<object>> UsingDirective(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("add: inserts a using (no-op if present). remove: deletes it. view: lists usings (name, isStatic, alias).")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add/remove, unused for operation=view.
        [Description("Required for add/remove. For static usings, prefix with \"static \" (e.g. \"static System.Math\").")]
        [Consumes(DataTag.SymbolName, required: false)] string? namespaceName = null,
        [Description("add only. After inserting, runs Roslyn's Simplifier over the file to shorten now-redundant fully-qualified references.")] bool simplifyExisting = false,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.UsingDirective(reason, filePath, operation, namespaceName, simplifyExisting, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "SummaryComment")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view a /// <summary> XML doc comment on a type or member. For overloaded targets, combine targetName with contextSnippet/lineBefore/lineAfter.")]
    public Task<SentinelCallToolResult<object>> SummaryComment(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("add: adds or replaces the summary. remove: deletes it (no-op if none). view: returns the current summary text (or null).")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add, unused for remove/view.
        [Description("Required for add - the new summary text.")][Consumes(DataTag.SourceCode, required: false)] string? summaryText = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ContainingTypeName)] string? containingTypeName = null,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.SummaryComment(reason, filePath, operation, targetName, summaryText, contextSnippet, lineBefore, lineAfter, containingTypeName, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ExtractLocalVariable")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts an inline expression into a named local variable declaration. exactExpressionText must be the WHOLE expression, copied verbatim.")]
    public Task<SentinelCallToolResult<object>> ExtractLocalVariable(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("The whole expression to extract, copied verbatim from a prior ReadFile/GetMethodSource result (whitespace differences are tolerated). Not a search anchor like contextSnippet: a partial expression may silently extract the wrong span.")]
        [Consumes(DataTag.ContextSnippet, required: true)] string exactExpressionText,
        [Consumes(DataTag.SymbolName)] string variableName,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ExtractLocalVariable(reason, filePath, exactExpressionText, variableName, lineBefore, lineAfter, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ExtractMethodSafe")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts selected statements into a new method with the return type inferred from the selection. Returns changeId.")]
    // Fixes MS BUG: where selections ending with "return <expression>" are extracted into a method declared "private void MethodName(...)", causing a compile error. This tool uses Roslyn's SemanticModel to determine the actual type of the returned expression, and DataFlowAnalysis to find the correct parameter list. Requires a loaded solution (via set_solution_path or equivalent).
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ExtractMethodSafe(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [ExternalInputRequired(DataTag.MethodName, required: true)] string newMethodName,
        [Description("The exact statements to extract, copied verbatim from a prior ReadFile/GetMethodSource result, including blank lines/comments inside the range. Not a search anchor like contextSnippet: the matched span is the extraction boundary, so a partial block silently extracts only that part - include the whole block.")]
        [Consumes(DataTag.ContextSnippet, required: true)] string exactSourceBlock,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ExtractMethodSafe(reason, filePath, newMethodName, exactSourceBlock, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);
}
