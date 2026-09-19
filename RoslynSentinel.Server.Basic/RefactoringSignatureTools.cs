using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

[McpServerToolType]
public class RefactoringSignatureTools
{
    private readonly RefactoringSignatureImpl _impl;

    public RefactoringSignatureTools(
        RefactoringEngine refactoringEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        ILogger logger)
    {
        _impl = new RefactoringSignatureImpl(refactoringEngine, workspaceManager, validationEngine, symbolNavigationEngine, logger);
    }

    [McpServerTool(Name = "RenameSymbol", UseStructuredContent = true, OutputSchemaType = typeof(RenameSymbolResultEnvelope))]
    [Produces(DataTag.ChangeId)]
    [Description("Renames a symbol and all its references across the solution, including mentions in XML doc comments, inline comments, and string literals. Returns changeId and updatedHandle for the renamed symbol, plus residualMentions for any leftover occurrences of the old name that rename couldn't reach (e.g. embedded in an unrelated identifier, or in a non-source file). Does NOT simplify call sites or add/remove using directives - if the rename target's new name needs a namespace not already in scope at a call site, or you want to shorten a fully-qualified reference, use the UsingDirective tool separately.")]
    public Task<ToolResult<object>> RenameSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.ProjectName)] string projectName,
        [Description(ToolParams.DocCommentId)][Consumes(DataTag.DocCommentId, required: true)] string docCommentId,
        [Description("New name for the symbol. Must be a valid C# identifier.")] string newName,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default) =>
        _impl.RenameSymbol(reason, projectName, docCommentId, newName, dryRun, returnDiff, requestParams, cancellationToken);

    // OutputSchemaType covers the "view" branch's shape only ({ Parameters }) - the add/remove
    // branches (ToJsonSummary / MemberChangedContentResult offload) are out of scope for this POC.
    // See proposal_structuredcontent_rollout.md.
    [McpServerTool(Name = "MethodSignature", UseStructuredContent = true, OutputSchemaType = typeof(MethodSignatureViewResultEnvelope))]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view a method's parameters (general-purpose - not limited to constructors; see ConstructorParameter for DI-style constructor parameters with a backing field). For overloaded methods, combine methodName with contextSnippet/lineBefore/lineAfter to disambiguate.")]
    public Task<ToolResult<object>> MethodSignature(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: appends a new parameter to the end of the parameter list. remove: only the LAST parameter can be removed (paramName must match it) - a deliberate restriction, since removing an earlier parameter would require reordering every call site's remaining positional arguments, which cannot always be done safely; call sites passing the removed argument positionally are updated automatically, but a call site using named arguments (or one that can't be safely re-parsed) causes the whole operation to be refused with no changes made. view: lists current parameters (name, type, default value); makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.MethodName, required: true)] string methodName,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add/remove, unused for operation=view.
        [Description("Required for add/remove. Not used for view.")]
        [Consumes(DataTag.SymbolName, required: false)] string? paramName = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add, unused for remove/view.
        [Description("Required for add. Not used for remove/view.")]
        [Consumes(DataTag.DataType, required: false)] string? paramType = null,
        [Description("add only. Optional literal or expression for the new parameter's default value (e.g. \"3\", \"\\\"foo\\\"\") - omit for a required parameter. Do NOT pass the literal string \"null\" here to get a null default - use nullDefault:true instead (some MCP clients corrupt the string \"null\" in transit, silently producing a required parameter instead of one defaulted to null). Mutually exclusive with nullDefault.")]
        [ExternalInputRequired(DataTag.Initializer, required: false)] string? defaultValue = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default,
        [Description("add only. Sets the new parameter's default to the null literal directly, bypassing defaultValue entirely - use this instead of defaultValue:\"null\". Mutually exclusive with defaultValue.")] bool nullDefault = false) =>
        _impl.MethodSignature(reason, filepath, operation, methodName, paramName, paramType, defaultValue, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken, nullDefault);

    [McpServerTool(Name = "ChangeAccessibility")]
    [Produces(DataTag.ChangeId)]
    [Description("Changes the accessibility (private, public, internal, protected, protected internal, private protected) of a type or member to the given target level in one step - replaces whatever accessibility is currently present, so there's no separate remove/add pairing to get wrong. For overloaded members, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. This tool covers accessibility only - use ChangeAccessibility for accessibility, ModifyAttribute for [Attribute] syntax, and ModifyModifier for non-accessibility keywords (virtual/abstract/static/etc.). Returns changeId.")]
    public Task<ToolResult<object>> ChangeAccessibility(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility, required: true)] AccessibilityLevel accessibility,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ChangeAccessibility(reason, filepath, targetName, accessibility, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ConstructorParameter")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view DI constructor parameters on a class. For classes with the same name in the same file, combine className with contextSnippet/lineBefore/lineAfter to disambiguate.")]
    public Task<ToolResult<object>> ConstructorParameter(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: creates a private readonly field, parameter, and body assignment in one step; creates a constructor if none exists. remove: deletes the parameter and its assignment statement - the backing field is only deleted if a solution-wide reference check confirms nothing else in the class still uses it, otherwise it's left in place. view: lists current constructor parameters and their inferred backing fields; makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.ClassName, required: true)] string className,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add/remove, unused for operation=view.
        [Description("Required for add/remove. Not used for view.")]
        [Consumes(DataTag.SymbolName, required: false)] string? paramName = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add, unused for remove/view.
        [Description("Required for add. Not used for remove/view.")]
        [Consumes(DataTag.DataType, required: false)] string? paramType = null,
        [Description("add only. Overrides the default derived field name (_camelCase); passing fieldName equal to paramName or its underscore-prefixed form both resolve to '_paramName', never a bare name that would collide with the parameter.")]
        [Consumes(DataTag.SymbolName, required: false)] string? fieldName = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ConstructorParameter(reason, filepath, operation, className, paramName, paramType, fieldName, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);
}
