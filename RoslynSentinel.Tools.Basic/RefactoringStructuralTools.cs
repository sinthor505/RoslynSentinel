using System.ComponentModel;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

// Added by AddTopLevelType(expected - used for diagnostics)
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
/// Named shape mirroring the anonymous object <see cref="RefactoringTools.RenameSymbol"/>
/// assigns to <c>SentinelCallToolResult<object>.Data</c> on its applied success path. Primary path only
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
/// Envelope shape mirroring <c>SentinelCallToolResult<object></c> as actually populated on
/// <see cref="RefactoringTools.RenameSymbol"/>'s primary success path, which sets only
/// <c>IsError</c> and <c>Data</c> (not TotalRecords/WorkspaceVersion/etc). Primary path only -
/// see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record RenameSymbolResultEnvelope(
    bool Success,
    RenameSymbolData? Data);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Envelope shape mirroring <c>SentinelCallToolResult<object></c> as actually populated on
/// <see cref="RefactoringTools.ModifyModifier"/>'s primary (autoStage=true, singular-edit,
/// non-batch) success path, which sets only <c>IsError</c> and <c>Data</c>. Deliberately does not
/// cover the batch (batchEdits != null), autoStage=false, or error branches - see
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
/// Named shape mirroring the engine-layer <c>MethodParameterInfo</c> (RoslynSentinel.Engines.Basic,
/// BasicRefactoringEngine.cs) as surfaced by MethodSignature's view branch. Declared here (rather than
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
/// Envelope shape mirroring <c>SentinelCallToolResult<object></c> as actually populated on
/// <see cref="RefactoringTools.MethodSignature"/>'s "view" branch only (operation=view),
/// which sets only <c>IsError</c> and <c>Data = new { Parameters }</c>. The add/remove branches
/// (both the non-autoStage ToJsonSummary shape and the autoStage applied-with-offload
/// MemberChangedContentResult/AppliedChangeSummary shape) are intentionally NOT covered by this
/// POC - see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record MethodSignatureViewResultEnvelope(
    bool Success,
    MethodSignatureViewData? Data);

// Decision 7 step 3 (plan_split_workspace_refactoring_tools_for_di.md): MCP-surface half of the
// RefactoringSignatureTools/Impl pair. All method bodies delegate one-line to _impl; all original
// [McpServerTool]/[Produces]/[Description] attributes are preserved verbatim from RefactoringTools.cs.
[McpServerToolType]
public class RefactoringStructuralTools
{
    private readonly RefactoringStructuralImpl _impl;

    public RefactoringStructuralTools(RefactoringStructuralImpl impl)
    {
        _impl = impl;
    }

    // CONDITIONAL-PARAM-REVIEW-REQUIRED: required-param set depends entirely on 'operation' -> add
    // needs containerName (or, for a brand-new top-level type, newMemberSource alone with no
    // typedKind); view needs containerName (list) or memberName (one member's source); remove needs memberName; replace needs memberName +
    // newMemberSource. Within add, exactly one of newMemberSource or typedKind+typedName+typedType
    // is required. No param besides filePath/operation is universally required, so a model can
    // supply the wrong subset for its chosen operation and only find out at runtime.
    [McpServerTool(Name = "Member")]
    [Produces(DataTag.ChangeId)]
    //[Description("Add (as a raw source member, a generated typed property/field, or a brand-new top-level type), remove, replace, or view a type member (method, property, field, constructor). This is the right choice even for a one-line change inside a member - read the member's current source first (e.g. via GetMethodSource/ReadFile), copy it verbatim, make your edit, and pass the whole resulting member as newMemberSource, not a fragment. Prefer this over a unified diff to edit part of a member: a whole-member replacement can't drift out of sync the way a hand-built diff hunk can.")]
    [Description("Add, remove, replace, or view a raw source member, a typed property/field, or a brand-new top-level type. Also views constructors.")]
    public Task<SentinelCallToolResult<object>> Member(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        //[Description("addMember: adds raw member source into an existing container (requires containerName + newMemberSource). addTopLevelType: adds a brand-new top-level type declaration - no container (requires newMemberSource as the full type source; optional namespaceName). addTypedMember: generates a property/field via typedKind/typedName/typedType into an existing container (requires containerName + typedKind + typedName + typedType). remove: deletes a member - by default checks for callers/implementations first (see skipPrecheck); for a zero-usages-only contract use SafeDeleteUnusedSymbol instead. replace: replaces a member's full source, including for small in-member batchEdits. view: lists a container's direct members (name, kind, signature, line range) to find the exact memberName/contextSnippet to pass to remove or replace.")]
        //[Description("Add, remove, replace, or view a raw source member, a typed property/field, or a brand-new top-level type. Also views constructors.")]
        [Description("Required params per operation. addMember: containerName+newMemberSource (optional position). addTypedMember: containerName+typedKind+typedName+typedType. addTopLevelType: newMemberSource (optional namespaceName). remove: memberName. replace: memberName+newMemberSource. view: containerName (lists its members) or memberName (returns that member's full source).")]
        [Consumes(DataTag.Action, required: true)] MemberAction operation,
        [Description("The type that holds the member.")]
        [Consumes(DataTag.SymbolName, required: false)] string? containerName = null,
        [Description("Target namespace for addTopLevelType, when the file has more than one.")]
        [ExternalInputRequired(DataTag.SymbolName, required: false)] string? namespaceName = null,
        [Description("The member to target. For overloads, combine with contextSnippet/lineBefore/lineAfter.")]
        [Consumes(DataTag.SymbolName, required: false)] string? memberName = null,
        [Description("Exactly one member declaration (full trivia, signature and body); for addTopLevelType, the full type declaration.")]
        [Consumes(DataTag.SourceCode, required: false)] string? newMemberSource = null,
        [Description("addMember: where to insert - null/\"end\" to append, \"after:MemberName\", or \"before:MemberName\".")]
        [ExternalInputRequired(DataTag.Position)] string? position = null,
        [Description("Kind to generate. For a method, class, record, struct or interface use addMember/addTopLevelType instead.")]
        [ExternalInputRequired(DataTag.SymbolKind, required: false)] TypedMemberKind? typedKind = null,
        [Description("The generated member's name.")]
        [ExternalInputRequired(DataTag.SymbolName, required: false)] string? typedName = null,
        [Description("The generated member's type.")]
        [ExternalInputRequired(DataTag.DataType, required: false)] string? typedType = null,
        [Description(ToolParams.AccessibilityValues)][ExternalInputRequired(DataTag.Accessibility)] string accessibility = "public",
        [Description("property only.")][ExternalInputRequired(DataTag.HasSetter)] bool hasSetter = true,
        [Description("property only.")][ExternalInputRequired(DataTag.IsInit)] bool isInit = false,
        [Description("field only.")][ExternalInputRequired(DataTag.IsReadonly)] bool isReadonly = false,
        [Description("field only.")][ExternalInputRequired(DataTag.IsStatic)] bool isStatic = false,
        [Description("field only: initializer expression.")][ExternalInputRequired(DataTag.Initializer)] string? initializer = null,
        [Description("Forcefully removes the member, skipping any precheck for callers or implementations.")] bool skipPrecheck = false,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default) =>
        _impl.Member(reason, filePath, operation, containerName, namespaceName, memberName, newMemberSource, position, typedKind, typedName, typedType,
            accessibility, hasSetter, isInit, isReadonly, isStatic, initializer, skipPrecheck, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff,
            requestParams, cancellationToken);

    [McpServerTool(Name = "ModifyEnum")]
    [Produces(DataTag.ChangeId)]
    [Description("Replaces an enum's complete member list in one operation. Use GetTypeInfo(typeName, include:\"members\") to see current values first.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyEnum(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.SymbolName, required: true)] string enumName,
        [Description("Member names in the desired order, as a comma-separated string (e.g. \"Pending,Shipped,Cancelled\") or a JSON array of strings; append \"=N\" for an explicit value (e.g. \"Archived=99\"). Pass the complete list every time, not a delta: omitted names are removed, new names are added, explicit values are preserved, implicit members take the next ordinal.")]
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string values,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ModifyEnum(reason, filePath, enumName, values, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyAttribute")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds, replaces, or removes an [Attribute] on a type or member. Use ChangeAccessibility for accessibility keywords and ModifyModifier for other modifier keywords.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyAttribute(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'batchEdits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Description("For overloads, combine with contextSnippet/lineBefore/lineAfter.")]
        [Consumes(DataTag.SymbolName, required: false)] string? targetName = null,
        [Description("The attribute to add/replace/remove, with or without [ ] brackets.")]
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string? existingAttribute = null,
        [Consumes(DataTag.Action, required: false)] AttributeModifyAction? action = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for action=replace, unused for add/remove.
        [Description("action=replace: the attribute to replace existingAttribute with.")]
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string? newAttribute = null,
        [Description("Alias for existingAttribute (e.g. \"Obsolete\" or \"[Obsolete]\").")]
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string? attribute = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AttributeEdits)] List<AttributeEdit>? batchEdits = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ModifyAttribute(reason, filePath, targetName, existingAttribute, action, newAttribute, attribute, contextSnippet, lineBefore, lineAfter, batchEdits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyModifier", UseStructuredContent = false, OutputSchemaType = typeof(ModifyModifierResultEnvelope))]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a non-accessibility modifier keyword (use ChangeAccessibility for private/public/etc., ModifyAttribute for [Attribute]). For overloaded targets, provide contextSnippet and optionally lineBefore/lineAfter. ADD STATIC on a method or property is a conversion: the member must use no instance state (no this/base or instance members of its type), and instance-qualified callers (receiver.M(...), receiver.P) are rewritten to Type.M(...) in the same atomic change; members that only use each other may be converted together in one 'edits' batch. Not supported for add static: auto-properties, init accessors, virtual/override/abstract members, interface implementations, members of generic types. Returns changeId.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyModifier(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'batchEdits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
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
        _impl.ModifyModifier(reason, filePath, targetName, modifier, action, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyBaseType")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a base type or interface from a type declaration.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyBaseType(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required only when 'batchEdits' is omitted -> see the either/or check below.
        [Consumes(DataTag.SourceFilepath, required: false)] string? filePath = null,
        [Description("For same-named types in one file, combine with contextSnippet/lineBefore/lineAfter.")]
        [Consumes(DataTag.SymbolName, required: false)] string? typeName = null,
        [Description("The base type or interface name.")] string? baseTypeName = null,
        AddRemoveAction? action = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.BaseTypeEdits)] List<BaseTypeEdit>? edits = null,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.ModifyBaseType(reason, filePath, typeName, baseTypeName, action, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "SyncTypeAndFilename")]
    [Produces(DataTag.ResultOnly)]
    [Description("Synchronizes the filename to match a type declared in the file.")]
    public Task<SentinelCallToolResult<object>> SyncTypeAndFilename(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Description("The top-level type to sync the filename to. Omit to use the first non-nested type in the file; name it explicitly when the file declares more than one top-level type.")]
        string? targetTypeName = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _impl.SyncTypeAndFilename(reason, filePath, targetTypeName, dryRun, returnDiff, cancellationToken);
}
