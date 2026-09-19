using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

// Decision 7 step 3 (plan_split_workspace_refactoring_tools_for_di.md): MCP-surface half of the
// RefactoringStructuralTools/Impl pair. All method bodies delegate one-line to _impl; all original
// [McpServerTool]/[Produces]/[Description] attributes are preserved verbatim from SentinelRefactoringTools.cs.
[McpServerToolType]
public class RefactoringStructuralTools
{
    private readonly RefactoringStructuralImpl _impl;

    public RefactoringStructuralTools(
        RefactoringEngine refactoringEngine,
        StructuralRefinementEngine structuralRefinementEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        ILogger logger)
    {
        _impl = new RefactoringStructuralImpl(refactoringEngine, structuralRefinementEngine, symbolNavigationEngine, workspaceManager, validationEngine, logger);
    }

    // CONDITIONAL-PARAM-REVIEW-REQUIRED: required-param set depends entirely on 'operation' -> add
    // needs containerName (or, for a brand-new top-level type, newMemberSource alone with no
    // typedKind); view needs containerName; remove needs memberName; replace needs memberName +
    // newMemberSource. Within add, exactly one of newMemberSource or typedKind+typedName+typedType
    // is required. No param besides filepath/operation is universally required, so a model can
    // supply the wrong subset for its chosen operation and only find out at runtime.
    [McpServerTool(Name = "Member")]
    [Produces(DataTag.ChangeId)]
    [Description("Add (as a raw source member, a generated typed property/field, or a brand-new top-level type), remove, replace, or view a type member (method, property, field, constructor). This is the right choice even for a one-line change inside a member - read the member's current source first (e.g. via GetMethodSource/ReadFile), copy it verbatim, make your edit, and pass the whole resulting member as newMemberSource, not a fragment. Prefer this over a unified diff to edit part of a member: a whole-member replacement can't drift out of sync the way a hand-built diff hunk can.")]
    public Task<ToolResult<object>> Member(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("addMember: adds raw member source into an existing container (requires containerName + newMemberSource). addTopLevelType: adds a brand-new top-level type declaration - no container (requires newMemberSource as the full type source; optional namespaceName). addTypedMember: generates a property/field via typedKind/typedName/typedType into an existing container (requires containerName + typedKind + typedName + typedType). remove: deletes a member - by default checks for callers/implementations first (see skipPrecheck); for a zero-usages-only contract use SafeDeleteUnusedSymbol instead. replace: replaces a member's full source, including for small in-member edits. view: lists a container's direct members (name, kind, signature, line range) to find the exact memberName/contextSnippet to pass to remove or replace.")]
        [Consumes(DataTag.Action, required: true)] MemberAction operation,
        [Description("Required for addMember and addTypedMember, and for view. Not used for addTopLevelType, remove, or replace.")]
        [Consumes(DataTag.SymbolName, required: false)] string? containerName = null,
        [Description("addTopLevelType only. Disambiguates which namespace to add the new type to, when the file has more than one. Not used otherwise.")]
        [ExternalInputRequired(DataTag.SymbolName, required: false)] string? namespaceName = null,
        [Description("Required for remove and replace - the member to target. For overloaded targets, combine with contextSnippet/lineBefore/lineAfter to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: false)] string? memberName = null,
        [Description("replace: the full replacement member source (signature + body). addMember: the full raw member source to insert into containerName - use position (\"after:MemberName\"/\"before:MemberName\"/\"end\") to place it. addTopLevelType: the full new type declaration (enum/class/record/struct/interface) - containerName is not used. Not used for addTypedMember, remove, or view.")]
        [Consumes(DataTag.SourceCode, required: false)] string? newMemberSource = null,
        [Description("addMember only: where to insert - null/\"end\" to append, \"after:MemberName\", or \"before:MemberName\". Not used for addTopLevelType, addTypedMember, remove, replace, or view.")]
        [ExternalInputRequired(DataTag.Position)] string? position = null,
        [Description("addTypedMember only (required): which kind to generate - \"property\" or \"field\". Requires typedName+typedType alongside it. To add a whole new method, class, record, struct, or interface, use addMember or addTopLevelType with newMemberSource instead.")]
        [ExternalInputRequired(DataTag.SymbolKind, required: false)] TypedMemberKind? typedKind = null,
        [Description("Required for addTypedMember: the generated member's name.")]
        [ExternalInputRequired(DataTag.SymbolName, required: false)] string? typedName = null,
        [Description("Required for addTypedMember: the generated member's type.")]
        [ExternalInputRequired(DataTag.DataType, required: false)] string? typedType = null,
        [Description(ToolParams.AccessibilityValues + " addTypedMember only.")][ExternalInputRequired(DataTag.Accessibility)] string accessibility = "public",
        [Description("addTypedMember, typedKind=property only.")][ExternalInputRequired(DataTag.HasSetter)] bool hasSetter = true,
        [Description("addTypedMember, typedKind=property only.")][ExternalInputRequired(DataTag.IsInit)] bool isInit = false,
        [Description("addTypedMember, typedKind=field only.")][ExternalInputRequired(DataTag.IsReadonly)] bool isReadonly = false,
        [Description("addTypedMember, typedKind=field only.")][ExternalInputRequired(DataTag.IsStatic)] bool isStatic = false,
        [Description("addTypedMember, typedKind=field only: optional initializer expression.")][ExternalInputRequired(DataTag.Initializer)] string? initializer = null,
        [Description("remove only. When false (default), refuses removal if the member has any callers or implementations (checked the same way as FindReferences(kind: all)). Set true to skip this check and remove unconditionally.")] bool skipPrecheck = false,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default) =>
        _impl.Member(reason, filepath, operation, containerName, namespaceName, memberName, newMemberSource, position, typedKind, typedName, typedType,
            accessibility, hasSetter, isInit, isReadonly, isStatic, initializer, skipPrecheck, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff,
            requestParams, cancellationToken);

    [McpServerTool(Name = "ModifyEnum")]
    [Produces(DataTag.ChangeId)]
    [Description("Replaces an enum's complete member list in one operation. Use GetTypeInfo(typeName, include:\"members\") to see current values first.")]
    public Task<ToolResult<object>> ModifyEnum(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string enumName,
        [Description("Comma-separated list of member names in the desired order (e.g. \"Pending,Shipped,Cancelled\"); append \"=N\" for an explicit value (e.g. \"Archived=99\"). Omitted names are removed, new names are added, explicit values are preserved, and implicit members take the next ordinal from their predecessor - as if hand-typed. Pass the complete list every time, not a delta.")]
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string values,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ModifyEnum(reason, filepath, enumName, values, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyAttribute")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds, replaces, or removes an [Attribute] on a type or member. Use ChangeAccessibility for accessibility keywords and ModifyModifier for other modifier keywords, not this tool.")]
    public Task<ToolResult<object>> ModifyAttribute(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'edits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)] FilePathWrapper? filepath = null,
        [Description("For overloaded/duplicate-named targets, combine with contextSnippet/lineBefore/lineAfter to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: false)] string? targetName = null,
        [Description("The attribute to add/replace/remove. May include or omit the surrounding [ ] brackets.")]
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string? existingAttribute = null,
        [Consumes(DataTag.Action, required: false)] AttributeModifyAction? action = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for action=replace, unused for add/remove.
        [Description("Required for action=replace - the attribute to replace existingAttribute with. Not used for add/remove.")]
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string? newAttribute = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AttributeEdits)] List<AttributeEdit>? edits = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ModifyAttribute(reason, filepath, targetName, existingAttribute, action, newAttribute, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyModifier", UseStructuredContent = true, OutputSchemaType = typeof(ModifyModifierResultEnvelope))]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a non-accessibility modifier keyword. Action: add or remove. For overloaded targets, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. Does NOT cover accessibility (private/public/etc.) - use ChangeAccessibility for those, or ModifyAttribute for [Attribute] syntax. Returns changeId.")]
    public Task<ToolResult<object>> ModifyModifier(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'edits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)] FilePathWrapper? filepath = null,
        [Consumes(DataTag.SymbolName, required: false)] string? targetName = null,
        [ExternalInputRequired(DataTag.Modifier, required: false)] NonAccessibilityModifier? modifier = null,
        [Consumes(DataTag.Action, required: false)] AddRemoveAction? action = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ModifierEdits)] List<ModifierEdit>? edits = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ModifyModifier(reason, filepath, targetName, modifier, action, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyBaseType")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a base type or interface from a type declaration.")]
    public Task<ToolResult<object>> ModifyBaseType(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'edits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)] FilePathWrapper? filepath = null,
        [Description("For types with the same name in the same file, combine with contextSnippet/lineBefore/lineAfter to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: false)] string? typeName = null,
        [Description("The base type or interface name to add or remove.")] string? baseTypeName = null,
        [Description("add or remove.")] AddRemoveAction? action = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.BaseTypeEdits)] List<BaseTypeEdit>? edits = null,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ModifyBaseType(reason, filepath, typeName, baseTypeName, action, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "SyncTypeAndFilename")]
    [Produces(DataTag.ResultOnly)]
    [Description("Synchronizes the filename to match a type declared in the file.")]
    public Task<ToolResult<object>> SyncTypeAndFilename(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("The top-level type in the file to sync the filename to. Omit to default to the first non-nested type declared in the file (fine for the common single-type-per-file case). Name it explicitly to get a specific result when the file declares more than one top-level type - without it, whichever type happens to be declared first wins, which is not necessarily the file's conceptual main type.")]
        string? targetTypeName = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.SyncTypeAndFilename(reason, filepath, targetTypeName, dryRun, returnDiff, cancellationToken);
}
