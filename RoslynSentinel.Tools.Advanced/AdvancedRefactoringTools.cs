using System.ComponentModel;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Advanced;
[McpServerToolType]
public class AdvancedRefactoringTools
{
    private readonly BasicRefactoringEngine _refactoringEngine;
    private readonly AdvancedRefactoringEngine _advancedRefactoringEngine;
    private readonly SemanticRefactoringEngine _semanticRefactoringEngine;
    private readonly StructuralRefactoringEngine _advancedStructuralEngine;
    // TODO _memberRefactoringEngine is assigned but never read since MoveMember moved to CoreRefactoringTools; drop it (and the constructor parameter) in the DI-shape cleanup slice of the core split, with BuildDiffAsync/BuildDiffFromPreImages below, which have no caller.
    private readonly MemberRefactoringEngine _memberRefactoringEngine;
    private readonly MappingEngine _mappingEngine;
    private readonly MsToolAugmentEngine _augmentEngine;
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<AdvancedRefactoringTools> _logger;
    // Which tools this server can serve, for the edit-impact next step; null (unit tests, hosts without a surface) = every tool callable.
    private readonly ActiveToolSurface? _activeToolSurface;
    public AdvancedRefactoringTools(IWorkspaceManager workspaceManager)
    {
        _validationEngine = new ValidationEngine(workspaceManager);
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
        _refactoringEngine = new BasicRefactoringEngine(workspaceManager);
        _advancedRefactoringEngine = new AdvancedRefactoringEngine(workspaceManager);
        _memberRefactoringEngine = new MemberRefactoringEngine(workspaceManager, _symbolNavigationEngine, _validationEngine);
        _semanticRefactoringEngine = new SemanticRefactoringEngine(workspaceManager);
        _advancedStructuralEngine = new StructuralRefactoringEngine(workspaceManager);
        _mappingEngine = new MappingEngine(workspaceManager);
        _augmentEngine = new MsToolAugmentEngine(workspaceManager);
        _codeGenerationEngine = new CodeGenerationEngine(workspaceManager);
        _workspaceManager = workspaceManager;
        _config = new SentinelConfiguration();
        _logger = NullLogger<AdvancedRefactoringTools>.Instance;
    }

    public AdvancedRefactoringTools(BasicRefactoringEngine refactoringEngine, AdvancedRefactoringEngine advancedRefactoringEngine, StructuralRefactoringEngine advancedStructuralEngine, MemberRefactoringEngine memberRefactoringEngine, SemanticRefactoringEngine semanticRefactoringEngine, MappingEngine mappingEngine, SyntaxModernizationEngine modernizationEngine, MsToolAugmentEngine augmentEngine, CodeGenerationEngine codeGenerationEngine, SymbolNavigationEngine symbolNavigationEngine, IWorkspaceManager workspaceManager, ValidationEngine validationEngine, SentinelConfiguration config, ILogger<AdvancedRefactoringTools> logger, ActiveToolSurface? activeToolSurface = null)
    {
        _activeToolSurface = activeToolSurface;
        _refactoringEngine = refactoringEngine;
        _advancedRefactoringEngine = advancedRefactoringEngine;
        _advancedStructuralEngine = advancedStructuralEngine;
        _memberRefactoringEngine = memberRefactoringEngine;
        _semanticRefactoringEngine = semanticRefactoringEngine;
        _mappingEngine = mappingEngine;
        _augmentEngine = augmentEngine;
        _codeGenerationEngine = codeGenerationEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _workspaceManager = workspaceManager;
        _validationEngine = validationEngine;
        _config = config;
        _logger = logger;
    }

    private async Task<string?> GetFileNotInSolutionError(FilePathWrapper filePath, CancellationToken cancellationToken)
    {
        System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.DocumentId>? ids;
        try
        {
            var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
            ids = solution.GetDocumentIdsWithFilePath(filePath);
        }
        catch (SolutionNotLoadedException)
        {
            ids = null;
        }

        if (ids == null || ids.Value.Length == 0)
            return $"File '{Path.GetFileName(filePath)}' not found in the loaded solution. " + $"Verify the path is correct and the solution is loaded. " + $"Loaded projects: {_workspaceManager.ProjectCount}.";
        return null;
    }

    /// <summary>
    /// Validates proposed changes against the current in-memory solution and, unless
    /// <paramref name = "dryRun"/> is set, writes them straight to disk (write-through -> no
    /// intermediate staging step). Rolls back any already-written files if a multi-file change
    /// partially fails, so a change never lands half-applied.
    /// </summary>
    private Task<ApplyOutcome> ValidateAndApplyAsync(Dictionary<FilePathWrapper, string> changes, string description, string operationName, bool dryRun = false, bool returnDiff = false, IProgress<EngineProgress>? progress = default, IReadOnlyCollection<FilePathWrapper>? removePaths = null, CancellationToken cancellationToken = default, IReadOnlyCollection<FilePathWrapper>? deletePaths = null, IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>? expectedEdits = null) => ValidateAndApplyHelper.ValidateAndApplyAsync(_validationEngine, _workspaceManager, _logger, changes, operationName, dryRun, returnDiff, progress, removePaths, cancellationToken: cancellationToken, deletePaths, describeValidationFailure: (report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, ct, isToolActive: _activeToolSurface is null ? null : _activeToolSurface.IsCallable), expectedEdits: expectedEdits, describeEditImpact: EditImpactGate.Describer(_workspaceManager, changes, _activeToolSurface, removePaths, deletePaths));
    private Task<string> BuildDiffAsync(Dictionary<FilePathWrapper, string> changes, CancellationToken cancellationToken = default) => ValidateAndApplyHelper.BuildDiffAsync(_workspaceManager, changes, cancellationToken);
    private static string BuildDiffFromPreImages(Dictionary<FilePathWrapper, string> changes, IReadOnlyDictionary<string, string?>? preImages) => ValidateAndApplyHelper.BuildDiffFromPreImages(changes, preImages);

    [McpServerTool(Name = "ConvertAnonymousToNamed")]
    [Produces(DataTag.ChangeId)]
    [Description("Converts the first anonymous object creation in a file into a named class declaration.")]
    public async Task<SentinelCallToolResult<object>> ConvertAnonymousToNamedTool([Description(ToolParams.Reason)] ToolCallReason reason, [ExternalInputRequired(DataTag.SourceFilepath, required: true)] string filePath, [ExternalInputRequired(DataTag.ClassName, required: true)] string newClassName, [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var changes = await _advancedStructuralEngine.ConvertAnonymousToNamedAsync(resolvedFilePath, newClassName, cancellationToken: cancellationToken);
            if (changes.Count == 0)
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.NotFound, $"ConvertAnonymousToNamed: no anonymous object found in '{resolvedFilePath}'.")
                };
            // Not wired into MemberChangedContentResult: the generated class's text isn't caller-
            // supplied or separately exposed -> ConvertAnonymousToNamedAsync only returns the whole-file
            // Changes dict, so showing just the new class declaration here would mean duplicating its
            // formatting logic. Revisit only if that engine method starts returning the new class's text
            // alongside the file changes.
            var apply = await ValidateAndApplyAsync(changes, $"Convert anonymous object to '{newClassName}'.", "ConvertAnonymousToNamed", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = apply.Error
                };
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = AppliedChangeSummary.From(apply, changes.Keys.ToList(), $"Converted anonymous object to named class '{newClassName}' in {Path.GetFileName(resolvedFilePath)}.", validated: false)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConvertAnonymousToNamed failed for '{NewClassName}' in '{FilePathWrapper}'", newClassName, resolvedFilePath);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ConvertAnonymousToNamed")
            };
        }
    }

    [McpServerTool(Name = "InlineClass")]
    [Produces(DataTag.ChangeId)]
    [Description("Merges a source class's members into a target class and removes the source declaration.")]
    public async Task<SentinelCallToolResult<object>> InlineClassTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string rawSourceFilePath, [Consumes(DataTag.SourceFilepath, required: true)] string rawTargetFilePath, [Consumes(DataTag.SymbolName, required: true)] string className, [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        FilePathWrapper sourceFilePath = _workspaceManager.ResolveFromWire(rawSourceFilePath);
        FilePathWrapper targetFilePath = _workspaceManager.ResolveFromWire(rawTargetFilePath);
        try
        {
            var changes = await _advancedStructuralEngine.InlineClassAsync(sourceFilePath, targetFilePath, className, cancellationToken: cancellationToken);
            var apply = await ValidateAndApplyAsync(changes, $"Inline class '{className}' into target.", "InlineClass", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = apply.Error
                };
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = AppliedChangeSummary.From(apply, changes.Keys.ToList(), $"Inlined '{className}' members into target class across {changes.Count} file(s).", validated: false)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InlineClass failed for '{ClassName}'", className);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "InlineClass")
            };
        }
    }

    [McpServerTool(Name = "InvertAssignments")]
    [Produces(DataTag.ChangeId)]
    [Description("Swaps left and right sides of all assignment statements within a range.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of (startLine and endLine) or contextSnippet must be supplied; none of these params is individually required by the schema.
    public async Task<SentinelCallToolResult<object>> InvertAssignmentsTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Description("Provide both startLine and endLine, or use contextSnippet instead.")][Consumes(DataTag.StartLine)] int startLine = 0, [Description("Provide both startLine and endLine, or use contextSnippet instead.")][Consumes(DataTag.EndLine)] int endLine = 0, [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null, [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null, [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null, [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            // Validate that we have either contextSnippet or both startLine/endLine
            if (!string.IsNullOrWhiteSpace(contextSnippet))
            {
                var result = await _mappingEngine.InvertAssignmentsAsync(resolvedFilePath, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
                var changes = new Dictionary<FilePathWrapper, string>
                {
                    [resolvedFilePath] = result.UpdatedText
                };
                var apply = await ValidateAndApplyAsync(changes, $"Invert assignments in snippet.", "InvertAssignments", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (apply.Error is not null)
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = apply.Error
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(apply, [resolvedFilePath], $"Inverted assignments in snippet of {Path.GetFileName(resolvedFilePath)}.", validated: false)
                };
            }
            else if (startLine > 0 && endLine > 0)
            {
                var result = await _mappingEngine.InvertAssignmentsAsync(resolvedFilePath, startLine, endLine, cancellationToken: cancellationToken);
                var changes = new Dictionary<FilePathWrapper, string>
                {
                    [resolvedFilePath] = result.UpdatedText
                };
                var apply = await ValidateAndApplyAsync(changes, $"Invert assignments in lines {startLine}-{endLine}.", "InvertAssignments", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (apply.Error is not null)
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = apply.Error
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(apply, [resolvedFilePath], $"Inverted assignments in lines {startLine}-{endLine} of {Path.GetFileName(resolvedFilePath)}.", validated: false)
                };
            }
            else
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "Either provide contextSnippet or both startLine and endLine (1-based).")
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvertAssignments failed in '{FilePathWrapper}'", resolvedFilePath);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "InvertAssignments")
            };
        }
    }

    // The engine message is "Introduced parameter object 'X'." followed by an optional "Updated N implementation(s) in M file(s)."
    // and an optional "WARNING: ..." sentence; the summary keeps the tail (counts and warnings) after its own first sentence.
    private static string IntroduceParameterObjectDescription(string methodName, string filePath, string? engineMessage)
    {
        var description = $"Introduced parameter object for '{methodName}' in {Path.GetFileName(filePath)}.";
        var tailStart = engineMessage is null ? -1 : engineMessage.IndexOf("'.", StringComparison.Ordinal);
        var tail = tailStart < 0 ? string.Empty : engineMessage![(tailStart + 2)..].Trim();
        if (tail.Length == 0)
        {
            return description;
        }

        // Implementers were rewritten: their call sites are still the caller's job.
        return tail.Contains("Updated ", StringComparison.Ordinal) ? $"{description} {tail} Call sites need manual follow-up." : $"{description} {tail}";
    }

    [McpServerTool(Name = "IntroduceParameterObject")]
    [Produces(DataTag.ChangeId)]
    [Description("Encapsulates method parameters into a new C# 12 record type; call sites need manual follow-up (flagged with TODO).")]
    public async Task<SentinelCallToolResult<object>> IntroduceParameterObjectTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.SymbolName, required: true)] string methodName, [Description("Name for the generated record type. Defaults to a name derived from the method.")] string? newTypeName = null, [Description("Parameters to group into the record. Defaults to all non-CancellationToken parameters.")] string[]? parameterNames = null, [Description(ToolParams.ContextSnippet)][ExternalInputRequired(DataTag.ContextSnippet, required: false)] string? contextSnippet = null, [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore, required: false)] string? lineBefore = null, [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter, required: false)] string? lineAfter = null, [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var fileErr = await GetFileNotInSolutionError(resolvedFilePath, cancellationToken);
            if (fileErr != null)
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.NotFound, fileErr)
                };
            var result = await _advancedStructuralEngine.IntroduceParameterObjectAsync(resolvedFilePath, methodName, newTypeName, parameterNames, contextSnippet, lineBefore, lineAfter, cancellationToken: cancellationToken);
            // Not wired into MemberChangedContentResult: the generated record's text isn't caller-
            // supplied or separately exposed -> IntroduceParameterObjectAsync only returns the whole-file
            // UpdatedText, so showing just the new record here would mean duplicating its formatting
            // logic. Revisit only if that engine method starts returning the record's text alongside
            // UpdatedText.
            // An interface-targeted method also edits every implementer (other files, plus the using they need): the engine
            // then returns every changed file in Changes (the target included); otherwise only the target file changed.
            var changes = result.Changes.Count > 0
                ? new Dictionary<FilePathWrapper, string>(result.Changes)
                : new Dictionary<FilePathWrapper, string>
                {
                    [resolvedFilePath] = result.UpdatedText
                };
            var apply = await ValidateAndApplyAsync(changes, $"Introduce parameter object for '{methodName}'.", "IntroduceParameterObject", dryRun, returnDiff, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = apply.Error
                };
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = AppliedChangeSummary.From(apply, changes.Keys.ToList(), IntroduceParameterObjectDescription(methodName, resolvedFilePath, result.Message), validated: false)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IntroduceParameterObject failed for '{MethodName}' in '{FilePathWrapper}'", methodName, resolvedFilePath);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "IntroduceParameterObject")
            };
        }
    }

    [McpServerTool(Name = "Introduce")]
    [Produces(DataTag.ChangeId)]
    [Description("Introduces a named local variable, field, parameter, or constant from an expression.")]
    public async Task<SentinelCallToolResult<object>> IntroduceSymbolTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.ContextSnippet, required: true)][Description("Short unique fragment identifying the target when its name alone is ambiguous, copied verbatim from a prior result.")] string contextSnippet, [ExternalInputRequired(DataTag.SymbolName)][Description("The name of the new symbol to introduce.")] string newName, [ExternalInputRequired(DataTag.SymbolKind)][Description("The kind of symbol to introduce.")] IntroduceAsType newType, [Consumes(DataTag.LineBefore)] string? lineBefore = null, [Consumes(DataTag.LineAfter)] string? lineAfter = null, [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            DocumentEditResult result;
            string stageDesc;
            if (newType == IntroduceAsType.localVariable)
            {
                result = await _advancedStructuralEngine.IntroduceVariableAsync(resolvedFilePath, contextSnippet, newName, lineBefore, lineAfter, cancellationToken: cancellationToken);
                stageDesc = $"Introduce local variable '{newName}'.";
            }
            else if (newType == IntroduceAsType.field)
            {
                result = await _advancedStructuralEngine.IntroduceFieldAsync(resolvedFilePath, contextSnippet, newName, lineBefore, lineAfter, cancellationToken: cancellationToken);
                stageDesc = $"Introduce field '{newName}'.";
            }
            else if (newType == IntroduceAsType.parameter)
            {
                result = await _advancedStructuralEngine.IntroduceParameterAsync(resolvedFilePath, contextSnippet, newName, lineBefore, lineAfter, cancellationToken: cancellationToken);
                stageDesc = $"Introduce parameter '{newName}'.";
            }
            else if (newType == IntroduceAsType.constant)
            {
                var constResult = await _augmentEngine.ExtractConstantSafeAsync(resolvedFilePath, contextSnippet, newName, lineBefore, lineAfter, cancellationToken: cancellationToken);
                if (constResult.IsError || string.IsNullOrEmpty(constResult.UpdatedContent))
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(constResult.ErrorCode ?? ToolErrorCode.Exception, constResult.Error ?? "ExtractConstantSafe failed.")
                    };
                var constChanges = new Dictionary<FilePathWrapper, string>
                {
                    [resolvedFilePath] = constResult.UpdatedContent
                };
                var constApply = await ValidateAndApplyAsync(constChanges, $"Introduce constant '{newName}'.", "Introduce(constant)", dryRun, returnDiff, cancellationToken: cancellationToken); // hint-ok: operation label, not a call to make
                if (constApply.Error is not null)
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = constApply.Error
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(constApply, [resolvedFilePath], $"Introduced '{newName}' as a constant in {Path.GetFileName(resolvedFilePath)}.", validated: false)
                };
            }
            else
            {
                return new SentinelCallToolResult<object>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown newType '{newType}'. Valid values: localVariable, field, parameter, constant.")
                };
            }

            // Not wired into MemberChangedContentResult: the new declaration's text isn't caller-
            // supplied or separately exposed -> IntroduceVariable/Field/ParameterAsync only return
            // the whole-file UpdatedText, so showing just the new declaration here would mean
            // duplicating their formatting logic. Revisit only if those engine methods start
            // returning the introduced declaration's text alongside UpdatedText.
            var changes = new Dictionary<FilePathWrapper, string>
            {
                [resolvedFilePath] = result.UpdatedText
            };
            var apply = await ValidateAndApplyAsync(changes, stageDesc, $"Introduce({newType})", dryRun, returnDiff, cancellationToken: cancellationToken); // hint-ok: operation label, not a call to make
            if (apply.Error is not null)
                return new SentinelCallToolResult<object>
                {
                    IsError = true,
                    ErrorData = apply.Error
                };
            return new SentinelCallToolResult<object>()
            {
                IsError = false,
                SuccessData = AppliedChangeSummary.From(apply, [resolvedFilePath], $"Introduced '{newName}' as {(newType == IntroduceAsType.localVariable ? "a local variable" : newType)} in {Path.GetFileName(resolvedFilePath)}.", validated: false)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Introduce ({As}) failed for '{NewName}' in '{FilePathWrapper}'", newType, newName, resolvedFilePath);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Introduce")
            };
        }
    }

    // Not wired into MemberChangedContentResult for any of its three branches (interface/partial/
    // superclass): each engine call (ExtractInterfaceAsync/ExtractMembersToPartialAsync/
    // ExtractSuperclassAsync) only returns whole-file Changes dicts, with no separately-exposed "just the
    // new type's text" fragment. Wiring this needs an engine-API-extension pass, not tool-layer wiring ->
    // revisit only if those engine methods start returning the extracted type's text alongside Changes.
    // newType=class was removed in favor of MoveMemberTool, which supersedes it (targetClassName omitted from the
    // solution -> same new-class behavior) and additionally supports moving into an EXISTING class.
    [McpServerTool(Name = "ExtractMembers")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts class members into a new interface or partial class (newType=superclass is not implemented).")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: newTypeName required for newType=interface/superclass; memberNames required for newType=partial; none individually required by the schema. Enforced at runtime.
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ExtractMembersTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)][Description("Source file containing the members to extract.")] string filePath, [Consumes(DataTag.SymbolName, required: true)][Description("The name of the class from which to extract members.")] string className, [ExternalInputRequired(DataTag.SymbolKind)][Description("Extraction target kind: interface, partial class, or superclass.")] ExtractAsType newType, [ExternalInputRequired(DataTag.SymbolName)][Description("Required for newType=interface/superclass: name of the new type.")] string? newTypeName = null, [ExternalInputRequired(DataTag.SymbolName)][Description("Required for newType=partial class: members to extract.")] string[]? memberNames = null, [ExternalInputRequired(DataTag.SourceFilepath)][Description("Required for newType=superclass: file paths matching classNames.")] string[]? memberFilePaths = null, [ExternalInputRequired(DataTag.ClassName)][Description("Required for newType=superclass: classes to extract common members from.")] string[]? classNames = null, // autoStage removed from the schema: stage-then-apply is retired, the value is hardcoded to true below.
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   // [ToolOption(ToolOptionTag.AutoStage, required: false)][Description("true (default) = write immediately; false = return content without writing.")] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        // Hardcoded: autoStage is hidden from the schema until the server has a real staging mode.
        bool autoStage = true;
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        FilePathWrapper[] resolvedMemberFilePaths = Enumerable.Empty<FilePathWrapper>().ToArray();

        if (memberFilePaths is not null)
        {
            resolvedMemberFilePaths = memberFilePaths.Select(p => _workspaceManager.ResolveFromWire(p)).ToArray() ?? Enumerable.Empty<FilePathWrapper>().ToArray();
        }

        var actualFilePaths = resolvedMemberFilePaths.Length > 0 ? resolvedMemberFilePaths : new[]
        {
            resolvedFilePath
        };

        try
        {
            if (newType == ExtractAsType.@interface)
            {
                if (string.IsNullOrEmpty(newTypeName))
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "newTypeName (interface name) is required when newType=interface.")
                    };
                }

                try
                {
                    var warnings = new List<string>();
                    var changes = await _refactoringEngine.ExtractInterfaceAsync(resolvedFilePath, className, newTypeName, cancellationToken: cancellationToken, warnings: warnings);
                    if (!autoStage)
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = false,
                            SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, changes, cancellationToken), null, changes.Keys.ToList(), $"Extract interface '{newTypeName}' from '{className}'.", dryRun: false, diff: null, changedContent: changes.Count == 0 ? null : changes, validated: false)
                        };
                    }

                    var apply = await ValidateAndApplyAsync(changes, $"Extract interface '{newTypeName}' from '{className}'.", "ExtractMembers_interface", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary>
                        {
                            IsError = true,
                            ErrorData = apply.Error
                        };
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = false,
                        SuccessData = AppliedChangeSummary.From(apply, changes.Keys.ToList(), $"Extracted interface '{newTypeName}' from '{className}'.", changedContent: changes, validated: true),
                        WarningDetails = warnings.Count > 0 ? string.Join(" ", warnings) : null
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExtractMembers/interface unexpected exception for '{NewTypeName}'", newTypeName);
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = true,
                        ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ExtractMembers newType=interface for '{newTypeName}'")
                    };
                }
            }

            if (newType == ExtractAsType.partialClass)
            {
                if (memberNames == null || memberNames.Length == 0)
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "memberNames is required when newType=partial.")
                    };
                }

                var partialChanges = await _advancedStructuralEngine.ExtractMembersToPartialWithSourceAsync(resolvedFilePath, className, memberNames, cancellationToken: cancellationToken);
                if (!autoStage)
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = false,
                        SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, partialChanges, cancellationToken), null, partialChanges.Keys.ToList(), $"Extract members to partial for '{className}'.", dryRun: false, diff: null, changedContent: partialChanges.Count == 0 ? null : partialChanges, validated: false)
                    };
                }

                var partialApply = await ValidateAndApplyAsync(partialChanges, $"Extract members to partial for '{className}'.", "ExtractMembers_partial", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (partialApply.Error is not null)
                    return new SentinelCallToolResult<AppliedChangeSummary>
                    {
                        IsError = true,
                        ErrorData = partialApply.Error
                    };
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(partialApply, partialChanges.Keys.ToList(), $"Extracted members of '{className}' to a new partial file.", changedContent: partialChanges, validated: true)
                };
            }

            if (newType == ExtractAsType.superclass)
            {
                // ExtractSuperclass is DELIBERATELY left unimplemented, low priority - do not re-litigate in reviews.
                // ExtractSuperclassAsync is a stub (copies only public properties, never rebases the derived class); refuse rather than report a half-done refactoring.
                throw new ToolNotImplementedException($"ExtractMembers newType=superclass is not implemented: it would only copy public properties into a new class and not rebase the derived class. Use newType=interface or newType=partial, or create the base class with {ToolCallHint.Call("CreateFile")}/Member and use {ToolCallHint.Call("ModifyBaseType")} on the derived class.");
            }

            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown newType '{newType}'. Valid values: interface, partial, superclass. For newType=class, use {ToolCallHint.Call("MoveMember")} instead.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractMembers ({As}) failed for '{ClassName}' in '{FilePathWrapper}'", newType, className, resolvedFilePath);
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ExtractMembers")
            };
        }
    }

    [McpServerTool(Name = "SyncInterface")]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates stub implementations, syncs missing members into an interface, or verifies implementation coverage.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: className required for action=implement/sync; not needed for action=verify. Enforced at runtime, not by the schema.
    public async Task<SentinelCallToolResult<object>> SyncInterfaceTool([Description(ToolParams.Reason)] ToolCallReason reason, [Description("Required for action=implement/sync; ignored for verify.")][Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.SymbolName, required: true)] string interfaceName, [Description("implement: stub unimplemented interface members on className. sync: add className's missing public members to interfaceName. verify: report coverage across implementers (className not needed).")][Consumes(DataTag.Action, required: true)] SyncInterfaceAction action, [Description("Required for action=implement/sync.")][Consumes(DataTag.SymbolName)] string? className = null, [Description("Scopes action=verify to one project.")][Consumes(DataTag.ProjectName)] string? projectName = null, [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
            if (action == SyncInterfaceAction.implement)
            {
                if (string.IsNullOrEmpty(className))
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required when action=implement.")
                    };
                var implFileErr = await GetFileNotInSolutionError(resolvedFilePath, cancellationToken);
                if (implFileErr != null)
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.NotFound, implFileErr)
                    };
                var implResult = await _codeGenerationEngine.ImplementInterfaceAsync(resolvedFilePath, className, interfaceName, cancellationToken: cancellationToken);
                var implGuard = RefactoringToolHelpers.RequireUpdatedText(implResult, "SyncInterface implement", resolvedFilePath, $"Verify the class and interface names (case-sensitive); use {ToolCallHint.Call("Search", ("mode", "symbol"))} to confirm both exist in the solution.");
                if (implGuard != null) return implGuard;
                var implChanges = new Dictionary<FilePathWrapper, string>
                {
                    [resolvedFilePath] = implResult.UpdatedText
                };
                var implApply = await ValidateAndApplyAsync(implChanges, $"Implement '{interfaceName}' on '{className}'.", "SyncInterface_implement", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (implApply.Error is not null)
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = implApply.Error
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(implApply, [resolvedFilePath], $"Implemented '{interfaceName}' on '{className}' in {Path.GetFileName(resolvedFilePath)}.", validated: false)
                };
            }

            if (action == SyncInterfaceAction.sync)
            {
                if (string.IsNullOrEmpty(className))
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required when action=sync.")
                    };
                var syncFileErr = await GetFileNotInSolutionError(resolvedFilePath, cancellationToken);
                if (syncFileErr != null)
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.NotFound, syncFileErr)
                    };
                var syncResult = await _advancedRefactoringEngine.SyncInterfaceToImplementationAsync(resolvedFilePath, className, interfaceName, cancellationToken: cancellationToken);
                var syncGuard = RefactoringToolHelpers.RequireUpdatedText(syncResult, "SyncInterface sync", resolvedFilePath, $"Verify the class and interface names (case-sensitive); use {ToolCallHint.Call("Search", ("mode", "symbol"))} to confirm both exist in the solution.");
                if (syncGuard != null) return syncGuard;
                // Cross-file sync: the engine returns the interface's text with the interface's path in FilePath.
                var syncTargetFile = !string.IsNullOrEmpty(syncResult.FilePath) && !string.Equals(syncResult.FilePath, resolvedFilePath, PathComparison.Comparison) ? syncResult.FilePath : resolvedFilePath;
                var syncChanges = new Dictionary<FilePathWrapper, string>
                {
                    [syncTargetFile] = syncResult.UpdatedText
                };
                var syncApply = await ValidateAndApplyAsync(syncChanges, $"Sync '{interfaceName}' to '{className}' implementation.", "SyncInterface_sync", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (syncApply.Error is not null)
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = syncApply.Error
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(syncApply, [syncTargetFile], $"Synced '{interfaceName}' to '{className}' implementation in {Path.GetFileName(syncTargetFile)}.", validated: false)
                };
            }

            if (action == SyncInterfaceAction.verify)
            {
                var result = await _symbolNavigationEngine.VerifyInterfaceCompletenessAsync(interfaceName, projectName, cancellationToken);
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = result
                };
            }

            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown action '{action}'. Valid values: implement, sync, verify.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncInterface ({Action}) failed for '{InterfaceName}'", action, interfaceName);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SyncInterface")
            };
        }
    }

    [McpServerTool(Name = "Inline")]
    [Produces(DataTag.ChangeId)]
    [Description("Inlines a symbol by replacing all usages with its definition.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: methodName required for kind=parameter; not needed otherwise. Enforced at runtime, not by the schema.
    public async Task<SentinelCallToolResult<object>> InlineSymbolTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Description("The symbol to inline.")][Consumes(DataTag.SymbolName, required: true)] string targetName, [Description("method: inline body at call sites (expression-body/single-return only). variable/field: inline into usages. parameter: NOT IMPLEMENTED yet; always refused with NotImplemented, do not use.")][Consumes(DataTag.SymbolKind, required: true)] InlineKind kind, [Description("Only used by kind=parameter, which is not implemented yet; leave unset.")][Consumes(DataTag.SymbolName)] string? methodName = null, [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            if (kind == InlineKind.method)
            {
                try
                {
                    var methodChanges = await _advancedStructuralEngine.InlineMethodAsync(resolvedFilePath, targetName, cancellationToken: cancellationToken);
                    var methodApply = await ValidateAndApplyAsync(methodChanges, $"Inline method '{targetName}'.", "Inline_method", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (methodApply.Error is not null)
                        return new SentinelCallToolResult<object>
                        {
                            IsError = true,
                            ErrorData = methodApply.Error
                        };
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = false,
                        SuccessData = AppliedChangeSummary.From(methodApply, methodChanges.Keys.ToList(), $"Inlined '{targetName}' at all call sites across {methodChanges.Count} file(s).", validated: false)
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Inline/method unexpected exception for '{TargetName}' in '{FilePathWrapper}'", targetName, resolvedFilePath);
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"Inline method '{targetName}' in '{resolvedFilePath}'")
                    };
                }
            }

            if (kind == InlineKind.variable)
            {
                var updated = await _semanticRefactoringEngine.InlineVariableAsync(resolvedFilePath, targetName, cancellationToken: cancellationToken);
                if (string.IsNullOrEmpty(updated))
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.NotFound, $"Inline/variable: variable '{targetName}' not found in '{resolvedFilePath}'.")
                    };
                var varChanges = new Dictionary<FilePathWrapper, string>
                {
                    [resolvedFilePath] = updated
                };
                var varApply = await ValidateAndApplyAsync(varChanges, $"Inline variable '{targetName}'.", "Inline_variable", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (varApply.Error is not null)
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = varApply.Error
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(varApply, [resolvedFilePath], $"Inlined variable '{targetName}' into its usages in {Path.GetFileName(resolvedFilePath)}.", validated: false)
                };
            }

            if (kind == InlineKind.field)
            {
                var fieldResult = await _advancedStructuralEngine.InlineFieldAsync(resolvedFilePath, targetName, cancellationToken: cancellationToken);
                var fieldChanges = new Dictionary<FilePathWrapper, string>
                {
                    [resolvedFilePath] = fieldResult.UpdatedText
                };
                var fieldApply = await ValidateAndApplyAsync(fieldChanges, $"Inline field '{targetName}'.", "Inline_field", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (fieldApply.Error is not null)
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = fieldApply.Error
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(fieldApply, [resolvedFilePath], $"Inlined field '{targetName}' into its usages in {Path.GetFileName(resolvedFilePath)}.", validated: false)
                };
            }

            if (kind == InlineKind.parameter)
            {
                if (string.IsNullOrEmpty(methodName))
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "methodName is required when kind=parameter.")
                    };
                var paramResult = await _advancedStructuralEngine.InlineParameterAsync(resolvedFilePath, methodName, targetName, cancellationToken: cancellationToken);
                var paramChanges = new Dictionary<FilePathWrapper, string>
                {
                    [resolvedFilePath] = paramResult.UpdatedText
                };
                var paramApply = await ValidateAndApplyAsync(paramChanges, $"Inline parameter '{targetName}' in '{methodName}'.", "Inline_parameter", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (paramApply.Error is not null)
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = paramApply.Error
                    };
                return new SentinelCallToolResult<object>()
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(paramApply, [resolvedFilePath], $"Inlined parameter '{targetName}' into '{methodName}' body in {Path.GetFileName(resolvedFilePath)}.", validated: false)
                };
            }

            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown kind '{kind}'. Valid values: method, variable, field, parameter.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inline ({Kind}) failed for '{TargetName}' in '{FilePathWrapper}'", kind, targetName, resolvedFilePath);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Inline")
            };
        }
    }

    [McpServerTool(Name = "WrapRange")]
    [Produces(DataTag.ChangeId)]
    [Description("Wraps a line range or snippet in a try/catch, using, or #region block.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of (startLine and endLine) or contextSnippet must be supplied. name is required for wrapper=using/region; optional for wrapper=tryCatch (defaults to exception type "Exception"). None of these is individually required by the schema.
    // TOOL-OPTION-REQUIRED-FLAG-STALE: wrapper is now a nullable enum (WrapRangeWrapper) with a null default, kept after startLine/endLine so existing positional call sites still bind; the schema still reports it optional, and a null wrapper is rejected with "wrapper is required".
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> WrapRangeTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Description("Provide both startLine and endLine, or use contextSnippet instead.")][Consumes(DataTag.StartLine)] int startLine = 0, [Description("Provide both startLine and endLine, or use contextSnippet instead.")][Consumes(DataTag.EndLine)] int endLine = 0, [Description("Which block to wrap in (using/region also require name).")][ExternalInputRequired(DataTag.Wrapper)] WrapRangeWrapper? wrapper = null, [Description("tryCatch: exception type name (default \"Exception\"). using: disposal variable name (required). region: region label (required).")][ExternalInputRequired(DataTag.SymbolName)] string? name = null, [Description("wrapper=tryCatch only: exception variable name.")][ExternalInputRequired(DataTag.SymbolName)] string catchVariableName = "ex", [Description("wrapper=tryCatch only: catch-block statements. Defaults to empty.")][ExternalInputRequired(DataTag.SourceCode)] string? catchBody = null, [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null, [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null, [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null, // autoStage removed from the schema: stage-then-apply is retired, the value is hardcoded to true below.
                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      // [Description(ToolParams.AutoStage)][ToolOptionAttribute(ToolOptionTag.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        // Hardcoded: autoStage is hidden from the schema until the server has a real staging mode.
        bool autoStage = true;
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            // Validate that we have either contextSnippet or both startLine/endLine
            if (!string.IsNullOrWhiteSpace(contextSnippet))
            {
                // Derive startLine/endLine from contextSnippet
                if (wrapper == WrapRangeWrapper.tryCatch)
                {
                    var exceptionType = name ?? "Exception";
                    var updated = await _refactoringEngine.WrapInTryCatchAsync(resolvedFilePath, contextSnippet, lineBefore, lineAfter, exceptionType, catchVariableName, catchBody, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText) ? new Dictionary<FilePathWrapper, string>() : new Dictionary<FilePathWrapper, string>
                        {
                            [resolvedFilePath] = updated.UpdatedText
                        };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = false,
                            SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, noStageChanges, cancellationToken), null, noStageChanges.Keys.ToList(), "Wrap snippet in try/catch.", dryRun: false, diff: null, changedContent: noStageChanges.Count == 0 ? null : noStageChanges, validated: false)
                        };
                    }

                    var changes = new Dictionary<FilePathWrapper, string>
                    {
                        [resolvedFilePath] = updated.UpdatedText
                    };
                    var apply = await ValidateAndApplyAsync(changes, $"Wrap snippet in try/catch.", "WrapRange_tryCatch", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary>
                        {
                            IsError = true,
                            ErrorData = apply.Error
                        };
                    var summary = AppliedChangeSummary.From(apply, [resolvedFilePath], $"Wrapped snippet in a try/{exceptionType} block in {Path.GetFileName(resolvedFilePath)}.", changedContent: changes);
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = false,
                        SuccessData = summary
                    };
                }

                if (wrapper == WrapRangeWrapper.@using)
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "name (disposalName) is required when wrapper=using.")
                        };
                    }

                    var updated = await _semanticRefactoringEngine.WrapInUsingAsync(resolvedFilePath, contextSnippet, lineBefore, lineAfter, name, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageUsingChanges = string.IsNullOrEmpty(updated.UpdatedText) ? new Dictionary<FilePathWrapper, string>() : new Dictionary<FilePathWrapper, string>
                        {
                            [resolvedFilePath] = updated.UpdatedText
                        };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = false,
                            SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, noStageUsingChanges, cancellationToken), null, noStageUsingChanges.Keys.ToList(), $"Wrap snippet in using ({name}).", dryRun: false, diff: null, changedContent: noStageUsingChanges.Count == 0 ? null : noStageUsingChanges, validated: false)
                        };
                    }

                    var usingChanges = new Dictionary<FilePathWrapper, string>
                    {
                        [resolvedFilePath] = updated.UpdatedText
                    };
                    var usingApply = await ValidateAndApplyAsync(usingChanges, $"Wrap snippet in using ({name}).", "WrapRange_using", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (usingApply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary>
                        {
                            IsError = true,
                            ErrorData = usingApply.Error
                        };
                    var usingSummary = AppliedChangeSummary.From(usingApply, [resolvedFilePath], $"Wrapped snippet in a using ({name}) block in {Path.GetFileName(resolvedFilePath)}.", changedContent: usingChanges);
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = false,
                        SuccessData = usingSummary
                    };
                }

                if (wrapper == WrapRangeWrapper.region)
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "name (regionName) is required when wrapper=region.")
                        };
                    }

                    var updated = await _refactoringEngine.WrapInRegionAsync(resolvedFilePath, contextSnippet, lineBefore, lineAfter, name, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText) ? new Dictionary<FilePathWrapper, string>() : new Dictionary<FilePathWrapper, string>
                        {
                            [resolvedFilePath] = updated.UpdatedText
                        };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = false,
                            SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, noStageChanges, cancellationToken), null, noStageChanges.Keys.ToList(), $"Wrap snippet in #region '{name}'.", dryRun: false, diff: null, changedContent: noStageChanges.Count == 0 ? null : noStageChanges, validated: false)
                        };
                    }

                    var changes = new Dictionary<FilePathWrapper, string>
                    {
                        [resolvedFilePath] = updated.UpdatedText
                    };
                    var apply = await ValidateAndApplyAsync(changes, $"Wrap snippet in #region '{name}'.", "WrapRange_region", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary>
                        {
                            IsError = true,
                            ErrorData = apply.Error
                        };
                    var summary = AppliedChangeSummary.From(apply, [resolvedFilePath], $"Wrapped snippet in #region '{name}' in {Path.GetFileName(resolvedFilePath)}.", changedContent: changes);
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = false,
                        SuccessData = summary
                    };
                }

                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "wrapper is required. Valid values: tryCatch, using, region.")
                };
            }
            else if (startLine > 0 && endLine > 0)
            {
                // Use existing line-based path
                if (wrapper == WrapRangeWrapper.tryCatch)
                {
                    var exceptionType = name ?? "Exception";
                    var updated = await _refactoringEngine.WrapInTryCatchAsync(resolvedFilePath, startLine, endLine, exceptionType, catchVariableName, catchBody, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText) ? new Dictionary<FilePathWrapper, string>() : new Dictionary<FilePathWrapper, string>
                        {
                            [resolvedFilePath] = updated.UpdatedText
                        };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = false,
                            SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, noStageChanges, cancellationToken), null, noStageChanges.Keys.ToList(), $"Wrap lines {startLine}-{endLine} in try/catch.", dryRun: false, diff: null, changedContent: noStageChanges.Count == 0 ? null : noStageChanges, validated: false)
                        };
                    }

                    var changes = new Dictionary<FilePathWrapper, string>
                    {
                        [resolvedFilePath] = updated.UpdatedText
                    };
                    var apply = await ValidateAndApplyAsync(changes, $"Wrap lines {startLine}-{endLine} in try/catch.", "WrapRange_tryCatch", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary>
                        {
                            IsError = true,
                            ErrorData = apply.Error
                        };
                    var summary = AppliedChangeSummary.From(apply, [resolvedFilePath], $"Wrapped lines {startLine}-{endLine} in a try/{exceptionType} block in {Path.GetFileName(resolvedFilePath)}.", changedContent: changes);
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = false,
                        SuccessData = summary
                    };
                }

                if (wrapper == WrapRangeWrapper.@using)
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "name (disposalName) is required when wrapper=using.")
                        };
                    }

                    var updated = await _semanticRefactoringEngine.WrapInUsingAsync(resolvedFilePath, startLine, endLine, name, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageUsingChanges = string.IsNullOrEmpty(updated.UpdatedText) ? new Dictionary<FilePathWrapper, string>() : new Dictionary<FilePathWrapper, string>
                        {
                            [resolvedFilePath] = updated.UpdatedText
                        };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = false,
                            SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, noStageUsingChanges, cancellationToken), null, noStageUsingChanges.Keys.ToList(), $"Wrap lines {startLine}-{endLine} in using ({name}).", dryRun: false, diff: null, changedContent: noStageUsingChanges.Count == 0 ? null : noStageUsingChanges, validated: false)
                        };
                    }

                    var usingChanges = new Dictionary<FilePathWrapper, string>
                    {
                        [resolvedFilePath] = updated.UpdatedText
                    };
                    var usingApply = await ValidateAndApplyAsync(usingChanges, $"Wrap lines {startLine}-{endLine} in using ({name}).", "WrapRange_using", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (usingApply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary>
                        {
                            IsError = true,
                            ErrorData = usingApply.Error
                        };
                    var usingSummary = AppliedChangeSummary.From(usingApply, [resolvedFilePath], $"Wrapped lines {startLine}-{endLine} in a using ({name}) block in {Path.GetFileName(resolvedFilePath)}.", changedContent: usingChanges);
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = false,
                        SuccessData = usingSummary
                    };
                }

                if (wrapper == WrapRangeWrapper.region)
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "name (regionName) is required when wrapper=region.")
                        };
                    }

                    var updated = await _refactoringEngine.WrapInRegionAsync(resolvedFilePath, startLine, endLine, name, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText) ? new Dictionary<FilePathWrapper, string>() : new Dictionary<FilePathWrapper, string>
                        {
                            [resolvedFilePath] = updated.UpdatedText
                        };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsError = false,
                            SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, noStageChanges, cancellationToken), null, noStageChanges.Keys.ToList(), $"Wrap lines {startLine}-{endLine} in #region '{name}'.", dryRun: false, diff: null, changedContent: noStageChanges.Count == 0 ? null : noStageChanges, validated: false)
                        };
                    }

                    var changes = new Dictionary<FilePathWrapper, string>
                    {
                        [resolvedFilePath] = updated.UpdatedText
                    };
                    var apply = await ValidateAndApplyAsync(changes, $"Wrap lines {startLine}-{endLine} in #region '{name}'.", "WrapRange_region", dryRun, returnDiff, cancellationToken: cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary>
                        {
                            IsError = true,
                            ErrorData = apply.Error
                        };
                    var summary = AppliedChangeSummary.From(apply, [resolvedFilePath], $"Wrapped lines {startLine}-{endLine} in #region '{name}' in {Path.GetFileName(resolvedFilePath)}.", changedContent: changes);
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsError = false,
                        SuccessData = summary
                    };
                }

                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "wrapper is required. Valid values: tryCatch, using, region.")
                };
            }
            else
            {
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsError = true,
                    ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "Either provide contextSnippet or both startLine and endLine (1-based).")
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WrapRange ({Wrapper}) failed in '{FilePathWrapper}'", wrapper, resolvedFilePath);
            return new SentinelCallToolResult<AppliedChangeSummary>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "WrapRange")
            };
        }
    }

    [McpServerTool(Name = "MoveType")]
    [Produces(DataTag.ChangeId)]
    [Description("Moves a type to its own file, or a nested type out to its containing namespace.")]
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> MoveTypeTool([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.SymbolName, required: true)] string typeName, [Description("ownFile: into its own new file. outerScope: nested type out to containing namespace.")] string destination, // autoStage removed from the schema: stage-then-apply is retired, the value is hardcoded to true below.
                                                                                                                                                                                                                                                                                                                                                                                                       // [Description(ToolParams.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false, [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false, // RequestContext<CallToolRequestParams> requestParams = null,
        [Description("outerScope only: when true, a nested type declared private/protected is widened to internal instead of refused. Default false.")] bool allowWidenToInternal = false,
        [Description("ownFile only: folder for the new file. Default: the source file's folder.")] string? targetFolder = null,
 CancellationToken cancellationToken = default)
    {
        // Hardcoded: autoStage is hidden from the schema until the server has a real staging mode.
        bool autoStage = true;
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            if (destination == "ownFile")
            {
                string? resolvedTargetFolder = null;
                if (!string.IsNullOrEmpty(targetFolder))
                {
                    resolvedTargetFolder = _workspaceManager.ResolveFromWire(targetFolder);
                }
                var changes = await _refactoringEngine.MoveTypeToFileAsync(resolvedFilePath, typeName, targetFolder: resolvedTargetFolder, cancellationToken: cancellationToken);
                if (!autoStage)
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>
                    {
                        IsError = false,
                        SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, changes, cancellationToken), null, changes.Keys.ToList(), $"Move type '{typeName}' from '{Path.GetFileName(resolvedFilePath)}'.", dryRun: false, diff: null, changedContent: changes.Count == 0 ? null : changes, validated: false)
                    };
                }

                var apply = await ValidateAndApplyAsync(changes, $"Move type '{typeName}' from '{Path.GetFileName(resolvedFilePath)}'.", "MoveType_ownFile", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (apply.Error is not null)
                    return new SentinelCallToolResult<AppliedChangeSummary>
                    {
                        IsError = true,
                        ErrorData = apply.Error
                    };
                var ownFileDescription = apply.ChangeId is not null ? $"Moves '{typeName}' to its own file. Call {ToolCallHint.Call("UndoLastApply", ("changeId", apply.ChangeId))} to revert if needed." : $"Moves '{typeName}' to its own file.";
                return new SentinelCallToolResult<AppliedChangeSummary>
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(apply, changes.Keys.ToList(), ownFileDescription, changedContent: changes)
                };
            }

            if (destination == "outerScope")
            {
                var outerResult = await _advancedStructuralEngine.MoveTypeToOuterScopeAsync(resolvedFilePath, typeName, allowWidenToInternal, cancellationToken: cancellationToken);
                if (!autoStage)
                {
                    var noStageOuterChanges = string.IsNullOrEmpty(outerResult.UpdatedText) ? new Dictionary<FilePathWrapper, string>() : new Dictionary<FilePathWrapper, string>
                    {
                        [resolvedFilePath] = outerResult.UpdatedText
                    };
                    return new SentinelCallToolResult<AppliedChangeSummary>
                    {
                        IsError = false,
                        SuccessData = AppliedChangeSummary.From(await ValidateAndApplyHelper.ComputeLineChangesAsync(_workspaceManager, noStageOuterChanges, cancellationToken), null, noStageOuterChanges.Keys.ToList(), $"Move type '{typeName}' to outer scope.", dryRun: false, diff: null, changedContent: noStageOuterChanges.Count == 0 ? null : noStageOuterChanges, validated: false)
                    };
                }

                var outerChanges = new Dictionary<FilePathWrapper, string>
                {
                    [resolvedFilePath] = outerResult.UpdatedText
                };
                var outerApply = await ValidateAndApplyAsync(outerChanges, $"Move type '{typeName}' to outer scope.", "MoveType_outerScope", dryRun, returnDiff, cancellationToken: cancellationToken);
                if (outerApply.Error is not null)
                    return new SentinelCallToolResult<AppliedChangeSummary>
                    {
                        IsError = true,
                        ErrorData = outerApply.Error
                    };
                return new SentinelCallToolResult<AppliedChangeSummary>
                {
                    IsError = false,
                    SuccessData = AppliedChangeSummary.From(outerApply, [resolvedFilePath], $"Moved '{typeName}' to outer namespace scope in {Path.GetFileName(resolvedFilePath)}." + (outerResult.Message?.Contains("widened") == true ? " " + outerResult.Message.TrimStart('/', ' ') : string.Empty), changedContent: outerChanges)
                };
            }

            return new SentinelCallToolResult<AppliedChangeSummary>
            {
                IsError = true,
                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown destination '{destination}'. Valid values: ownFile, outerScope.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveType ({Destination}) failed for '{TypeName}' in '{FilePathWrapper}'", destination, typeName, resolvedFilePath);
            return new SentinelCallToolResult<AppliedChangeSummary>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MoveType")
            };
        }
    }
}