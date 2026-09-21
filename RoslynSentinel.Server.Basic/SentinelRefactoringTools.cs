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
    [Produces(DataTag.ChangeId)]
    [Description("Renames a symbol and all its references across the solution, including mentions in XML doc comments, inline comments, and string literals. Returns changeId and updatedHandle for the renamed symbol, plus residualMentions for any leftover occurrences of the old name that rename couldn't reach (e.g. embedded in an unrelated identifier, or in a non-source file). Does NOT simplify call sites or add/remove using directives - if the rename target's new name needs a namespace not already in scope at a call site, or you want to shorten a fully-qualified reference, use the UsingDirective tool separately.")]
    public Task<SentinelCallToolResult<object>> RenameSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.ProjectName)] string projectName,
        [Description(ToolParams.DocCommentId)][Consumes(DataTag.DocCommentId, required: true)] string docCommentId,
        [Description("New name for the symbol. Must be a valid C# identifier.")] string newName,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default) =>
        _signature.RenameSymbol(reason, projectName, docCommentId, newName, dryRun, returnDiff, requestParams, cancellationToken);

    [McpServerTool(Name = "MethodSignature")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view a method's parameters (general-purpose - not limited to constructors; see ConstructorParameter for DI-style constructor parameters with a backing field). For overloaded methods, combine methodName with contextSnippet/lineBefore/lineAfter to disambiguate.")]
    public Task<SentinelCallToolResult<object>> MethodSignature(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: appends a new parameter to the end of the parameter list. remove: only the LAST parameter can be removed (paramName must match it) - a deliberate restriction, since removing an earlier parameter would require reordering every call site's remaining positional arguments, which cannot always be done safely; call sites passing the removed argument positionally are updated automatically, but a call site using named arguments (or one that can't be safely re-parsed) causes the whole operation to be refused with no changes made. view: lists current parameters (name, type, default value); makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.MethodName, required: true)] string methodName,
        [Description("Required for add/remove. Not used for view.")]
        [Consumes(DataTag.SymbolName, required: false)] string? paramName = null,
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
        _signature.MethodSignature(reason, filepath, operation, methodName, paramName, paramType, defaultValue, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken, nullDefault);

    [McpServerTool(Name = "ChangeAccessibility")]
    [Produces(DataTag.ChangeId)]
    [Description("Changes the accessibility (private, public, internal, protected, protected internal, private protected) of a type or member to the given target level in one step - replaces whatever accessibility is currently present, so there's no separate remove/add pairing to get wrong. For overloaded members, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. This tool covers accessibility only - use ChangeAccessibility for accessibility, ModifyAttribute for [Attribute] syntax, and ModifyModifier for non-accessibility keywords (virtual/abstract/static/etc.). Returns changeId.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ChangeAccessibility(
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
        _signature.ChangeAccessibility(reason, filepath, targetName, accessibility, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ConstructorParameter")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view DI constructor parameters on a class. For classes with the same name in the same file, combine className with contextSnippet/lineBefore/lineAfter to disambiguate.")]
    public Task<SentinelCallToolResult<object>> ConstructorParameter(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: creates a private readonly field, parameter, and body assignment in one step; creates a constructor if none exists. remove: deletes the parameter and its assignment statement - the backing field is only deleted if a solution-wide reference check confirms nothing else in the class still uses it, otherwise it's left in place. view: lists current constructor parameters and their inferred backing fields; makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.ClassName, required: true)] string className,
        [Description("Required for add/remove. Not used for view.")]
        [Consumes(DataTag.SymbolName, required: false)] string? paramName = null,
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
        _signature.ConstructorParameter(reason, filepath, operation, className, paramName, paramType, fieldName, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "GenerateMapping")]
    [Produces(DataTag.ChangeId)]
    [Description("Generates a mapping method between fromType and toType. Returns changeId.")]
    public Task<SentinelCallToolResult<object>> GenerateMapping(
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [ExternalInputRequired(DataTag.DataType, required: true)] string fromType,
        [ExternalInputRequired(DataTag.DataType)] string toType,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default) =>
        _extractionDocs.GenerateMapping(filepath, fromType, toType, dryRun, returnDiff, requestParams, cancellationToken);

    [McpServerTool(Name = "Member")]
    [Produces(DataTag.ChangeId)]
    [Description("Add (as a raw source member, a generated typed property/field, or a brand-new top-level type), remove, replace, or view a type member (method, property, field, constructor). This is the right choice even for a one-line change inside a member - read the member's current source first (e.g. via GetMethodSource/ReadFile), copy it verbatim, make your edit, and pass the whole resulting member as newMemberSource, not a fragment. Prefer this over a unified diff to edit part of a member: a whole-member replacement can't drift out of sync the way a hand-built diff hunk can.")]
    public Task<SentinelCallToolResult<object>> Member(
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
        _structural.Member(reason, filepath, operation, containerName, namespaceName, memberName, newMemberSource, position, typedKind, typedName, typedType,
            accessibility, hasSetter, isInit, isReadonly, isStatic, initializer, skipPrecheck, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff,
            requestParams, cancellationToken);

    [McpServerTool(Name = "UsingDirective")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view using directives in a file.")]
    public Task<SentinelCallToolResult<object>> UsingDirective(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: inserts a using; no-op if already present. remove: deletes the matching using directive. view: lists current using directives (name, isStatic, alias); makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Description("Required for add/remove. For static usings, prefix with \"static \" (e.g. \"static System.Math\"). Not required for view.")]
        [Consumes(DataTag.SymbolName, required: false)] string? namespaceName = null,
        [Description("add only. After inserting, runs Roslyn's Simplifier (semantic-model-based, not text find/replace) over the file to shorten now-redundant fully-qualified references - only reduces a name when doing so introduces no ambiguity.")] bool simplifyExisting = false,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _extractionDocs.UsingDirective(reason, filepath, operation, namespaceName, simplifyExisting, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyEnum")]
    [Produces(DataTag.ChangeId)]
    [Description("Replaces an enum's complete member list in one operation. Use GetTypeInfo(typeName, include:\"members\") to see current values first.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyEnum(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string enumName,
        [Description("List of member names in the desired order, either a comma-separated string (e.g. \"Pending,Shipped,Cancelled\") or a JSON array of strings (e.g. [\"Pending\",\"Shipped\",\"Cancelled\"]) - both are accepted; append \"=N\" for an explicit value (e.g. \"Archived=99\"). Omitted names are removed, new names are added, explicit values are preserved, and implicit members take the next ordinal from their predecessor - as if hand-typed. Pass the complete list every time, not a delta.")]
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string values,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _structural.ModifyEnum(reason, filepath, enumName, values, contextSnippet, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "SummaryComment")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view a /// <summary> XML doc comment on a type or member. For overloaded targets, combine targetName with contextSnippet/lineBefore/lineAfter to disambiguate.")]
    public Task<SentinelCallToolResult<object>> SummaryComment(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: adds or replaces the summary, overwriting any existing one. remove: deletes the summary comment if present; no-op if none exists. view: returns the current summary text (or null if none); makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [Description("Required for add - the new summary text. Not used for remove/view.")][Consumes(DataTag.SourceCode, required: false)] string? summaryText = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ContainingTypeName)] string? containingTypeName = null,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        CancellationToken cancellationToken = default) =>
        _extractionDocs.SummaryComment(reason, filepath, operation, targetName, summaryText, contextSnippet, lineBefore, lineAfter, containingTypeName, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ExtractLocalVariable")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts an inline expression into a named local variable declaration. exactExpressionText is NOT a search fragment (unlike contextSnippet on other tools) - it must be the WHOLE expression to extract, copied verbatim.")]
    public Task<SentinelCallToolResult<object>> ExtractLocalVariable(
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
        _extractionDocs.ExtractLocalVariable(reason, filepath, exactExpressionText, variableName, lineBefore, lineAfter, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ExtractMethodSafe")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts selected statements into a new method with the correct return type inferred from the selection. newMethodName must be a valid C# identifier. exactSourceBlock is NOT a search fragment (unlike contextSnippet on other tools) - the entire range you want extracted must appear in it verbatim, since its matched span IS the extraction boundary; a too-short excerpt silently extracts only that narrower range, not the whole intended block. Written to disk (or staged, per autoStage) like other refactoring tools - not preview-only. Returns changeId.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ExtractMethodSafe(
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
        _extractionDocs.ExtractMethodSafe(reason, filepath, newMethodName, exactSourceBlock, lineBefore, lineAfter, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyAttribute")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds, replaces, or removes an [Attribute] on a type or member. Use ChangeAccessibility for accessibility keywords and ModifyModifier for other modifier keywords, not this tool.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyAttribute(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: false)] FilePathWrapper? filepath = null,
        [Description("For overloaded/duplicate-named targets, combine with contextSnippet/lineBefore/lineAfter to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: false)] string? targetName = null,
        [Description("The attribute to add/replace/remove. May include or omit the surrounding [ ] brackets.")]
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string? existingAttribute = null,
        [Consumes(DataTag.Action, required: false)] AttributeModifyAction? action = null,
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
        _structural.ModifyAttribute(reason, filepath, targetName, existingAttribute, action, newAttribute, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyModifier", UseStructuredContent = true, OutputSchemaType = typeof(ModifyModifierResultEnvelope))]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a non-accessibility modifier keyword. Action: add or remove. For overloaded targets, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. Does NOT cover accessibility (private/public/etc.) - use ChangeAccessibility for those, or ModifyAttribute for [Attribute] syntax. Returns changeId.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyModifier(
        [Description(ToolParams.Reason)] ToolCallReason reason,
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
        _structural.ModifyModifier(reason, filepath, targetName, modifier, action, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "ModifyBaseType")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a base type or interface from a type declaration.")]
    public Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyBaseType(
        [Description(ToolParams.Reason)] ToolCallReason reason,
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
        _structural.ModifyBaseType(reason, filepath, typeName, baseTypeName, action, contextSnippet, lineBefore, lineAfter, edits, autoStage, dryRun, returnDiff, cancellationToken);

    [McpServerTool(Name = "SyncTypeAndFilename")]
    [Produces(DataTag.ResultOnly)]
    [Description("Synchronizes the filename to match a type declared in the file.")]
    public Task<SentinelCallToolResult<object>> SyncTypeAndFilename(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("The top-level type in the file to sync the filename to. Omit to default to the first non-nested type declared in the file (fine for the common single-type-per-file case). Name it explicitly to get a specific result when the file declares more than one top-level type - without it, whichever type happens to be declared first wins, which is not necessarily the file's conceptual main type.")]
        string? targetTypeName = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
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
/// <see cref="RenameSymbolResultEnvelope"/>'s <c>SuccessDetails</c>, built from <see cref="SymbolHandle"/>.
/// Primary path only - see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record RenameSymbolUpdatedHandle(
    [property: Produces(DataTag.SessionId)] string SessionId,
    [property: Produces(DataTag.ProjectName)] string ProjectName,
    [property: Produces(DataTag.DocCommentId)] string DocCommentId);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Named shape mirroring the anonymous object <see cref="SentinelRefactoringTools.RenameSymbol"/>
/// assigns to <c>SentinelCallToolResult<object>.SuccessDetails</c> on its applied success path. Primary path only
/// (the resolution-failed / no-pending-changes / apply-failed error paths return a different,
/// error-shaped envelope with no SuccessDetails) - see proposal_structuredcontent_rollout.md.
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
/// <see cref="SentinelRefactoringTools.RenameSymbol"/>'s primary success path, which sets only
/// <c>IsSuccess</c> and <c>SuccessDetails</c> (not TotalRecords/WorkspaceVersion/etc). Primary path only -
/// see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record RenameSymbolResultEnvelope(
    bool Success,
    RenameSymbolData? Data);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Envelope shape mirroring <c>SentinelCallToolResult<object></c> as actually populated on
/// <see cref="SentinelRefactoringTools.ModifyModifier"/>'s primary (autoStage=true, singular-edit,
/// non-batch) success path, which sets only <c>IsSuccess</c> and <c>SuccessDetails</c>. Deliberately does not
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
/// Mirrors the anonymous <c>new { Parameters = parameters }</c> object assigned to SuccessDetails on
/// MethodSignature's view branch.
/// </summary>
public sealed record MethodSignatureViewData(
    IReadOnlyList<MethodSignatureParameterInfo> Parameters);
// Added by AddTopLevelType (expected - used for diagnostics)
/// <summary>
/// Envelope shape mirroring <c>SentinelCallToolResult<object></c> as actually populated on
/// <see cref="SentinelRefactoringTools.MethodSignature"/>'s "view" branch only (operation=view),
/// which sets only <c>IsSuccess</c> and <c>SuccessDetails = new { Parameters }</c>. The add/remove branches
/// (both the non-autoStage ToJsonSummary shape and the autoStage applied-with-offload
/// MemberChangedContentResult/AppliedChangeSummary shape) are intentionally NOT covered by this
/// POC - see proposal_structuredcontent_rollout.md.
/// </summary>
public sealed record MethodSignatureViewResultEnvelope(
    bool Success,
    MethodSignatureViewData? Data);
