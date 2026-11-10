using System.ComponentModel;

using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Wire-shape input for one entry in <c>ChangeSignatureTool</c>'s <c>parameters</c> array. Flat and
/// nullable-field-based (rather than a polymorphic/discriminated-union shape) so the emitted MCP
/// JSON schema stays a plain object schema an agent can construct directly: pass
/// <paramref name = "originalIndex"/> alone to keep an existing parameter at this new position, or
/// omit it and pass <paramref name = "name"/>/<paramref name = "type"/>/<paramref name = "defaultValue"/>
/// to insert a brand-new parameter here (its default value is also the literal fill-in argument
/// inserted at every existing call site).
/// </summary>
public sealed record ChangeSignatureParameterInput(int? originalIndex = null, string? name = null, string? type = null, string? defaultValue = null);

/// <summary>
/// The refactoring tools every flavor serves (ChangeSignature, MoveMember, MoveAllTypesToFiles), promoted out of
/// <c>AdvancedRefactoringTools</c> so they need nothing from the Advanced engines (slice 1 of the core split).
/// </summary>
[McpServerToolType]
public class CoreRefactoringTools
{
    private readonly BasicRefactoringEngine _refactoringEngine;
    private readonly MemberRefactoringEngine _memberRefactoringEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly ILogger _logger;
    // Which tools this server can serve, for the edit-impact next step; null (unit tests, hosts without a surface) = every tool callable.
    private readonly ActiveToolSurface? _activeToolSurface;

    public CoreRefactoringTools(IWorkspaceManager workspaceManager)
    {
        _validationEngine = new ValidationEngine(workspaceManager);
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
        _refactoringEngine = new BasicRefactoringEngine(workspaceManager);
        _memberRefactoringEngine = new MemberRefactoringEngine(workspaceManager, _symbolNavigationEngine, _validationEngine);
        _workspaceManager = workspaceManager;
        _logger = NullLogger<CoreRefactoringTools>.Instance;
    }

    public CoreRefactoringTools(BasicRefactoringEngine refactoringEngine, MemberRefactoringEngine memberRefactoringEngine, SymbolNavigationEngine symbolNavigationEngine, IWorkspaceManager workspaceManager, ValidationEngine validationEngine, ILogger<CoreRefactoringTools> logger, ActiveToolSurface? activeToolSurface = null)
    {
        _activeToolSurface = activeToolSurface;
        _refactoringEngine = refactoringEngine;
        _memberRefactoringEngine = memberRefactoringEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _logger = logger;
    }

    /// <summary>
    /// Validates proposed changes against the current in-memory solution and, unless
    /// <paramref name = "dryRun"/> is set, writes them straight to disk (write-through -> no
    /// intermediate staging step). Rolls back any already-written files if a multi-file change
    /// partially fails, so a change never lands half-applied.
    /// </summary>
    private Task<ApplyOutcome> ValidateAndApplyAsync(Dictionary<FilePathWrapper, string> changes, string description, string operationName, bool dryRun = false, bool returnDiff = false, IProgress<EngineProgress>? progress = default, IReadOnlyCollection<FilePathWrapper>? removePaths = null, CancellationToken cancellationToken = default, IReadOnlyCollection<FilePathWrapper>? deletePaths = null, IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>? expectedEdits = null) => ValidateAndApplyHelper.ValidateAndApplyAsync(_validationEngine, _workspaceManager, _logger, changes, operationName, dryRun, returnDiff, progress, removePaths, cancellationToken: cancellationToken, deletePaths, describeValidationFailure: (report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, ct), expectedEdits: expectedEdits, describeEditImpact: EditImpactGate.Describer(_workspaceManager, changes, _activeToolSurface, removePaths, deletePaths));

    /// <summary>Normalized "fullPath:line" key; relative paths resolve against the solution root. Null if the path is unusable.</summary>
    private static string? FixupLineKey(string? filePath, int line, string? solutionRoot)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        try
        {
            var absolute = Path.IsPathRooted(filePath) || string.IsNullOrEmpty(solutionRoot) ? filePath : Path.Combine(solutionRoot, filePath);
            return $"{Path.GetFullPath(absolute)}:{line}";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// True for refusal codes that MoveMemberTool must return as-is instead of letting the unresolved-call-site
    /// hints mask them. Public static (not internal) so tests can call it directly (kept public as when it lived in Tools.Advanced, which had no InternalsVisibleTo for its tests).
    /// </summary>
    public static bool IsRefusalThatMustNotBeMasked(string errorCode) =>
        errorCode is ToolErrorCode.CollateralChangeRefused or ToolErrorCode.EolChangeRefused;

    /// <summary>
    /// Builds the UnresolvedCallSites refusal for a move whose call sites the engine could not rewrite.
    /// messagePrefix is prepended to the standard message (empty for the default refusal).
    /// </summary>
    private static SentinelCallToolResult<AppliedChangeSummary> UnresolvedCallSitesResult(IReadOnlyList<CallSiteLedgerEntry> unresolvedEntries, string messagePrefix)
    {
        // Detail must echo e.FilePath verbatim (not Path.GetFileName): that full path is the
        // literal callSiteFixups key MoveInstanceMembersAsync matches against
        // ("{row.FilePath}:{row.Line}") - a bare filename here would silently never match.
        var detail = string.Join("; ", unresolvedEntries.Select(e => $"{e.FilePath}:{e.Line} [{e.BrokenExpression}] {e.Status} ({e.BlockReason})" + (e.CandidatesInScope.Count > 0 ? $" - candidates in scope: {string.Join(", ", e.CandidatesInScope)}" : "") + (e.SuggestedFix != null ? $" - suggested fix: {e.SuggestedFix}" : "")));
        return new SentinelCallToolResult<AppliedChangeSummary>
        {
            IsError = true,
            ErrorData = new ResultError(ToolErrorCode.UnresolvedCallSites, messagePrefix + $"{unresolvedEntries.Count} call site(s) could not be automatically rewritten, so the move was rejected. Retry with callSiteFixups keyed \"FilePath:Line\" (the full path shown below) for each site, or \"FilePath:*\" / \"*\" to apply one value to every unresolved site in a file / everywhere.", detail, unresolvedEntries.Cast<object>().ToList())
        };
    }

    /// <summary>
    /// When MoveMemberTool's compile gate rejects a change that included caller-supplied callSiteFixups,
    /// prepends a note naming the fixup value(s) whose rewritten lines carry the new errors. The raw
    /// compiler dump alone attributes them to the compiler, not to the value the caller chose, so a
    /// caller cannot otherwise tell a bad fixup expression from a genuinely unrelated break.
    /// Returns <paramref name = "error"/> unchanged when no error lands on a fixup-rewritten line.
    /// Matching uses the structured diagnostics (file + 1-based start line), never the message text.
    /// </summary>
    private static ResultError AttributeValidationErrorsToCallSiteFixups(ResultError error, DiagnosticReport? validation, IReadOnlyList<AppliedCallSiteFixup>? appliedFixups, string? solutionRoot)
    {
        if (validation == null || appliedFixups is not { Count: > 0 })
        {
            return error;
        }

        var fixupByLine = new Dictionary<string, AppliedCallSiteFixup>(StringComparer.OrdinalIgnoreCase);
        foreach (var fixup in appliedFixups)
        {
            if (FixupLineKey(fixup.FilePath, fixup.Line, solutionRoot) is { } key)
            {
                fixupByLine.TryAdd(key, fixup);
            }
        }

        var hitValues = new List<string>();
        var hitCount = 0;
        foreach (var diagnostic in validation.Diagnostics)
        {
            if (!string.Equals(diagnostic.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (FixupLineKey(diagnostic.FilePath, diagnostic.StartLine, solutionRoot) is not { } key || !fixupByLine.TryGetValue(key, out var hit))
            {
                continue;
            }

            hitCount++;
            if (!hitValues.Contains(hit.FixupValue, StringComparer.Ordinal))
            {
                hitValues.Add(hit.FixupValue);
            }
        }

        if (hitCount == 0)
        {
            return error;
        }

        const int maxValuesShown = 3;
        var valuesText = string.Join(", ", hitValues.Take(maxValuesShown).Select(v => $"'{v}'")) + (hitValues.Count > maxValuesShown ? $" (+{hitValues.Count - maxValuesShown} more)" : "");
        var note = $"{hitCount} error(s) are on lines rewritten by your callSiteFixups value(s) {valuesText} - the fixup expression itself is likely the cause. ";
        return error with
        {
            Message = note + error.Message
        };
    }

    /// <summary>
    /// The ChangeSignature summary sentence, worded from what the new parameter list does to the old one: "Adds", "Removes" and/or "Reorders" (a new entry is an add;
    /// an existing parameter missing from the list is a remove; kept parameters out of ascending index order is a reorder). Falls back to "Changes" when none of
    /// those is detectable, e.g. the declaration could not be read.
    /// </summary>
    private static string DescribeSignatureChange(string methodName, IReadOnlyList<SignatureParameterSpec> specs, (int ParameterCount, bool IsConstructor)? targetShape, string fileName)
    {
        var keptIndexes = specs.OfType<ExistingParameterSpec>().Select(s => s.OriginalIndex).ToList();
        var verbs = new List<string>();
        if (specs.OfType<NewParameterSpec>().Any())
        {
            verbs.Add("adds");
        }

        if (targetShape is { } shape && keptIndexes.Count < shape.ParameterCount)
        {
            verbs.Add("removes");
        }

        if (keptIndexes.Zip(keptIndexes.Skip(1), (previous, next) => previous > next).Any(outOfOrder => outOfOrder))
        {
            verbs.Add("reorders");
        }

        var what = verbs.Count switch
        {
            0 => "Changes",
            1 => verbs[0],
            _ => string.Join(", ", verbs.Take(verbs.Count - 1)) + " and " + verbs[^1]
        };
        what = char.ToUpperInvariant(what[0]) + what[1..];
        var target = targetShape is { IsConstructor: true } ? $"the '{methodName}' constructor" : $"'{methodName}'";
        return $"{what} parameters of {target} in {fileName}.";
    }

    [McpServerTool(Name = "ChangeSignature")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reorders, adds, or removes method parameters and updates all call sites solution-wide. Refuses across an interface/implementer boundary - edit each side directly.")]
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ChangeSignatureTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.SymbolName, required: true)] string methodName, [Description("The desired end-state parameter list, in order. Each entry is {originalIndex: N} to keep an existing 0-based-index parameter, or {name, type, defaultValue} to insert a new one. Omit an originalIndex to remove that parameter.")][ExternalInputRequired(DataTag.Order, required: true)] ChangeSignatureParameterInput[] parameters, // autoStage removed from the schema: stage-then-apply is retired, the value is hardcoded to true below.
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          // [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
    [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, [Description(ToolParams.ContextSnippet)] string? contextSnippet = null, [Description(ToolParams.LineBefore)] string? lineBefore = null, [Description(ToolParams.LineAfter)] string? lineAfter = null, [Description("auto (default): old signature removed if every caller migrates, else kept as an [Obsolete] wrapper and the remaining callers listed. remove: refuses if any caller cannot migrate. keepAsDeprecatedWrapper: always keep. No wrapper (refused) for virtual/override/abstract/interface members, identical parameter types.")] ChangeSignatureOldSignature oldSignature = ChangeSignatureOldSignature.auto, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        // Hardcoded: autoStage is hidden from the schema until the server has a real staging mode.
        bool autoStage = true;
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var specs = new List<SignatureParameterSpec>();
            foreach (var p in parameters)
            {
                if (p.originalIndex is int idx)
                {
                    specs.Add(new ExistingParameterSpec(idx));
                }
                // With oldSignature auto or keepAsDeprecatedWrapper a new parameter without a defaultValue is passed through so the engine refuses it with its own explanation (the call sites and the wrapper have nothing to pass).
                else if (!string.IsNullOrEmpty(p.name) && !string.IsNullOrEmpty(p.type) && (!string.IsNullOrEmpty(p.defaultValue) || oldSignature != ChangeSignatureOldSignature.remove))
                {
                    specs.Add(new NewParameterSpec(p.name, p.type, p.defaultValue ?? string.Empty));
                }
                else
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "Each parameters entry must set either originalIndex (to keep an existing parameter), or all of name/type/defaultValue (to insert a new one).")
                    };
                }
            }

            var result = await _refactoringEngine.ChangeSignatureAsync(resolvedFilePath, methodName, specs, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken, oldSignature: oldSignature);
            if (result.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.Exception, result.Error)
                };
            var changes = result.Changes;
            // Read before ValidateAndApplyAsync writes: after an apply the committed declaration already has the new parameter list.
            var targetShape = await _refactoringEngine.GetChangeSignatureTargetShapeAsync(resolvedFilePath, methodName, contextSnippet, lineBefore, lineAfter, cancellationToken);
            // Reflection-style construction (ActivatorUtilities.CreateInstance and friends) compiles after a constructor change and fails at runtime: a Caution per site, in every outcome mode.
            var reflectiveSites = result.ReflectiveConstructionSites ?? [];
            var reflectiveFindings = reflectiveSites.Take(25)
                .Select(s => new Finding("ChangeSignature", $"{s.Reason}: check {Path.GetFileName(s.FilePath)}:{s.LineNumber}", FindingSeverity.Caution))
                .ToList();
            if (reflectiveSites.Count > 25)
            {
                reflectiveFindings.Add(new Finding("ChangeSignature", $"Reflective construction listing capped at 25: {reflectiveSites.Count} site(s) in total.", FindingSeverity.Caution));
            }

            if (!autoStage)
            {
                var noStageSummaryNote = DescribeSignatureChange(methodName, specs, targetShape, Path.GetFileName(resolvedFilePath));
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = false,
                    Findings = reflectiveFindings,
                    SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, changes, cancellationToken), null, changes.Keys.ToList(), noStageSummaryNote, dryRun: false, diff: null, changedContent: changes, validated: false)
                };
            }

            var apply = await ValidateAndApplyAsync(changes, $"Change signature of method '{methodName}'.", "ChangeSignature", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary>
                {
                    IsError = true,
                    ErrorData = apply.Error
                };
            var summaryNote = DescribeSignatureChange(methodName, specs, targetShape, Path.GetFileName(resolvedFilePath));
            var wrapperFindings = new List<Finding>();
            if (result.OldSignatureOutcome == ChangeSignatureOldSignature.remove)
            {
                // The outcome is stated for remove too (and for auto resolved to remove): the caller must not have to infer it from a missing wrapper.
                summaryNote += " Old signature removed: every caller was migrated.";
            }

            if (result.WrapperSignature is not null)
            {
                if (oldSignature == ChangeSignatureOldSignature.auto)
                {
                    wrapperFindings.Add(new Finding("ChangeSignature", $"oldSignature auto kept the old signature as an [Obsolete] wrapper because {result.SkippedCallSites.Count} call site(s) could not be migrated; the compiler (CS0618) lists them.", FindingSeverity.Caution));
                }

                // Wrapper mode: say which call sites moved to the new signature and which stay on the deprecated wrapper.
                IReadOnlyList<RewrittenCallSite> migrated = result.MigratedCallSites ?? [];
                var leftOnWrapper = result.SkippedCallSites;
                summaryNote = $"Changed '{methodName}' to {result.ReplacementSignature} in {Path.GetFileName(resolvedFilePath)}; migrated {migrated.Count} call site(s); kept {result.WrapperSignature} as an [Obsolete] wrapper"
                    + (leftOnWrapper.Count > 0
                        ? $", still used by {leftOnWrapper.Count} call site(s) left on it: " + string.Join("; ", leftOnWrapper.Take(10).Select(s => $"{Path.GetFileName(s.FilePath)}:{s.LineNumber} ({s.Reason})")) + (leftOnWrapper.Count > 10 ? $"; +{leftOnWrapper.Count - 10} more" : string.Empty)
                        : ", with no call site left on it (callers outside the solution still compile)")
                    + ".";
                wrapperFindings.AddRange(leftOnWrapper.Take(25).Select(s => new Finding("ChangeSignature", $"Left on deprecated wrapper {result.WrapperSignature}: {Path.GetFileName(s.FilePath)}:{s.LineNumber} ({s.Reason})", FindingSeverity.Caution)));
                wrapperFindings.AddRange(migrated.Take(25).Select(m => new Finding("ChangeSignature", $"Migrated {m.OldText} -> {m.NewText} ({Path.GetFileName(m.FilePath)}:{m.Line})")));
                if (leftOnWrapper.Count > 25 || migrated.Count > 25)
                {
                    wrapperFindings.Add(new Finding("ChangeSignature", $"Listing capped at 25 per kind: {migrated.Count} migrated, {leftOnWrapper.Count} left on the wrapper."));
                }
            }

            wrapperFindings.AddRange(reflectiveFindings);
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = false,
                Findings = wrapperFindings,
                SuccessData = AppliedChangeSummary.From(apply, changes.Keys.ToList(), summaryNote, changedContent: changes, validated: true)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeSignature failed for '{MethodName}' in '{FilePathWrapper}'", methodName, resolvedFilePath);
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ChangeSignature")
            };
        }
    }

    private async Task<SentinelCallToolResult<object>> MoveAllTypesToFilesCore(Dictionary<FilePathWrapper, string> changes, bool autoStage, bool dryRun, bool returnDiff, string description, bool previewFiles, CancellationToken cancellationToken = default)
    {
        if (!autoStage)
        {
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, changes, cancellationToken), null, changes.Keys.ToList(), description, dryRun: false, diff: null, changedContent: changes.Count == 0 ? null : changes, validated: false)
            };
        }

        if (changes.Count == 0)
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = "No secondary types found to move."
            };
        var apply = await ValidateAndApplyAsync(changes, description, "MoveAllTypesToFiles", dryRun, returnDiff, cancellationToken: cancellationToken);
        if (apply.Error is not null)
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = apply.Error
            };
        if (previewFiles)
        {
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = new
                {
                    ChangeId = apply.ChangeId,
                    DryRun = apply.DryRun,
                    Diff = apply.Diff,
                    Description = apply.ChangeId is not null ? $"{description}. Call {ToolCallHint.Call("UndoLastApply", ("changeId", apply.ChangeId))} to revert if needed." : description,
                    AffectedFiles = changes.Keys.Select(kvp => Path.GetFileName(kvp)).ToList(),
                    ContentPreviews = changes.ToDictionary(kvp => Path.GetFileName(kvp.Key)!, kvp => RefactoringToolHelpers.PreviewFileContent(kvp.Value))
                }
            };
        }

        return new SentinelCallToolResult<object>()
        {
            IsError = false,
            SuccessData = AppliedChangeSummary.From(apply, changes.Keys.ToList(), description, changedContent: changes, validated: true)
        };
    }

    [McpServerTool(Name = "MoveAllTypesToFiles")]
    [Produces(DataTag.Report)]
    [Description("Moves secondary types out of a file into their own files.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: target is required for scope=file (a file path) and scope=project (a project name); ignored for scope=solution. Enforced at runtime, not by the schema.
    public async Task<SentinelCallToolResult<object>> MoveAllTypesToFilesTool([Description(ToolParams.Reason)] ToolCallReason reason, [ExternalInputRequired(DataTag.Scope)] ToolScope scope, [Description("Required for scope=file/project (path or project name).")][ExternalInputRequired(DataTag.SourceFilepath), ExternalInputRequired(DataTag.ProjectName)] string? target = null, // autoStage removed from the schema: stage-then-apply is retired, the value is hardcoded to true below.
                                                                                                                                                                                                                                                                                                                                                                                         // [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage)] bool autoStage = true,
    [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        // Hardcoded: autoStage is hidden from the schema until the server has a real staging mode.
        bool autoStage = true;
        try
        {
            if (scope == ToolScope.file)
            {
                if (target is null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "target (file path) is required for scope=file.")
                    };
                }

                return await MoveAllTypesToFilesCore(await _refactoringEngine.MoveAllTypesToFilesAsync(target, cancellationToken: cancellationToken), autoStage, dryRun, returnDiff, $"Move all types to files in '{Path.GetFileName(target)}'", previewFiles: true, cancellationToken: cancellationToken);
            }

            if (scope == ToolScope.project)
            {
                if (target is null)
                {
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "target (project name) is required for scope=project.")
                    };
                }

                return await MoveAllTypesToFilesCore(await _refactoringEngine.MoveAllTypesToFilesInProjectAsync(target, cancellationToken: cancellationToken), autoStage, dryRun, returnDiff, $"Move all types to files in project '{target}'", previewFiles: false, cancellationToken: cancellationToken);
            }

            if (scope == ToolScope.solution)
            {
                return await MoveAllTypesToFilesCore(await _refactoringEngine.MoveAllTypesToFilesInSolutionAsync(cancellationToken: cancellationToken), autoStage, dryRun, returnDiff, "Move all types to files in solution", previewFiles: false, cancellationToken: cancellationToken);
            }

            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown scope '{scope}'. Valid: file, project, solution.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveAllTypesToFiles ({Scope}) failed", scope);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MoveAllTypesToFiles")
            };
        }
    }

    [McpServerTool(Name = "MoveMember")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves methods/properties/fields from one class to another as a single atomic change, rewriting call sites.")]
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> MoveMemberTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.ClassName, required: true)] string className, [Description("Members to move. Overloaded method: pass its signature, e.g. \"Save(int)\"; a bare overloaded name is refused.")][Consumes(DataTag.SymbolName, required: true)] string[] memberNames, [Description("Target class name: an existing base type pulls members up; an existing unrelated class receives them as-is (receivers rewritten per callSiteFixups); an existing STATIC class receives instance members that use no instance state as STATIC; callers are rewritten to 'Target.Member'; a nonexistent name creates a new class.")][ExternalInputRequired(DataTag.ClassName, required: true)] string targetClassName, [Description("Narrows which class named targetClassName to use, if ambiguous.")][ExternalInputRequired(DataTag.SourceFilepath)] string? targetFilepath = null, // autoStage removed from the schema: stage-then-apply is retired, the value is hardcoded to true below.
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     // [Description(ToolParams.AutoStage)][ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
    [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, [Description("Auto-rewrites unambiguous call sites when moving instance members. Set false to require manual resolution via callSiteFixups.")] bool autoResolveCallSites = true, [Description("Manual resolution for call sites autoResolveCallSites couldn't handle. Key: \"FilePath:Line\", \"FilePath:*\" (every unresolved site in that file) or \"*\" (every unresolved site); an exact line beats FilePath:*, which beats *. Value: a receiver expression (e.g. \"_target\" or \"new Target(_dep, _config)\" - pass ALL constructor arguments), or \"new\" = zero-argument 'new Target()' only.")] Dictionary<string, string>? callSiteFixups = null, [Description("Instance moves only. Maps a private field of the source class that the moved members use to an existing field of the target class with the same or a convertible type, e.g. {\"_readNav\":\"_impl\"}. References to the source field inside the moved bodies are rewritten to the target field; the source field stays in place.")] Dictionary<string, string>? fieldMap = null, [Description("How the source member is treated after the move. remove (default) deletes it. keepDelegator replaces it with a plain forwarder to the moved member. keepAsDeprecatedWrapper does the same with [Obsolete]. Callers are not rewritten in keep modes, which are refused for properties, fields, nested types, extension methods, virtual, abstract, override or interface members, base-type pull-up, and partial classes.")] MoveMemberOldMember oldMember = MoveMemberOldMember.remove, [Description("Keep modes on an instance move only: a receiver expression valid inside the source class that the forwarder calls through, e.g. _impl. Omit to let the engine pick the single target-typed field. Refused with remove or with a static move.")] string? forwarderReceiver = null, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        // Hardcoded: autoStage is hidden from the schema until the server has a real staging mode.
        bool autoStage = true;
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            if (memberNames == null || memberNames.Length == 0)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "memberNames is required and must be non-empty.")
                };
            }

            FilePathWrapper? targetFilePath = string.IsNullOrEmpty(targetFilepath) ? (FilePathWrapper?)null : _workspaceManager.ResolveFromWire(targetFilepath);
            var result = await _memberRefactoringEngine.MoveMemberAsync(resolvedFilePath, className, memberNames, targetClassName, targetFilePath, cancellationToken, autoResolveCallSites, callSiteFixups, fieldMap, oldMember, forwarderReceiver);
            if (!autoStage)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, result.Changes, cancellationToken), null, result.Changes.Keys.ToList(), $"Move [{string.Join(", ", memberNames)}] from '{className}' to '{targetClassName}'.", dryRun: false, diff: null, changedContent: result.Changes.Count == 0 ? null : result.Changes, validated: false)
                };
            }

            // In MultiAgent mode another agent could edit the unresolved files between the steps of a multi-step
            // edit, so refuse before anything is written. dryRun is unchanged.
            if (!dryRun && AgentOperatingModeOptions.Effective == AgentOperatingMode.MultiAgent && result.PendingLedgerEntries is { Count: > 0 } multiAgentPending)
            {
                return UnresolvedCallSitesResult(multiAgentPending, "MultiAgent mode: a move that leaves call sites unresolved needs a multi-step edit, which is refused here, so nothing was written. ");
            }

            var apply = await ValidateAndApplyAsync(result.Changes, $"Move [{string.Join(", ", memberNames)}] from '{className}' to '{targetClassName}'.", "MoveMember", dryRun, returnDiff, cancellationToken: cancellationToken, expectedEdits: result.AppliedEdits);
            if (apply.Error is not null)
            {
                // A collateral-change or EOL-change refusal is the real cause on its own (a step after the engine's edits
                // rewrote text they did not touch, or the write would flip line endings), so return it as-is rather than
                // let the unresolved-call-site hints below mask it.
                if (IsRefusalThatMustNotBeMasked(apply.Error.ErrorCode))
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>
                    {
                        IsError = true,
                        ErrorData = apply.Error
                    };
                }

                // The compile-gate rejection is often just a symptom: the engine already knows
                // exactly which call sites it couldn't rewrite and why (BlockReason,
                // CandidatesInScope) - surface that instead of the raw compiler-error dump so the
                // caller can retry with a correct callSiteFixups entry rather than guessing.
                if (result.PendingLedgerEntries is { Count: > 0 } unresolvedEntries)
                {
                    var unresolvedResult = UnresolvedCallSitesResult(unresolvedEntries, string.Empty);
                    return unresolvedResult with { ErrorData = unresolvedResult.ErrorData! with { Findings = apply.Error.Findings } };
                }

                if (result.SkippedCallSites.Count > 0)
                {
                    var detail = string.Join("; ", result.SkippedCallSites.Select(s => $"{s.FilePath}:{s.LineNumber} ({s.Reason})"));
                    return new SentinelCallToolResult<AppliedChangeSummary>
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.UnresolvedCallSites, $"{result.SkippedCallSites.Count} call site(s) could not be automatically rewritten, so the move was rejected. Retry with callSiteFixups keyed \"FilePath:Line\" (the full path shown below) for each site, or \"FilePath:*\" / \"*\" to apply one value to every unresolved site in a file / everywhere.", detail, result.SkippedCallSites.Cast<object>().ToList()) { Findings = apply.Error.Findings }
                    };
                }

                // Raw compile-gate rejection. If caller-supplied fixups were applied and the errors sit
                // on the very lines those fixups rewrote, say so up front - otherwise the dump reads as
                // a compiler problem, not as "your fixup value is wrong".
                var rawError = callSiteFixups is { Count: > 0 } && apply.Error.ErrorCode != ToolErrorCode.EolChangeRefused ? AttributeValidationErrorsToCallSiteFixups(apply.Error, apply.Validation, result.AppliedFixups, _workspaceManager.GetSolutionRoot()) : apply.Error;
                return new SentinelCallToolResult<AppliedChangeSummary>
                {
                    IsError = true,
                    ErrorData = rawError
                };
            }

            var summaryNote = $"Moved [{string.Join(", ", memberNames)}] from '{className}' to '{targetClassName}'.";
            IReadOnlyList<RewrittenCallSite> rewrittenCallSites = result.RewrittenCallSites ?? [];
            var conversionFindings = (result.Notes ?? []).Select(n => new Finding("MoveMember", n, FindingSeverity.Caution))
                .Concat(rewrittenCallSites.Take(25).Select(r => new Finding("MoveMember", $"Rewrote same-class call {r.OldText} -> {r.NewText} ({r.FilePath}:{r.Line})", FindingSeverity.Caution)))
                .Concat(rewrittenCallSites.Count > 25 ? new[] { new Finding("MoveMember", $"+{rewrittenCallSites.Count - 25} more rewritten call sites", FindingSeverity.Caution) } : Array.Empty<Finding>())
                .ToList();
            // Ledger opened here, after the atomic apply above has actually landed - not inside
            // MoveInstanceMembersAsync, since IsBlocked would otherwise refuse that same apply's
            // own source/target file write (neither necessarily has an unresolved ledger entry for
            // itself). dryRun never opens a ledger: nothing was written, so there is nothing to track.
            if (!dryRun && result.PendingLedgerEntries is { Count: > 0 } pendingEntries)
            {
                var opened = ((IScopedOperationLedger)_workspaceManager).TryOpen(result.PendingLedgerOperationName ?? "MoveMember", pendingEntries, out var rejectionReason, apply.ChangeId);
                summaryNote += opened ? $" Opened a scoped operation ledger with {pendingEntries.Count} unresolved call site(s) - resolve them before making unrelated changes." : $" WARNING: {pendingEntries.Count} call site(s) could not be automatically rewritten, and a ledger could not be opened to track them ({rejectionReason}). Fix them manually: " + string.Join("; ", pendingEntries.Select(e => $"{Path.GetFileName(e.FilePath)}:{e.Line} ({e.BlockReason})"));
            }

            if (result.SkippedCallSites.Count > 0)
                summaryNote += $" {result.SkippedCallSites.Count} call site(s) could not be automatically rewritten and must be fixed manually: " + string.Join("; ", result.SkippedCallSites.Select(s => $"{Path.GetFileName(s.FilePath)}:{s.LineNumber} ({s.Reason})"));
            // Not wired into MemberChangedContentResult: this can touch 2-3 files (source, target,
            // and any rewritten call-site files) with no single "the new text" the way Member's
            // single-file operations do. The moved text is NOT echoed on a real apply or a dry run:
            // returnDiff defaults to false and AppliedChangeSummary drops ChangedContent unless
            // InlineOnApply is on. Fetch it via the largeResult handle (apply), or pass returnDiff: true.
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = false,
                Findings = conversionFindings,
                SuccessData = AppliedChangeSummary.From(apply, result.Changes.Keys.ToList(), summaryNote, changedContent: result.Changes, validated: true, editSpans: apply.DryRun ? AppliedEditSpan.FromEdits(result.AppliedEdits) : null)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveMember failed for [{MemberNames}] in '{ClassName}'", string.Join(", ", memberNames ?? []), className);
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MoveMember")
            };
        }
    }
}
