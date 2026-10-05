using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// <c>Declaration</c>: one tool for changing a declaration, merging ModifyModifier, ChangeAccessibility,
/// ModifyAttribute and ModifyBaseType behind a closed <see cref="DeclarationOperation"/> enum.
/// Opt-in and additive: exposed only in claude-lean, through the <c>declarations</c> toolset
/// (see ToolsetCatalog and ServiceRegistrationExtensionsBasic); the four original tools are untouched and stay
/// available in the other modes. Each operation delegates to the same Impl method its original tool calls, so
/// behavior is identical. proposal_reduce_tool_schema_token_cost.md, Step 4a.
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
    // the checks below turn a wrong subset into an InvalidArgument naming the missing/unexpected params.
    [McpServerTool(Name = "Declaration")]
    [Produces(DataTag.ChangeId)]
    [Description("Changes a declaration's modifier, accessibility, attribute or base type. For overloaded targets provide contextSnippet. " +
        "modifier: ADD STATIC on a method or property is a conversion - the member must use no instance state, and instance-qualified callers are rewritten to Type.M(...) in the same atomic change; " +
        "not supported for auto-properties, init accessors, virtual/override/abstract members, interface implementations, members of generic types. Returns changeId.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> Declaration(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Required params per operation. " +
            "modifier: add or remove a non-accessibility modifier keyword; filePath+targetName+modifier+action (add|remove), or edits instead of those four. " +
            "accessibility: set accessibility, replacing the current one; filePath+targetName+accessibility. " +
            "attribute: add, replace or remove an [Attribute]; filePath+targetName+existingAttribute+action, plus newAttribute for replace, or batchEdits instead. " +
            "baseType: add or remove a base type or interface; filePath+typeName+baseTypeName+action (add|remove), or baseTypeEdits instead.")]
        [Consumes(DataTag.Action, required: true)] DeclarationOperation operation,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Description("modifier, accessibility, attribute: the declaration to change.")]
        [Consumes(DataTag.SymbolName, required: false)] string? targetName = null,
        [ExternalInputRequired(DataTag.Modifier, required: false)] NonAccessibilityModifier? modifier = null,
        [Description("add or remove; replace is for attribute only.")]
        [Consumes(DataTag.Action, required: false)] DeclarationAction? action = null,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility, required: false)] AccessibilityLevel? accessibility = null,
        [Description("attribute: the attribute to add/replace/remove, with or without [ ] brackets.")]
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string? existingAttribute = null,
        [Description("attribute, action=replace: the attribute to replace existingAttribute with.")]
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string? newAttribute = null,
        [Description("baseType: the type to change.")]
        [Consumes(DataTag.SymbolName, required: false)] string? typeName = null,
        [Description("baseType: the base type or interface name.")] string? baseTypeName = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description("modifier. " + ToolParams.ModifierEdits)] List<ModifierEdit>? edits = null,
        [Description("attribute. " + ToolParams.AttributeEdits)] List<AttributeEdit>? batchEdits = null,
        [Description("baseType. " + ToolParams.BaseTypeEdits)] List<BaseTypeEdit>? baseTypeEdits = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        switch (operation)
        {
            case DeclarationOperation.modifier:
            {
                var rejected = RejectForeignParams("modifier", ("accessibility", accessibility.HasValue), ("existingAttribute", existingAttribute is not null),
                    ("newAttribute", newAttribute is not null), ("typeName", typeName is not null), ("baseTypeName", baseTypeName is not null),
                    ("batchEdits", batchEdits is not null), ("baseTypeEdits", baseTypeEdits is not null));
                if (rejected is not null)
                {
                    return rejected;
                }

                if (!TryToAddRemove(action, out var addRemove))
                {
                    return InvalidArgument("action 'replace' is only valid for operation 'attribute'; operation 'modifier' takes add or remove.");
                }

                return _structural.ModifyModifier(reason, filePath, targetName, modifier, addRemove, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);
            }

            case DeclarationOperation.accessibility:
            {
                var rejected = RejectForeignParams("accessibility", ("modifier", modifier.HasValue), ("action", action.HasValue), ("edits", edits is not null),
                    ("existingAttribute", existingAttribute is not null), ("newAttribute", newAttribute is not null), ("typeName", typeName is not null),
                    ("baseTypeName", baseTypeName is not null), ("batchEdits", batchEdits is not null), ("baseTypeEdits", baseTypeEdits is not null));
                if (rejected is not null)
                {
                    return rejected;
                }

                if (filePath is null || string.IsNullOrEmpty(targetName) || !accessibility.HasValue)
                {
                    return InvalidArgument("operation 'accessibility' requires 'filePath', 'targetName' and 'accessibility'.");
                }

                return _signature.ChangeAccessibility(reason, filePath, targetName, accessibility.Value, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);
            }

            case DeclarationOperation.attribute:
            {
                var rejected = RejectForeignParams("attribute", ("modifier", modifier.HasValue), ("accessibility", accessibility.HasValue), ("edits", edits is not null),
                    ("typeName", typeName is not null), ("baseTypeName", baseTypeName is not null), ("baseTypeEdits", baseTypeEdits is not null));
                if (rejected is not null)
                {
                    return rejected;
                }

                bool hasSingular = filePath is not null || !string.IsNullOrEmpty(targetName) || !string.IsNullOrEmpty(existingAttribute) || action.HasValue;
                if (hasSingular && batchEdits is not null)
                {
                    return InvalidArgument("operation 'attribute' takes either filePath/targetName/existingAttribute/action or 'batchEdits', not both.");
                }

                if (batchEdits is { Count: 0 })
                {
                    return InvalidArgument("operation 'attribute': 'batchEdits' was supplied but is empty.");
                }

                if (batchEdits is null && (filePath is null || string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(existingAttribute) || !action.HasValue))
                {
                    return InvalidArgument("operation 'attribute' requires 'filePath', 'targetName', 'existingAttribute' and 'action' (plus 'newAttribute' for replace), unless 'batchEdits' is supplied instead.");
                }

                return _structural.ModifyAttribute(reason, filePath, targetName, existingAttribute, ToAttributeAction(action), newAttribute, null, contextSnippet, lineBefore, lineAfter, batchEdits, autoStage, dryRun, returnDiff, cancellationToken);
            }

            case DeclarationOperation.baseType:
            {
                var rejected = RejectForeignParams("baseType", ("modifier", modifier.HasValue), ("accessibility", accessibility.HasValue), ("targetName", targetName is not null),
                    ("existingAttribute", existingAttribute is not null), ("newAttribute", newAttribute is not null), ("edits", edits is not null), ("batchEdits", batchEdits is not null));
                if (rejected is not null)
                {
                    return rejected;
                }

                if (!TryToAddRemove(action, out var addRemove))
                {
                    return InvalidArgument("action 'replace' is only valid for operation 'attribute'; operation 'baseType' takes add or remove.");
                }

                bool hasSingular = filePath is not null || !string.IsNullOrEmpty(typeName) || !string.IsNullOrEmpty(baseTypeName) || action.HasValue;
                if (hasSingular && baseTypeEdits is not null)
                {
                    return InvalidArgument("operation 'baseType' takes either filePath/typeName/baseTypeName/action or 'baseTypeEdits', not both.");
                }

                if (baseTypeEdits is { Count: 0 })
                {
                    return InvalidArgument("operation 'baseType': 'baseTypeEdits' was supplied but is empty.");
                }

                if (baseTypeEdits is null && (filePath is null || string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(baseTypeName) || !addRemove.HasValue))
                {
                    return InvalidArgument("operation 'baseType' requires 'filePath', 'typeName', 'baseTypeName' and 'action', unless 'baseTypeEdits' is supplied instead.");
                }

                return _structural.ModifyBaseType(reason, filePath, typeName, baseTypeName, addRemove, contextSnippet, lineBefore, lineAfter, baseTypeEdits, autoStage, dryRun, returnDiff, cancellationToken);
            }

            default:
                return InvalidArgument($"Unhandled operation '{operation}'. Valid values: {string.Join(", ", Enum.GetNames<DeclarationOperation>())}.");
        }
    }

    // add/remove map one-to-one onto AddRemoveAction; replace has no equivalent there (attribute only).
    private static bool TryToAddRemove(DeclarationAction? action, out AddRemoveAction? addRemove)
    {
        addRemove = action switch
        {
            DeclarationAction.add => AddRemoveAction.add,
            DeclarationAction.remove => AddRemoveAction.remove,
            _ => null,
        };
        return action != DeclarationAction.replace;
    }

    private static AttributeModifyAction? ToAttributeAction(DeclarationAction? action) => action switch
    {
        DeclarationAction.add => AttributeModifyAction.add,
        DeclarationAction.remove => AttributeModifyAction.remove,
        DeclarationAction.replace => AttributeModifyAction.replace,
        _ => null,
    };

    // Names every supplied param that belongs to another operation, so a wrong subset fails before delegation.
    private static Task<SentinelCallToolResult<AppliedChangeSummary>>? RejectForeignParams(string operationName, params (string Name, bool Present)[] foreign)
    {
        var present = foreign.Where(p => p.Present).Select(p => $"'{p.Name}'").ToList();
        return present.Count == 0
            ? null
            : InvalidArgument($"operation '{operationName}' does not take {string.Join(", ", present)}; see the 'operation' description for the params each operation takes.");
    }

    private static Task<SentinelCallToolResult<AppliedChangeSummary>> InvalidArgument(string message) =>
        Task.FromResult(new SentinelCallToolResult<AppliedChangeSummary>
        {
            IsError = true,
            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Declaration: {message}"),
        });
}
