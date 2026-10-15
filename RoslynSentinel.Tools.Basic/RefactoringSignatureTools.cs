using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

[McpServerToolType]
public class RefactoringSignatureTools
{
    private readonly RefactoringSignatureImpl _impl;

    public RefactoringSignatureTools(RefactoringSignatureImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "RenameSymbol", UseStructuredContent = false, OutputSchemaType = typeof(RenameSymbolResultEnvelope))]
    [Produces(DataTag.ChangeId)]
    [Description("Renames a symbol and all its references across the solution, including XML doc comments, inline comments and string literals. Returns changeId, updatedHandle and residualMentions (old-name occurrences it couldn't reach). Does not touch using directives (use UsingDirective).")]
    public Task<SentinelCallToolResult<object>> RenameSymbol(
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
    [McpServerTool(Name = "MethodSignature", UseStructuredContent = false, OutputSchemaType = typeof(MethodSignatureViewResultEnvelope))]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view a method's parameters (any method; ConstructorParameter handles DI constructor parameters with a backing field). For overloaded methods, combine methodName with contextSnippet/lineBefore/lineAfter.")]
    public Task<SentinelCallToolResult<object>> MethodSignature(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("add (needs paramName+paramType): appends a parameter to the end of the list. remove (needs paramName): " + ToolParams.RemoveLastParameter + " the operation refuse. view: lists parameters (name, type, default).")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.MethodName, required: true)] string methodName,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add/remove, unused for operation=view.
        [Consumes(DataTag.SymbolName, required: false)] string? paramName = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add, unused for remove/view.
        [Consumes(DataTag.DataType, required: false)] string? paramType = null,
        [Description(ToolParams.DefaultValueLead + "\"3\", \"\\\"foo\\\"\"" + ToolParams.DefaultValueTail)]
        [ExternalInputRequired(DataTag.Initializer, required: false)] string? defaultValue = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        // autoStage removed from the schema: stage-then-apply is retired, the value is hardcoded to true below.
        // [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default,
        [Description(ToolParams.NullDefaultLead + " (use instead of defaultValue:\"null\")." + ToolParams.NullDefaultTail)] bool nullDefault = false)
    {
        // Hardcoded: autoStage is hidden from the schema until the server has a real staging mode.
        bool autoStage = true;
        return _impl.MethodSignature(reason, filePath, operation, methodName, paramName, paramType, defaultValue, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken, nullDefault);
    }

    [McpServerTool(Name = "ChangeAccessibility")]
    [Produces(DataTag.ChangeId)]
    [Description("Sets the accessibility of a type or member to the given level, replacing whatever is present. For overloaded members, provide contextSnippet and optionally lineBefore/lineAfter. Accessibility only (others: ModifyModifier, ModifyAttribute). Returns changeId.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ChangeAccessibility(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility, required: true)] AccessibilityLevel accessibility,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        // autoStage removed from the schema: stage-then-apply is retired, the value is hardcoded to true below.
        // [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        // Hardcoded: autoStage is hidden from the schema until the server has a real staging mode.
        bool autoStage = true;
        return _impl.ChangeAccessibility(reason, filePath, targetName, accessibility, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);
    }

    [McpServerTool(Name = "ConstructorParameter")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view DI constructor parameters on a class. For same-named classes in one file, combine className with contextSnippet/lineBefore/lineAfter. add accepts defaultValue/nullDefault so existing call sites that omit the argument keep compiling.")]
    public Task<SentinelCallToolResult<object>> ConstructorParameter(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("add (needs paramName+paramType): creates a private readonly field, parameter and body assignment; creates a constructor if none exists. remove (needs paramName): deletes the parameter and its assignment; the backing field is deleted only if nothing else in the class uses it. view: lists constructor parameters and their backing fields.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.ClassName, required: true)] string className,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add/remove, unused for operation=view.
        [Consumes(DataTag.SymbolName, required: false)] string? paramName = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add, unused for remove/view.
        [Consumes(DataTag.DataType, required: false)] string? paramType = null,
        [Description("add only. " + ToolParams.FieldNameOverride)]
        [Consumes(DataTag.SymbolName, required: false)] string? fieldName = null,
        [Description(ToolParams.DefaultValueLead + "\"null!\", \"new SentinelConfiguration()\"" + ToolParams.DefaultValueTail)]
        [ExternalInputRequired(DataTag.Initializer, required: false)] string? defaultValue = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        // autoStage removed from the schema: stage-then-apply is retired, the value is hardcoded to true below.
        // [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default,
        [Description(ToolParams.NullDefaultLead + " (use instead of defaultValue:\"null\")." + ToolParams.NullDefaultTail)] bool nullDefault = false,
        [Description("add only. Supplies the new parameter's argument at existing constructor call sites " + ToolParams.CallSiteTargets + " " + ToolParams.CallSiteKeys + " an exact line beats FilePath:*, which beats *. Value: the argument expression for that site (e.g. \"config\", \"_config\" or \"new SentinelConfiguration()\"); appended as a named argument when the site uses named arguments or omits trailing optional parameters. " + ToolParams.CallSiteFallback + " exact key.")] Dictionary<string, string>? callSiteFixups = null)
    {
        // Hardcoded: autoStage is hidden from the schema until the server has a real staging mode.
        bool autoStage = true;
        return _impl.ConstructorParameter(reason, filePath, operation, className, paramName, paramType, fieldName, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken, defaultValue, nullDefault, callSiteFixups);
    }
}
