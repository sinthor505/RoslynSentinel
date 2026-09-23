using System.ComponentModel;

using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Wire-shape input for one entry in <c>ChangeSignature</c>'s <c>parameters</c> array. Flat and
/// nullable-field-based (rather than a polymorphic/discriminated-union shape) so the emitted MCP
/// JSON schema stays a plain object schema an agent can construct directly: pass
/// <paramref name="originalIndex"/> alone to keep an existing parameter at this new position, or
/// omit it and pass <paramref name="name"/>/<paramref name="type"/>/<paramref name="defaultValue"/>
/// to insert a brand-new parameter here (its default value is also the literal fill-in argument
/// inserted at every existing call site).
/// </summary>
public sealed record ChangeSignatureParameterInput(
    int? originalIndex = null,
    string? name = null,
    string? type = null,
    string? defaultValue = null);

[McpServerToolType]
public class AdvancedRefactoringTools
{
    private readonly RefactoringEngine _refactoringEngine;
    //private readonly StandardRefactoringEngine _standardRefactoringEngine;
    private readonly AdvancedStructuralEngine _advancedStructuralEngine;
    private readonly MappingEngine _mappingEngine;
    private readonly SemanticRefactoringLibrary _semanticRefactoringLibrary;
    private readonly GranularRefactoringEngine _granularRefactoringEngine;
    //private readonly AdvancedLogicEngine _advancedLogicEngine;
    private readonly RefinementEngine _refinementEngine;
    private readonly AdvancedTypeEngine _advancedTypeEngine;
    //private readonly CodeStyleEngine _codeStyleEngine;
    //private readonly CodeFlowEngine _codeFlowEngine;
    //private readonly AdvancedRefactoringEngine _advancedRefactoringEngine;
    //private readonly LogicOptimizationEngine _logicOptimizationEngine;
    //private readonly OutParamRefactoringEngine _outParamRefactoringEngine;
    private readonly MsToolAugmentEngine _augmentEngine;
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ValidationEngine _validationEngine;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<AdvancedRefactoringTools> _logger;

    public AdvancedRefactoringTools(
        RefactoringEngine refactoringEngine,
        //StandardRefactoringEngine standardRefactoringEngine,
        AdvancedStructuralEngine advancedStructuralEngine,
        MappingEngine mappingEngine,
        SemanticRefactoringLibrary semanticRefactoringLibrary,
        GranularRefactoringEngine granularRefactoringEngine,
        //AdvancedLogicEngine advancedLogicEngine,
        RefinementEngine refinementEngine,
        AdvancedTypeEngine advancedTypeEngine,
        //CodeStyleEngine codeStyleEngine,
        //CodeFlowEngine codeFlowEngine,
        //AdvancedRefactoringEngine advancedRefactoringEngine,
        //LogicOptimizationEngine logicOptimizationEngine,
        ModernizationEngine modernizationEngine,
        //OutParamRefactoringEngine outParamRefactoringEngine,
        MsToolAugmentEngine augmentEngine,
        CodeGenerationEngine codeGenerationEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        IWorkspaceManager workspaceManager,
        ValidationEngine validationEngine,
        SentinelConfiguration config,
        ILogger<AdvancedRefactoringTools> logger)
    {
        _refactoringEngine = refactoringEngine;
        //_standardRefactoringEngine = standardRefactoringEngine;
        _advancedStructuralEngine = advancedStructuralEngine;
        _mappingEngine = mappingEngine;
        _semanticRefactoringLibrary = semanticRefactoringLibrary;
        _granularRefactoringEngine = granularRefactoringEngine;
        //_advancedLogicEngine = advancedLogicEngine;
        _refinementEngine = refinementEngine;
        _advancedTypeEngine = advancedTypeEngine;
        //_codeStyleEngine = codeStyleEngine;
        //_codeFlowEngine = codeFlowEngine;
        //_advancedRefactoringEngine = advancedRefactoringEngine;
        //_logicOptimizationEngine = logicOptimizationEngine;
        //_outParamRefactoringEngine = outParamRefactoringEngine;
        _augmentEngine = augmentEngine;
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

    private string? GetFileNotInSolutionError(FilePathWrapper filePath)
    {
        var ids = _workspaceManager.CurrentSolution?.GetDocumentIdsWithFilePath(filePath);
        if (ids == null || ids.Value.Length == 0)
            return $"File '{Path.GetFileName(filePath)}' not found in the loaded solution. " +
                   $"Verify the path is correct and the solution is loaded. " +
                   $"Loaded projects: {_workspaceManager.ProjectCount}.";
        return null;
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
        CancellationToken cancellationToken = default) =>
        ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine, _workspaceManager, _logger, changes, operationName,
            dryRun, returnDiff, progress: null, cancellationToken: cancellationToken,
            describeValidationFailure: (report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, ct));

    private Task<string> BuildDiffAsync(Dictionary<FilePathWrapper, string> changes, CancellationToken cancellationToken = default) =>
        ValidateAndApplyHelper.BuildDiffAsync(_workspaceManager, changes, cancellationToken);

    private static string BuildDiffFromPreImages(
        Dictionary<FilePathWrapper, string> changes,
        IReadOnlyDictionary<string, string?>? preImages) =>
        ValidateAndApplyHelper.BuildDiffFromPreImages(changes, preImages);

    [McpServerTool(Name = "ChangeSignature")]
    [Produces(DataTag.ResultOnly)]
    [Description("Reorders, adds, or removes method parameters and updates all call sites solution-wide. Refuses across an interface/implementer boundary - edit each side directly.")]
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ChangeSignature(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        [Description("The desired end-state parameter list, in order. Each entry is {originalIndex: N} to keep an existing 0-based-index parameter, or {name, type, defaultValue} to insert a new one. Omit an originalIndex to remove that parameter.")]
        [ExternalInputRequired(DataTag.Order, required: true)] ChangeSignatureParameterInput[] parameters,
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var specs = new List<SignatureParameterSpec>();
            foreach (var p in parameters)
            {
                if (p.originalIndex is int idx)
                {
                    specs.Add(new ExistingParameterSpec(idx));
                }
                else if (!string.IsNullOrEmpty(p.name) && !string.IsNullOrEmpty(p.type) && !string.IsNullOrEmpty(p.defaultValue))
                {
                    specs.Add(new NewParameterSpec(p.name, p.type, p.defaultValue));
                }
                else
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "Each parameters entry must set either originalIndex (to keep an existing parameter), or all of name/type/defaultValue (to insert a new one).") };
                }
            }

            var result = await _refactoringEngine.ChangeSignatureAsync(filePath, methodName, specs);
            if (result.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, result.Error) };

            var changes = result.Changes;
            if (!autoStage)
            {
                var noStageSummaryNote = $"Reorders parameters of '{methodName}' in {Path.GetFileName(filePath)}.";
                if (result.SkippedCallSites.Count > 0)
                    noStageSummaryNote += $" WARNING: {result.SkippedCallSites.Count} call site(s) could not be automatically reordered and must be fixed manually: " +
                        string.Join("; ", result.SkippedCallSites.Select(s => $"{Path.GetFileName(s.FilePath)}:{s.LineNumber} ({s.Reason})"));
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsSuccess = true,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: changes.Keys.ToList(),
                        Description: noStageSummaryNote,
                        DryRun: false,
                        Diff: null,
                        ChangedContent: changes,
                        Validated: false)
                };
            }

            var apply = await ValidateAndApplyAsync(changes, $"Change signature of method '{methodName}'.", "ChangeSignature", dryRun, returnDiff, cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };

            // Not wired into MemberChangedContentResult: this already has bespoke handling
            // (SkippedCallSites folded into the summary note below) that the generic offload
            // mechanism doesn't add value over -> there's no separate "new content" fragment,
            // just the reordered declaration text a caller can already see via ReturnDiff.
            var summaryNote = $"Reorders parameters of '{methodName}' in {Path.GetFileName(filePath)}.";
            if (result.SkippedCallSites.Count > 0)
                summaryNote += $" WARNING: {result.SkippedCallSites.Count} call site(s) could not be automatically reordered and must be fixed manually: " +
                    string.Join("; ", result.SkippedCallSites.Select(s => $"{Path.GetFileName(s.FilePath)}:{s.LineNumber} ({s.Reason})"));

            return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), summaryNote, apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangeSignature failed for '{MethodName}' in '{FilePathWrapper}'", methodName, filePath);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ChangeSignature") };
        }
    }

    [McpServerTool(Name = "ConvertAnonymousToNamed")]
    [Produces(DataTag.ChangeId)]
    [Description("Converts the first anonymous object creation in a file into a named class declaration.")]
    public async Task<SentinelCallToolResult<object>> ConvertAnonymousToNamed(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [ExternalInputRequired(DataTag.ClassName, required: true)] string newClassName,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var changes = await _advancedTypeEngine.ConvertAnonymousToNamedAsync(filePath, newClassName);
            if (changes.Count == 0)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"ConvertAnonymousToNamed: no anonymous object found in '{filePath}'.") };

            // Not wired into MemberChangedContentResult: the generated class's text isn't caller-
            // supplied or separately exposed -> ConvertAnonymousToNamedAsync only returns the whole-file
            // Changes dict, so showing just the new class declaration here would mean duplicating its
            // formatting logic. Revisit only if that engine method starts returning the new class's text
            // alongside the file changes.
            var apply = await ValidateAndApplyAsync(changes, $"Convert anonymous object to '{newClassName}'.", "ConvertAnonymousToNamed", dryRun, returnDiff, cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
            return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), $"Converted anonymous object to named class '{newClassName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConvertAnonymousToNamed failed for '{NewClassName}' in '{FilePathWrapper}'", newClassName, filePath);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ConvertAnonymousToNamed") };
        }
    }

    [McpServerTool(Name = "InlineClass")]
    [Produces(DataTag.ChangeId)]
    [Description("Merges a source class's members into a target class and removes the source declaration.")]
    public async Task<SentinelCallToolResult<object>> InlineClass(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] string rawSourceFilePath,
        [Consumes(DataTag.SourceFilepath, required: true)] string rawTargetFilePath,
        [Consumes(DataTag.SymbolName, required: true)] string className,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper sourceFilePath = FilePathWrapper.FromWire(rawSourceFilePath, _workspaceManager.GetSolutionRoot());
        FilePathWrapper targetFilePath = FilePathWrapper.FromWire(rawTargetFilePath, _workspaceManager.GetSolutionRoot());
        try
        {
            var changes = await _advancedStructuralEngine.InlineClassAsync(sourceFilePath, targetFilePath, className);
            if (changes.Count == 0)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"InlineClass: class '{className}' not found in '{sourceFilePath}'.") };

            var apply = await ValidateAndApplyAsync(changes, $"Inline class '{className}' into target.", "InlineClass", dryRun, returnDiff, cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
            return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), $"Inlined '{className}' members into target class across {changes.Count} file(s).", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InlineClass failed for '{ClassName}'", className);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "InlineClass") };
        }
    }

    [McpServerTool(Name = "MoveAllTypesToFiles")]
    [Produces(DataTag.Report)]
    [Description("Moves secondary types out of a file into their own files.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: target is required for scope=file (a file path) and scope=project (a project name); ignored for scope=solution. Enforced at runtime, not by the schema.
    public async Task<SentinelCallToolResult<object>> MoveAllTypesToFiles(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.Scope)] ToolScope scope,
        [Description("Required for scope=file/project (path or project name).")]
        [ExternalInputRequired(DataTag.SourceFilepath), ExternalInputRequired(DataTag.ProjectName)] string? target = null,
        [Description(ToolParams.AutoStage)]
        [ToolOption(ToolOptionTag.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (scope == ToolScope.file)
            {
                if (target is null)
                {
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "target (file path) is required for scope=file.") };
                }

                return await MoveAllTypesToFilesCore(
                    await _refactoringEngine.MoveAllTypesToFilesAsync(target),
                    autoStage, dryRun, returnDiff, $"Move all types to files in '{Path.GetFileName(target)}'",
                    previewFiles: true,
                    cancellationToken: cancellationToken);
            }
            if (scope == ToolScope.project)
            {
                if (target is null)
                {
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "target (project name) is required for scope=project.") };
                }

                return await MoveAllTypesToFilesCore(
                    await _refactoringEngine.MoveAllTypesToFilesInProjectAsync(target),
                    autoStage, dryRun, returnDiff, $"Move all types to files in project '{target}'",
                    previewFiles: false,
                    cancellationToken: cancellationToken);
            }
            if (scope == ToolScope.solution)
            {
                return await MoveAllTypesToFilesCore(
                    await _refactoringEngine.MoveAllTypesToFilesInSolutionAsync(),
                    autoStage, dryRun, returnDiff, "Move all types to files in solution",
                    previewFiles: false,
                    cancellationToken: cancellationToken);
            }
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown scope '{scope}'. Valid: file, project, solution.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveAllTypesToFiles ({Scope}) failed", scope);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MoveAllTypesToFiles") };
        }
    }

    private async Task<SentinelCallToolResult<object>> MoveAllTypesToFilesCore(
    Dictionary<FilePathWrapper, string> changes,
    bool autoStage,
    bool dryRun,
    bool returnDiff,
    string description,
    bool previewFiles,
    CancellationToken cancellationToken = default)
    {
        if (!autoStage)
        {
            return new SentinelCallToolResult<object>()
            {
                IsSuccess = true,
                SuccessData = new AppliedChangeSummary(
                    ChangeId: null,
                    AffectedFiles: changes.Keys.ToList(),
                    Description: description,
                    DryRun: false,
                    Diff: null,
                    ChangedContent: changes.Count == 0 ? null : changes,
                    Validated: false)
            };
        }

        if (changes.Count == 0)
            return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = "No secondary types found to move." };

        var apply = await ValidateAndApplyAsync(changes, description, "MoveAllTypesToFiles", dryRun, returnDiff, cancellationToken);
        if (apply.Error is not null)
            return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };

        if (previewFiles)
        {
            return new SentinelCallToolResult<object>()
            {
                IsSuccess = true,
                SuccessData = new
                {
                    ChangeId = apply.ChangeId,
                    DryRun = apply.DryRun,
                    Diff = apply.Diff,
                    Description = apply.ChangeId is not null
                        ? $"{description}. Call UndoLastApply(changeId=\"{apply.ChangeId}\") to revert if needed."
                        : description,
                    AffectedFiles = changes.Keys.Select(kvp => Path.GetFileName(kvp)).ToList(),
                    ContentPreviews = changes.ToDictionary(
                        kvp => Path.GetFileName(kvp.Key)!,
                    kvp => PreviewFileContent(kvp.Value))
                }
            };
        }

        return new SentinelCallToolResult<object>()
        {
            IsSuccess = true,
            SuccessData = new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), description, apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true)
        };
    }

    [McpServerTool(Name = "InvertAssignments")]
    [Produces(DataTag.ChangeId)]
    [Description("Swaps left and right sides of all assignment statements within a range.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of (startLine and endLine) or contextSnippet must be supplied; none of these params is individually required by the schema.
    public async Task<SentinelCallToolResult<object>> InvertAssignments(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Provide both startLine and endLine, or use contextSnippet instead.")]
        [Consumes(DataTag.StartLine)] int startLine = 0,
        [Description("Provide both startLine and endLine, or use contextSnippet instead.")]
        [Consumes(DataTag.EndLine)] int endLine = 0,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            // Validate that we have either contextSnippet or both startLine/endLine
            if (!string.IsNullOrWhiteSpace(contextSnippet))
            {
                var result = await _mappingEngine.InvertAssignmentsAsync(filePath, contextSnippet, lineBefore, lineAfter);
                if (string.IsNullOrEmpty(result.UpdatedText))
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"InvertAssignments: no assignments found in the snippet of '{filePath}'.") };

                var changes = new Dictionary<FilePathWrapper, string> { [filePath] = result.UpdatedText };
                var apply = await ValidateAndApplyAsync(changes, $"Invert assignments in snippet.", "InvertAssignments", dryRun, returnDiff, cancellationToken);
                if (apply.Error is not null)
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Inverted assignments in snippet of {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
            }
            else if (startLine > 0 && endLine > 0)
            {
                var result = await _mappingEngine.InvertAssignmentsAsync(filePath, startLine, endLine);
                if (string.IsNullOrEmpty(result.UpdatedText))
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"InvertAssignments: no assignments found in lines {startLine}-{endLine} of '{filePath}'.") };

                var changes = new Dictionary<FilePathWrapper, string> { [filePath] = result.UpdatedText };
                var apply = await ValidateAndApplyAsync(changes, $"Invert assignments in lines {startLine}-{endLine}.", "InvertAssignments", dryRun, returnDiff, cancellationToken);
                if (apply.Error is not null)
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Inverted assignments in lines {startLine}-{endLine} of {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
            }
            else
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "Either provide contextSnippet or both startLine and endLine (1-based).") };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvertAssignments failed in '{FilePathWrapper}'", filePath);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "InvertAssignments") };
        }
    }

    [McpServerTool(Name = "MoveMember")]
    [Produces(DataTag.ResultOnly)]
    [Description("Moves methods/properties/fields from one class to another as a single atomic change, rewriting call sites.")]
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> MoveMember(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.ClassName, required: true)] string className,
        [Description("Members to move.")]
        [Consumes(DataTag.SymbolName, required: true)] string[] memberNames,
        [Description("Target class name: an existing base type pulls members up; an existing unrelated class receives them as-is; a nonexistent name creates a new class.")]
        [ExternalInputRequired(DataTag.ClassName, required: true)] string targetClassName,
        [Description("Narrows which class named targetClassName to use, if ambiguous.")]
        [ExternalInputRequired(DataTag.SourceFilepath)] string? targetFilepath = null,
        [Description(ToolParams.AutoStage)]
        [ToolOption(ToolOptionTag.AutoStage, required: false)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        [Description("Auto-rewrites unambiguous call sites when moving instance members (default true). Set false to require manual resolution via callSiteFixups.")]
        bool autoResolveCallSites = true,
        [Description("Manual resolution for call sites autoResolveCallSites couldn't handle. Key is \"FilePath:Line\"; value is a receiver expression or \"new\".")]
        Dictionary<string, string>? callSiteFixups = null,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (memberNames == null || memberNames.Length == 0)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "memberNames is required and must be non-empty.") };
            }

            FilePathWrapper? targetFilePath = string.IsNullOrEmpty(targetFilepath)
                ? (FilePathWrapper?)null
                : FilePathWrapper.FromWire(targetFilepath, _workspaceManager.GetSolutionRoot());

            var result = await _advancedStructuralEngine.MoveMemberAsync(filePath, className, memberNames, targetClassName, targetFilePath, cancellationToken, autoResolveCallSites, callSiteFixups);
            if (!autoStage)
            {
                return new SentinelCallToolResult<AppliedChangeSummary>()
                {
                    IsSuccess = true,
                    SuccessData = new AppliedChangeSummary(
                        ChangeId: null,
                        AffectedFiles: result.Changes.Keys.ToList(),
                        Description: $"Move [{string.Join(", ", memberNames)}] from '{className}' to '{targetClassName}'.",
                        DryRun: false,
                        Diff: null,
                        ChangedContent: result.Changes.Count == 0 ? null : result.Changes,
                        Validated: false)
                };
            }

            var apply = await ValidateAndApplyAsync(result.Changes, $"Move [{string.Join(", ", memberNames)}] from '{className}' to '{targetClassName}'.", "MoveMember", dryRun, returnDiff, cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };

            var summaryNote = $"Moved [{string.Join(", ", memberNames)}] from '{className}' to '{targetClassName}'.";

            // Ledger opened here, after the atomic apply above has actually landed - not inside
            // MoveInstanceMembersAsync, since IsBlocked would otherwise refuse that same apply's
            // own source/target file write (neither necessarily has an unresolved ledger entry for
            // itself). dryRun never opens a ledger: nothing was written, so there is nothing to track.
            if (!dryRun && result.PendingLedgerEntries is { Count: > 0 } pendingEntries)
            {
                var opened = ((IScopedOperationLedger)_workspaceManager).TryOpen(
                    result.PendingLedgerOperationName ?? "MoveMember", pendingEntries, out var rejectionReason, apply.ChangeId);
                summaryNote += opened
                    ? $" Opened a scoped operation ledger with {pendingEntries.Count} unresolved call site(s) - resolve them before making unrelated changes."
                    : $" WARNING: {pendingEntries.Count} call site(s) could not be automatically rewritten, and a ledger could not be opened to track them ({rejectionReason}). Fix them manually: " +
                        string.Join("; ", pendingEntries.Select(e => $"{Path.GetFileName(e.FilePath)}:{e.Line} ({e.BlockReason})"));
            }

            if (result.SkippedCallSites.Count > 0)
                summaryNote += $" {result.SkippedCallSites.Count} call site(s) could not be automatically rewritten and must be fixed manually: " +
                    string.Join("; ", result.SkippedCallSites.Select(s => $"{Path.GetFileName(s.FilePath)}:{s.LineNumber} ({s.Reason})"));

            // Not wired into MemberChangedContentResult: this can touch 2-3 files (source, target,
            // and any rewritten call-site files) with no single "the new text" the way Member's
            // single-file operations do -> the moved member's text is already visible in the diff.
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, result.Changes.Keys.ToList(), summaryNote, apply.DryRun, apply.Diff, ChangedContent: result.Changes, Validated: true) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveMember failed for [{MemberNames}] in '{ClassName}'", string.Join(", ", memberNames ?? []), className);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MoveMember") };
        }
    }

    [McpServerTool(Name = "IntroduceParameterObject")]
    [Produces(DataTag.ChangeId)]
    [Description("Encapsulates method parameters into a new C# 12 record type; call sites need manual follow-up (flagged with TODO).")]
    public async Task<SentinelCallToolResult<object>> IntroduceParameterObject(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        [Description("Name for the generated record type. Defaults to a name derived from the method.")]
        string? newTypeName = null,
        [Description("Parameters to group into the record. Defaults to all non-CancellationToken parameters.")]
        string[]? parameterNames = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var fileErr = GetFileNotInSolutionError(filePath);
            if (fileErr != null) return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, fileErr) };

            var result = await _granularRefactoringEngine.IntroduceParameterObjectAsync(filePath, methodName, newTypeName, parameterNames);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new SentinelCallToolResult<object>()
                {
                    IsSuccess = false,
                    ErrorData = new ResultError(ToolErrorCode.Exception,
                    $"IntroduceParameterObject: method '{methodName}' not found in '{Path.GetFileName(filePath)}'. " +
                    "Verify the method name (case-sensitive) or use GetFileOutline to list available methods.")
                };

            // Not wired into MemberChangedContentResult: the generated record's text isn't caller-
            // supplied or separately exposed -> IntroduceParameterObjectAsync only returns the whole-file
            // UpdatedText, so showing just the new record here would mean duplicating its formatting
            // logic. Revisit only if that engine method starts returning the record's text alongside
            // UpdatedText.
            var changes = new Dictionary<FilePathWrapper, string> { [filePath] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Introduce parameter object for '{methodName}'.", "IntroduceParameterObject", dryRun, returnDiff, cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
            return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Introduced parameter object for '{methodName}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IntroduceParameterObject failed for '{MethodName}' in '{FilePathWrapper}'", methodName, filePath);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "IntroduceParameterObject") };
        }
    }

    [McpServerTool(Name = "Introduce")]
    [Produces(DataTag.ChangeId)]
    [Description("Introduces a named local variable, field, parameter, or constant from an expression.")]
    public async Task<SentinelCallToolResult<object>> Introduce(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.ContextSnippet, required: true)][Description("Short unique fragment identifying the target when its name alone is ambiguous, copied verbatim from a prior result.")] string contextSnippet,
        [ExternalInputRequired(DataTag.SymbolName)][Description("The name of the new symbol to introduce.")] string newName,
        [ExternalInputRequired(DataTag.SymbolKind)][Description("The kind of symbol to introduce.")] IntroduceAsType newType,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            DocumentEditResult result;
            string stageDesc;
            if (newType == IntroduceAsType.localVariable)
            {
                result = await _granularRefactoringEngine.IntroduceVariableAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
                stageDesc = $"Introduce local variable '{newName}'.";
            }
            else if (newType == IntroduceAsType.field)
            {
                result = await _granularRefactoringEngine.IntroduceFieldAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
                stageDesc = $"Introduce field '{newName}'.";
            }
            else if (newType == IntroduceAsType.parameter)
            {
                result = await _granularRefactoringEngine.IntroduceParameterAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
                stageDesc = $"Introduce parameter '{newName}'.";
            }
            else if (newType == IntroduceAsType.constant)
            {
                var constResult = await _augmentEngine.ExtractConstantSafeAsync(filePath, contextSnippet, newName, lineBefore, lineAfter);
                if (!constResult.Success || string.IsNullOrEmpty(constResult.UpdatedContent))
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, constResult.Error ?? "ExtractConstantSafe failed.") };

                var constChanges = new Dictionary<FilePathWrapper, string> { [filePath] = constResult.UpdatedContent };
                var constApply = await ValidateAndApplyAsync(constChanges, $"Introduce constant '{newName}'.", "Introduce(constant)", dryRun, returnDiff, cancellationToken);
                if (constApply.Error is not null)
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = constApply.Error };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(constApply.ChangeId, [filePath], $"Introduced '{newName}' as a constant in {Path.GetFileName(filePath)}.", constApply.DryRun, constApply.Diff) };
            }
            else
            {
                return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown newType '{newType}'. Valid values: localVariable, field, parameter, constant.") };
            }

            if (string.IsNullOrEmpty(result.UpdatedText))
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Introduce({newType}): context snippet '{contextSnippet}' not matched in '{filePath}'.") };

            // Not wired into MemberChangedContentResult: the new declaration's text isn't caller-
            // supplied or separately exposed -> IntroduceVariable/Field/ParameterAsync only return
            // the whole-file UpdatedText, so showing just the new declaration here would mean
            // duplicating their formatting logic. Revisit only if those engine methods start
            // returning the introduced declaration's text alongside UpdatedText.
            var changes = new Dictionary<FilePathWrapper, string> { [filePath] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, stageDesc, $"Introduce({newType})", dryRun, returnDiff, cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
            return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Introduced '{newName}' as {(newType == IntroduceAsType.localVariable ? "a local variable" : newType)} in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Introduce ({As}) failed for '{NewName}' in '{FilePathWrapper}'", newType, newName, filePath);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Introduce") };
        }
    }

    // Not wired into MemberChangedContentResult for any of its three branches (interface/partial/
    // superclass): each engine call (ExtractInterfaceAsync/ExtractMembersToPartialAsync/
    // ExtractSuperclassAsync) only returns whole-file Changes dicts, with no separately-exposed "just the
    // new type's text" fragment. Wiring this needs an engine-API-extension pass, not tool-layer wiring ->
    // revisit only if those engine methods start returning the extracted type's text alongside Changes.
    // newType=class was removed in favor of MoveMember, which supersedes it (targetClassName omitted from the
    // solution -> same new-class behavior) and additionally supports moving into an EXISTING class.
    [McpServerTool(Name = "ExtractMembers")]
    [Produces(DataTag.ChangeId)]
    [Description("Extracts class members into a new interface, partial class, or superclass.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: newTypeName required for newType=interface/superclass; memberNames required for newType=partial; none individually required by the schema. Enforced at runtime.
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> ExtractMembers(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)][Description("The file path of the destination file.")] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)][Description("The name of the class from which to extract members.")] string className,
        [ExternalInputRequired(DataTag.SymbolKind)][Description("Extraction target kind: interface, partial class, or superclass.")] ExtractAsType newType,
        [ExternalInputRequired(DataTag.SymbolName)][Description("Required for newType=interface/superclass: name of the new type.")] string? newTypeName = null,
        [ExternalInputRequired(DataTag.SymbolName)][Description("Required for newType=partial class: members to extract.")] string[]? memberNames = null,
        [ExternalInputRequired(DataTag.SourceFilepath)][Description("Required for newType=superclass: file paths matching classNames.")] FilePathWrapper[]? memberFilePaths = null,
        [ExternalInputRequired(DataTag.ClassName)][Description("Required for newType=superclass: classes to extract common members from.")] string[]? classNames = null,
        [ToolOption(ToolOptionTag.AutoStage, required: false)][Description("true (default) = write immediately; false = return content without writing.")] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (newType == ExtractAsType.@interface)
            {
                if (string.IsNullOrEmpty(newTypeName))
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "newTypeName (interface name) is required when newType=interface.") };
                }
                try
                {
                    var changes = await _refactoringEngine.ExtractInterfaceAsync(filePath, className, newTypeName);
                    if (!autoStage)
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsSuccess = true,
                            SuccessData = new AppliedChangeSummary(
                                ChangeId: null,
                                AffectedFiles: changes.Keys.ToList(),
                                Description: $"Extract interface '{newTypeName}' from '{className}'.",
                                DryRun: false,
                                Diff: null,
                                ChangedContent: changes.Count == 0 ? null : changes,
                                Validated: false)
                        };
                    }

                    var apply = await ValidateAndApplyAsync(changes, $"Extract interface '{newTypeName}' from '{className}'.", "ExtractMembers_interface", dryRun, returnDiff, cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), $"Extracted interface '{newTypeName}' from '{className}'.", apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true) };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExtractMembers/interface unexpected exception for '{NewTypeName}'", newTypeName);
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ExtractMembers newType=interface for '{newTypeName}'") };
                }
            }
            if (newType == ExtractAsType.partialClass)
            {
                if (memberNames == null || memberNames.Length == 0)
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "memberNames is required when newType=partial.") };
                }
                var partialChanges = await _granularRefactoringEngine.ExtractMembersToPartialAsync(filePath, className, memberNames);
                if (!autoStage)
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>()
                    {
                        IsSuccess = true,
                        SuccessData = new AppliedChangeSummary(
                            ChangeId: null,
                            AffectedFiles: partialChanges.Keys.ToList(),
                            Description: $"Extract members to partial for '{className}'.",
                            DryRun: false,
                            Diff: null,
                            ChangedContent: partialChanges.Count == 0 ? null : partialChanges,
                            Validated: false)
                    };
                }

                var partialApply = await ValidateAndApplyAsync(partialChanges, $"Extract members to partial for '{className}'.", "ExtractMembers_partial", dryRun, returnDiff, cancellationToken);
                if (partialApply.Error is not null)
                    return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = partialApply.Error };
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(partialApply.ChangeId, partialChanges.Keys.ToList(), $"Extracted members of '{className}' to a new partial file.", partialApply.DryRun, partialApply.Diff, ChangedContent: partialChanges, Validated: true) };
            }
            if (newType == ExtractAsType.superclass)
            {
                if (string.IsNullOrEmpty(newTypeName))
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "newTypeName (new base class name) is required when newType=superclass.") };
                }
                var actualFilePaths = memberFilePaths ?? new[] { filePath };
                var actualClassNames = classNames ?? new[] { className };
                try
                {
                    var changes = await _advancedStructuralEngine.ExtractSuperclassAsync(actualFilePaths, actualClassNames, newTypeName);
                    if (!autoStage)
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsSuccess = true,
                            SuccessData = new AppliedChangeSummary(
                                ChangeId: null,
                                AffectedFiles: changes.Keys.ToList(),
                                Description: $"Extract superclass '{newTypeName}' from {actualClassNames.Length} class(es).",
                                DryRun: false,
                                Diff: null,
                                ChangedContent: changes.Count == 0 ? null : changes,
                                Validated: false)
                        };
                    }

                    var apply = await ValidateAndApplyAsync(changes, $"Extract superclass '{newTypeName}' from {actualClassNames.Length} class(es).", "ExtractMembers_superclass", dryRun, returnDiff, cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, changes.Keys.ToList(), $"Extracted superclass '{newTypeName}' from {actualClassNames.Length} class(es).", apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true) };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExtractMembers/superclass unexpected exception for '{NewTypeName}'", newTypeName);
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ExtractMembers newType=superclass for '{newTypeName}'") };
                }
            }
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown newType '{newType}'. Valid values: interface, partial, superclass. For newType=class, use MoveMember instead.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractMembers ({As}) failed for '{ClassName}' in '{FilePathWrapper}'", newType, className, filePath);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "ExtractMembers") };
        }
    }

    [McpServerTool(Name = "SyncInterface")]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates stub implementations, syncs missing members into an interface, or verifies implementation coverage.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: className required for action=implement/sync; not needed for action=verify. Enforced at runtime, not by the schema.
    public async Task<SentinelCallToolResult<object>> SyncInterface(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Required for action=implement/sync; ignored for verify.")]
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string interfaceName,
        [Description("implement: stub unimplemented interface members on className. sync: add className's missing public members to interfaceName. verify: report coverage across implementers (className not needed).")]
        [Consumes(DataTag.Action, required: true)] SyncInterfaceAction action,
        [Description("Required for action=implement/sync.")]
        [Consumes(DataTag.SymbolName)] string? className = null,
        [Description("Scopes action=verify to one project.")]
        [Consumes(DataTag.ProjectName)] string? projectName = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
            if (action == SyncInterfaceAction.implement)
            {
                if (string.IsNullOrEmpty(className))
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required when action=implement.") };

                var implFileErr = GetFileNotInSolutionError(filePath);
                if (implFileErr != null) return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, implFileErr) };

                var implResult = await _codeGenerationEngine.ImplementInterfaceAsync(filePath, className, interfaceName);
                if (string.IsNullOrEmpty(implResult.UpdatedText))
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData = new ResultError(ToolErrorCode.Exception,
                        $"SyncInterface implement: class '{className}' or interface '{interfaceName}' not found in '{Path.GetFileName(filePath)}'. " +
                        "Verify both names are spelled correctly (case-sensitive). Use LocateSymbol to confirm the interface exists in the solution.")
                    };

                var implChanges = new Dictionary<FilePathWrapper, string> { [filePath] = implResult.UpdatedText };
                var implApply = await ValidateAndApplyAsync(implChanges, $"Implement '{interfaceName}' on '{className}'.", "SyncInterface_implement", dryRun, returnDiff, cancellationToken);
                if (implApply.Error is not null)
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = implApply.Error };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(implApply.ChangeId, [filePath], $"Implemented '{interfaceName}' on '{className}' in {Path.GetFileName(filePath)}.", implApply.DryRun, implApply.Diff) };
            }
            if (action == SyncInterfaceAction.sync)
            {
                if (string.IsNullOrEmpty(className))
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required when action=sync.") };

                var syncFileErr = GetFileNotInSolutionError(filePath);
                if (syncFileErr != null) return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, syncFileErr) };

                var syncResult = await _refactoringEngine.SyncInterfaceToImplementationAsync(filePath, className, interfaceName);
                if (string.IsNullOrEmpty(syncResult.UpdatedText))
                    return new SentinelCallToolResult<object>()
                    {
                        IsSuccess = false,
                        ErrorData = new ResultError(ToolErrorCode.Exception,
                        $"SyncInterface sync: class '{className}' or interface '{interfaceName}' not found in '{Path.GetFileName(filePath)}'. " +
                        "Verify both names are spelled correctly (case-sensitive). Use LocateSymbol to confirm the interface exists in the solution.")
                    };

                var syncChanges = new Dictionary<FilePathWrapper, string> { [filePath] = syncResult.UpdatedText };
                var syncApply = await ValidateAndApplyAsync(syncChanges, $"Sync '{interfaceName}' to '{className}' implementation.", "SyncInterface_sync", dryRun, returnDiff, cancellationToken);
                if (syncApply.Error is not null)
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = syncApply.Error };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(syncApply.ChangeId, [filePath], $"Synced '{interfaceName}' to '{className}' implementation in {Path.GetFileName(filePath)}.", syncApply.DryRun, syncApply.Diff) };
            }
            if (action == SyncInterfaceAction.verify)
            {
                var result = await _symbolNavigationEngine.VerifyInterfaceCompletenessAsync(interfaceName, projectName, cancellationToken);
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = result };
            }
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown action '{action}'. Valid values: implement, sync, verify.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncInterface ({Action}) failed for '{InterfaceName}'", action, interfaceName);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "SyncInterface") };
        }
    }

    [McpServerTool(Name = "Inline")]
    [Produces(DataTag.ChangeId)]
    [Description("Inlines a symbol by replacing all usages with its definition.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: methodName required for kind=parameter; not needed otherwise. Enforced at runtime, not by the schema.
    public async Task<SentinelCallToolResult<object>> Inline(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("The symbol to inline (parameter name when kind=parameter).")]
        [Consumes(DataTag.SymbolName, required: true)] string targetName,
        [Description("method: inline body at call sites (expression-body/single-return only). variable/field: inline into usages. parameter: inline a constant parameter (methodName required).")]
        [Consumes(DataTag.SymbolKind, required: true)] InlineKind kind,
        [Description("Required for kind=parameter: the declaring method.")]
        [Consumes(DataTag.SymbolName)] string? methodName = null,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (kind == InlineKind.method)
            {
                try
                {
                    var methodChanges = await _refinementEngine.InlineMethodAsync(filePath, targetName);
                    var methodApply = await ValidateAndApplyAsync(methodChanges, $"Inline method '{targetName}'.", "Inline_method", dryRun, returnDiff, cancellationToken);
                    if (methodApply.Error is not null)
                        return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = methodApply.Error };
                    return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(methodApply.ChangeId, methodChanges.Keys.ToList(), $"Inlined '{targetName}' at all call sites across {methodChanges.Count} file(s).", methodApply.DryRun, methodApply.Diff) };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Inline/method unexpected exception for '{TargetName}' in '{FilePathWrapper}'", targetName, filePath);
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"Inline method '{targetName}' in '{filePath}'") };
                }
            }
            if (kind == InlineKind.variable)
            {
                var updated = await _semanticRefactoringLibrary.InlineVariableAsync(filePath, targetName);
                if (string.IsNullOrEmpty(updated))
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Inline/variable: variable '{targetName}' not found in '{filePath}'.") };

                var varChanges = new Dictionary<FilePathWrapper, string> { [filePath] = updated };
                var varApply = await ValidateAndApplyAsync(varChanges, $"Inline variable '{targetName}'.", "Inline_variable", dryRun, returnDiff, cancellationToken);
                if (varApply.Error is not null)
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = varApply.Error };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(varApply.ChangeId, [filePath], $"Inlined variable '{targetName}' into its usages in {Path.GetFileName(filePath)}.", varApply.DryRun, varApply.Diff) };
            }
            if (kind == InlineKind.field)
            {
                var fieldResult = await _granularRefactoringEngine.InlineFieldAsync(filePath, targetName);
                if (string.IsNullOrEmpty(fieldResult.UpdatedText))
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Inline/field: field '{targetName}' not found in '{filePath}'.") };

                var fieldChanges = new Dictionary<FilePathWrapper, string> { [filePath] = fieldResult.UpdatedText };
                var fieldApply = await ValidateAndApplyAsync(fieldChanges, $"Inline field '{targetName}'.", "Inline_field", dryRun, returnDiff, cancellationToken);
                if (fieldApply.Error is not null)
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = fieldApply.Error };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(fieldApply.ChangeId, [filePath], $"Inlined field '{targetName}' into its usages in {Path.GetFileName(filePath)}.", fieldApply.DryRun, fieldApply.Diff) };
            }
            if (kind == InlineKind.parameter)
            {
                if (string.IsNullOrEmpty(methodName))
                    return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "methodName is required when kind=parameter.") };

                var paramResult = await _granularRefactoringEngine.InlineParameterAsync(filePath, methodName, targetName);
                if (string.IsNullOrEmpty(paramResult.UpdatedText))
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Inline/parameter: parameter '{targetName}' not found in method '{methodName}' in '{filePath}'.") };

                var paramChanges = new Dictionary<FilePathWrapper, string> { [filePath] = paramResult.UpdatedText };
                var paramApply = await ValidateAndApplyAsync(paramChanges, $"Inline parameter '{targetName}' in '{methodName}'.", "Inline_parameter", dryRun, returnDiff, cancellationToken);
                if (paramApply.Error is not null)
                    return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = paramApply.Error };
                return new SentinelCallToolResult<object>() { IsSuccess = true, SuccessData = new AppliedChangeSummary(paramApply.ChangeId, [filePath], $"Inlined parameter '{targetName}' into '{methodName}' body in {Path.GetFileName(filePath)}.", paramApply.DryRun, paramApply.Diff) };
            }
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown kind '{kind}'. Valid values: method, variable, field, parameter.") };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inline ({Kind}) failed for '{TargetName}' in '{FilePathWrapper}'", kind, targetName, filePath);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "Inline") };
        }
    }

    [McpServerTool(Name = "WrapRange")]
    [Produces(DataTag.ChangeId)]
    [Description("Wraps a line range or snippet in a try/catch, using, or #region block.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: exactly one of (startLine and endLine) or contextSnippet must be supplied. name is required for wrapper=using/region; optional for wrapper=tryCatch (defaults to exception type "Exception"). None of these is individually required by the schema.
    // TOOL-OPTION-REQUIRED-FLAG-STALE: wrapper carries a C# default ("") purely so it can stay after startLine/endLine in parameter order (existing positional call sites depend on this order); it is actually unconditionally required -> the body always falls through to an "Unknown wrapper" error when it doesn't match tryCatch/using/region. The schema wrongly reports it optional.
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> WrapRange(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Provide both startLine and endLine, or use contextSnippet instead.")]
        [Consumes(DataTag.StartLine)] int startLine = 0,
        [Description("Provide both startLine and endLine, or use contextSnippet instead.")]
        [Consumes(DataTag.EndLine)] int endLine = 0,
        [Description("tryCatch/using/region - which block to wrap in (using/region also require name).")]
        [ExternalInputRequired(DataTag.Wrapper)] string wrapper = "",
        [Description("tryCatch: exception type name (default \"Exception\"). using: disposal variable name (required). region: region label (required).")]
        [ExternalInputRequired(DataTag.SymbolName)] string? name = null,
        [Description("wrapper=tryCatch only: exception variable name.")]
        [ExternalInputRequired(DataTag.SymbolName)] string catchVariableName = "ex",
        [Description("wrapper=tryCatch only: catch-block statements. Defaults to empty.")]
        [ExternalInputRequired(DataTag.SourceCode)] string? catchBody = null,
        [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null,
        [Description(ToolParams.LineBefore)][ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)][ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        [Description(ToolParams.AutoStage)]
        [ToolOptionAttribute(ToolOptionTag.AutoStage)] bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            // Validate that we have either contextSnippet or both startLine/endLine
            if (!string.IsNullOrWhiteSpace(contextSnippet))
            {
                // Derive startLine/endLine from contextSnippet
                if (wrapper == "tryCatch")
                {
                    var exceptionType = name ?? "Exception";
                    var updated = await _refactoringEngine.WrapInTryCatchAsync(filePath, contextSnippet, lineBefore, lineAfter, exceptionType, catchVariableName, catchBody, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                            ? new Dictionary<FilePathWrapper, string>()
                            : new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsSuccess = true,
                            SuccessData = new AppliedChangeSummary(
                                ChangeId: null,
                                AffectedFiles: noStageChanges.Keys.ToList(),
                                Description: "Wrap snippet in try/catch.",
                                DryRun: false,
                                Diff: null,
                                ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                                Validated: false)
                        };
                    }
                    var changes = new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                    var apply = await ValidateAndApplyAsync(changes, $"Wrap snippet in try/catch.", "WrapRange_tryCatch", dryRun, returnDiff, cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
                    var summary = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Wrapped snippet in a try/{exceptionType} block in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true);
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = summary };
                }
                if (wrapper == "using")
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "name (disposalName) is required when wrapper=using.") };
                    }
                    var updated = await _semanticRefactoringLibrary.WrapInUsingAsync(filePath, contextSnippet, lineBefore, lineAfter, name, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageUsingChanges = string.IsNullOrEmpty(updated.UpdatedText)
                            ? new Dictionary<FilePathWrapper, string>()
                            : new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsSuccess = true,
                            SuccessData = new AppliedChangeSummary(
                                ChangeId: null,
                                AffectedFiles: noStageUsingChanges.Keys.ToList(),
                                Description: $"Wrap snippet in using ({name}).",
                                DryRun: false,
                                Diff: null,
                                ChangedContent: noStageUsingChanges.Count == 0 ? null : noStageUsingChanges,
                                Validated: false)
                        };
                    }
                    var usingChanges = new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                    var usingApply = await ValidateAndApplyAsync(usingChanges, $"Wrap snippet in using ({name}).", "WrapRange_using", dryRun, returnDiff, cancellationToken);
                    if (usingApply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = usingApply.Error };
                    var usingSummary = new AppliedChangeSummary(usingApply.ChangeId, [filePath], $"Wrapped snippet in a using ({name}) block in {Path.GetFileName(filePath)}.", usingApply.DryRun, usingApply.Diff, ChangedContent: usingChanges, Validated: true);
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = usingSummary };
                }
                if (wrapper == "region")
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "name (regionName) is required when wrapper=region.") };
                    }
                    var updated = await _refactoringEngine.WrapInRegionAsync(filePath, contextSnippet, lineBefore, lineAfter, name, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                            ? new Dictionary<FilePathWrapper, string>()
                            : new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsSuccess = true,
                            SuccessData = new AppliedChangeSummary(
                                ChangeId: null,
                                AffectedFiles: noStageChanges.Keys.ToList(),
                                Description: $"Wrap snippet in #region '{name}'.",
                                DryRun: false,
                                Diff: null,
                                ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                                Validated: false)
                        };
                    }
                    var changes = new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                    var apply = await ValidateAndApplyAsync(changes, $"Wrap snippet in #region '{name}'.", "WrapRange_region", dryRun, returnDiff, cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
                    var summary = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Wrapped snippet in #region '{name}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true);
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = summary };
                }
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown wrapper '{wrapper}'. Valid values: tryCatch, using, region.") };
            }
            else if (startLine > 0 && endLine > 0)
            {
                // Use existing line-based path
                if (wrapper == "tryCatch")
                {
                    var exceptionType = name ?? "Exception";
                    var updated = await _refactoringEngine.WrapInTryCatchAsync(filePath, startLine, endLine, exceptionType, catchVariableName, catchBody, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                            ? new Dictionary<FilePathWrapper, string>()
                            : new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsSuccess = true,
                            SuccessData = new AppliedChangeSummary(
                                ChangeId: null,
                                AffectedFiles: noStageChanges.Keys.ToList(),
                                Description: $"Wrap lines {startLine}-{endLine} in try/catch.",
                                DryRun: false,
                                Diff: null,
                                ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                                Validated: false)
                        };
                    }
                    var changes = new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                    var apply = await ValidateAndApplyAsync(changes, $"Wrap lines {startLine}-{endLine} in try/catch.", "WrapRange_tryCatch", dryRun, returnDiff, cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
                    var summary = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Wrapped lines {startLine}-{endLine} in a try/{exceptionType} block in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true);
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = summary };
                }
                if (wrapper == "using")
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "name (disposalName) is required when wrapper=using.") };
                    }
                    var updated = await _semanticRefactoringLibrary.WrapInUsingAsync(filePath, startLine, endLine, name, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageUsingChanges = string.IsNullOrEmpty(updated.UpdatedText)
                            ? new Dictionary<FilePathWrapper, string>()
                            : new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsSuccess = true,
                            SuccessData = new AppliedChangeSummary(
                                ChangeId: null,
                                AffectedFiles: noStageUsingChanges.Keys.ToList(),
                                Description: $"Wrap lines {startLine}-{endLine} in using ({name}).",
                                DryRun: false,
                                Diff: null,
                                ChangedContent: noStageUsingChanges.Count == 0 ? null : noStageUsingChanges,
                                Validated: false)
                        };
                    }
                    var usingChanges = new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                    var usingApply = await ValidateAndApplyAsync(usingChanges, $"Wrap lines {startLine}-{endLine} in using ({name}).", "WrapRange_using", dryRun, returnDiff, cancellationToken);
                    if (usingApply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = usingApply.Error };
                    var usingSummary = new AppliedChangeSummary(usingApply.ChangeId, [filePath], $"Wrapped lines {startLine}-{endLine} in a using ({name}) block in {Path.GetFileName(filePath)}.", usingApply.DryRun, usingApply.Diff, ChangedContent: usingChanges, Validated: true);
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = usingSummary };
                }
                if (wrapper == "region")
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "name (regionName) is required when wrapper=region.") };
                    }
                    var updated = await _refactoringEngine.WrapInRegionAsync(filePath, startLine, endLine, name, cancellationToken);
                    if (!autoStage)
                    {
                        var noStageChanges = string.IsNullOrEmpty(updated.UpdatedText)
                            ? new Dictionary<FilePathWrapper, string>()
                            : new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                        return new SentinelCallToolResult<AppliedChangeSummary>()
                        {
                            IsSuccess = true,
                            SuccessData = new AppliedChangeSummary(
                                ChangeId: null,
                                AffectedFiles: noStageChanges.Keys.ToList(),
                                Description: $"Wrap lines {startLine}-{endLine} in #region '{name}'.",
                                DryRun: false,
                                Diff: null,
                                ChangedContent: noStageChanges.Count == 0 ? null : noStageChanges,
                                Validated: false)
                        };
                    }
                    var changes = new Dictionary<FilePathWrapper, string> { [filePath] = updated.UpdatedText! };
                    var apply = await ValidateAndApplyAsync(changes, $"Wrap lines {startLine}-{endLine} in #region '{name}'.", "WrapRange_region", dryRun, returnDiff, cancellationToken);
                    if (apply.Error is not null)
                        return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
                    var summary = new AppliedChangeSummary(apply.ChangeId, [filePath], $"Wrapped lines {startLine}-{endLine} in #region '{name}' in {Path.GetFileName(filePath)}.", apply.DryRun, apply.Diff, ChangedContent: changes, Validated: true);
                    return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = true, SuccessData = summary };
                }
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown wrapper '{wrapper}'. Valid values: tryCatch, using, region.") };
            }
            else
            {
                return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "Either provide contextSnippet or both startLine and endLine (1-based).") };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WrapRange ({Wrapper}) failed in '{FilePathWrapper}'", wrapper, filePath);
            return new SentinelCallToolResult<AppliedChangeSummary>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "WrapRange") };
        }
    }

    [McpServerTool(Name = "MoveType")]
    [Produces(DataTag.ChangeId)]
    [Description("Moves a type to its own file, or a nested type out to its containing namespace.")]
    public async Task<SentinelCallToolResult<AppliedChangeSummary>> MoveType(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string typeName,
        [Description("ownFile: into its own new file. outerScope: nested type out to containing namespace.")]
        string destination,
        [Description(ToolParams.AutoStage)]
        bool autoStage = true,
        [Description(ToolParams.DryRun)][ToolOption(ToolOptionTag.DryRun)] bool dryRun = false,
        [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            if (destination == "ownFile")
            {
                var changes = await _refactoringEngine.MoveTypeToFileAsync(filePath, typeName);
                if (!autoStage)
                {
                    return new SentinelCallToolResult<AppliedChangeSummary>
                    {
                        IsSuccess = true,
                        SuccessData = new AppliedChangeSummary(
                            ChangeId: null,
                            AffectedFiles: changes.Keys.ToList(),
                            Description: $"Move type '{typeName}' from '{Path.GetFileName(filePath)}'.",
                            DryRun: false,
                            Diff: null,
                            ChangedContent: changes.Count == 0 ? null : changes,
                            Validated: false)
                    };
                }

                var apply = await ValidateAndApplyAsync(changes, $"Move type '{typeName}' from '{Path.GetFileName(filePath)}'.", "MoveType_ownFile", dryRun, returnDiff, cancellationToken);
                if (apply.Error is not null)
                    return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = apply.Error };
                var ownFileDescription = apply.ChangeId is not null
                    ? $"Moves '{typeName}' to its own file. Call UndoLastApply(changeId=\"{apply.ChangeId}\") to revert if needed."
                    : $"Moves '{typeName}' to its own file.";
                return new SentinelCallToolResult<AppliedChangeSummary>
                {
                    IsSuccess = true,
                    SuccessData = new AppliedChangeSummary(
                        apply.ChangeId, changes.Keys.ToList(), ownFileDescription, apply.DryRun, apply.Diff,
                        ChangedContent: changes, Validated: true)
                };
            }
            if (destination == "outerScope")
            {
                var outerResult = await _granularRefactoringEngine.MoveTypeToOuterScopeAsync(filePath, typeName);
                if (!autoStage)
                {
                    var noStageOuterChanges = string.IsNullOrEmpty(outerResult.UpdatedText)
                        ? new Dictionary<FilePathWrapper, string>()
                        : new Dictionary<FilePathWrapper, string> { [filePath] = outerResult.UpdatedText! };
                    return new SentinelCallToolResult<AppliedChangeSummary>
                    {
                        IsSuccess = true,
                        SuccessData = new AppliedChangeSummary(
                            ChangeId: null,
                            AffectedFiles: noStageOuterChanges.Keys.ToList(),
                            Description: $"Move type '{typeName}' to outer scope.",
                            DryRun: false,
                            Diff: null,
                            ChangedContent: noStageOuterChanges.Count == 0 ? null : noStageOuterChanges,
                            Validated: false)
                    };
                }

                if (string.IsNullOrEmpty(outerResult.UpdatedText))
                    return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"MoveType/outerScope: nested type '{typeName}' not found in '{filePath}'.") };

                var outerChanges = new Dictionary<FilePathWrapper, string> { [filePath] = outerResult.UpdatedText };
                var outerApply = await ValidateAndApplyAsync(outerChanges, $"Move type '{typeName}' to outer scope.", "MoveType_outerScope", dryRun, returnDiff, cancellationToken);
                if (outerApply.Error is not null)
                    return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = false, ErrorData = outerApply.Error };
                return new SentinelCallToolResult<AppliedChangeSummary> { IsSuccess = true, SuccessData = new AppliedChangeSummary(outerApply.ChangeId, [filePath], $"Moved '{typeName}' to outer namespace scope in {Path.GetFileName(filePath)}.", outerApply.DryRun, outerApply.Diff, ChangedContent: outerChanges, Validated: true) };
            }
            return new SentinelCallToolResult<AppliedChangeSummary>
            {
                IsSuccess = false,
                ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown destination '{destination}'. Valid values: ownFile, outerScope.")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MoveType ({Destination}) failed for '{TypeName}' in '{FilePathWrapper}'", destination, typeName, filePath);
            return new SentinelCallToolResult<AppliedChangeSummary>
            {
                IsSuccess = false,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "MoveType")
            };
        }
    }
}
