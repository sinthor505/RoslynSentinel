using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

[McpServerToolType]
public class SentinelRefactoringTools
{
    private readonly RefactoringEngine _refactoringEngine;
    private readonly StandardRefactoringEngine _standardRefactoringEngine;
    // private readonly AdvancedStructuralEngine _advancedStructuralEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly MappingEngine _mappingEngine;
    private readonly SemanticRefactoringLibrary _semanticRefactoringLibrary;
    private readonly GranularRefactoringEngine _granularRefactoringEngine;
    // private readonly AdvancedLogicEngine _advancedLogicEngine;
    // private readonly RefinementEngine _refinementEngine;
    // private readonly AdvancedTypeEngine _advancedTypeEngine;
    private readonly CodeStyleEngine _codeStyleEngine;
    private readonly CodeFlowEngine _codeFlowEngine;
    // private readonly AdvancedRefactoringEngine _advancedRefactoringEngine;
    // private readonly LogicOptimizationEngine _logicOptimizationEngine;
    // private readonly OutParamRefactoringEngine _outParamRefactoringEngine;
    private readonly MsToolAugmentEngine _msToolAugmentEngine;
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<SentinelRefactoringTools> _logger;

    public SentinelRefactoringTools(
        RefactoringEngine refactoringEngine,
        StandardRefactoringEngine standardRefactoringEngine,
        MappingEngine mappingEngine,
        SemanticRefactoringLibrary semanticRefactoringLibrary,
        GranularRefactoringEngine granularRefactoringEngine,
    // AdvancedLogicEngine advancedLogicEngine,
    // RefinementEngine refinementEngine,
    // AdvancedTypeEngine advancedTypeEngine,
    StructuralRefinementEngine structuralRefinementEngine,
    CodeStyleEngine codeStyleEngine,
        CodeFlowEngine codeFlowEngine,
        // AdvancedRefactoringEngine advancedRefactoringEngine,
        // LogicOptimizationEngine logicOptimizationEngine,
        // ModernizationEngine modernizationEngine,
        // OutParamRefactoringEngine outParamRefactoringEngine,
        MsToolAugmentEngine augmentEngine,
        CodeGenerationEngine codeGenerationEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        SentinelConfiguration config,
        ILogger<SentinelRefactoringTools> logger)
    {
        _refactoringEngine = refactoringEngine;
        _standardRefactoringEngine = standardRefactoringEngine;
        _mappingEngine = mappingEngine;
        _semanticRefactoringLibrary = semanticRefactoringLibrary;
        _granularRefactoringEngine = granularRefactoringEngine;
        // _advancedLogicEngine = advancedLogicEngine;
        // _refinementEngine = refinementEngine;
        // _advancedTypeEngine = advancedTypeEngine;
        _structuralRefinementEngine = structuralRefinementEngine;
        _codeStyleEngine = codeStyleEngine;
        _codeFlowEngine = codeFlowEngine;
        //_advancedRefactoringEngine = advancedRefactoringEngine;
        // _logicOptimizationEngine = logicOptimizationEngine;
        // _outParamRefactoringEngine = outParamRefactoringEngine;
        _msToolAugmentEngine = augmentEngine;
        _codeGenerationEngine = codeGenerationEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _config = config;
        _logger = logger;
    }

    private static string PreviewFileContent(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length <= 20)
        {
            return content;
        }

        var head = lines.Take(10);
        var tail = lines.TakeLast(10);
        return string.Join("\n", head) + "\n// ... (truncated)\n" + string.Join("\n", tail);
    }

    /// <summary>
    /// Guards against staging an unintended empty-file overwrite: when a document-edit engine
    /// method can't locate its target (wrong name, wrong attribute/modifier, etc.), it returns
    /// Outcome != Modified and leaves UpdatedText at its string.Empty default rather than null —
    /// so skipping this check would silently propose replacing the whole file with nothing.
    /// Returns null when updated.UpdatedText is safe to use as the new file content.
    /// </summary>
    /// <remarks>
    /// The error code is derived from the outcome rather than always being
    /// <see cref="ToolErrorCode.Exception"/>: a target the engine simply couldn't find is an
    /// ordinary, caller-correctable condition, and reporting it as an exception told the agent
    /// the server had faulted. Run 20260910-013550-398 hit this via a generic containerName —
    /// see docs/current/feedback_agent_friendly_error_messages.md.
    /// </remarks>
    private static ToolResult<object>? RequireUpdatedText(DocumentEditResult updated, string operationName, FilePathWrapper filePath)
    {
        if (!string.IsNullOrEmpty(updated.UpdatedText))
        {
            return null;
        }

        return new ToolResult<object>
        {
            Success = false,
            Error = new ResultError(ErrorCodeFor(updated.Outcome),
                $"{operationName}: no change produced for '{filePath}' ({updated.Outcome}). {updated.Message}")
        };
    }

    /// <summary>
    /// Maps a document-edit outcome to the error code the agent sees. Shared so the
    /// <c>Member(replace)</c> path, which builds its own message, cannot drift from
    /// <see cref="RequireUpdatedText"/> on the code.
    /// </summary>
    private static string ErrorCodeFor(EditOutcome outcome) => outcome switch
    {
        EditOutcome.TargetNotFound or EditOutcome.DocumentNotFound => ToolErrorCode.NotFound,
        EditOutcome.SourceInvalid => ToolErrorCode.InvalidArgument,
        _ => ToolErrorCode.Exception
    };

    /// <summary>
    /// Validates proposed changes against the current in-memory solution and, unless
    /// <paramref name="dryRun"/> is set, writes them straight to disk (write-through — no
    /// intermediate staging step). Rolls back any already-written files if a multi-file change
    /// partially fails, so a change never lands half-applied.
    /// </summary>
    private Task<ApplyOutcome> ValidateAndApplyAsync(
        Dictionary<FilePathWrapper, string> changes,
        string description,
        string operationName,
        bool dryRun = false,
        bool returnDiff = false,
        IProgress<ProgressNotificationValue>? progress = default,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        CancellationToken cancellationToken = default) =>
        ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine, _workspaceManager, _logger, changes, operationName,
            dryRun, returnDiff, progress, removePaths, cancellationToken,
            describeValidationFailure: (report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, ct));

    private Task<string> BuildDiffAsync(Dictionary<FilePathWrapper, string> changes, CancellationToken cancellationToken) =>
        ValidateAndApplyHelper.BuildDiffAsync(_workspaceManager, changes, cancellationToken);

    internal static string BuildDiffFromPreImages(
        Dictionary<FilePathWrapper, string> changes,
        IReadOnlyDictionary<string, string?>? preImages) =>
        ValidateAndApplyHelper.BuildDiffFromPreImages(changes, preImages);

    [McpServerTool(Name = "RenameSymbol")]
    [Produces(DataTag.ChangeId)]
    [Description("Renames a symbol and all its references across the solution, including mentions in XML doc comments, inline comments, and string literals. Returns changeId and updatedHandle for the renamed symbol, plus residualMentions for any leftover occurrences of the old name that rename couldn't reach (e.g. embedded in an unrelated identifier, or in a non-source file). Does NOT simplify call sites or add/remove using directives — if the rename target's new name needs a namespace not already in scope at a call site, or you want to shorten a fully-qualified reference, use the UsingDirective tool separately.")]
    public async Task<ToolResult<object>> RenameSymbol(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description(ToolParams.ProjectName)] string projectName,
        [Description(ToolParams.DocCommentId)] string docCommentId,
        [Description("New name for the symbol. Must be a valid C# identifier.")] string newName,
        [Description(ToolParams.SessionId)] string sessionId = "",
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
        IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

        SymbolResolution resolution = await _workspaceManager.ResolveFromWireAsync(
            sessionId, projectName, docCommentId, cancellationToken);
        if (!resolution.Resolved)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, resolution.Error!.Message)
            };
        }

        RenameSymbolResult result = await _refactoringEngine.RenameSymbolAsync(
            resolution.Handle, resolution.Symbol!, newName, cancellationToken);

        if (result.Error is not null)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, result.Error)
            };
        }

        if (result.PendingChanges.Count == 0)
        {
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception,
                    $"RenameSymbol produced no file changes for '{result.OldName}' → '{result.NewName}'.")
            };
        }

        var apply = await ValidateAndApplyAsync(
            result.PendingChanges,
            $"Rename '{result.OldName}' to '{result.NewName}'.",
            "RenameSymbol", dryRun, returnDiff, cancellationToken: cancellationToken);

        if (apply.Error is not null)
            return new ToolResult<object> { Success = false, Error = apply.Error };

        // Deliberately not wired into ForPossiblyLargeDataAsync/MemberChangedContentResult: this
        // already returns rich custom Data (oldName/newName/residualMentions/updatedHandle) instead
        // of a bare AppliedChangeSummary, and newName is caller-supplied verbatim — there is no
        // separate "new content" fragment the offload mechanism would add value for.
        return new ToolResult<object>
        {
            Success = true,
            Data = new
            {
                changeId = apply.ChangeId,
                dryRun = apply.DryRun,
                diff = apply.Diff,
                oldName = result.OldName,
                newName = result.NewName,
                filesChanged = result.PendingChanges.Count,
                updatedHandle = result.UpdatedHandle is SymbolHandle h
                    ? new
                    {
                        h.SessionId,
                        h.ProjectName,
                        h.DocCommentId
                    }
                    : null,
                residualMentions = result.ResidualMentions is { Count: > 0 } rm ? rm : null,
                residualMentionsNote = result.ResidualMentions is { Count: > 0 }
                    ? $"'{result.OldName}' still appears in {result.ResidualMentions.Count} place(s) Rename couldn't reach (e.g. embedded in another identifier, or in a non-source file). Review residualMentions."
                    : null
            }
        };
    }

    [McpServerTool(Name = "GenerateMapping")]
    [Produces(DataTag.ChangeId)]
    [Description("Generates a mapping method between fromType and toType. Returns changeId.")]
    public async Task<ToolResult<object>> GenerateMapping(
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [ExternalInputRequired(DataTag.DataType, required: true)] string fromType,
        [ExternalInputRequired(DataTag.DataType)] string toType,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
            IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

            var result = await _mappingEngine.GenerateMappingAsync(filePathResolved, fromType, toType, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"GenerateMapping produced no output for '{fromType}' → '{toType}' in '{filePathResolved}'. Ensure both types exist in the solution.") };

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Generate mapping from '{fromType}' to '{toType}'.", "GenerateMapping", dryRun, returnDiff, progress, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Generated mapping from '{fromType}' to '{toType}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateMapping failed for '{FromType}' to '{ToType}' in '{FilePathWrapper}'", fromType, toType, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GenerateMapping") };
        }
    }
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: required-param set depends entirely on 'operation' — add
    // needs containerName (or, for a brand-new top-level type, newMemberSource alone with no
    // typedKind); view needs containerName; remove needs memberName; replace needs memberName +
    // newMemberSource. Within add, exactly one of newMemberSource or typedKind+typedName+typedType
    // is required. No param besides filepath/operation is universally required, so a model can
    // supply the wrong subset for its chosen operation and only find out at runtime.
    [McpServerTool(Name = "Member")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, replace, or view a type member (method, property, field, constructor), or add a brand-new top-level type. This is the right choice even for a one-line change inside a member — read the member's current source first (e.g. via GetMethodSource/ReadFile), copy it verbatim, make your edit, and pass the whole resulting member as newMemberSource, not a fragment. Prefer this over a unified diff to edit part of a member: a whole-member replacement can't drift out of sync the way a hand-built diff hunk can.")]
    public async Task<ToolResult<object>> Member(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: adds a member (or a new top-level type). remove: deletes a member — by default checks for callers/implementations first (see skipPrecheck); for a zero-usages-only contract use SafeDeleteUnusedSymbol instead. replace: replaces a member's full source, including for small in-member edits. view: lists a container's direct members (name, kind, signature, line range) to find the exact memberName/contextSnippet to pass to remove or replace.")]
        [Consumes(DataTag.Action, required: true)] MemberAction operation,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add (unless adding a brand-new
        // top-level type — see newMemberSource) and operation=view; unused for remove/replace, which
        // resolve memberName directly regardless of container.
        [Description("Required for add (except when adding a brand-new top-level type) and view. Not needed for remove/replace.")]
        [Consumes(DataTag.SymbolName, required: false)] string? containerName = null,
        [Description("Only used for operation=add when newMemberSource is a top-level type declaration (enum/class/record/struct/interface) and containerName is omitted. Disambiguates which namespace to add it to, when the file has more than one. Ignored otherwise.")]
        [ExternalInputRequired(DataTag.SymbolName, required: false)] string? namespaceName = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=remove and operation=replace;
        // unused for add/view.
        [Description("Required for remove and replace — the member to target. For overloaded targets, combine with contextSnippet/lineBefore/lineAfter to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: false)] string? memberName = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=replace; for operation=add,
        // required unless typedKind+typedName+typedType is supplied instead (exactly one of the two
        // forms is required, not both); unused for remove/view. Also doubles as the full top-level
        // type declaration when adding a brand-new type with containerName omitted.
        [Description("replace: the full replacement member source (signature + body). add: either the full raw member source (with containerName), or a brand-new top-level type declaration (enum/class/record/struct/interface) with containerName omitted — mutually exclusive with typedKind.")]
        [Consumes(DataTag.SourceCode, required: false)] string? newMemberSource = null,
        [Description("add only: where to insert — null/\"end\" to append, \"after:MemberName\", or \"before:MemberName\". Ignored for a brand-new top-level type.")]
        [ExternalInputRequired(DataTag.Position)] string? position = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: alternative to newMemberSource for operation=add — set
        // this (with typedName+typedType) to generate a typed property/field instead of supplying
        // raw source. Mutually exclusive with newMemberSource; unused for remove/view/replace.
        [Description("add only, alternative to newMemberSource: generates a typed property or field. Requires typedName+typedType alongside it.")]
        [ExternalInputRequired(DataTag.SymbolKind, required: false)] TypedMemberKind? typedKind = null,
        [Description("Required when typedKind is set: the generated member's name.")]
        [ExternalInputRequired(DataTag.SymbolName, required: false)] string? typedName = null,
        [Description("Required when typedKind is set: the generated member's type.")]
        [ExternalInputRequired(DataTag.DataType, required: false)] string? typedType = null,
        [Description(ToolParams.AccessibilityValues + " typedKind generation only.")][ExternalInputRequired(DataTag.Accessibility)] string accessibility = "public",
        [Description("typedKind=property only.")][ExternalInputRequired(DataTag.HasSetter)] bool hasSetter = true,
        [Description("typedKind=property only.")][ExternalInputRequired(DataTag.IsInit)] bool isInit = false,
        [Description("typedKind=field only.")][ExternalInputRequired(DataTag.IsReadonly)] bool isReadonly = false,
        [Description("typedKind=field only.")][ExternalInputRequired(DataTag.IsStatic)] bool isStatic = false,
        [Description("typedKind=field only: optional initializer expression.")][ExternalInputRequired(DataTag.Initializer)] string? initializer = null,
        [Description("remove only. When false (default), refuses removal if the member has any callers or implementations (checked the same way as FindReferences(kind: all)). Set true to skip this check and remove unconditionally.")] bool skipPrecheck = false,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == MemberAction.view)
            {
                if (string.IsNullOrEmpty(containerName))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: containerName is required for operation 'view'.") };

                var (outcome, message, members) = await _refactoringEngine.GetContainerMembersAsync(filePathResolved, containerName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Member: {message}") };
                return new ToolResult<object>() { Success = true, Data = new { Members = members } };
            }

            if (operation == MemberAction.replace)
            {
                if (string.IsNullOrEmpty(memberName) || string.IsNullOrEmpty(newMemberSource))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: memberName and newMemberSource are required for operation 'replace'.") };

                ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
                IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

                var replaceEnumName = await _refactoringEngine.TryGetEnumMemberContainerNameAsync(filePathResolved, memberName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (replaceEnumName != null)
                {
                    var enumReplaced = await _refactoringEngine.ReplaceEnumMemberAsync(filePathResolved, replaceEnumName, memberName, newMemberSource, contextSnippet, lineBefore, lineAfter, cancellationToken);
                    if (string.IsNullOrEmpty(enumReplaced.UpdatedText))
                        return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Member: {enumReplaced.Message} Retry using ModifyEnum(enumName: \"{replaceEnumName}\", values: ...) directly with the full desired member list if this keeps failing.") };

                    var enumReplaceChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = enumReplaced.UpdatedText! };
                    var enumReplaceApply = await ValidateAndApplyAsync(enumReplaceChanges, $"Replaced '{memberName}' in enum '{replaceEnumName}'.", "Member", dryRun, returnDiff, progress, cancellationToken: cancellationToken);
                    if (enumReplaceApply.Error is not null)
                        return new ToolResult<object> { Success = false, Error = enumReplaceApply.Error };
                    return await ToolResult<object>.ForPossiblyLargeDataAsync(
                        new MemberChangedContentResult
                        {
                            Summary = new AppliedChangeSummary(enumReplaceApply.ChangeId, [filePathResolved], $"Replaced '{memberName}' in enum '{replaceEnumName}' in {Path.GetFileName(filePathResolved)}.", enumReplaceApply.DryRun, enumReplaceApply.Diff),
                            ChangedContent = newMemberSource
                        },
                        _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                        workspaceVersion: _workspaceManager.WorkspaceVersion);
                }

                var result = await _refactoringEngine.ReplaceMemberAsync(filePathResolved, memberName, newMemberSource, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (string.IsNullOrEmpty(result.UpdatedText))
                {
                    string errorReason = result.Outcome switch
                    {
                        EditOutcome.DocumentNotFound => $"Member: document '{filePathResolved}' not found in the workspace.",
                        EditOutcome.SourceInvalid => $"Member: newMemberSource for '{memberName}' is not a valid member declaration. " +
                            "Provide the full member (signature + body, e.g. 'private decimal Foo() { ... }'), not just a statement or method body fragment.",
                        EditOutcome.TargetNotFound => $"Member: member '{memberName}' not found in '{filePathResolved}'.",
                        _ => $"Member: no changes produced for '{memberName}' in '{filePathResolved}' ({result.Outcome}). {result.Message}"
                    };
                    return new ToolResult<object> { Success = false, Error = new ResultError(ErrorCodeFor(result.Outcome), errorReason) };
                }

                var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedText };
                var apply = await ValidateAndApplyAsync(changes, $"Replace member '{memberName}'.", "Member", dryRun, returnDiff, progress, cancellationToken: cancellationToken);
                if (apply.Error is not null)
                    return new ToolResult<object> { Success = false, Error = apply.Error };
                return await ToolResult<object>.ForPossiblyLargeDataAsync(
                    new MemberChangedContentResult
                    {
                        Summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Replaced '{memberName}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff),
                        ChangedContent = newMemberSource
                    },
                    _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                    workspaceVersion: _workspaceManager.WorkspaceVersion);
            }

            if (operation == MemberAction.remove)
            {
                if (string.IsNullOrEmpty(memberName))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: memberName is required for operation 'remove'.") };

                if (!skipPrecheck)
                {
                    var callers = await _symbolNavigationEngine.FindCallersAsync(filePathResolved, memberName, contextSnippet: contextSnippet, lineBefore: lineBefore, lineAfter: lineAfter, cancellationToken: cancellationToken);
                    var implementations = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePathResolved, memberName, contextSnippet: contextSnippet, lineBefore: lineBefore, lineAfter: lineAfter, cancellationToken: cancellationToken);
                    if (callers.Count > 0 || implementations.Count > 0)
                    {
                        var parts = new List<string>();
                        if (callers.Count > 0) parts.Add($"{callers.Count} caller(s)");
                        if (implementations.Count > 0) parts.Add($"{implementations.Count} implementation(s)");
                        return new ToolResult<object>
                        {
                            Success = false,
                            Error = new ResultError(ToolErrorCode.InvalidArgument,
                                $"Member: '{memberName}' has {string.Join(" and ", parts)} — refusing to remove. " +
                                "Pass skipPrecheck: true to remove anyway, or resolve the callers/implementations first. " +
                                $"Callers: {System.Text.Json.JsonSerializer.Serialize(callers)}. " +
                                $"Implementations: {System.Text.Json.JsonSerializer.Serialize(implementations)}.")
                        };
                    }
                }

                var removeEnumName = await _refactoringEngine.TryGetEnumMemberContainerNameAsync(filePathResolved, memberName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (removeEnumName != null)
                {
                    var enumRemoved = await _refactoringEngine.RemoveEnumMemberAsync(filePathResolved, removeEnumName, memberName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                    if (string.IsNullOrEmpty(enumRemoved.UpdatedText))
                        return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Member: {enumRemoved.Message} Retry using ModifyEnum(enumName: \"{removeEnumName}\", values: ...) directly with the full desired member list if this keeps failing.") };

                    var enumRemoveChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = enumRemoved.UpdatedText! };
                    var enumRemoveApply = await ValidateAndApplyAsync(enumRemoveChanges, $"Removed '{memberName}' from enum '{removeEnumName}'.", "Member", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (enumRemoveApply.Error is not null)
                        return new ToolResult<object> { Success = false, Error = enumRemoveApply.Error };
                    return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(enumRemoveApply.ChangeId, [filePathResolved], $"Removed '{memberName}' from enum '{removeEnumName}' in {Path.GetFileName(filePathResolved)}.", enumRemoveApply.DryRun, enumRemoveApply.Diff, _workspaceManager.WorkspaceVersion) };
                }

                var result = await _refactoringEngine.RemoveMemberAsync(filePathResolved, memberName, contextSnippet, lineBefore, lineAfter);
                if (string.IsNullOrEmpty(result.UpdatedText))
                    return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Member: member '{memberName}' not found in '{filePathResolved}'.") };

                var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedText };
                var apply = await ValidateAndApplyAsync(changes, $"Remove member '{memberName}'.", "Member", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (apply.Error is not null)
                    return new ToolResult<object> { Success = false, Error = apply.Error };
                return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Removed '{memberName}' from {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
            }

            // operation == MemberAction.add
            if (string.IsNullOrEmpty(containerName))
            {
                if (string.IsNullOrEmpty(newMemberSource) || typedKind != null)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: containerName is required for operation 'add', unless newMemberSource is a brand-new top-level type declaration (enum/class/record/struct/interface) with no typedKind set.") };

                var topLevelResult = await _refactoringEngine.AddTopLevelTypeAsync(filePathResolved, newMemberSource, namespaceName, cancellationToken);
                if (!autoStage)
                    return new ToolResult<object>() { Success = true, Data = topLevelResult.ToJsonSummary() };
                if (RequireUpdatedText(topLevelResult, "Member", filePathResolved) is { } topLevelGuardResult)
                    return topLevelGuardResult;

                var topLevelDescription = $"Added new top-level type to {Path.GetFileName(filePathResolved)}.";

                var topLevelChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = topLevelResult.UpdatedText! };
                var topLevelApply = await ValidateAndApplyAsync(topLevelChanges, topLevelDescription, "Member", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (topLevelApply.Error is not null)
                    return new ToolResult<object> { Success = false, Error = topLevelApply.Error };
                return await ToolResult<object>.ForPossiblyLargeDataAsync(
                    new MemberChangedContentResult
                    {
                        Summary = new AppliedChangeSummary(topLevelApply.ChangeId, [filePathResolved], topLevelDescription, topLevelApply.DryRun, topLevelApply.Diff),
                        ChangedContent = newMemberSource
                    },
                    _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                    workspaceVersion: _workspaceManager.WorkspaceVersion);
            }

            var hasRawSource = !string.IsNullOrEmpty(newMemberSource);
            var hasTypedSpec = typedKind != null;
            if (hasRawSource == hasTypedSpec)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: for operation 'add', pass exactly one of newMemberSource (raw source) or typedKind+typedName+typedType (generated property/field).") };
            }

            if (await _refactoringEngine.IsEnumContainerAsync(filePathResolved, containerName, contextSnippet, lineBefore, lineAfter, cancellationToken))
            {
                if (hasTypedSpec)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: '{containerName}' is an enum — typedKind (property/field generation) doesn't apply. Pass newMemberSource as a bare member token ('Name' or 'Name=IntValue') instead, or retry using ModifyEnum(enumName: \"{containerName}\", values: ...) directly.") };

                string? afterName = position != null && position.StartsWith("after:", StringComparison.OrdinalIgnoreCase) ? position.Substring("after:".Length) : null;
                string? beforeName = position != null && position.StartsWith("before:", StringComparison.OrdinalIgnoreCase) ? position.Substring("before:".Length) : null;
                if (position != null && afterName == null && beforeName == null && position != "end")
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.") };

                var enumAdded = await _refactoringEngine.AddEnumMemberAsync(filePathResolved, containerName, newMemberSource!, afterName, beforeName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (string.IsNullOrEmpty(enumAdded.UpdatedText))
                    return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"Member: {enumAdded.Message} Retry using ModifyEnum(enumName: \"{containerName}\", values: ...) directly with the full desired member list if this keeps failing.") };

                if (!autoStage)
                    return new ToolResult<object>() { Success = true, Data = enumAdded.ToJsonSummary() };

                var enumAddDescription = $"Added member to enum '{containerName}' in {Path.GetFileName(filePathResolved)}.";
                var enumAddChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = enumAdded.UpdatedText! };
                var enumAddApply = await ValidateAndApplyAsync(enumAddChanges, enumAddDescription, "Member", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (enumAddApply.Error is not null)
                    return new ToolResult<object> { Success = false, Error = enumAddApply.Error };
                return await ToolResult<object>.ForPossiblyLargeDataAsync(
                    new MemberChangedContentResult
                    {
                        Summary = new AppliedChangeSummary(enumAddApply.ChangeId, [filePathResolved], enumAddDescription, enumAddApply.DryRun, enumAddApply.Diff),
                        ChangedContent = newMemberSource ?? ""
                    },
                    _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                    workspaceVersion: _workspaceManager.WorkspaceVersion);
            }

            DocumentEditResult updated;
            string description;
            // Only the raw-source path has the added member's exact text available verbatim
            // (the caller already supplied it). The typed-generation path (AddPropertyAsync/
            // AddFieldAsync) builds its source string inside the engine and doesn't return it
            // separately from the whole-file UpdatedText, so ChangedContent stays null there
            // rather than duplicating the engine's formatting logic at the tool layer.
            string? addedMemberSource = hasTypedSpec ? null : newMemberSource;
            if (hasTypedSpec)
            {
                if (string.IsNullOrEmpty(typedName) || string.IsNullOrEmpty(typedType))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "Member: typedName and typedType are required when typedKind is set.") };

                if (typedKind == TypedMemberKind.property)
                {
                    updated = await _refactoringEngine.AddPropertyAsync(filePathResolved, containerName, typedName, typedType, accessibility, hasSetter, isInit, contextSnippet, lineBefore, lineAfter);
                    description = $"Added '{typedType} {typedName}' property to '{containerName}' in {Path.GetFileName(filePathResolved)}.";
                }
                else
                {
                    updated = await _refactoringEngine.AddFieldAsync(filePathResolved, containerName, typedName, typedType, accessibility, isReadonly, isStatic, initializer, contextSnippet, lineBefore, lineAfter);
                    description = $"Added '{typedType} {typedName}' field to '{containerName}' in {Path.GetFileName(filePathResolved)}.";
                }
            }
            else if (string.IsNullOrEmpty(position) || position == "end")
            {
                updated = await _refactoringEngine.AddMemberAsync(filePathResolved, containerName, newMemberSource!, contextSnippet, lineBefore, lineAfter);
                description = $"Added new member to '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else if (position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
            {
                var afterMemberName = position.Substring("after:".Length);
                updated = await _refactoringEngine.InsertMemberAfterAsync(filePathResolved, containerName, afterMemberName, newMemberSource!, contextSnippet, lineBefore, lineAfter);
                description = $"Inserted new member after '{afterMemberName}' in '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else if (position.StartsWith("before:", StringComparison.OrdinalIgnoreCase))
            {
                var beforeMemberName = position.Substring("before:".Length);
                updated = await _refactoringEngine.InsertMemberBeforeAsync(filePathResolved, containerName, beforeMemberName, newMemberSource!, contextSnippet, lineBefore, lineAfter);
                description = $"Inserted new member before '{beforeMemberName}' in '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.") };
            }

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RequireUpdatedText(updated, "Member", filePathResolved) is { } guardResult)
                return guardResult;

            var addChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var addApply = await ValidateAndApplyAsync(addChanges, description, "Member", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (addApply.Error is not null)
                return new ToolResult<object> { Success = false, Error = addApply.Error };
            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult
                {
                    Summary = new AppliedChangeSummary(addApply.ChangeId, [filePathResolved], description, addApply.DryRun, addApply.Diff),
                    ChangedContent = addedMemberSource ?? ""
                },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Member ({Operation}) failed for '{ContainerOrMemberName}' in '{FilePathWrapper}'", operation, containerName ?? memberName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Member") };
        }
    }
    [McpServerTool(Name = "UsingDirective")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view using directives in a file.")]
    public async Task<ToolResult<object>> UsingDirective(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: inserts a using; no-op if already present. remove: deletes the matching using directive. view: lists current using directives (name, isStatic, alias); makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add/remove, unused for operation=view.
        [Description("Required for add/remove. For static usings, prefix with \"static \" (e.g. \"static System.Math\"). Not required for view.")]
        [Consumes(DataTag.SymbolName, required: false)] string? namespaceName = null,
        [Description("add only. After inserting, runs Roslyn's Simplifier (semantic-model-based, not text find/replace) over the file to shorten now-redundant fully-qualified references — only reduces a name when doing so introduces no ambiguity.")] bool simplifyExisting = false,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var usings = await _refactoringEngine.GetUsingDirectivesAsync(filePathResolved, cancellationToken);
                return new ToolResult<object>() { Success = true, Data = new { Usings = usings } };
            }

            if (string.IsNullOrEmpty(namespaceName))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"UsingDirective: namespaceName is required for operation '{operation}'.") };
            }

            DocumentEditResult updated;
            string opName;
            if (operation == AddRemoveViewAction.add)
            {
                updated = await _refactoringEngine.AddUsingDirectiveAsync(filePathResolved, namespaceName, simplifyExisting, cancellationToken);
                opName = "Add";
            }
            else
            {
                updated = await _refactoringEngine.RemoveUsingDirectiveAsync(filePathResolved, namespaceName, cancellationToken);
                opName = "Remove";
            }

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "UsingDirective", filePathResolved) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            // returnDiff is forced to true here regardless of the caller's own returnDiff flag:
            // the add path below needs the real before/after diff to populate ChangedContent, not
            // just to decide whether to include a preview Diff in the summary.
            var needsDiff = returnDiff || operation == AddRemoveViewAction.add;
            var apply = await ValidateAndApplyAsync(changes, $"{opName} using {namespaceName}.", "UsingDirective", dryRun, needsDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var description = operation == AddRemoveViewAction.add
                ? $"Adds 'using {namespaceName};' to {Path.GetFileName(filePathResolved)}."
                : $"Removes 'using {namespaceName};' from {Path.GetFileName(filePathResolved)}.";
            if (operation != AddRemoveViewAction.add)
            {
                return new ToolResult<object>() { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, returnDiff ? apply.Diff : null, _workspaceManager.WorkspaceVersion) };
            }

            // ChangedContent is derived from the actual before/after document diff (apply.Diff,
            // built by ValidateAndApplyHelper.BuildDiffFromPreImages) rather than reconstructed
            // from the caller's namespaceName argument. A hardcoded "using {namespaceName};" could
            // never reveal any other change bundled into the same write (formatting drift,
            // accessibility changes, whitespace normalization, etc.) — the caller had no way to
            // know from this tool's own result whether something unexpected also changed.
            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult
                {
                    Summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, returnDiff ? apply.Diff : null, _workspaceManager.WorkspaceVersion),
                    ChangedContent = apply.Diff ?? ""
                },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UsingDirective failed for '{Namespace}' in '{FilePathWrapper}'", namespaceName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "UsingDirective") };
        }
    }
    [McpServerTool(Name = "ModifyEnum")]
    [Produces(DataTag.ChangeId)]
    [Description("Replaces an enum's complete member list in one operation. Use GetTypeInfo(typeName, include:\"members\") to see current values first.")]
    public async Task<ToolResult<object>> ModifyEnum(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string enumName,
        [Description("Comma-separated list of member names in the desired order (e.g. \"Pending,Shipped,Cancelled\"); append \"=N\" for an explicit value (e.g. \"Archived=99\"). Omitted names are removed, new names are added, explicit values are preserved, and implicit members take the next ordinal from their predecessor — as if hand-typed. Pass the complete list every time, not a delta.")]
        [ExternalInputRequired(DataTag.SymbolName, required: true)] string values,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.ModifyEnumAsync(filePathResolved, enumName, values, contextSnippet, lineBefore, lineAfter);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "ModifyEnum", filePathResolved) is { } guardResult)
                return guardResult;

            var description = string.IsNullOrEmpty(updated.Message)
                ? $"Sets '{enumName}' members in {Path.GetFileName(filePathResolved)} to match the requested list."
                : $"'{enumName}' in {Path.GetFileName(filePathResolved)}: {updated.Message}.";

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "ModifyEnum", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent: the new member list is just the caller-supplied `values` string
            // already passed in verbatim — same reasoning as ChangeAccessibility/ModifyModifier.
            return new ToolResult<object>() { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyEnum failed for '{EnumName}' in '{FilePathWrapper}'", enumName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyEnum") };
        }
    }

    [McpServerTool(Name = "ChangeAccessibility")]
    [Produces(DataTag.ChangeId)]
    [Description("Changes the accessibility (private, public, internal, protected, protected internal, private protected) of a type or member to the given target level in one step — replaces whatever accessibility is currently present, so there's no separate remove/add pairing to get wrong. For overloaded members, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. This tool covers accessibility only — use ChangeAccessibility for accessibility, ModifyAttribute for [Attribute] syntax, and ModifyModifier for non-accessibility keywords (virtual/abstract/static/etc.). Returns changeId.")]
    public async Task<ToolResult<object>> ChangeAccessibility(
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
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var updated = await _refactoringEngine.ChangeAccessibilityAsync(filePathResolved, targetName, accessibility, contextSnippet, lineBefore, lineAfter);
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "ChangeAccessibility", filePathResolved) is { } guardResult)
                return guardResult;

            var accessibilityKeyword = accessibility switch
            {
                AccessibilityLevel.protectedInternal => "protected internal",
                AccessibilityLevel.privateProtected => "private protected",
                _ => accessibility.ToString()
            };
            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"Change accessibility of '{targetName}' to '{accessibilityKeyword}'.", "ChangeAccessibility", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent here: the only "new" text is the accessibility keyword itself,
            // which the caller already passed in verbatim — echoing it back adds nothing the
            // caller doesn't already have, unlike a reconstructed multi-part snippet.
            return new ToolResult<object>() { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Changed accessibility of '{targetName}' to '{accessibilityKeyword}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeAccessibility failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ChangeAccessibility") };
        }
    }
    [McpServerTool(Name = "SummaryComment")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view a /// <summary> XML doc comment on a type or member. For overloaded targets, combine targetName with contextSnippet/lineBefore/lineAfter to disambiguate.")]
    public async Task<ToolResult<object>> SummaryComment(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: adds or replaces the summary, overwriting any existing one. remove: deletes the summary comment if present; no-op if none exists. view: returns the current summary text (or null if none); makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add, unused for remove/view.
        [Description("Required for add — the new summary text. Not used for remove/view.")][Consumes(DataTag.SourceCode, required: false)] string? summaryText = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.ContainingTypeName)] string? containingTypeName = null,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var (outcome, message, text) = await _refactoringEngine.GetSummaryCommentAsync(filePathResolved, targetName, contextSnippet, lineBefore, lineAfter, containingTypeName, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SummaryComment: {message}") };
                return new ToolResult<object>() { Success = true, Data = new { SummaryText = text } };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(summaryText))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "SummaryComment: summaryText is required for operation 'add'.") };
            }

            var updated = operation == AddRemoveViewAction.add
                ? await _refactoringEngine.AddSummaryCommentAsync(filePathResolved, targetName, summaryText!, contextSnippet, lineBefore, lineAfter, containingTypeName)
                : await _refactoringEngine.RemoveSummaryCommentAsync(filePathResolved, targetName, contextSnippet, lineBefore, lineAfter, containingTypeName, cancellationToken);

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "SummaryComment", filePathResolved) is { } guardResult)
                return guardResult;

            var description = operation == AddRemoveViewAction.add
                ? $"Added XML summary comment to '{targetName}' in {Path.GetFileName(filePathResolved)}."
                : $"Removed XML summary comment from '{targetName}' in {Path.GetFileName(filePathResolved)}.";

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "SummaryComment", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff);

            // add: summaryText is caller-supplied verbatim, echoed back as the added content
            // (same reasoning as Member(add)'s raw-source path). remove has no new content to show.
            if (operation != AddRemoveViewAction.add)
            {
                return new ToolResult<object>() { Success = true, Data = summary };
            }

            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult { Summary = summary, ChangedContent = summaryText! },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SummaryComment failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SummaryComment") };
        }
    }
    [McpServerTool(Name = "ConstructorParameter")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view DI constructor parameters on a class. For classes with the same name in the same file, combine className with contextSnippet/lineBefore/lineAfter to disambiguate.")]
    public async Task<ToolResult<object>> ConstructorParameter(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: creates a private readonly field, parameter, and body assignment in one step; creates a constructor if none exists. remove: deletes the parameter and its assignment statement — the backing field is only deleted if a solution-wide reference check confirms nothing else in the class still uses it, otherwise it's left in place. view: lists current constructor parameters and their inferred backing fields; makes no changes.")]
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
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var (outcome, message, parameters) = await _refactoringEngine.GetConstructorParametersAsync(filePath: filePathResolved, className, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ConstructorParameter: {message}") };
                return new ToolResult<object>() { Success = true, Data = new { Parameters = parameters } };
            }

            if (string.IsNullOrEmpty(paramName))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"ConstructorParameter: paramName is required for operation '{operation}'.") };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(paramType))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "ConstructorParameter: paramType is required for operation 'add'.") };
            }

            DocumentEditResult updated;
            string resolvedFieldName;
            if (operation == AddRemoveViewAction.add)
            {
                updated = await _refactoringEngine.AddConstructorParameterAsync(filePathResolved, className, paramName, paramType!, fieldName, contextSnippet, lineBefore, lineAfter);
                // updated.Message carries "// paramName='x', fieldName='_x'" on success — surface the
                // resolved field name explicitly since it may differ from what the caller passed
                // (see fieldName/paramName collision disambiguation in AddConstructorParameterAsync).
                resolvedFieldName = updated.Message is { Length: > 0 } msg
                    && System.Text.RegularExpressions.Regex.Match(msg, "fieldName='([^']*)'") is { Success: true } m
                    ? m.Groups[1].Value
                    : $"_{char.ToLower(paramName[0])}{paramName[1..]}";
            }
            else
            {
                updated = await _refactoringEngine.RemoveConstructorParameterAsync(filePathResolved, className, paramName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                resolvedFieldName = updated.Message is { Length: > 0 } msg
                    && System.Text.RegularExpressions.Regex.Match(msg, "fieldName='([^']*)'") is { Success: true } m
                    ? m.Groups[1].Value
                    : "";
            }

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            if (RequireUpdatedText(updated, "ConstructorParameter", filePathResolved) is { } guardResult)
                return guardResult;

            var description = operation == AddRemoveViewAction.add
                ? $"Added '{paramType} {paramName}' DI parameter to '{className}' in {Path.GetFileName(filePathResolved)}, backed by field '{resolvedFieldName}'."
                : $"Removed '{paramName}' DI parameter from '{className}' in {Path.GetFileName(filePathResolved)}."
                    + (updated.Message?.Contains("fieldRemoved='True'") == true ? $" Also removed unused backing field '{resolvedFieldName}'." : "");

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "ConstructorParameter", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };

            // Added-parameter text is reconstructed here (paramType/paramName are already known)
            // rather than extracted from AddConstructorParameterAsync's internal formatting — same
            // reasoning as Member(add)'s typed-generation path. Remove has no new content to show.
            var changedContent = operation == AddRemoveViewAction.add ? $"{paramType} {paramName}" : "";
            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult
                {
                    Summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff),
                    ChangedContent = changedContent
                },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConstructorParameter failed for '{ClassName}' in '{FilePathWrapper}'", className, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ConstructorParameter") };
        }
    }
    [McpServerTool(Name = "MethodSignature")]
    [Produces(DataTag.ChangeId)]
    [Description("Add, remove, or view a method's parameters (general-purpose — not limited to constructors; see ConstructorParameter for DI-style constructor parameters with a backing field). For overloaded methods, combine methodName with contextSnippet/lineBefore/lineAfter to disambiguate.")]
    public async Task<ToolResult<object>> MethodSignature(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("add: appends a new parameter to the end of the parameter list. remove: only the LAST parameter can be removed (paramName must match it) — a deliberate restriction, since removing an earlier parameter would require reordering every call site's remaining positional arguments, which cannot always be done safely; call sites passing the removed argument positionally are updated automatically, but a call site using named arguments (or one that can't be safely re-parsed) causes the whole operation to be refused with no changes made. view: lists current parameters (name, type, default value); makes no changes.")]
        [Consumes(DataTag.Action, required: true)] AddRemoveViewAction operation,
        [Consumes(DataTag.MethodName, required: true)] string methodName,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add/remove, unused for operation=view.
        [Description("Required for add/remove. Not used for view.")]
        [Consumes(DataTag.SymbolName, required: false)] string? paramName = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for operation=add, unused for remove/view.
        [Description("Required for add. Not used for remove/view.")]
        [Consumes(DataTag.DataType, required: false)] string? paramType = null,
        [Description("add only. Optional literal or expression for the new parameter's default value (e.g. \"3\", \"\\\"foo\\\"\") — omit for a required parameter. Do NOT pass the literal string \"null\" here to get a null default — use nullDefault:true instead (some MCP clients corrupt the string \"null\" in transit, silently producing a required parameter instead of one defaulted to null). Mutually exclusive with nullDefault.")]
        [ExternalInputRequired(DataTag.Initializer, required: false)] string? defaultValue = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default,
        [Description("add only. Sets the new parameter's default to the null literal directly, bypassing defaultValue entirely — use this instead of defaultValue:\"null\". Mutually exclusive with defaultValue.")] bool nullDefault = false)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (operation == AddRemoveViewAction.view)
            {
                var (outcome, message, parameters) = await _refactoringEngine.GetMethodParametersAsync(filePathResolved, methodName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (outcome is EditOutcome.DocumentNotFound or EditOutcome.CannotEdit or EditOutcome.TargetNotFound)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"MethodSignature: {message}") };
                return new ToolResult<object>() { Success = true, Data = new { Parameters = parameters } };
            }

            if (string.IsNullOrEmpty(paramName))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"MethodSignature: paramName is required for operation '{operation}'.") };
            }

            if (operation == AddRemoveViewAction.add && string.IsNullOrEmpty(paramType))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "MethodSignature: paramType is required for operation 'add'.") };
            }

            if (nullDefault && defaultValue != null)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "MethodSignature: nullDefault and defaultValue are mutually exclusive — pass only one.") };
            }

            if (nullDefault && operation != AddRemoveViewAction.add)
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"MethodSignature: nullDefault is only valid for operation 'add', not '{operation}'.") };
            }

            DocumentEditResult updated;
            Dictionary<FilePathWrapper, string> changes;
            if (operation == AddRemoveViewAction.add)
            {
                updated = await _refactoringEngine.AddMethodParameterAsync(filePathResolved, methodName, paramName, paramType!, defaultValue, contextSnippet, lineBefore, lineAfter, cancellationToken, nullDefault);
                if (RequireUpdatedText(updated, "MethodSignature", filePathResolved) is { } addGuardResult)
                    return addGuardResult;
                changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            }
            else
            {
                updated = await _refactoringEngine.RemoveMethodParameterAsync(filePathResolved, methodName, paramName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (updated.Outcome == EditOutcome.CannotRemove)
                {
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"MethodSignature: {updated.Message}") };
                }

                if (RequireUpdatedText(updated, "MethodSignature", filePathResolved) is { } removeGuardResult)
                    return removeGuardResult;
                changes = updated.Changes;
            }

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }

            var description = operation == AddRemoveViewAction.add
                ? $"Added parameter '{paramType} {paramName}{(nullDefault ? " = null" : defaultValue != null ? $" = {defaultValue}" : "")}' to '{methodName}' in {Path.GetFileName(filePathResolved)}."
                : $"Removed parameter '{paramName}' from '{methodName}' in {Path.GetFileName(filePathResolved)}, updating {changes.Count - 1} call site(s).";
            var apply = await ValidateAndApplyAsync(changes, description, "MethodSignature", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };

            var changedContent = operation == AddRemoveViewAction.add ? $"{paramType} {paramName}" : "";
            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult
                {
                    Summary = new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), description, apply.DryRun, apply.Diff),
                    ChangedContent = changedContent
                },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MethodSignature failed for '{MethodName}' in '{FilePathWrapper}'", methodName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MethodSignature") };
        }
    }

    [McpServerTool(Name = "ExtractLocalVariable")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts an inline expression into a named local variable declaration. exactExpressionText is NOT a search fragment (unlike contextSnippet on other tools) — it must be the WHOLE expression to extract, copied verbatim.")]
    public async Task<ToolResult<object>> ExtractLocalVariable(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("The exact expression to extract, copied VERBATIM character-for-character from a prior ReadFile/GetMethodSource result — the whole expression, not a shortened/unique fragment. This is NOT a search anchor like contextSnippet on other tools: it must match the target expression's full text exactly (whitespace differences are tolerated, but the expression itself must be complete). A partial expression may still resolve to the nearest enclosing expression rather than the one you intended, silently extracting the wrong span — if in doubt, include the whole expression, not less.")]
        [Consumes(DataTag.ContextSnippet, required: true)] string exactExpressionText,
        [Consumes(DataTag.SymbolName)] string variableName,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _refactoringEngine.ExtractLocalVariableAsync(filePathResolved, exactExpressionText, variableName, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                string errorReason = result.Outcome switch
                {
                    EditOutcome.DocumentNotFound => $"ExtractLocalVariable: document '{filePathResolved}' not found in the workspace.",
                    EditOutcome.SourceInvalid => $"ExtractLocalVariable: exactExpressionText not found in '{filePathResolved}'. {result.Message}",
                    EditOutcome.CannotConvert => $"ExtractLocalVariable: could not extract '{variableName}' in '{filePathResolved}'. {result.Message}",
                    _ => $"ExtractLocalVariable: no change produced for '{variableName}' in '{filePathResolved}' ({result.Outcome}). {result.Message}"
                };
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, errorReason) };
            }

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Extract local variable '{variableName}'.", "ExtractLocalVariable", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // Not wired into MemberChangedContentResult: unlike Member/ConstructorParameter, the new
            // declaration text isn't caller-supplied or separately exposed — DocumentEditResult only
            // returns the whole-file UpdatedText, so reconstructing just the new "var x = ..." line
            // here would mean duplicating ExtractLocalVariableAsync's formatting logic. Revisit only
            // if that engine method is changed to return the new declaration text alongside UpdatedText.
            return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Extracted '{variableName}' as a local variable in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractLocalVariable failed for '{VariableName}' in '{FilePathWrapper}'", variableName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ExtractLocalVariable") };
        }
    }

    [McpServerTool(Name = "ExtractMethodSafe")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts selected statements into a new method with the correct return type inferred from the selection. newMethodName must be a valid C# identifier. exactSourceBlock is NOT a search fragment (unlike contextSnippet on other tools) — the entire range you want extracted must appear in it verbatim, since its matched span IS the extraction boundary; a too-short excerpt silently extracts only that narrower range, not the whole intended block. Written to disk (or staged, per autoStage) like other refactoring tools — not preview-only. Returns changeId.")]
    // Fixes MS BUG: where selections ending with "return <expression>" are extracted into a method declared "private void MethodName(...)", causing a compile error. This tool uses Roslyn's SemanticModel to determine the actual type of the returned expression, and DataFlowAnalysis to find the correct parameter list. Requires a loaded solution (via set_solution_path or equivalent).
    public async Task<ToolResult<object>> ExtractMethodSafe(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [ExternalInputRequired(DataTag.MethodName, required: true)] string newMethodName,
        [Description("The exact statements to extract, copied VERBATIM character-for-character from a prior ReadFile/GetMethodSource result — not retyped from memory, not a shortened/unique fragment. This is NOT a search anchor like contextSnippet on other tools: the whole extracted range (every statement, including blank lines/comments within it, exactly as they appear in the file) must be present here, because the matched span directly becomes the extraction boundary. Passing only part of the intended range (e.g. just the first statement) will silently extract only that part, stranding the rest — some ambiguous narrow selections are refused with an error, but do not rely on that guard catching every case; when in doubt, include more of the surrounding block, not less.")]
        [Consumes(DataTag.ContextSnippet, required: true)] string exactSourceBlock,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("ExtractMethodSafe: {File} method={Name}", filePathResolved, newMethodName);
        }
        try
        {
            var result = await _msToolAugmentEngine.ExtractMethodSafeAsync(
                filePathResolved, newMethodName, exactSourceBlock, lineBefore, lineAfter, cancellationToken: cancellationToken);

            if (!result.Success)
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"ExtractMethodSafe: {result.Error}")
                };
            }

            if (!autoStage)
            {
                return new ToolResult<object> { Success = true, Data = result };
            }

            if (string.IsNullOrEmpty(result.UpdatedContent))
            {
                return new ToolResult<object>
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.Exception, $"ExtractMethodSafe: no change produced for '{filePathResolved}'.")
                };
            }

            // Not wired into MemberChangedContentResult: the extracted method's body isn't caller-
            // supplied or separately exposed — ExtractMethodSafeAsync's result only carries the
            // whole-file UpdatedContent, so showing just the new method here would mean duplicating
            // its formatting/signature-inference logic. Revisit only if that engine method starts
            // returning the extracted method's text alongside UpdatedContent.
            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedContent };
            var apply = await ValidateAndApplyAsync(changes, $"Extract '{newMethodName}' from '{filePathResolved}'.", "ExtractMethodSafe", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            return new ToolResult<object>
            {
                Success = true,
                Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Extracted '{newMethodName}' into a new method in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractMethodSafe failed for '{NewMethodName}' in '{FilePathWrapper}'", newMethodName, filePathResolved);
            return new ToolResult<object>
            {
                Success = false,
                Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ExtractMethodSafe")
            };
        }
    }
    [McpServerTool(Name = "ModifyAttribute")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds, replaces, or removes an [Attribute] on a type or member. Use ChangeAccessibility for accessibility keywords and ModifyModifier for other modifier keywords, not this tool.")]
    public async Task<ToolResult<object>> ModifyAttribute(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("For overloaded/duplicate-named targets, combine with contextSnippet/lineBefore/lineAfter to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [Description("The attribute to add/replace/remove. May include or omit the surrounding [ ] brackets.")]
        [ExternalInputRequired(DataTag.AttributeName, required: true)] string existingAttribute,
        [Consumes(DataTag.Action, required: true)] AttributeModifyAction action,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: required for action=replace, unused for add/remove.
        [Description("Required for action=replace — the attribute to replace existingAttribute with. Not used for add/remove.")]
        [ExternalInputRequired(DataTag.AttributeName, required: false)] string? newAttribute = null,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (action == AttributeModifyAction.replace && string.IsNullOrEmpty(newAttribute))
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyAttribute: newAttribute is required for action 'replace'.") };
            }

            DocumentEditResult updated;
            if (action == AttributeModifyAction.add)
            {
                updated = await _refactoringEngine.AddAttributeAsync(filePathResolved, targetName, existingAttribute, contextSnippet, lineBefore, lineAfter);
            }
            else if (action == AttributeModifyAction.replace)
            {
                updated = await _refactoringEngine.ReplaceAttributeAsync(filePathResolved, targetName, existingAttribute, newAttribute!, contextSnippet, lineBefore, lineAfter);
            }
            else if (action == AttributeModifyAction.remove)
            {
                updated = await _refactoringEngine.RemoveAttributeAsync(filePathResolved, targetName, existingAttribute, contextSnippet, lineBefore, lineAfter);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RequireUpdatedText(updated, "ModifyAttribute", filePathResolved) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} attribute '{existingAttribute}' on '{targetName}'.", "ModifyAttribute", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"{(action == AttributeModifyAction.add ? "Added" : action == AttributeModifyAction.replace ? "Replaced" : "Removed")} '{existingAttribute}' attribute on '{targetName}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff);

            // add/replace: existingAttribute (add) or newAttribute (replace) already holds the
            // exact attribute source the caller composed — echoed back verbatim, same reasoning
            // as Member(add)'s raw-source path. remove has no new content to show.
            if (action == AttributeModifyAction.remove)
            {
                return new ToolResult<object>() { Success = true, Data = summary };
            }

            var changedAttribute = action == AttributeModifyAction.add ? existingAttribute : newAttribute;
            return await ToolResult<object>.ForPossiblyLargeDataAsync(
                new MemberChangedContentResult { Summary = summary, ChangedContent = changedAttribute ?? "" },
                _workspaceManager.GetSolutionRoot(), "MemberChangedContent", ResultWrapperType.MemberChangedContent,
                workspaceVersion: _workspaceManager.WorkspaceVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyAttribute failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyAttribute") };
        }
    }

    [McpServerTool(Name = "ModifyModifier")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a non-accessibility modifier keyword. Action: add or remove. For overloaded targets, provide contextSnippet (distinctive substring) and optionally lineBefore/lineAfter to disambiguate. Does NOT cover accessibility (private/public/etc.) — use ChangeAccessibility for those, or ModifyAttribute for [Attribute] syntax. Returns changeId.")]
    public async Task<ToolResult<object>> ModifyModifier(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [ExternalInputRequired(DataTag.Modifier, required: true)] NonAccessibilityModifier modifier,
        [Consumes(DataTag.Action, required: true)] AddRemoveAction action,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        var modifierText = modifier.ToString();
        try
        {
            DocumentEditResult updated;
            if (action == AddRemoveAction.add)
            {
                updated = await _refactoringEngine.AddModifierAsync(filePathResolved, targetName, modifierText, contextSnippet, lineBefore, lineAfter);
            }
            else if (action == AddRemoveAction.remove)
            {
                updated = await _refactoringEngine.RemoveModifierAsync(filePathResolved, targetName, modifierText, contextSnippet, lineBefore, lineAfter);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RequireUpdatedText(updated, "ModifyModifier", filePathResolved) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} '{modifierText}' modifier on '{targetName}'.", "ModifyModifier", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent: the only "new" text is the single modifier keyword the caller
            // already passed in — same reasoning as ChangeAccessibility.
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"{(action == AddRemoveAction.add ? "Added" : "Removed")} '{modifierText}' modifier on '{targetName}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyModifier failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyModifier") };
        }
    }
    [McpServerTool(Name = "ModifyBaseType")]
    [Produces(DataTag.ChangeId)]
    [Description("Adds or removes a base type or interface from a type declaration.")]
    public async Task<ToolResult<object>> ModifyBaseType(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("For types with the same name in the same file, combine with contextSnippet/lineBefore/lineAfter to disambiguate.")]
        [Consumes(DataTag.SymbolName, required: true)] string typeName,
        [Description("The base type or interface name to add or remove.")] string baseTypeName,
        [Description("add or remove.")] AddRemoveAction action,
        [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == AddRemoveAction.add)
            {
                updated = await _refactoringEngine.AddBaseTypeAsync(filePathResolved, typeName, baseTypeName, contextSnippet, lineBefore, lineAfter);
            }
            else if (action == AddRemoveAction.remove)
            {
                updated = await _refactoringEngine.RemoveBaseTypeAsync(filePathResolved, typeName, baseTypeName, contextSnippet, lineBefore, lineAfter);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RequireUpdatedText(updated, "ModifyBaseType", filePathResolved) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} base type '{baseTypeName}' on '{typeName}'.", "ModifyBaseType", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent: the only "new" text is the base type name the caller already
            // passed in — same reasoning as ChangeAccessibility/ModifyModifier.
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"{(action == AddRemoveAction.add ? "Added" : "Removed")} '{baseTypeName}' on '{typeName}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyBaseType failed for '{TypeName}' in '{FilePathWrapper}'", typeName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyBaseType") };
        }
    }

    [McpServerTool(Name = "SyncTypeAndFilename")]
    [Produces(DataTag.ResultOnly)]
    [Description("Synchronizes the filename to match the primary type declared in the file.")]
    public async Task<ToolResult<object>> SyncTypeAndFilename(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePathResolved, cancellationToken);
            if (result.Outcome != EditOutcome.Modified || result.Changes.Count == 0)
            {
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename: no change produced for '{filePathResolved}' ({result.Outcome}). {result.Message}") };
            }

            var (newPath, content) = result.Changes.First();
            if (File.Exists(newPath))
            {
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename: target file '{newPath}' already exists — refusing to overwrite.") };
            }

            var changes = new Dictionary<FilePathWrapper, string> { [newPath] = content };
            var apply = await ValidateAndApplyAsync(changes, result.Message ?? $"Rename '{Path.GetFileName(filePathResolved)}' to '{Path.GetFileName(newPath)}'.", "SyncTypeAndFilename", dryRun, returnDiff, removePaths: [filePathResolved], cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };

            // dryRun: ValidateAndApplyAsync never wrote newPath, so deleting filePath here would
            // destroy the original with nothing on disk to replace it. Report the preview as-is.
            if (apply.DryRun)
            {
                return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved, newPath], $"[DryRun] Would rename '{Path.GetFileName(filePathResolved)}' to '{Path.GetFileName(newPath)}'.", apply.DryRun, apply.Diff) };
            }

            // Only remove the old file after the new one is validated and written, so the
            // two never coexist as a validated on-disk duplicate of the same type.
            try
            {
                await FileIoHelper.DeleteAsync(filePathResolved, cancellationToken);
            }
            catch (Exception ex)
            {
                // Deliberately not routed through ToolErrorMapper (unlike other catches in this
                // file): this is a partial-success condition (new file written and validated, only
                // the old-file delete failed), not a plain failure, and the mapper's generic
                // "failed unexpectedly" wording would drop the actionable remediation advice below.
                _logger.LogError(ex, "SyncTypeAndFilename wrote '{NewPath}' but failed to delete old file '{OldPath}'", newPath, filePathResolved);
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename wrote '{Path.GetFileName(newPath)}' but failed to delete the old file '{filePathResolved}': {ex.Message}. Delete it manually to avoid a duplicate-type compile error.") };
            }

            // The old file is gone from disk, but ApplyProposedChangesAsync only ever added the
            // new Document — it has no reason to know the old one should be dropped too. Without
            // this, the old Document stays tracked and the type it declares now exists twice in
            // the compilation, corrupting symbol resolution for every subsequent call.
            await _workspaceManager.RemoveDocumentByPathAsync(filePathResolved, cancellationToken);

            // No ChangedContent: this only moves a file to a new name — the file's content is
            // byte-for-byte unchanged, so there is no new text to show beyond the summary.
            return new ToolResult<object> { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved, newPath], $"Renamed '{Path.GetFileName(filePathResolved)}' to '{Path.GetFileName(newPath)}'.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncTypeAndFilename unexpected exception for '{FilePathWrapper}'", filePathResolved);
            return new ToolResult<object> { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"SyncTypeAndFilename for '{filePathResolved}'") };
        }
    }
}