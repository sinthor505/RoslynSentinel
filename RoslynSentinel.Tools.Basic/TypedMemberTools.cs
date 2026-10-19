using System.ComponentModel;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Generates a typed property or field in an existing type, split out of the Member tool so the
/// always-loaded Member schema does not carry the nine typed-only params. Delegates to
/// <see cref="RefactoringStructuralImpl.TypedMember"/>.
/// </summary>
[McpServerToolType]
public class TypedMemberTools
{
    private readonly RefactoringStructuralImpl _impl;

    public TypedMemberTools(RefactoringStructuralImpl impl)
    {
        _impl = impl;
    }

    [McpServerTool(Name = "TypedMember")]
    [Produces(DataTag.ChangeId)]
    [Description("Generates a typed property or field in an existing type. Raw member source, remove, replace and view are the Member tool.")]
    public Task<SentinelCallToolResult<object>> TypedMemberTool(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("The type that receives the member. Required.")]
        [Consumes(DataTag.SymbolName, required: true)] string containerName,
        [Description("Kind to generate: property or field.")]
        [ExternalInputRequired(DataTag.SymbolKind, required: true)] TypedMemberKind typedKind,
        [Description("The generated member's name.")]
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string typedName,
        [Description("The generated member's type.")]
        [ExternalInputRequired(DataTag.DataType, required: true)] string typedType,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility)] string accessibility = "public",
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: hasSetter and isInit apply to typedKind=property only; isReadonly, isStatic and initializer to typedKind=field only.
        [Description("property only.")][ExternalInputRequired(DataTag.HasSetter)] bool hasSetter = true,
        [Description("property only.")][ExternalInputRequired(DataTag.IsInit)] bool isInit = false,
        [Description("field only.")][ExternalInputRequired(DataTag.IsReadonly)] bool isReadonly = false,
        [Description("field only.")][ExternalInputRequired(DataTag.IsStatic)] bool isStatic = false,
        [Description("field only: initializer expression.")][ExternalInputRequired(DataTag.Initializer)] string? initializer = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        return _impl.TypedMember(reason, filePath, containerName, typedKind, typedName, typedType,
            accessibility, hasSetter, isInit, isReadonly, isStatic, initializer, contextSnippet, lineBefore, lineAfter,
            dryRun, returnDiff, cancellationToken);
    }
}
