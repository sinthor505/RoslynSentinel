using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// <c>Declaration</c>: one tool for changing a declaration, merging ModifyModifier and ChangeAccessibility
/// (later slices add ModifyAttribute and ModifyBaseType as further <see cref="DeclarationOperation"/> values).
/// Opt-in and additive: exposed only in claude-lean, through the <c>declarations</c> toolset
/// (see ToolsetCatalog and ServiceRegistrationExtensionsBasic); the four original tools are untouched. Each
/// operation delegates to the same Impl method its original tool calls, so behavior is identical.
/// proposal_reduce_tool_schema_token_cost.md, Step 4a.
/// </summary>
[McpServerToolType]
public class DeclarationTools
{
    private readonly RefactoringStructuralImpl _structural;
    private readonly RefactoringSignatureImpl _signature;

    public DeclarationTools(RefactoringStructuralImpl structural, RefactoringSignatureImpl signature)
    {
        _structural = structural;
        _signature = signature;
    }

    // CONDITIONAL-PARAM-REVIEW-REQUIRED: the required-param set depends on 'operation' (documented once on it);
    // the check below turns a wrong subset into an InvalidArgument naming the missing/unexpected params.
    [McpServerTool(Name = "Declaration")]
    [Produces(DataTag.ChangeId)]
    [Description("Changes a declaration's modifier or accessibility. For overloaded targets provide contextSnippet. " +
        "modifier: ADD STATIC on a method or property is a conversion - the member must use no instance state, and instance-qualified callers are rewritten to Type.M(...) in the same atomic change; " +
        "not supported for auto-properties, init accessors, virtual/override/abstract members, interface implementations, members of generic types. Returns changeId.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> Declaration(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Required params per operation. modifier: add or remove a non-accessibility modifier keyword; filePath+targetName+modifier+action, or edits instead of those four. accessibility: set accessibility, replacing the current one; filePath+targetName+accessibility.")]
        [Consumes(DataTag.Action, required: true)] DeclarationOperation operation,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Consumes(DataTag.SymbolName, required: false)] string? targetName = null,
        [ExternalInputRequired(DataTag.Modifier, required: false)] NonAccessibilityModifier? modifier = null,
        [Consumes(DataTag.Action, required: false)] AddRemoveAction? action = null,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility, required: false)] AccessibilityLevel? accessibility = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ModifierEdits)] List<ModifierEdit>? edits = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        switch (operation)
        {
            case DeclarationOperation.modifier:
                if (accessibility.HasValue)
                {
                    return InvalidArgument("operation 'modifier' does not take 'accessibility'; use operation 'accessibility' to change accessibility.");
                }

                return _structural.ModifyModifier(reason, filePath, targetName, modifier, action, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

            case DeclarationOperation.accessibility:
                if (modifier.HasValue || action.HasValue || edits is not null)
                {
                    return InvalidArgument("operation 'accessibility' does not take 'modifier', 'action' or 'edits'; use operation 'modifier' for those.");
                }

                if (filePath is null || string.IsNullOrEmpty(targetName) || !accessibility.HasValue)
                {
                    return InvalidArgument("operation 'accessibility' requires 'filePath', 'targetName' and 'accessibility'.");
                }

                return _signature.ChangeAccessibility(reason, filePath, targetName, accessibility.Value, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

            default:
                return InvalidArgument($"Unhandled operation '{operation}'. Valid values: {string.Join(", ", Enum.GetNames<DeclarationOperation>())}.");
        }
    }

    private static Task<SentinelCallToolResult<AppliedChangeSummary>> InvalidArgument(string message) =>
        Task.FromResult(new SentinelCallToolResult<AppliedChangeSummary>
        {
            IsSuccess = false,
            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Declaration: {message}"),
        });
}
