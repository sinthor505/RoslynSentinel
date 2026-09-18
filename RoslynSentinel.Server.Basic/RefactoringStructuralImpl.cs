using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

// Decision 7 step 3 (plan_split_workspace_refactoring_tools_for_di.md): implementation half of the
// RefactoringStructuralTools/Impl pair. Method bodies below are moved verbatim from
// SentinelRefactoringTools.cs (Member, ModifyEnum, ModifyAttribute, ModifyModifier, ModifyBaseType,
// SyncTypeAndFilename) plus their 3 private batch helpers (ModifyModifierBatch, ModifyAttributeBatch,
// ModifyBaseTypeBatch), each called from exactly one of the public methods above. ValidateAndApplyAsync
// is duplicated per-Impl-class per Decision 1-Amendment.
public class RefactoringStructuralImpl
{
    private readonly RefactoringEngine _refactoringEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly ILogger _logger;

    // Reuses ReplaceSnippet's batch cap (SentinelWorkspaceTools.MaxSnippetEditsPerBatch) as a starting
    // value -> these are keyword/name edits rather than text blocks, so the limit is purely count-based,
    // not size-derived.
    private const int MaxModifierFamilyEditsPerBatch = 20;

    public RefactoringStructuralImpl(
        RefactoringEngine refactoringEngine,
        StructuralRefinementEngine structuralRefinementEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        ILogger logger)
    {
        _refactoringEngine = refactoringEngine;
        _structuralRefinementEngine = structuralRefinementEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _logger = logger;
    }

    /// <summary>
    /// Validates proposed changes against the current in-memory solution and, unless
    /// <paramref name="dryRun"/> is set, writes them straight to disk (write-through -> no
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


    // Added by InsertMemberAfter (expected - used for diagnostics)
    private static string DescribeMemberOutcome(DocumentEditResult updated, string fallbackLabel)
    {
        // The engine's Message is set to e.g. "// Added FieldDeclaration '_foo'." on success - strip
        // the leading comment marker and trailing period so it composes into the tool-layer sentence.
        var message = updated.Message?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            return fallbackLabel;
        }

        var trimmed = message.TrimStart('/', ' ').TrimEnd('.');
        return trimmed.Length > 0 ? trimmed : fallbackLabel;
    }


    private async Task<ToolResult<object>> ModifyModifierBatch(List<ModifierEdit> edits, bool dryRun, bool returnDiff, CancellationToken cancellationToken)
    {
        if (edits.Count > MaxModifierFamilyEditsPerBatch)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    $"ModifyModifier: edits has {edits.Count} entries (limit {MaxModifierFamilyEditsPerBatch}). Split into multiple calls.")
            };
        }

        var perEditErrors = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            if (string.IsNullOrEmpty(edits[i].FilePath))
            {
                perEditErrors.Add($"edits[{i}]: filePath is required.");
            }
            if (string.IsNullOrEmpty(edits[i].TargetName))
            {
                perEditErrors.Add($"edits[{i}] ({edits[i].FilePath}): targetName is required.");
            }
        }

        if (perEditErrors.Count > 0)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyModifier batch rejected before resolving targets:\n" + string.Join("\n", perEditErrors))
            };
        }

        var editsByFile = edits
            .Select((edit, index) => (edit, index))
            .GroupBy(pair => _workspaceManager.SetFilePath(pair.edit.FilePath));

        var finalContents = new Dictionary<FilePathWrapper, string>();
        var touchedFiles = new List<FilePathWrapper>();
        foreach (var fileGroup in editsByFile)
        {
            var filePathResolved = fileGroup.Key;
            if (!filePathResolved.Validated)
            {
                perEditErrors.Add($"'{fileGroup.Key}': {(filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded ? "no solution is loaded." : "path could not be resolved.")}");
                continue;
            }

            var engineEdits = fileGroup.Select(pair => (
                pair.index,
                pair.edit.TargetName,
                pair.edit.Modifier.ToString(),
                pair.edit.Action,
                pair.edit.ContextSnippet,
                pair.edit.LineBefore,
                pair.edit.LineAfter)).ToList();

            var updated = await _refactoringEngine.ApplyModifierBatchAsync(filePathResolved, engineEdits, cancellationToken);
            if (updated.Outcome != EditOutcome.Modified || string.IsNullOrEmpty(updated.UpdatedText))
            {
                perEditErrors.Add(updated.Message ?? $"'{filePathResolved}': batch failed.");
                continue;
            }

            finalContents[filePathResolved] = updated.UpdatedText;
            touchedFiles.Add(filePathResolved);
        }

        if (perEditErrors.Count > 0)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyModifier batch rejected - no changes were written:\n" + string.Join("\n", perEditErrors))
            };
        }

        var apply = await ValidateAndApplyAsync(finalContents, $"Batch-modified {edits.Count} modifier edit(s) across {touchedFiles.Count} file(s).", "ModifyModifier", dryRun, returnDiff, cancellationToken: cancellationToken);
        if (apply.Error is not null)
            return new ToolResult<object> { Success = false, Error = apply.Error };

        var summary = new AppliedChangeSummary(apply.ChangeId, touchedFiles, $"Applied {edits.Count} modifier edit(s) across {touchedFiles.Count} file(s).", apply.DryRun, apply.Diff);
        return new ToolResult<object>() { Success = true, Data = summary };
    }

    private async Task<ToolResult<object>> ModifyAttributeBatch(List<AttributeEdit> edits, bool dryRun, bool returnDiff, CancellationToken cancellationToken)
    {
        if (edits.Count > MaxModifierFamilyEditsPerBatch)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    $"ModifyAttribute: edits has {edits.Count} entries (limit {MaxModifierFamilyEditsPerBatch}). Split into multiple calls.")
            };
        }

        var perEditErrors = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            if (string.IsNullOrEmpty(edits[i].FilePath))
            {
                perEditErrors.Add($"edits[{i}]: filePath is required.");
            }
            if (string.IsNullOrEmpty(edits[i].TargetName))
            {
                perEditErrors.Add($"edits[{i}] ({edits[i].FilePath}): targetName is required.");
            }
            if (string.IsNullOrEmpty(edits[i].ExistingAttribute))
            {
                perEditErrors.Add($"edits[{i}] ({edits[i].FilePath}): existingAttribute is required.");
            }
            if (edits[i].Action == AttributeModifyAction.replace && string.IsNullOrEmpty(edits[i].NewAttribute))
            {
                perEditErrors.Add($"edits[{i}] ({edits[i].FilePath}): newAttribute is required for action 'replace'.");
            }
        }

        if (perEditErrors.Count > 0)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyAttribute batch rejected before resolving targets:\n" + string.Join("\n", perEditErrors))
            };
        }

        var editsByFile = edits
            .Select((edit, index) => (edit, index))
            .GroupBy(pair => _workspaceManager.SetFilePath(pair.edit.FilePath));

        var finalContents = new Dictionary<FilePathWrapper, string>();
        var touchedFiles = new List<FilePathWrapper>();
        foreach (var fileGroup in editsByFile)
        {
            var filePathResolved = fileGroup.Key;
            if (!filePathResolved.Validated)
            {
                perEditErrors.Add($"'{fileGroup.Key}': {(filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded ? "no solution is loaded." : "path could not be resolved.")}");
                continue;
            }

            var engineEdits = fileGroup.Select(pair => (
                pair.index,
                pair.edit.TargetName,
                pair.edit.ExistingAttribute,
                pair.edit.Action,
                pair.edit.NewAttribute,
                pair.edit.ContextSnippet,
                pair.edit.LineBefore,
                pair.edit.LineAfter)).ToList();

            var updated = await _refactoringEngine.ApplyAttributeBatchAsync(filePathResolved, engineEdits, cancellationToken);
            if (updated.Outcome != EditOutcome.Modified || string.IsNullOrEmpty(updated.UpdatedText))
            {
                perEditErrors.Add(updated.Message ?? $"'{filePathResolved}': batch failed.");
                continue;
            }

            finalContents[filePathResolved] = updated.UpdatedText;
            touchedFiles.Add(filePathResolved);
        }

        if (perEditErrors.Count > 0)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyAttribute batch rejected - no changes were written:\n" + string.Join("\n", perEditErrors))
            };
        }

        var apply = await ValidateAndApplyAsync(finalContents, $"Batch-modified {edits.Count} attribute edit(s) across {touchedFiles.Count} file(s).", "ModifyAttribute", dryRun, returnDiff, cancellationToken: cancellationToken);
        if (apply.Error is not null)
            return new ToolResult<object> { Success = false, Error = apply.Error };

        var summary = new AppliedChangeSummary(apply.ChangeId, touchedFiles, $"Applied {edits.Count} attribute edit(s) across {touchedFiles.Count} file(s).", apply.DryRun, apply.Diff);
        return new ToolResult<object>() { Success = true, Data = summary };
    }

    private async Task<ToolResult<object>> ModifyBaseTypeBatch(List<BaseTypeEdit> edits, bool dryRun, bool returnDiff, CancellationToken cancellationToken)
    {
        if (edits.Count > MaxModifierFamilyEditsPerBatch)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    $"ModifyBaseType: edits has {edits.Count} entries (limit {MaxModifierFamilyEditsPerBatch}). Split into multiple calls.")
            };
        }

        var perEditErrors = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            if (string.IsNullOrEmpty(edits[i].FilePath))
            {
                perEditErrors.Add($"edits[{i}]: filePath is required.");
            }
            if (string.IsNullOrEmpty(edits[i].TypeName))
            {
                perEditErrors.Add($"edits[{i}] ({edits[i].FilePath}): typeName is required.");
            }
            if (string.IsNullOrEmpty(edits[i].BaseTypeName))
            {
                perEditErrors.Add($"edits[{i}] ({edits[i].FilePath}): baseTypeName is required.");
            }
        }

        if (perEditErrors.Count > 0)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyBaseType batch rejected before resolving targets:\n" + string.Join("\n", perEditErrors))
            };
        }

        var editsByFile = edits
            .Select((edit, index) => (edit, index))
            .GroupBy(pair => _workspaceManager.SetFilePath(pair.edit.FilePath));

        var finalContents = new Dictionary<FilePathWrapper, string>();
        var touchedFiles = new List<FilePathWrapper>();
        foreach (var fileGroup in editsByFile)
        {
            var filePathResolved = fileGroup.Key;
            if (!filePathResolved.Validated)
            {
                perEditErrors.Add($"'{fileGroup.Key}': {(filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded ? "no solution is loaded." : "path could not be resolved.")}");
                continue;
            }

            var engineEdits = fileGroup.Select(pair => (
                pair.index,
                pair.edit.TypeName,
                pair.edit.BaseTypeName,
                pair.edit.Action,
                pair.edit.ContextSnippet,
                pair.edit.LineBefore,
                pair.edit.LineAfter)).ToList();

            var updated = await _refactoringEngine.ApplyBaseTypeBatchAsync(filePathResolved, engineEdits, cancellationToken);
            if (updated.Outcome != EditOutcome.Modified || string.IsNullOrEmpty(updated.UpdatedText))
            {
                perEditErrors.Add(updated.Message ?? $"'{filePathResolved}': batch failed.");
                continue;
            }

            finalContents[filePathResolved] = updated.UpdatedText;
            touchedFiles.Add(filePathResolved);
        }

        if (perEditErrors.Count > 0)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyBaseType batch rejected - no changes were written:\n" + string.Join("\n", perEditErrors))
            };
        }

        var apply = await ValidateAndApplyAsync(finalContents, $"Batch-modified {edits.Count} base type edit(s) across {touchedFiles.Count} file(s).", "ModifyBaseType", dryRun, returnDiff, cancellationToken: cancellationToken);
        if (apply.Error is not null)
            return new ToolResult<object> { Success = false, Error = apply.Error };

        var summary = new AppliedChangeSummary(apply.ChangeId, touchedFiles, $"Applied {edits.Count} base type edit(s) across {touchedFiles.Count} file(s).", apply.DryRun, apply.Diff);
        return new ToolResult<object>() { Success = true, Data = summary };
    }

    public async Task<ToolResult<object>> Member(
        ToolCallReason reason,
        FilePathWrapper filepath,
        MemberAction operation,
        string? containerName = null,
        string? namespaceName = null,
        string? memberName = null,
        string? newMemberSource = null,
        string? position = null,
        TypedMemberKind? typedKind = null,
        string? typedName = null,
        string? typedType = null,
        string accessibility = "public",
        bool hasSetter = true,
        bool isInit = false,
        bool isReadonly = false,
        bool isStatic = false,
        string? initializer = null,
        bool skipPrecheck = false,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
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
                    return new ToolResult<object> { Success = false, Error = new ResultError(RefactoringToolHelpers.ErrorCodeFor(result.Outcome), errorReason) };
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
                                $"Member: '{memberName}' has {string.Join(" and ", parts)} - refusing to remove. " +
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

            // operation is addMember, addTopLevelType, or addTypedMember below.
            static string RequiredParamsHint(MemberAction op) => op switch
            {
                MemberAction.addMember => "Required params for addMember: containerName, newMemberSource. Optional: position.",
                MemberAction.addTopLevelType => "Required params for addTopLevelType: newMemberSource (the full type declaration). Optional: namespaceName.",
                MemberAction.addTypedMember => "Required params for addTypedMember: containerName, typedKind, typedName, typedType. Optional: accessibility, hasSetter/isInit (typedKind=property), isReadonly/isStatic/initializer (typedKind=field).",
                _ => throw new ArgumentOutOfRangeException(nameof(op))
            };

            static string? IgnoredNonDefaultTypedOnlyParamsNote(MemberAction op, string accessibility, bool hasSetter, bool isInit, bool isReadonly, bool isStatic, string? initializer)
            {
                if (op == MemberAction.addTypedMember)
                    return null;

                var ignored = new List<string>();
                if (accessibility != "public") ignored.Add($"accessibility={accessibility}");
                if (!hasSetter) ignored.Add($"hasSetter={hasSetter}");
                if (isInit) ignored.Add($"isInit={isInit}");
                if (isReadonly) ignored.Add($"isReadonly={isReadonly}");
                if (isStatic) ignored.Add($"isStatic={isStatic}");
                if (initializer != null) ignored.Add($"initializer=\"{initializer}\"");

                return ignored.Count == 0
                    ? null
                    : $"informational: {string.Join(", ", ignored)} was supplied but is not used for {op}.";
            }

            if (operation == MemberAction.addTopLevelType)
            {
                if (containerName != null)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: containerName is not used for operation 'addTopLevelType' (there is no container - the type is added at the top level). {RequiredParamsHint(operation)} If you meant to add a member to an existing container, use operation 'addMember' instead.") };
                if (typedKind != null || typedName != null || typedType != null)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: typedKind/typedName/typedType are not used for operation 'addTopLevelType'. {RequiredParamsHint(operation)}") };
                if (position != null)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: position is not used for operation 'addTopLevelType' (the type is always appended). {RequiredParamsHint(operation)}") };
                if (string.IsNullOrEmpty(newMemberSource))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: newMemberSource is required for operation 'addTopLevelType'. {RequiredParamsHint(operation)}") };

                var topLevelResult = await _refactoringEngine.AddTopLevelTypeAsync(filePathResolved, newMemberSource, namespaceName, cancellationToken);
                if (!autoStage)
                    return new ToolResult<object>() { Success = true, Data = topLevelResult.ToJsonSummary() };
                if (RefactoringToolHelpers.RequireUpdatedText(topLevelResult, "Member", filePathResolved) is { } topLevelGuardResult)
                    return topLevelGuardResult;

                var topLevelDescription = $"Added new top-level type to {Path.GetFileName(filePathResolved)}.";
                var topLevelNote = IgnoredNonDefaultTypedOnlyParamsNote(operation, accessibility, hasSetter, isInit, isReadonly, isStatic, initializer);
                if (topLevelNote != null)
                    topLevelDescription += " " + topLevelNote;

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

            if (operation == MemberAction.addMember)
            {
                if (string.IsNullOrEmpty(containerName))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: containerName is required for operation 'addMember'. {RequiredParamsHint(operation)}") };
                if (string.IsNullOrEmpty(newMemberSource))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: newMemberSource is required for operation 'addMember'. {RequiredParamsHint(operation)}") };
                if (typedKind != null || typedName != null || typedType != null)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: typedKind/typedName/typedType are not used for operation 'addMember' - use addTypedMember instead. {RequiredParamsHint(operation)}") };
            }
            else // operation == MemberAction.addTypedMember
            {
                if (string.IsNullOrEmpty(containerName))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: containerName is required for operation 'addTypedMember'. {RequiredParamsHint(operation)}") };
                if (typedKind == null)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: typedKind is required for operation 'addTypedMember'. {RequiredParamsHint(operation)}") };
                if (string.IsNullOrEmpty(typedName) || string.IsNullOrEmpty(typedType))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: typedName and typedType are required for operation 'addTypedMember'. {RequiredParamsHint(operation)}") };
                if (!string.IsNullOrEmpty(newMemberSource))
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: newMemberSource is not used for operation 'addTypedMember' - use typedKind/typedName/typedType instead. {RequiredParamsHint(operation)}") };
                if (position != null)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: position is not used for operation 'addTypedMember' (generated members are always appended). {RequiredParamsHint(operation)}") };
            }

            var hasTypedSpec = operation == MemberAction.addTypedMember;

            if (await _refactoringEngine.IsEnumContainerAsync(filePathResolved, containerName, contextSnippet, lineBefore, lineAfter, cancellationToken))
            {
                if (hasTypedSpec)
                    return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Member: '{containerName}' is an enum - typedKind (property/field generation) doesn't apply. Pass newMemberSource as a bare member token ('Name' or 'Name=IntValue') instead via addMember, or retry using ModifyEnum(enumName: \"{containerName}\", values: ...) directly.") };

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
                var enumAddNote = IgnoredNonDefaultTypedOnlyParamsNote(operation, accessibility, hasSetter, isInit, isReadonly, isStatic, initializer);
                if (enumAddNote != null)
                    enumAddDescription += " " + enumAddNote;
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
                description = $"{DescribeMemberOutcome(updated, "Added new member")} to '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else if (position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
            {
                var afterMemberName = position.Substring("after:".Length);
                updated = await _refactoringEngine.InsertMemberAfterAsync(filePathResolved, containerName, afterMemberName, newMemberSource!, contextSnippet, lineBefore, lineAfter);
                description = $"{DescribeMemberOutcome(updated, "Inserted new member")} after '{afterMemberName}' in '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else if (position.StartsWith("before:", StringComparison.OrdinalIgnoreCase))
            {
                var beforeMemberName = position.Substring("before:".Length);
                updated = await _refactoringEngine.InsertMemberBeforeAsync(filePathResolved, containerName, beforeMemberName, newMemberSource!, contextSnippet, lineBefore, lineAfter);
                description = $"{DescribeMemberOutcome(updated, "Inserted new member")} before '{beforeMemberName}' in '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.") };
            }

            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RefactoringToolHelpers.RequireUpdatedText(updated, "Member", filePathResolved) is { } guardResult)
                return guardResult;

            var addNote = IgnoredNonDefaultTypedOnlyParamsNote(operation, accessibility, hasSetter, isInit, isReadonly, isStatic, initializer);
            if (addNote != null)
                description += " " + addNote;

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

    public async Task<ToolResult<object>> ModifyEnum(
        ToolCallReason reason,
        FilePathWrapper filepath,
        string enumName,
        string values,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
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

            if (RefactoringToolHelpers.RequireUpdatedText(updated, "ModifyEnum", filePathResolved) is { } guardResult)
                return guardResult;

            var description = string.IsNullOrEmpty(updated.Message)
                ? $"Sets '{enumName}' members in {Path.GetFileName(filePathResolved)} to match the requested list."
                : $"'{enumName}' in {Path.GetFileName(filePathResolved)}: {updated.Message}.";

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "ModifyEnum", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent: the new member list is just the caller-supplied `values` string
            // already passed in verbatim -> same reasoning as ChangeAccessibility/ModifyModifier.
            return new ToolResult<object>() { Success = true, Data = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyEnum failed for '{EnumName}' in '{FilePathWrapper}'", enumName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyEnum") };
        }
    }

    public async Task<ToolResult<object>> ModifyAttribute(
        ToolCallReason reason,
        FilePathWrapper? filepath = null,
        string? targetName = null,
        string? existingAttribute = null,
        AttributeModifyAction? action = null,
        string? newAttribute = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<AttributeEdit>? edits = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        bool hasSingularEdit = filepath.HasValue || !string.IsNullOrEmpty(targetName) || !string.IsNullOrEmpty(existingAttribute) || action.HasValue;
        bool hasBatchEdit = edits != null;

        if (hasSingularEdit && hasBatchEdit)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyAttribute: supply either filepath/targetName/existingAttribute/action or 'edits', not both.")
            };
        }

        if (hasBatchEdit)
        {
            if (edits!.Count == 0)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyAttribute: 'edits' was supplied but is empty.")
                };
            }

            return await ModifyAttributeBatch(edits, dryRun, returnDiff, cancellationToken);
        }

        if (!filepath.HasValue || string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(existingAttribute) || !action.HasValue)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyAttribute: 'filepath', 'targetName', 'existingAttribute', and 'action' are all required, unless 'edits' is supplied instead.")
            };
        }

        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath.Value, _workspaceManager.GetSolutionRoot());
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
            if (RefactoringToolHelpers.RequireUpdatedText(updated, "ModifyAttribute", filePathResolved) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} attribute '{existingAttribute}' on '{targetName}'.", "ModifyAttribute", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"{(action == AttributeModifyAction.add ? "Added" : action == AttributeModifyAction.replace ? "Replaced" : "Removed")} '{existingAttribute}' attribute on '{targetName}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff);

            // add/replace: existingAttribute (add) or newAttribute (replace) already holds the
            // exact attribute source the caller composed -> echoed back verbatim, same reasoning
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

    public async Task<ToolResult<object>> ModifyModifier(
        ToolCallReason reason,
        FilePathWrapper? filepath = null,
        string? targetName = null,
        NonAccessibilityModifier? modifier = null,
        AddRemoveAction? action = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<ModifierEdit>? edits = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        bool hasSingularEdit = filepath.HasValue || !string.IsNullOrEmpty(targetName) || modifier.HasValue || action.HasValue;
        bool hasBatchEdit = edits != null;

        if (hasSingularEdit && hasBatchEdit)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyModifier: supply either filepath/targetName/modifier/action or 'edits', not both.")
            };
        }

        if (hasBatchEdit)
        {
            if (edits!.Count == 0)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyModifier: 'edits' was supplied but is empty.")
                };
            }

            return await ModifyModifierBatch(edits, dryRun, returnDiff, cancellationToken);
        }

        if (!filepath.HasValue || string.IsNullOrEmpty(targetName) || !modifier.HasValue || !action.HasValue)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyModifier: 'filepath', 'targetName', 'modifier', and 'action' are all required, unless 'edits' is supplied instead.")
            };
        }

        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath.Value, _workspaceManager.GetSolutionRoot());
        var modifierText = modifier.Value.ToString();
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
            if (RefactoringToolHelpers.RequireUpdatedText(updated, "ModifyModifier", filePathResolved) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} '{modifierText}' modifier on '{targetName}'.", "ModifyModifier", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent: the only "new" text is the single modifier keyword the caller
            // already passed in -> same reasoning as ChangeAccessibility.
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"{(action == AddRemoveAction.add ? "Added" : "Removed")} '{modifierText}' modifier on '{targetName}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyModifier failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyModifier") };
        }
    }

    public async Task<ToolResult<object>> ModifyBaseType(
        ToolCallReason reason,
        FilePathWrapper? filepath = null,
        string? typeName = null,
        string? baseTypeName = null,
        AddRemoveAction? action = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<BaseTypeEdit>? edits = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        bool hasSingularEdit = filepath.HasValue || !string.IsNullOrEmpty(typeName) || !string.IsNullOrEmpty(baseTypeName) || action.HasValue;
        bool hasBatchEdit = edits != null;

        if (hasSingularEdit && hasBatchEdit)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyBaseType: supply either filepath/typeName/baseTypeName/action or 'edits', not both.")
            };
        }

        if (hasBatchEdit)
        {
            if (edits!.Count == 0)
            {
                return new ToolResult<object>()
                {
                    Success = false,
                    Error = new ResultError(ToolErrorCode.InvalidArgument, "ModifyBaseType: 'edits' was supplied but is empty.")
                };
            }

            return await ModifyBaseTypeBatch(edits, dryRun, returnDiff, cancellationToken);
        }

        if (!filepath.HasValue || string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(baseTypeName) || !action.HasValue)
        {
            return new ToolResult<object>()
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyBaseType: 'filepath', 'typeName', 'baseTypeName', and 'action' are all required, unless 'edits' is supplied instead.")
            };
        }

        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath.Value, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult updated;
            if (action == AddRemoveAction.add)
            {
                updated = await _refactoringEngine.AddBaseTypeAsync(filePathResolved, typeName!, baseTypeName!, contextSnippet, lineBefore, lineAfter);
            }
            else if (action == AddRemoveAction.remove)
            {
                updated = await _refactoringEngine.RemoveBaseTypeAsync(filePathResolved, typeName!, baseTypeName!, contextSnippet, lineBefore, lineAfter);
            }
            else
            {
                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                return new ToolResult<object>() { Success = true, Data = updated.ToJsonSummary() };
            }
            if (RefactoringToolHelpers.RequireUpdatedText(updated, "ModifyBaseType", filePathResolved) is { } guardResult)
                return guardResult;

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} base type '{baseTypeName}' on '{typeName}'.", "ModifyBaseType", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new ToolResult<object> { Success = false, Error = apply.Error };
            // No ChangedContent: the only "new" text is the base type name the caller already
            // passed in -> same reasoning as ChangeAccessibility/ModifyModifier.
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"{(action == AddRemoveAction.add ? "Added" : "Removed")} '{baseTypeName}' on '{typeName}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff);
            return new ToolResult<object>() { Success = true, Data = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyBaseType failed for '{TypeName}' in '{FilePathWrapper}'", typeName, filePathResolved);
            return new ToolResult<object>() { Success = false, Error = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyBaseType") };
        }
    }

    public async Task<ToolResult<object>> SyncTypeAndFilename(
        ToolCallReason reason,
        FilePathWrapper filepath,
        bool dryRun = false,
        bool returnDiff = false,
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
                return new ToolResult<object> { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename: target file '{newPath}' already exists - refusing to overwrite.") };
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
            // new Document -> it has no reason to know the old one should be dropped too. Without
            // this, the old Document stays tracked and the type it declares now exists twice in
            // the compilation, corrupting symbol resolution for every subsequent call.
            await _workspaceManager.RemoveDocumentByPathAsync(filePathResolved, cancellationToken);

            // No ChangedContent: this only moves a file to a new name -> the file's content is
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
