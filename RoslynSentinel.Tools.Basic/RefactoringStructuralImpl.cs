using Microsoft.Extensions.Logging;

using ModelContextProtocol;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

// Decision 7 step 3 (plan_split_workspace_refactoring_tools_for_di.md): implementation half of the
// RefactoringStructuralTools/Impl pair. Method bodies below are moved verbatim from
// RefactoringStructuralTools.cs (MemberTool, ModifyEnumTool, ModifyAttributeTool, ModifyModifierTool, ModifyBaseTypeTool,
// SyncTypeAndFilenameTool) plus their 3 private batch helpers (ModifyModifierBatch, ModifyAttributeBatch,
// ModifyBaseTypeBatch), each called from exactly one of the public methods above. ValidateAndApplyAsync
// is duplicated per-Impl-class per Decision 1-Amendment.
public class RefactoringStructuralImpl
{
    private readonly BasicRefactoringEngine _refactoringEngine;
    private readonly MemberRefactoringEngine _memberRefactoringEngine;
    private readonly StructuralRefinementEngine _structuralRefinementEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly ILogger _logger;

    // Reuses ReplaceSnippetTool's batch cap (WorkspaceTools.MaxSnippetEditsPerBatch) as a starting
    // value -> these are keyword/name batchEdits rather than text blocks, so the limit is purely count-based,
    // not size-derived.
    private const int MaxModifierFamilyEditsPerBatch = 20;

    public RefactoringStructuralImpl(
        BasicRefactoringEngine refactoringEngine,
        MemberRefactoringEngine memberRefactoringEngine,
        StructuralRefinementEngine structuralRefinementEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        ILogger logger)
    {
        _refactoringEngine = refactoringEngine;
        _memberRefactoringEngine = memberRefactoringEngine;
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
        IProgress<EngineProgress>? progress = default,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<FilePathWrapper>? deletePaths = null) =>
        ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine, _workspaceManager, _logger, changes, operationName,
            dryRun, returnDiff, progress, removePaths, cancellationToken, deletePaths,
            describeValidationFailure: (report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, ct));

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

    /// <summary>
    /// ModifyModifierTool add static on methods/properties: makes them static AND rewrites their instance-qualified callers as one
    /// change set (see MemberRefactoringEngine.ConvertMembersToStaticAsync). Returns null when the conversion took over none of the
    /// requests (a field, an already-static member, an unresolved target), so the caller falls back to the plain modifier path,
    /// which reports those cases. A refusal (instance state still used, unsupported member, unrewritable call site) is a
    /// TargetIneligible error naming the offending uses.
    /// </summary>
    private async Task<SentinelCallToolResult<AppliedChangeSummary>?> TryApplyStaticConversionAsync(IReadOnlyList<StaticConversionRequest> requests, bool autoStage, bool dryRun, bool returnDiff, string description, CancellationToken cancellationToken)
    {
        try
        {
            var conversion = await _memberRefactoringEngine.ConvertMembersToStaticAsync(requests, cancellationToken);
            if (conversion.HandledIndexes.Count == 0)
            {
                return null;
            }

            var findings = conversion.Notes.Select(n => new Finding("ModifyModifier", n, FindingSeverity.Caution)).ToList();
            if (!autoStage)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, Findings = findings, SuccessData = new AppliedChangeSummary(ChangeId: null, AffectedFiles: conversion.Changes.Keys.ToList(), Description: description, DryRun: false, Diff: null, ChangedContent: conversion.Changes.Count == 0 ? null : conversion.Changes, Validated: false) };
            }

            var apply = await ValidateAndApplyAsync(conversion.Changes, description, "ModifyModifier", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
            {
                return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = apply.Error };
            }

            var summary = new AppliedChangeSummary(apply.ChangeId, conversion.Changes.Keys.ToList(), description, apply.DryRun, apply.Diff, ChangedContent: conversion.Changes, Validated: true, LineChanges: apply.LineChanges, ChangedContentResultId: apply.ChangedContentResultId);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, StatusMessage = description, Findings = findings, SuccessData = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyModifier add static failed for {Count} target(s)", requests.Count);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyModifier") };
        }
    }

    /// <summary>
    /// The error code for a Modify*Batch rejection: the engine's code when EVERY collected error came from an engine result
    /// that carried one and they all agree (so a model can branch on NotFound vs Ambiguous), else InvalidArgument.
    /// </summary>
    private static string BatchErrorCode(IReadOnlyCollection<string> engineCodes, int totalErrorCount)
        => engineCodes.Count == totalErrorCount && engineCodes.Distinct().Count() == 1 ? engineCodes.First() : ToolErrorCode.InvalidArgument;

    private async Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyModifierBatch(List<ModifierEdit> edits, bool dryRun, bool returnDiff, CancellationToken cancellationToken)
    {
        if (edits.Count > MaxModifierFamilyEditsPerBatch)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    $"ModifyModifier: batchEdits has {edits.Count} entries (limit {MaxModifierFamilyEditsPerBatch}). Split into multiple calls.")
            };
        }

        var perEditErrors = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            if (string.IsNullOrEmpty(edits[i].FilePath))
            {
                perEditErrors.Add($"batchEdits[{i}]: filePath is required.");
            }
            if (string.IsNullOrEmpty(edits[i].TargetName))
            {
                perEditErrors.Add($"batchEdits[{i}] ({edits[i].FilePath}): targetName is required.");
            }
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ModifyModifier batch rejected before resolving targets:\n" + string.Join("\n", perEditErrors))
            };
        }

        var finalContents = new Dictionary<FilePathWrapper, string>();
        var touchedFiles = new List<FilePathWrapper>();
        var engineErrorCodes = new List<string>();
        var handledByStaticConversion = new HashSet<int>();
        var staticNotes = new List<string>();

        // add static on a method/property converts the member AND rewrites its callers, so every such edit is resolved together
        // (a member that only calls another member converted in this same batch is fine). Anything the conversion does not
        // apply to (fields, already-static members, unresolved targets) stays with the plain modifier path below.
        var staticRequests = new List<StaticConversionRequest>();
        for (int i = 0; i < edits.Count; i++)
        {
            if (edits[i].Action == AddRemoveAction.add && edits[i].Modifier == NonAccessibilityModifier.@static)
            {
                var resolvedPath = _workspaceManager.ResolveFromWire(edits[i].FilePath);
                if (resolvedPath.Validated)
                {
                    staticRequests.Add(new StaticConversionRequest(i, resolvedPath, edits[i].TargetName, edits[i].ContextSnippet, edits[i].LineBefore, edits[i].LineAfter));
                }
            }
        }

        if (staticRequests.Count > 0)
        {
            try
            {
                var conversion = await _memberRefactoringEngine.ConvertMembersToStaticAsync(staticRequests, cancellationToken);
                handledByStaticConversion = conversion.HandledIndexes;
                staticNotes = conversion.Notes;
                foreach (var change in conversion.Changes)
                {
                    finalContents[change.Key] = change.Value;
                    touchedFiles.Add(change.Key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ModifyModifier batch add static failed for {Count} target(s)", staticRequests.Count);
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyModifier") };
            }
        }

        var editsByFile = edits
            .Select((edit, index) => (edit, index))
            .Where(pair => !handledByStaticConversion.Contains(pair.index))
            .GroupBy(pair => _workspaceManager.ResolveFromWire(pair.edit.FilePath));

        foreach (var fileGroup in editsByFile)
        {
            var filePathResolved = fileGroup.Key;
            if (!filePathResolved.Validated)
            {
                perEditErrors.Add($"'{fileGroup.Key}': {(filePathResolved.FailureReason == FilePathFailureReason.NoSolutionLoaded ? "no solution is loaded." : "path could not be resolved.")}");
                continue;
            }

            if (finalContents.ContainsKey(filePathResolved))
            {
                // Both halves were computed against the file's original text, so they cannot be merged: refuse rather than drop one.
                perEditErrors.Add($"'{filePathResolved}': this file is changed both by an 'add static' conversion (which also rewrites callers) and by other modifier batchEdits ({string.Join(", ", fileGroup.Select(p => $"batchEdits[{p.index}]"))}). Split them into separate calls.");
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

            DocumentEditResult updated;
            try
            {
                updated = await _memberRefactoringEngine.ApplyModifierBatchAsync(filePathResolved, engineEdits, cancellationToken);
            }
            catch (ToolException ex)
            {
                perEditErrors.Add(ex.Message);
                engineErrorCodes.Add(ex.ErrorCode);
                continue;
            }
            finalContents[filePathResolved] = updated.UpdatedText!; // TODO S4: UpdatedText becomes non-nullable
            touchedFiles.Add(filePathResolved);
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(BatchErrorCode(engineErrorCodes, perEditErrors.Count), "ModifyModifier batch rejected - no changes were written:\n" + string.Join("\n", perEditErrors))
            };
        }

        var apply = await ValidateAndApplyAsync(finalContents, $"Batch-modified {edits.Count} modifier edit(s) across {touchedFiles.Count} file(s).", "ModifyModifier", dryRun, returnDiff, cancellationToken: cancellationToken);
        if (apply.Error is not null)
            return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = apply.Error };

        var description = $"Applied {edits.Count} modifier edit(s) across {touchedFiles.Count} file(s)." + (handledByStaticConversion.Count > 0 ? $" {handledByStaticConversion.Count} add-static edit(s) also rewrote instance-qualified callers." : string.Empty);
        var summary = new AppliedChangeSummary(apply.ChangeId, touchedFiles, description, apply.DryRun, apply.Diff, LineChanges: apply.LineChanges, ChangedContentResultId: apply.ChangedContentResultId);
        return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, StatusMessage = description, SuccessData = summary, Findings = staticNotes.Select(n => new Finding("ModifyModifier", n, FindingSeverity.Caution)).ToList() };
    }

    /// <summary>
    /// 'attribute' is an accepted alias for 'existingAttribute' (small models reach for the shorter name on action: add,
    /// where nothing "exists" yet). Returns the effective attribute source, or null with <paramref name="error"/> set when
    /// both were supplied with different values - silently preferring one would apply an attribute the caller did not
    /// necessarily mean.
    /// </summary>
    private static string? ResolveAttributeAlias(string? existingAttribute, string? attribute, string where, out string? error)
    {
        error = null;
        static string Normalize(string value) => value.Trim().TrimStart('[').TrimEnd(']').Trim();
        if (!string.IsNullOrWhiteSpace(existingAttribute) && !string.IsNullOrWhiteSpace(attribute)
            && !string.Equals(Normalize(existingAttribute), Normalize(attribute), StringComparison.Ordinal))
        {
            error = $"{where}: 'attribute' and 'existingAttribute' are aliases for the same value but were both supplied with different values ('{attribute}' vs '{existingAttribute}'). Supply only one of them.";
            return null;
        }

        return string.IsNullOrWhiteSpace(existingAttribute) ? attribute : existingAttribute;
    }

    private async Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyAttributeBatch(List<AttributeEdit> edits, bool dryRun, bool returnDiff, CancellationToken cancellationToken)
    {
        if (edits.Count > MaxModifierFamilyEditsPerBatch)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    $"ModifyAttribute: batchEdits has {edits.Count} entries (limit {MaxModifierFamilyEditsPerBatch}). Split into multiple calls.")
            };
        }

        var perEditErrors = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            if (string.IsNullOrEmpty(edits[i].FilePath))
            {
                perEditErrors.Add($"batchEdits[{i}]: filePath is required.");
            }
            if (string.IsNullOrEmpty(edits[i].TargetName))
            {
                perEditErrors.Add($"batchEdits[{i}] ({edits[i].FilePath}): targetName is required.");
            }
            var resolvedAttributeSource = ResolveAttributeAlias(edits[i].ExistingAttribute, edits[i].Attribute, $"batchEdits[{i}] ({edits[i].FilePath})", out var aliasError);
            if (aliasError != null)
            {
                perEditErrors.Add(aliasError);
            }
            else
            {
                edits[i].ExistingAttribute = resolvedAttributeSource ?? "";
                if (string.IsNullOrEmpty(edits[i].ExistingAttribute))
                {
                    perEditErrors.Add($"batchEdits[{i}] ({edits[i].FilePath}): existingAttribute (or its alias 'attribute') is required.");
                }
            }
            if (edits[i].Action == AttributeModifyAction.replace && string.IsNullOrEmpty(edits[i].NewAttribute))
            {
                perEditErrors.Add($"batchEdits[{i}] ({edits[i].FilePath}): newAttribute is required for action 'replace'.");
            }
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ModifyAttribute batch rejected before resolving targets:\n" + string.Join("\n", perEditErrors))
            };
        }

        var editsByFile = edits
            .Select((edit, index) => (edit, index))
            .GroupBy(pair => _workspaceManager.ResolveFromWire(pair.edit.FilePath));

        var finalContents = new Dictionary<FilePathWrapper, string>();
        var touchedFiles = new List<FilePathWrapper>();
        var appliedIndexes = new HashSet<int>();
        var engineErrorCodes = new List<string>();
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

            DocumentEditResult updated;
            try
            {
                updated = await _memberRefactoringEngine.ApplyAttributeBatchAsync(filePathResolved, engineEdits, cancellationToken);
            }
            catch (ToolException ex)
            {
                perEditErrors.Add(ex.Message);
                engineErrorCodes.Add(ex.ErrorCode);
                continue;
            }
            // Never trust the number of batchEdits submitted: only the engine knows which ones changed text.
            if (updated.AppliedEditIndexes is null)
            {
                perEditErrors.Add($"'{filePathResolved}': the engine did not report which batchEdits were applied, so success cannot be confirmed.");
                continue;
            }

            foreach (var appliedIndex in updated.AppliedEditIndexes)
            {
                appliedIndexes.Add(appliedIndex);
            }

            if (updated.AppliedEditIndexes.Count == 0)
            {
                continue;
            }

            finalContents[filePathResolved] = updated.UpdatedText!; // TODO S4: UpdatedText becomes non-nullable
            touchedFiles.Add(filePathResolved);
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(BatchErrorCode(engineErrorCodes, perEditErrors.Count), "ModifyAttribute batch rejected - no changes were written:\n" + string.Join("\n", perEditErrors))
            };
        }

        var noEffect = Enumerable.Range(0, edits.Count)
            .Where(index => !appliedIndexes.Contains(index))
            .Select(index => $"batchEdits[{index}] ({edits[index].TargetName}: {edits[index].Action} '{edits[index].ExistingAttribute}')")
            .ToList();
        if (appliedIndexes.Count == 0)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ModifyAttribute batch changed nothing - no changes were written. No effect: " + string.Join(", ", noEffect) + ". For action 'remove' this means the attribute is not present on the target.")
            };
        }

        var apply = await ValidateAndApplyAsync(finalContents, $"Batch-modified {appliedIndexes.Count} attribute edit(s) across {touchedFiles.Count} file(s).", "ModifyAttribute", dryRun, returnDiff, cancellationToken: cancellationToken);
        if (apply.Error is not null)
            return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = apply.Error };

        var description = noEffect.Count == 0
            ? $"Applied {appliedIndexes.Count} attribute edit(s) across {touchedFiles.Count} file(s)."
            : $"Applied {appliedIndexes.Count} of {edits.Count} attribute edit(s) across {touchedFiles.Count} file(s); no effect: {string.Join(", ", noEffect)}.";
        var summary = new AppliedChangeSummary(apply.ChangeId, touchedFiles, description, apply.DryRun, apply.Diff, ChangedContentResultId: apply.ChangedContentResultId);
        return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, StatusMessage = description, SuccessData = summary };
    }

    private async Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyBaseTypeBatch(List<BaseTypeEdit> edits, bool dryRun, bool returnDiff, CancellationToken cancellationToken)
    {
        if (edits.Count > MaxModifierFamilyEditsPerBatch)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    $"ModifyBaseType: batchEdits has {edits.Count} entries (limit {MaxModifierFamilyEditsPerBatch}). Split into multiple calls.")
            };
        }

        var perEditErrors = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            if (string.IsNullOrEmpty(edits[i].FilePath))
            {
                perEditErrors.Add($"batchEdits[{i}]: filePath is required.");
            }
            if (string.IsNullOrEmpty(edits[i].TypeName))
            {
                perEditErrors.Add($"batchEdits[{i}] ({edits[i].FilePath}): typeName is required.");
            }
            if (string.IsNullOrEmpty(edits[i].BaseTypeName))
            {
                perEditErrors.Add($"batchEdits[{i}] ({edits[i].FilePath}): baseTypeName is required.");
            }
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ModifyBaseType batch rejected before resolving targets:\n" + string.Join("\n", perEditErrors))
            };
        }

        var editsByFile = edits
            .Select((edit, index) => (edit, index))
            .GroupBy(pair => _workspaceManager.ResolveFromWire(pair.edit.FilePath));

        var finalContents = new Dictionary<FilePathWrapper, string>();
        var touchedFiles = new List<FilePathWrapper>();
        var engineErrorCodes = new List<string>();
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

            DocumentEditResult updated;
            try
            {
                updated = await _memberRefactoringEngine.ApplyBaseTypeBatchAsync(filePathResolved, engineEdits, cancellationToken);
            }
            catch (ToolException ex)
            {
                perEditErrors.Add(ex.Message);
                engineErrorCodes.Add(ex.ErrorCode);
                continue;
            }
            finalContents[filePathResolved] = updated.UpdatedText!; // TODO S4: UpdatedText becomes non-nullable
            touchedFiles.Add(filePathResolved);
        }

        if (perEditErrors.Count > 0)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(BatchErrorCode(engineErrorCodes, perEditErrors.Count), "ModifyBaseType batch rejected - no changes were written:\n" + string.Join("\n", perEditErrors))
            };
        }

        var apply = await ValidateAndApplyAsync(finalContents, $"Batch-modified {edits.Count} base type edit(s) across {touchedFiles.Count} file(s).", "ModifyBaseType", dryRun, returnDiff, cancellationToken: cancellationToken);
        if (apply.Error is not null)
            return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = apply.Error };

        var description = $"Applied {edits.Count} base type edit(s) across {touchedFiles.Count} file(s).";
        var summary = new AppliedChangeSummary(apply.ChangeId, touchedFiles, description, apply.DryRun, apply.Diff, ChangedContentResultId: apply.ChangedContentResultId);
        return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, StatusMessage = description, SuccessData = summary };
    }

    public async Task<SentinelCallToolResult<object>> Member(
        ToolCallReason reason,
        string filePath,
        MemberAction operation,
        string? containerName = null,
        string? namespaceName = null,
        string? memberName = null,
        string? newMemberSource = null,
        string? position = null,
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
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            if (operation == MemberAction.view)
            {
                if (!string.IsNullOrEmpty(memberName))
                {
                    // TODO Member(view) keeps the lenient container fallback; Step 5e of plan_unknown_parameter_rejection_reduction flips the flag to true and removes this
                    var (sourceOutcome, sourceMessage, sourceErrorCode, source) = await _symbolNavigationEngine.GetMemberSourceAsync(filePathResolved, memberName, containerName, contextSnippet, lineBefore, lineAfter, false, cancellationToken);
                    if (sourceOutcome != EditOutcome.Modified || source is null)
                        return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(sourceErrorCode ?? RefactoringToolHelpers.ErrorCodeFor(sourceOutcome), $"Member: {sourceMessage}") };
                    return new SentinelCallToolResult<object>() { IsError = false, SuccessData = new MemberSourceViewResult(source) };
                }

                if (string.IsNullOrEmpty(containerName))
                    return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "Member: containerName is required for operation 'view' when memberName is not given.") };

                var (_, _, members) = await _symbolNavigationEngine.GetContainerMembersAsync(filePathResolved, containerName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                return new SentinelCallToolResult<object>() { IsError = false, SuccessData = new MemberViewResult(members) };
            }

            if (operation == MemberAction.replace)
            {
                if (string.IsNullOrEmpty(memberName) || string.IsNullOrEmpty(newMemberSource))
                    return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "Member: memberName and newMemberSource are required for operation 'replace'.") };

                ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
                IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = msg.Progress, Total = msg.Total, Message = msg.Message }, null, cancellationToken));

                var replaceEnumName = await _symbolNavigationEngine.TryGetEnumMemberContainerNameAsync(filePathResolved, memberName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (replaceEnumName != null)
                {
                    var enumReplaceHint = $"Retry using ModifyEnum(enumName: \"{replaceEnumName}\", values: ...) directly with the full desired member list if this keeps failing.";
                    DocumentEditResult enumReplaced;
                    try
                    {
                        enumReplaced = await _memberRefactoringEngine.ReplaceEnumMemberAsync(filePathResolved, replaceEnumName, memberName, newMemberSource, contextSnippet, lineBefore, lineAfter, cancellationToken);
                    }
                    catch (ToolException toolEx)
                    {
                        var mappedError = ToolErrorMapper.ToResultError(toolEx, _workspaceManager, "Member");
                        return new SentinelCallToolResult<object> { IsError = true, ErrorData = mappedError with { Message = $"{mappedError.Message} {enumReplaceHint}" } };
                    }
                    if (RefactoringToolHelpers.RequireUpdatedText(enumReplaced, "Member", filePathResolved, recoveryHint: enumReplaceHint) is { } enumReplaceError)
                        return enumReplaceError;

                    var enumReplaceChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = enumReplaced.UpdatedText! };
                    var enumReplaceApply = await ValidateAndApplyAsync(enumReplaceChanges, $"Replaced '{memberName}' in enum '{replaceEnumName}'.", "Member", dryRun, returnDiff, progress.ToEngineProgress(), cancellationToken: cancellationToken);
                    if (enumReplaceApply.Error is not null)
                        return new SentinelCallToolResult<object> { IsError = true, ErrorData = enumReplaceApply.Error };
                    return await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                        new AppliedChangeSummary(enumReplaceApply.ChangeId, [filePathResolved], $"Replaced '{memberName}' in enum '{replaceEnumName}' in {Path.GetFileName(filePathResolved)}.", enumReplaceApply.DryRun, enumReplaceApply.Diff, ChangedContentResultId: enumReplaceApply.ChangedContentResultId),
                        _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
                        workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: $"Replaced '{memberName}' in enum '{replaceEnumName}' in {Path.GetFileName(filePathResolved)}.", cancellationToken: cancellationToken);
                }

                var result = await _memberRefactoringEngine.ReplaceMemberAsync(filePathResolved, memberName, newMemberSource, contextSnippet, lineBefore, lineAfter, containerName, cancellationToken);
                if (result.Outcome == EditOutcome.AlreadyInTargetState && RefactoringToolHelpers.RequireUpdatedText(result, "Member", filePathResolved) is { } replaceAlreadyInStateResult)
                    return replaceAlreadyInStateResult;
                var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedText! }; // TODO S4: UpdatedText becomes non-nullable
                var apply = await ValidateAndApplyAsync(changes, $"Replace member '{memberName}'.", "Member", dryRun, returnDiff, progress.ToEngineProgress(), cancellationToken: cancellationToken);
                if (apply.Error is not null)
                    return new SentinelCallToolResult<object> { IsError = true, ErrorData = apply.Error };
                var replaceEnvelope = await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                    new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Replaced '{memberName}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff, ChangedContentResultId: apply.ChangedContentResultId),
                    _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
                    workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: $"Replaced '{memberName}' in {Path.GetFileName(filePathResolved)}.", cancellationToken: cancellationToken);
                return replaceEnvelope with { WarningDetails = result.Warnings is { Count: > 0 } ? string.Join(" ", result.Warnings) : null };
            }

            if (operation == MemberAction.remove)
            {
                if (string.IsNullOrEmpty(memberName))
                    return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "Member: memberName is required for operation 'remove'.") };

                if (!skipPrecheck)
                {
                    var callers = await _symbolNavigationEngine.FindCallersAsync(filePathResolved, memberName, contextSnippet: contextSnippet, lineBefore: lineBefore, lineAfter: lineAfter, cancellationToken: cancellationToken);
                    var implementations = await _symbolNavigationEngine.FindImplementationsForMemberAsync(filePathResolved, memberName, contextSnippet: contextSnippet, lineBefore: lineBefore, lineAfter: lineAfter, cancellationToken: cancellationToken);
                    if (callers.Count > 0 || implementations.Count > 0)
                    {
                        var parts = new List<string>();
                        if (callers.Count > 0) parts.Add($"{callers.Count} caller(s)");
                        if (implementations.Count > 0) parts.Add($"{implementations.Count} implementation(s)");
                        return new SentinelCallToolResult<object>
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                                $"Member: '{memberName}' has {string.Join(" and ", parts)} - refusing to remove. " +
                                "Pass skipPrecheck: true to remove anyway, or resolve the callers/implementations first. " +
                                $"Callers: {System.Text.Json.JsonSerializer.Serialize(callers)}. " +
                                $"Implementations: {System.Text.Json.JsonSerializer.Serialize(implementations)}.")
                        };
                    }
                }

                var removeEnumName = await _symbolNavigationEngine.TryGetEnumMemberContainerNameAsync(filePathResolved, memberName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                if (removeEnumName != null)
                {
                    var enumRemoveHint = $"Retry using ModifyEnum(enumName: \"{removeEnumName}\", values: ...) directly with the full desired member list if this keeps failing.";
                    DocumentEditResult enumRemoved;
                    try
                    {
                        enumRemoved = await _memberRefactoringEngine.RemoveEnumMemberAsync(filePathResolved, removeEnumName, memberName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                    }
                    catch (ToolException toolEx)
                    {
                        var mappedError = ToolErrorMapper.ToResultError(toolEx, _workspaceManager, "Member");
                        return new SentinelCallToolResult<object> { IsError = true, ErrorData = mappedError with { Message = $"{mappedError.Message} {enumRemoveHint}" } };
                    }
                    if (RefactoringToolHelpers.RequireUpdatedText(enumRemoved, "Member", filePathResolved, recoveryHint: enumRemoveHint) is { } enumRemoveError)
                        return enumRemoveError;

                    var enumRemoveChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = enumRemoved.UpdatedText! };
                    var enumRemoveApply = await ValidateAndApplyAsync(enumRemoveChanges, $"Removed '{memberName}' from enum '{removeEnumName}'.", "Member", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (enumRemoveApply.Error is not null)
                        return new SentinelCallToolResult<object> { IsError = true, ErrorData = enumRemoveApply.Error };
                    return new SentinelCallToolResult<object> { IsError = false, SuccessData = new AppliedChangeSummary(enumRemoveApply.ChangeId, [filePathResolved], $"Removed '{memberName}' from enum '{removeEnumName}' in {Path.GetFileName(filePathResolved)}.", enumRemoveApply.DryRun, enumRemoveApply.Diff, _workspaceManager.WorkspaceVersion, ChangedContentResultId: enumRemoveApply.ChangedContentResultId) };
                }

                var result = await _memberRefactoringEngine.RemoveMemberAsync(filePathResolved, memberName, contextSnippet, lineBefore, lineAfter, containerName, cancellationToken: cancellationToken);

                var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedText! };
                var apply = await ValidateAndApplyAsync(changes, $"Remove member '{memberName}'.", "Member", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (apply.Error is not null)
                    return new SentinelCallToolResult<object> { IsError = true, ErrorData = apply.Error };
                var removeMessage = $"Removed '{memberName}' from {Path.GetFileName(filePathResolved)}.";
                string? removeWarning = null;
                if (!apply.DryRun)
                {
                    var stillPresent = await _memberRefactoringEngine.IsDeclarationStillPresentAsync(filePathResolved, memberName, contextSnippet, lineBefore, lineAfter, containerName, cancellationToken);
                    if (stillPresent == false)
                    {
                        removeMessage += $" Verified: a repeat lookup for '{memberName}' finds no declaration. To double-check, call GetFileOutline on the file.";
                    }
                    else if (stillPresent == true)
                    {
                        removeWarning = $"A declaration named '{memberName}' still resolves after removal (possibly an overload or another member of the same name); check with GetFileOutline on the file.";
                        removeMessage += " " + removeWarning;
                    }
                }
                return new SentinelCallToolResult<object> { IsError = false, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], removeMessage, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion, ChangedContentResultId: apply.ChangedContentResultId), WarningDetails = removeWarning };
            }

            // operation is addMember or addTopLevelType below (a generated typed property/field is the TypedMember tool).
            static string RequiredParamsHint(MemberAction op) => op switch
            {
                MemberAction.addMember => "Required params for addMember: containerName, newMemberSource. Optional: position.",
                MemberAction.addTopLevelType => "Required params for addTopLevelType: newMemberSource (the full type declaration). Optional: namespaceName.",
                _ => throw new ArgumentOutOfRangeException(nameof(op))
            };

            if (operation == MemberAction.addTopLevelType)
            {
                if (containerName != null)
                    return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Member: containerName is not used for operation 'addTopLevelType' (there is no container - the type is added at the top level). {RequiredParamsHint(operation)} If you meant to add a member to an existing container, use operation 'addMember' instead.") };
                if (position != null)
                    return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Member: position is not used for operation 'addTopLevelType' (the type is always appended). {RequiredParamsHint(operation)}") };
                if (string.IsNullOrEmpty(newMemberSource))
                    return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Member: newMemberSource is required for operation 'addTopLevelType'. {RequiredParamsHint(operation)}") };

                var topLevelResult = await _memberRefactoringEngine.AddTopLevelTypeAsync(filePathResolved, newMemberSource, namespaceName, cancellationToken);
                if (!autoStage)
                {
                    var topLevelNoStageDescription = $"Added new top-level type to {Path.GetFileName(filePathResolved)}.";
                    var topLevelNoStageChanges = string.IsNullOrEmpty(topLevelResult.UpdatedText)
                        ? new Dictionary<FilePathWrapper, string>()
                        : new Dictionary<FilePathWrapper, string> { [filePathResolved] = topLevelResult.UpdatedText! };
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = false,
                        SuccessData = new AppliedChangeSummary(
                            ChangeId: null,
                            AffectedFiles: topLevelNoStageChanges.Keys.ToList(),
                            Description: topLevelNoStageDescription,
                            DryRun: false,
                            Diff: null,
                            ChangedContent: topLevelNoStageChanges.Count == 0 ? null : topLevelNoStageChanges,
                            Validated: false)
                    };
                }
                var topLevelDescription = $"Added new top-level type to {Path.GetFileName(filePathResolved)}.";

                var topLevelChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = topLevelResult.UpdatedText! };
                var topLevelApply = await ValidateAndApplyAsync(topLevelChanges, topLevelDescription, "Member", dryRun, returnDiff || dryRun, cancellationToken: cancellationToken);
                if (topLevelApply.Error is not null)
                    return new SentinelCallToolResult<object> { IsError = true, ErrorData = topLevelApply.Error };
                return await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                    new AppliedChangeSummary(topLevelApply.ChangeId, [filePathResolved], topLevelDescription, topLevelApply.DryRun, topLevelApply.Diff, Validated: true, LineChanges: topLevelApply.LineChanges, ChangedContentResultId: topLevelApply.ChangedContentResultId),
                    _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
                    workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: topLevelDescription, cancellationToken: cancellationToken);
            }

            if (operation == MemberAction.addMember)
            {
                if (string.IsNullOrEmpty(containerName))
                    return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Member: containerName is required for operation 'addMember'. {RequiredParamsHint(operation)}") };
                if (string.IsNullOrEmpty(newMemberSource))
                    return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Member: newMemberSource is required for operation 'addMember'. {RequiredParamsHint(operation)}") };
            }

            if (await _symbolNavigationEngine.IsEnumContainerAsync(filePathResolved, containerName, contextSnippet, lineBefore, lineAfter, cancellationToken))
            {
                string? afterName = position != null && position.StartsWith("after:", StringComparison.OrdinalIgnoreCase) ? position.Substring("after:".Length) : null;
                string? beforeName = position != null && position.StartsWith("before:", StringComparison.OrdinalIgnoreCase) ? position.Substring("before:".Length) : null;
                if (position != null && afterName == null && beforeName == null && position != "end")
                    return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.") };

                var enumAddHint = $"Retry using ModifyEnum(enumName: \"{containerName}\", values: ...) directly with the full desired member list if this keeps failing.";
                DocumentEditResult enumAdded;
                try
                {
                    enumAdded = await _memberRefactoringEngine.AddEnumMemberAsync(filePathResolved, containerName, newMemberSource!, afterName, beforeName, contextSnippet, lineBefore, lineAfter, cancellationToken);
                }
                catch (ToolException toolEx)
                {
                    var mappedError = ToolErrorMapper.ToResultError(toolEx, _workspaceManager, "Member");
                    return new SentinelCallToolResult<object> { IsError = true, ErrorData = mappedError with { Message = $"{mappedError.Message} {enumAddHint}" } };
                }
                var enumAddError = RefactoringToolHelpers.RequireUpdatedText(enumAdded, "Member", filePathResolved, recoveryHint: enumAddHint);
                if (enumAddError is not null)
                    return enumAddError;

                if (!autoStage)
                {
                    var enumAddNoStageDescription = $"Added member to enum '{containerName}' in {Path.GetFileName(filePathResolved)}.";
                    var enumAddNoStageChanges = string.IsNullOrEmpty(enumAdded.UpdatedText)
                        ? new Dictionary<FilePathWrapper, string>()
                        : new Dictionary<FilePathWrapper, string> { [filePathResolved] = enumAdded.UpdatedText! };
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = false,
                        SuccessData = new AppliedChangeSummary(
                            ChangeId: null,
                            AffectedFiles: enumAddNoStageChanges.Keys.ToList(),
                            Description: enumAddNoStageDescription,
                            DryRun: false,
                            Diff: null,
                            ChangedContent: enumAddNoStageChanges.Count == 0 ? null : enumAddNoStageChanges,
                            Validated: false)
                    };
                }

                var enumAddDescription = $"Added member to enum '{containerName}' in {Path.GetFileName(filePathResolved)}.";
                var enumAddChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = enumAdded.UpdatedText! };
                var enumAddApply = await ValidateAndApplyAsync(enumAddChanges, enumAddDescription, "Member", dryRun, returnDiff || dryRun, cancellationToken: cancellationToken);
                if (enumAddApply.Error is not null)
                    return new SentinelCallToolResult<object> { IsError = true, ErrorData = enumAddApply.Error };
                return await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
                    new AppliedChangeSummary(enumAddApply.ChangeId, [filePathResolved], enumAddDescription, enumAddApply.DryRun, enumAddApply.Diff, Validated: true, LineChanges: enumAddApply.LineChanges, ChangedContentResultId: enumAddApply.ChangedContentResultId),
                    _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
                    workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: enumAddDescription, cancellationToken: cancellationToken);
            }

            DocumentEditResult updated;
            string description;
            if (string.IsNullOrEmpty(position) || position == "end")
            {
                updated = await _memberRefactoringEngine.AddMemberAsync(filePathResolved, containerName, newMemberSource!, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
                description = $"{DescribeMemberOutcome(updated, "Added new member")} to '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else if (position.StartsWith("after:", StringComparison.OrdinalIgnoreCase))
            {
                var afterMemberName = position.Substring("after:".Length);
                updated = await _memberRefactoringEngine.InsertMemberAfterAsync(filePathResolved, containerName, afterMemberName, newMemberSource!, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
                description = $"{DescribeMemberOutcome(updated, "Inserted new member")} after '{afterMemberName}' in '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else if (position.StartsWith("before:", StringComparison.OrdinalIgnoreCase))
            {
                var beforeMemberName = position.Substring("before:".Length);
                updated = await _memberRefactoringEngine.InsertMemberBeforeAsync(filePathResolved, containerName, beforeMemberName, newMemberSource!, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
                description = $"{DescribeMemberOutcome(updated, "Inserted new member")} before '{beforeMemberName}' in '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else
            {
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown position '{position}'. Valid values: null, 'end', 'after:MemberName', 'before:MemberName'.") };
            }

            if (!autoStage)
            {
                var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: noStageChanges.Keys.ToList(),
                        Description: description,
                        DryRun: false,
                        Diff: null,
                        ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                        Validated: false)
                };
            }
            return await ApplyAddedMemberAsync(updated, description, filePathResolved, "Member", dryRun, returnDiff, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Member ({Operation}) failed for '{ContainerOrMemberName}' in '{FilePathWrapper}'", operation, containerName ?? memberName, filePathResolved);
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Member") };
        }
    }

    /// <summary>
    /// Shared tail of the add paths (Member's addMember branch and TypedMember): validates and
    /// writes the already-computed <paramref name="updated"/> text and wraps the result as an AppliedChangeSummary.
    /// </summary>
    private async Task<SentinelCallToolResult<object>> ApplyAddedMemberAsync(
        DocumentEditResult updated,
        string description,
        FilePathWrapper filePathResolved,
        string operationName,
        bool dryRun,
        bool returnDiff,
        CancellationToken cancellationToken)
    {
        var addChanges = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
        var addApply = await ValidateAndApplyAsync(addChanges, description, operationName, dryRun, returnDiff || dryRun, cancellationToken: cancellationToken);
        if (addApply.Error is not null)
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = addApply.Error };
        return await SentinelCallToolResult<object>.ForPossiblyLargeDataAsync(
            new AppliedChangeSummary(addApply.ChangeId, [filePathResolved], description, addApply.DryRun, addApply.Diff, Validated: true, LineChanges: addApply.LineChanges, ChangedContentResultId: addApply.ChangedContentResultId),
            _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
            workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: description, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Generates a typed property or field in an existing container (the TypedMember tool). Split out of
    /// <see cref="Member"/> so the always-loaded Member schema does not carry the nine typed-only params.
    /// </summary>
    public async Task<SentinelCallToolResult<object>> TypedMember(
        ToolCallReason reason,
        string filePath,
        string? containerName,
        TypedMemberKind? typedKind,
        string? typedName,
        string? typedType,
        string accessibility = "public",
        bool hasSetter = true,
        bool isInit = false,
        bool isReadonly = false,
        bool isStatic = false,
        string? initializer = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            const string requiredParamsHint = "Required params: containerName, typedKind, typedName, typedType. Optional: accessibility, hasSetter/isInit (typedKind=property), isReadonly/isStatic/initializer (typedKind=field).";
            if (string.IsNullOrEmpty(containerName))
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"TypedMember: containerName is required. {requiredParamsHint}") };
            if (typedKind == null)
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"TypedMember: typedKind is required. {requiredParamsHint}") };
            if (string.IsNullOrWhiteSpace(typedName) || string.IsNullOrWhiteSpace(typedType))
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"TypedMember: typedName and typedType are required. {requiredParamsHint}") };

            if (await _symbolNavigationEngine.IsEnumContainerAsync(filePathResolved, containerName, contextSnippet, lineBefore, lineAfter, cancellationToken))
                return new SentinelCallToolResult<object>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"TypedMember: '{containerName}' is an enum - typedKind (property/field generation) doesn't apply. Pass newMemberSource as a bare member token ('Name' or 'Name=IntValue') via Member(operation: addMember) instead, or use ModifyEnum(enumName: \"{containerName}\", values: ...) directly.") };

            DocumentEditResult updated;
            string description;
            // The typed-generation path (AddPropertyAsync/AddFieldAsync) builds its source string inside the engine
            // and doesn't return it separately from the whole-file UpdatedText, so there is no ChangedContent to echo.
            if (typedKind == TypedMemberKind.property)
            {
                updated = await _memberRefactoringEngine.AddPropertyAsync(filePathResolved, containerName, typedName, typedType, accessibility, hasSetter, isInit, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
                description = $"Added '{typedType} {typedName}' property to '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }
            else
            {
                updated = await _memberRefactoringEngine.AddFieldAsync(filePathResolved, containerName, typedName, typedType, accessibility, isReadonly, isStatic, initializer, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
                description = $"Added '{typedType} {typedName}' field to '{containerName}' in {Path.GetFileName(filePathResolved)}.";
            }

            return await ApplyAddedMemberAsync(updated, description, filePathResolved, "TypedMember", dryRun, returnDiff, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TypedMember ({Kind}) failed for '{ContainerName}' in '{FilePathWrapper}'", typedKind, containerName, filePathResolved);
            return new SentinelCallToolResult<object>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "TypedMember") };
        }
    }

    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyEnum(
        ToolCallReason reason,
        string filePath,
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
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var updated = await _memberRefactoringEngine.ModifyEnumAsync(filePathResolved, enumName, values, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            if (!autoStage)
            {
                var noStageDescription = string.IsNullOrEmpty(updated.Message)
                    ? $"Sets '{enumName}' members in {Path.GetFileName(filePathResolved)} to match the requested list."
                    : $"'{enumName}' in {Path.GetFileName(filePathResolved)}: {updated.Message}.";
                var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = false,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: noStageChanges.Keys.ToList(),
                        Description: noStageDescription,
                        DryRun: false,
                        Diff: null,
                        ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                        Validated: false)
                };
            }

            if (RefactoringToolHelpers.RequireUpdatedText(updated, "ModifyEnum", filePathResolved) is { } guardResult)
                return RefactoringToolHelpers.ToSummaryResult(guardResult);

            var description = string.IsNullOrEmpty(updated.Message)
                ? $"Sets '{enumName}' members in {Path.GetFileName(filePathResolved)} to match the requested list."
                : $"'{enumName}' in {Path.GetFileName(filePathResolved)}: {updated.Message}.";

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, description, "ModifyEnum", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = apply.Error };
            // ChangedContent is dropped centrally by AppliedChangeSummary for a real apply: the new member list is just the caller-supplied `values` string
            // already passed in verbatim -> same reasoning as ChangeAccessibilityTool/ModifyModifierTool.
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff, _workspaceManager.WorkspaceVersion, ChangedContent: changes, Validated: true, LineChanges: apply.LineChanges, ChangedContentResultId: apply.ChangedContentResultId) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyEnum failed for '{EnumName}' in '{FilePathWrapper}'", enumName, filePathResolved);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyEnum") };
        }
    }

    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyAttribute(
        ToolCallReason reason,
        string? filePath = null,
        string? targetName = null,
        string? existingAttribute = null,
        AttributeModifyAction? action = null,
        string? newAttribute = null,
        string? attribute = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<AttributeEdit>? batchEdits = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        bool hasSingularEdit = filePath is not null || !string.IsNullOrEmpty(targetName) || !string.IsNullOrEmpty(existingAttribute) || !string.IsNullOrEmpty(attribute) || action.HasValue;
        bool hasBatchEdit = batchEdits != null;

        if (hasSingularEdit && hasBatchEdit)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyAttribute: supply either filePath/targetName/existingAttribute/action or 'batchEdits', not both.")
            };
        }

        if (hasBatchEdit)
        {
            if (batchEdits!.Count == 0)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ModifyAttribute: 'batchEdits' was supplied but is empty.")
                };
            }

            return await ModifyAttributeBatch(batchEdits, dryRun, returnDiff, cancellationToken);
        }

        var resolvedAttributeSource = ResolveAttributeAlias(existingAttribute, attribute, "ModifyAttribute", out var aliasError);
        if (aliasError != null)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, aliasError)
            };
        }

        existingAttribute = resolvedAttributeSource;

        if (filePath is null || string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(existingAttribute) || !action.HasValue)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyAttribute: 'filePath', 'targetName', 'existingAttribute' (or its alias 'attribute'), and 'action' are all required, unless 'batchEdits' is supplied instead.")
            };
        }

        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            if (action == AttributeModifyAction.replace && string.IsNullOrEmpty(newAttribute))
            {
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ModifyAttribute: newAttribute is required for action 'replace'.") };
            }

            DocumentEditResult updated;
            if (action == AttributeModifyAction.add)
            {
                updated = await _memberRefactoringEngine.AddAttributeAsync(filePathResolved, targetName, existingAttribute, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            }
            else if (action == AttributeModifyAction.replace)
            {
                updated = await _memberRefactoringEngine.ReplaceAttributeAsync(filePathResolved, targetName, existingAttribute, newAttribute!, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            }
            else if (action == AttributeModifyAction.remove)
            {
                updated = await _memberRefactoringEngine.RemoveAttributeAsync(filePathResolved, targetName, existingAttribute, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            }
            else
            {
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = false,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: noStageChanges.Keys.ToList(),
                        Description: $"{action} attribute '{existingAttribute}' on '{targetName}'.",
                        DryRun: false,
                        Diff: null,
                        ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                        Validated: false)
                };
            }

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} attribute '{existingAttribute}' on '{targetName}'.", "ModifyAttribute", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = apply.Error };
            var description = $"{(action == AttributeModifyAction.add ? "Added" : action == AttributeModifyAction.replace ? "Replaced" : "Removed")} '{existingAttribute}' attribute on '{targetName}' in {Path.GetFileName(filePathResolved)}.";
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true, LineChanges: apply.LineChanges, ChangedContentResultId: apply.ChangedContentResultId);

            // add/replace: existingAttribute (add) or newAttribute (replace) already holds the
            // exact attribute source the caller composed -> echoed back verbatim, same reasoning
            // as MemberTool(add)'s raw-source path. remove has no new content to show.
            if (action == AttributeModifyAction.remove)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, StatusMessage = description, SuccessData = summary };
            }

            return await SentinelCallToolResult<AppliedChangeSummary>.ForPossiblyLargeDataAsync(
                summary,
                _workspaceManager.GetSolutionRoot(), "AppliedChangeSummary", ResultWrapperType.AppliedChangeSummaryResult,
                workspaceVersion: _workspaceManager.WorkspaceVersion, statusMessage: description, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyAttribute failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyAttribute") };
        }
    }

    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyModifier(
        ToolCallReason reason,
        string? filePath = null,
        string? targetName = null,
        NonAccessibilityModifier? modifier = null,
        AddRemoveAction? action = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<ModifierEdit>? batchEdits = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        bool hasSingularEdit = filePath is not null || !string.IsNullOrEmpty(targetName) || modifier.HasValue || action.HasValue;
        bool hasBatchEdit = batchEdits != null;

        if (hasSingularEdit && hasBatchEdit)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyModifier: supply either filePath/targetName/modifier/action or 'batchEdits', not both.")
            };
        }

        if (hasBatchEdit)
        {
            if (batchEdits!.Count == 0)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ModifyModifier: 'batchEdits' was supplied but is empty.")
                };
            }

            return await ModifyModifierBatch(batchEdits, dryRun, returnDiff, cancellationToken);
        }

        if (filePath is null || string.IsNullOrEmpty(targetName) || !modifier.HasValue || !action.HasValue)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyModifier: 'filePath', 'targetName', 'modifier', and 'action' are all required, unless 'batchEdits' is supplied instead.")
            };
        }

        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        if (action == AddRemoveAction.add && modifier == NonAccessibilityModifier.@static)
        {
            // add static on a method/property is a conversion, not a keyword insert: the callers' receivers change too.
            // A target the conversion does not apply to (field, already static, unresolved) falls through to the plain path below.
            var singular = await TryApplyStaticConversionAsync([new StaticConversionRequest(0, filePathResolved, targetName, contextSnippet, lineBefore, lineAfter)], autoStage, dryRun, returnDiff, $"Made '{targetName}' static in {Path.GetFileName(filePathResolved)} and rewrote its instance-qualified callers.", cancellationToken);
            if (singular != null)
            {
                return singular;
            }
        }

        var modifierText = modifier.Value.ToString();
        try
        {
            DocumentEditResult updated;
            if (action == AddRemoveAction.add)
            {
                updated = await _memberRefactoringEngine.AddModifierAsync(filePathResolved, targetName, modifierText, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            }
            else if (action == AddRemoveAction.remove)
            {
                updated = await _memberRefactoringEngine.RemoveModifierAsync(filePathResolved, targetName, modifierText, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            }
            else
            {
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = false,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: noStageChanges.Keys.ToList(),
                        Description: $"{action} '{modifierText}' modifier on '{targetName}'.",
                        DryRun: false,
                        Diff: null,
                        ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                        Validated: false)
                };
            }
            if (RefactoringToolHelpers.RequireUpdatedText(updated, "ModifyModifier", filePathResolved) is { } guardResult)
                return RefactoringToolHelpers.ToSummaryResult(guardResult);

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} '{modifierText}' modifier on '{targetName}'.", "ModifyModifier", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = apply.Error };
            // ChangedContent is dropped centrally by AppliedChangeSummary for a real apply: the only "new" text is the single modifier keyword the caller
            // already passed in -> same reasoning as ChangeAccessibilityTool.
            var description = $"{(action == AddRemoveAction.add ? "Added" : "Removed")} '{modifierText}' modifier on '{targetName}' in {Path.GetFileName(filePathResolved)}.";
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true, LineChanges: apply.LineChanges, ChangedContentResultId: apply.ChangedContentResultId);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, StatusMessage = description, SuccessData = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyModifier failed for '{TargetName}' in '{FilePathWrapper}'", targetName, filePathResolved);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyModifier") };
        }
    }

    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ModifyBaseType(
        ToolCallReason reason,
        string? filePath = null,
        string? typeName = null,
        string? baseTypeName = null,
        AddRemoveAction? action = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        List<BaseTypeEdit>? batchEdits = null,
        bool autoStage = true,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        bool hasSingularEdit = filePath is not null || !string.IsNullOrEmpty(typeName) || !string.IsNullOrEmpty(baseTypeName) || action.HasValue;
        bool hasBatchEdit = batchEdits != null;

        if (hasSingularEdit && hasBatchEdit)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyBaseType: supply either filePath/typeName/baseTypeName/action or 'batchEdits', not both.")
            };
        }

        if (hasBatchEdit)
        {
            if (batchEdits!.Count == 0)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "ModifyBaseType: 'batchEdits' was supplied but is empty.")
                };
            }

            return await ModifyBaseTypeBatch(batchEdits, dryRun, returnDiff, cancellationToken);
        }

        if (filePath is null || string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(baseTypeName) || !action.HasValue)
        {
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument,
                    "ModifyBaseType: 'filePath', 'typeName', 'baseTypeName', and 'action' are all required, unless 'batchEdits' is supplied instead.")
            };
        }

        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            DocumentEditResult updated;
            if (action == AddRemoveAction.add)
            {
                updated = await _memberRefactoringEngine.AddBaseTypeAsync(filePathResolved, typeName!, baseTypeName!, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            }
            else if (action == AddRemoveAction.remove)
            {
                updated = await _memberRefactoringEngine.RemoveBaseTypeAsync(filePathResolved, typeName!, baseTypeName!, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            }
            else
            {
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unhandled action '{action}'.") };
            }
            if (!autoStage)
            {
                var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                    ? new Dictionary<FilePathWrapper, string>()
                    : new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = false,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: noStageChanges.Keys.ToList(),
                        Description: $"{action} base type '{baseTypeName}' on '{typeName}'.",
                        DryRun: false,
                        Diff: null,
                        ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                        Validated: false)
                };
            }
            if (RefactoringToolHelpers.RequireUpdatedText(updated, "ModifyBaseType", filePathResolved) is { } guardResult)
                return RefactoringToolHelpers.ToSummaryResult(guardResult);

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = updated.UpdatedText! };
            var apply = await ValidateAndApplyAsync(changes, $"{action} base type '{baseTypeName}' on '{typeName}'.", "ModifyBaseType", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = apply.Error };
            // ChangedContent is dropped centrally by AppliedChangeSummary for a real apply: the only "new" text is the base type name the caller already
            // passed in -> same reasoning as ChangeAccessibilityTool/ModifyModifierTool.
            var description = $"{(action == AddRemoveAction.add ? "Added" : "Removed")} '{baseTypeName}' on '{typeName}' in {Path.GetFileName(filePathResolved)}.";
            var summary = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], description, apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true, LineChanges: apply.LineChanges, ChangedContentResultId: apply.ChangedContentResultId);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = false, StatusMessage = description, SuccessData = summary };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModifyBaseType failed for '{TypeName}' in '{FilePathWrapper}'", typeName, filePathResolved);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ModifyBaseType") };
        }
    }

    public async Task<SentinelCallToolResult<object>> SyncTypeAndFilename(
        ToolCallReason reason,
        string filePath,
        string? targetTypeName = null,
        bool dryRun = false,
        bool returnDiff = false,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = _workspaceManager.ResolveFromWire(filePath);

        try
        {
            var result = await _structuralRefinementEngine.SyncTypeAndFilenameAsync(filePathResolved, targetTypeName, cancellationToken);
            if (result.Outcome == EditOutcome.AlreadyInTargetState)
                return RefactoringToolHelpers.RequireUpdatedText(result, "SyncTypeAndFilename", filePathResolved)!;

            var (newPath, content) = result.Changes.First();
            if (File.Exists(newPath))
            {
                return new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError(ToolErrorCode.Exception, $"SyncTypeAndFilename: target file '{newPath}' already exists - refusing to overwrite.") };
            }

            // deletePaths (not removePaths) so the old file's delete goes through
            // ApplyProposedChangesAsync's tracked delete path: that's what captures its pre-image
            // content into the operation blob, which is what makes UndoLastApplyTool able to actually
            // restore a rename instead of reporting NoReversibleItems for it. It also removes the
            // old Document from the workspace itself, so a separate RemoveDocumentByPathAsync call
            // is no longer needed afterward. Passing filePathResolved in BOTH removePaths and
            // deletePaths would ask ValidateChangesAsync to RemoveDocument the same path twice
            // against its own candidate solution, throwing InvalidOperationException on the second
            // attempt (deletePaths already covers pre-validate exclusion via ValidateAndApplyHelper's
            // allRemovePaths merge) -> removePaths is intentionally omitted here.
            var changes = new Dictionary<FilePathWrapper, string> { [newPath] = content };
            var apply = await ValidateAndApplyAsync(changes, result.Message ?? $"Rename '{Path.GetFileName(filePathResolved)}' to '{Path.GetFileName(newPath)}'.", "SyncTypeAndFilename", dryRun, returnDiff, cancellationToken: cancellationToken, deletePaths: [filePathResolved]);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsError = true, ErrorData = apply.Error };

            // dryRun: ValidateAndApplyAsync never wrote newPath or deleted filePath. Report the
            // preview as-is.
            if (apply.DryRun)
            {
                return new SentinelCallToolResult<object> { IsError = false, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved, newPath], $"[DryRun] Would rename '{Path.GetFileName(filePathResolved)}' to '{Path.GetFileName(newPath)}'.", apply.DryRun, apply.Diff, ChangedContentResultId: apply.ChangedContentResultId) };
            }

            // No ChangedContent: this only moves a file to a new name -> the file's content is
            // byte-for-byte unchanged, so there is no new text to show beyond the summary.
            return new SentinelCallToolResult<object> { IsError = false, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved, newPath], $"Renamed '{Path.GetFileName(filePathResolved)}' to '{Path.GetFileName(newPath)}'.", apply.DryRun, apply.Diff, ChangedContentResultId: apply.ChangedContentResultId) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncTypeAndFilename unexpected exception for '{FilePathWrapper}'", filePathResolved);
            return new SentinelCallToolResult<object> { IsError = true, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"SyncTypeAndFilename for '{filePathResolved}'") };
        }
    }
}
