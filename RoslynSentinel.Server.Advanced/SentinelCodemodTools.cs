using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

[McpServerToolType]
public class SentinelCodemodTools
{
    // ── shared engines ────────────────────────────────────────────────────────
    private readonly RefactoringEngine _refactoringEngine;
    private readonly LogicOptimizationEngine _logicOptimizationEngine;
    private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    private readonly SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private readonly CodeStyleEngine _codeStyleEngine;
    private readonly AdvancedLogicEngine _advancedLogicEngine;
    private readonly ModernizationEngine _modernizationEngine;
    private readonly CodeGenerationEngine _codeGenerationEngine;
    // ── apply_file_codemod engines ────────────────────────────────────────────
    private readonly IDEStyleEngine _ideStyleEngine;
    private readonly CodeHealingEngine _codeHealingEngine;
    private readonly AdvancedRefactoringEngine _advancedRefactoringEngine;
    private readonly MsToolAugmentEngine _msToolAugmentEngine;
    private readonly DocumentationEngine _documentationEngine;
    private readonly ProjectStructureEngine _projectStructureEngine;
    // ── apply_method_codemod engines ──────────────────────────────────────────
    private readonly ThreadSafetyEngine _threadSafetyEngine;
    private readonly GranularRefactoringEngine _granularRefactoringEngine;
    private readonly OutParamRefactoringEngine _outParamRefactoringEngine;
    private readonly StandardRefactoringEngine _standardRefactoringEngine;
    private readonly CodeFlowEngine _codeFlowEngine;
    // ── apply_class_codemod engines ───────────────────────────────────────────
    private readonly AdvancedStructuralEngine _advancedStructuralEngine;
    private readonly ModernLoggingEngine _modernLoggingEngine;
    private readonly ImmutabilityEngine _immutabilityEngine;
    private readonly ApiIntegrationEngine _apiIntegrationEngine;
    private readonly ArchitecturalEngine _architecturalEngine;
    // ── generate engines ──────────────────────────────────────────────────────
    private readonly AnalysisEngine _analysisEngine;
    private readonly TestingEngine _testingEngine;
    private readonly PathDrivenTestEngine _pathDrivenTestEngine;

    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelCodemodTools> _logger;

    public SentinelCodemodTools(
        RefactoringEngine refactoringEngine,
        LogicOptimizationEngine logicOptimizationEngine,
        AsyncOptimizationEngine asyncOptimizationEngine,
        SyntaxUpgradeEngine syntaxUpgradeEngine,
        CodeStyleEngine codeStyleEngine,
        AdvancedLogicEngine advancedLogicEngine,
        ModernizationEngine modernizationEngine,
        CodeGenerationEngine codeGenerationEngine,
        IDEStyleEngine ideStyleEngine,
        CodeHealingEngine codeHealingEngine,
        AdvancedRefactoringEngine advancedRefactoringEngine,
        MsToolAugmentEngine augmentEngine,
        DocumentationEngine documentationEngine,
        ProjectStructureEngine projectStructureEngine,
        ThreadSafetyEngine threadSafetyEngine,
        GranularRefactoringEngine granularRefactoringEngine,
        OutParamRefactoringEngine outParamRefactoringEngine,
        StandardRefactoringEngine standardRefactoringEngine,
        CodeFlowEngine codeFlowEngine,
        AdvancedStructuralEngine advancedStructuralEngine,
        ModernLoggingEngine modernLoggingEngine,
        ImmutabilityEngine immutabilityEngine,
        ApiIntegrationEngine apiIntegrationEngine,
        ArchitecturalEngine architecturalEngine,
        AnalysisEngine analysisEngine,
        TestingEngine testingEngine,
        PathDrivenTestEngine pathDrivenTestEngine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelCodemodTools> logger)
    {
        _refactoringEngine = refactoringEngine;
        _logicOptimizationEngine = logicOptimizationEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _syntaxUpgradeEngine = syntaxUpgradeEngine;
        _codeStyleEngine = codeStyleEngine;
        _advancedLogicEngine = advancedLogicEngine;
        _modernizationEngine = modernizationEngine;
        _codeGenerationEngine = codeGenerationEngine;
        _ideStyleEngine = ideStyleEngine;
        _codeHealingEngine = codeHealingEngine;
        _advancedRefactoringEngine = advancedRefactoringEngine;
        _msToolAugmentEngine = augmentEngine;
        _documentationEngine = documentationEngine;
        _projectStructureEngine = projectStructureEngine;
        _threadSafetyEngine = threadSafetyEngine;
        _granularRefactoringEngine = granularRefactoringEngine;
        _outParamRefactoringEngine = outParamRefactoringEngine;
        _standardRefactoringEngine = standardRefactoringEngine;
        _codeFlowEngine = codeFlowEngine;
        _advancedStructuralEngine = advancedStructuralEngine;
        _modernLoggingEngine = modernLoggingEngine;
        _immutabilityEngine = immutabilityEngine;
        _apiIntegrationEngine = apiIntegrationEngine;
        _architecturalEngine = architecturalEngine;
        _analysisEngine = analysisEngine;
        _testingEngine = testingEngine;
        _pathDrivenTestEngine = pathDrivenTestEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    // ── 1. apply_file_codemod ─────────────────────────────────────────────────

    [McpServerTool(Name = "ApplyFileCodemod")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Applies a file-wide code transformation; most transforms return updated file content. transform: call describe_advanced_tool_options("apply_file_codemod") for valid values. libraryMode=true → .ConfigureAwait(false) on all awaits (for add_configure_await_false). preview=true → returns updated content without writing (for format_document_safe / sort_and_deduplicate_usings). Some transforms return type-specific results (SourceTransformResult, UsingsCleanupResult, etc.). Throws InvalidOperationException if file not found or no changes needed.
        """)]
    public async Task<ToolResult<object>> ApplyFileCodemod(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.DataType)] string transform,
        [ExternalInputRequired(DataTag.LibraryMode)] bool libraryMode = true,
        [ToolOption(ToolOptionTag.Preview)] bool preview = false,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        try
        {
            FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
            switch (transform)
            {
                case "add_braces":
                    {
                        var r = await _syntaxUpgradeEngine.AddBracesAsync(filePath);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No brace-less control flow statements found in '{filePath}'. File already uses braces consistently." };
                        }

                        return new ToolResult<object>() { Success = true, Data = r.ToJsonSummary() };
                    }
                case "cleanup_implicit_spans":
                    {
                        var r = await _syntaxUpgradeEngine.CleanupImplicitSpansAsync(filePath);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No implicit Span/Memory conversion patterns found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = r.ToJsonSummary() };
                    }
                case "convert_to_null_coalescing":
                    {
                        var r = await _logicOptimizationEngine.ConvertToNullCoalescingAsync(filePath);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No null-check patterns eligible for ??/??= conversion found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = r.ToJsonSummary() };
                    }
                case "convert_to_pattern":
                    {
                        var r = await _modernizationEngine.ConvertToPatternAsync(filePath);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No if/switch chains eligible for pattern-matching conversion found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = r.ToJsonSummary() };
                    }
                case "convert_to_switch":
                    {
                        var r = await _logicOptimizationEngine.ConvertToSwitchAsync(filePath);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No if-else chains eligible for switch expression conversion found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = r.ToJsonSummary() };
                    }
                case "fix_mismatched_namespaces":
                    {
                        var r = await _projectStructureEngine.FixMismatchedNamespacesAsync(filePath);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No namespace/folder mismatches found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = r.ToJsonSummary() };
                    }
                case "fix_thread_sleep":
                    {
                        try
                        {
                            var r = await _codeHealingEngine.FixThreadSleepAsync(filePath);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new ToolResult<object>() { Success = true, Data = $"No Thread.Sleep calls eligible for async conversion found in '{filePath}'." };
                            }

                            return new ToolResult<object>() { Success = true, Data = r.ToJsonSummary() };
                        }
                        catch (InvalidOperationException ioe) { return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"fix_thread_sleep failed for '{filePath}': {ioe.Message}. Ensure the solution is loaded.") }; }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "fix_thread_sleep unexpected exception for '{FilePath}'", filePath);
                            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"fix_thread_sleep for '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
                        }
                    }
                case "format_document_preview":
                    var result = await _refactoringEngine.FormatDocumentPreviewAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result };
                case "format_document_safe":
                    var result2 = await _msToolAugmentEngine.FormatDocumentSafeAsync(filePath, preview);
                    return new ToolResult<object>() { Success = true, Data = result2 };
                case "generate_xml_documentation_stubs":
                    {
                        var r = await _documentationEngine.GenerateXmlDocumentationStubsAsync(filePath);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No undocumented public members found in '{filePath}'. File already has XML doc stubs." };
                        }

                        return new ToolResult<object>() { Success = true, Data = r.ToJsonSummary() };
                    }
                case "optimize_task_wait":
                    {
                        var result3 = await _advancedRefactoringEngine.OptimizeTaskWaitAsync(filePath);
                        if (string.IsNullOrEmpty(result3.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No synchronous Task.Wait/.Result/.GetAwaiter().GetResult() patterns found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result3.Outcome };
                    }
                case "preview_add_missing_usings":
                    var result4 = await _msToolAugmentEngine.PreviewAddMissingUsingsAsync(filePath);
                    return new ToolResult<object>() { Success = true, Data = result4 };
                case "add_configure_await_false":
                    {
                        var result5 = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync(filePath, libraryMode);
                        if (string.IsNullOrEmpty(result5.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No awaits missing .ConfigureAwait(false) found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = new SourceTransformResult(result5.UpdatedText, false, false, filePath) };
                    }
                case "remove_configure_await_false":
                    {
                        var result6 = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync(filePath);
                        if (string.IsNullOrEmpty(result6.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No .ConfigureAwait(false) calls found to remove in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = new SourceTransformResult(result6.UpdatedText, false, false, filePath) };
                    }
                case "simplify_boolean_expressions":
                    {
                        var result7 = await _logicOptimizationEngine.SimplifyBooleanExpressionsAsync(filePath);
                        if (string.IsNullOrEmpty(result7.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No boolean expressions eligible for simplification found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result7 };
                    }
                case "simplify_member_access":
                    {
                        var result8 = await _ideStyleEngine.SimplifyMemberAccessAsync(filePath);
                        if (string.IsNullOrEmpty(result8.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No qualified member access patterns found to simplify in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result8 };
                    }
                case "simplify_verbosity":
                    {
                        var result9 = await _codeStyleEngine.SimplifyVerbosityAsync(filePath);
                        if (string.IsNullOrEmpty(result9.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No verbose patterns found to simplify in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result9 };
                    }
                case "sort_and_deduplicate_usings":
                    {
                        var result17 = await _msToolAugmentEngine.SortAndDeduplicateUsingsAsync(filePath, !preview);
                        if (result17 == null)
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No unsorted or duplicate using directives found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result17 };
                    }
                case "upgrade_pattern_matching":
                    {
                        var result10 = await _syntaxUpgradeEngine.UpgradePatternMatchingAsync(filePath);
                        if (string.IsNullOrEmpty(result10.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No type-check/cast patterns eligible for modern pattern matching found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result10 };
                    }
                case "upgrade_thread_safety":
                    {
                        var result11 = await _codeStyleEngine.FixDangerousLockAsync(filePath);
                        if (string.IsNullOrEmpty(result11.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No dangerous lock patterns found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result11 };
                    }
                case "upgrade_to_file_scoped_namespace":
                    {
                        var result12 = await _syntaxUpgradeEngine.UpgradeToFileScopedNamespaceAsync(filePath);
                        if (string.IsNullOrEmpty(result12.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No block-scoped namespace declarations found in '{filePath}'. File already uses file-scoped namespaces." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result12 };
                    }
                case "upgrade_to_modern_guards":
                    {
                        var result13 = await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync(filePath);
                        if (string.IsNullOrEmpty(result13.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No legacy null/argument guard patterns found in '{filePath}'. File already uses modern guards." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result13 };
                    }
                case "use_field_backed_properties":
                    {
                        var result14 = await _syntaxUpgradeEngine.UseFieldBackedPropertiesAsync(filePath);
                        if (string.IsNullOrEmpty(result14.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No auto-properties eligible for field-backed conversion found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result14 };
                    }
                case "use_index_from_end":
                    {
                        var result15 = await _codeStyleEngine.UseIndexFromEndAsync(filePath);
                        if (string.IsNullOrEmpty(result15.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No array/list indexing patterns eligible for index-from-end (^n) syntax found in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result15 };
                    }
                case "use_time_provider":
                    {
                        var result16 = await _codeStyleEngine.UseTimeProviderAsync(filePath);
                        if (string.IsNullOrEmpty(result16.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No DateTime.Now/UtcNow calls found to replace with ITimeProvider in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = result16 };
                    }
                default:
                    return new ToolResult<object>()
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"Unknown transform '{transform}'. Valid values: add_braces, cleanup_implicit_spans, " +
                        "convert_to_null_coalescing, convert_to_pattern, convert_to_switch, fix_mismatched_namespaces, " +
                        "fix_thread_sleep, format_document_preview, format_document_safe, generate_xml_documentation_stubs, " +
                        "optimize_task_wait, preview_add_missing_usings, add_configure_await_false, remove_configure_await_false, " +
                        "simplify_boolean_expressions, simplify_member_access, simplify_verbosity, sort_and_deduplicate_usings, " +
                        "upgrade_pattern_matching, upgrade_thread_safety, upgrade_to_file_scoped_namespace, " +
                        "upgrade_to_modern_guards, use_field_backed_properties, use_index_from_end, use_time_provider.")
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyFileCodemod ({Transform}) failed", transform);
            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"ApplyFileCodemod ({transform}) failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    // ── 2. apply_method_codemod ───────────────────────────────────────────────

    [McpServerTool(Name = "ApplyMethodCodemod")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Applies a method-scoped code transformation; most transforms return updated file content. transform: call describe_advanced_tool_options("apply_method_codemod") for valid values. direction: required for convert_expression_body — "ToExpression" or "ToBlock". lockFieldName names the lock field for make_method_thread_safe (default "_lock"). contextSnippet/lineBefore/lineAfter disambiguate convert_expression_body. Some transforms return type-specific results (SourceTransformResult, OutParamConversionResult). Throws InvalidOperationException if file or method not found.
        """)]
    public async Task<ToolResult<object>> ApplyMethodCodemod(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.MethodName, required: true)] string methodName,
        [ExternalInputRequired(DataTag.Transform)] string transform,
        [ToolOption(ToolOptionTag.Direction)] string? direction = null,
        [Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet = null,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null,
        [ExternalInputRequired(DataTag.SymbolName)] string lockFieldName = "_lock",
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        try
        {
            FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
            switch (transform)
            {
                case "add_guard_clauses":
                    {
                        var r = await _logicOptimizationEngine.AddGuardClausesAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No parameters eligible for guard clause insertion found in '{methodName}' in '{filePath}'." };
                        }

                        return new ToolResult<object>() { Success = true, Data = new SourceTransformResult(r.UpdatedText, false, false, filePath) };
                    }
                case "convert_expression_body":
                    {
                        if (string.IsNullOrEmpty(direction))
                        {
                            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.InvalidArgument, "direction is required for convert_expression_body. Valid values: ToExpression, ToBlock.") };
                        }

                        var r = await _refactoringEngine.ConvertExpressionBodyAsync(filePath, methodName, direction, contextSnippet, lineBefore, lineAfter);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception,
                                $"convert_expression_body ({direction}) found nothing to convert for '{methodName}' in '{filePath}'. " +
                                "Possible causes: member not found (verify name and file are correct), member already has the target body style, " +
                                "or contextSnippet did not uniquely match. Use get_file_outline to confirm the member exists.") };
                        }

                        return new ToolResult<object>() { Success = true, Data = new SourceTransformResult(r.UpdatedText, false, false, filePath) };
                    }
                case "convert_lock_to_semaphore_slim":
                    {
                        var r = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = true, Data = $"No lock statements found in '{methodName}' in '{filePath}' to convert to SemaphoreSlim." };
                        }

                        return new ToolResult<object>() { Success = true, Data = new SourceTransformResult(r.UpdatedText, false, false, filePath) };
                    }
                case "convert_method_to_indexer":
                    {
                        var r = await _granularRefactoringEngine.ConvertMethodToIndexerAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception,
                                $"convert_method_to_indexer: method '{methodName}' not found or not eligible in '{filePath}'. " +
                                "The method must have exactly one parameter and return a value. Use get_file_outline to verify the method exists.") };
                        }

                        return new ToolResult<object>() { Success = true, Data = new SourceTransformResult(r.UpdatedText, false, false, filePath) };
                    }
                case "convert_out_params_to_value_tuple":
                    var result = await _outParamRefactoringEngine.ConvertOutParamsToValueTupleAsync(filePath, methodName);
                    return new ToolResult<object>
                    {
                        Success = result?.Success ?? false,
                        Data = result,
                        Error = result != null && !result.Success
                            ? new ResultError(ToolErrorCode.Exception, $"convert_out_params_to_value_tuple failed for '{methodName}' in '{filePath}': {result.Message}")
                            : null
                    };
                case "convert_static_to_extension":
                    {
                        try
                        {
                            var r = await _advancedLogicEngine.ConvertStaticToExtensionAsync(filePath, methodName);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception,
                                    $"convert_static_to_extension: method '{methodName}' not found or not eligible in '{filePath}'. " +
                                    "The method must be static and have at least one parameter to become the 'this' parameter. Use get_file_outline to verify.") };
                            }

                            return new ToolResult<object>() { Success = true, Data = new SourceTransformResult(r.UpdatedText, false, false, filePath) };
                        }
                        catch (InvalidOperationException) { return new ToolResult<object>() { Success = false, Error = new ResultError(ToolErrorCode.Exception, $"convert_static_to_extension failed for '{methodName}' in '{filePath}': invalid operation. Ensure the solution is loaded.") }; }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "convert_static_to_extension unexpected exception for '{MethodName}' in '{FilePath}'", methodName, filePath);
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception, $"convert_static_to_extension for '{methodName}' in '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
                            };
                        }
                    }
                case "convert_switch_to_expression":
                    {
                        var r = await _syntaxUpgradeEngine.ConvertSwitchToExpressionAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = $"No switch statements eligible for switch expression conversion found in '{methodName}' in '{filePath}'."
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "convert_to_async_enumerable":
                    {
                        var r = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"convert_to_async_enumerable: method '{methodName}' not found or not eligible in '{filePath}'. " +
                                    "The method must return Task<List<T>> or Task<IEnumerable<T>>. Use get_file_outline to verify the method signature.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "extension_to_static":
                    {
                        var r = await _advancedLogicEngine.ExtensionToStaticAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"extension_to_static: method '{methodName}' not found or not an extension method in '{filePath}'. " +
                                    "The method must be in a static class and have a 'this' parameter. Use get_file_outline to verify.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "generate_async_overload":
                    {
                        try
                        {
                            var r = await _asyncOptimizationEngine.GenerateAsyncOverloadAsync(filePath, methodName);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new ToolResult<object>
                                {
                                    Success = false,
                                    Error = new ResultError(ToolErrorCode.Exception,
                                        $"generate_async_overload: method '{methodName}' not found or not eligible in '{filePath}'. " +
                                        "The method must be synchronous and non-void. Use get_file_outline to verify the method exists and its signature.")
                                };
                            }

                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                            };
                        }
                        catch (InvalidOperationException)
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception, $"generate_async_overload failed for '{methodName}' in '{filePath}': invalid operation. Ensure the solution is loaded.")
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "generate_async_overload unexpected exception for '{MethodName}' in '{FilePath}'", methodName, filePath);
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception, $"generate_async_overload for '{methodName}' in '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
                            };
                        }
                    }
                case "make_method_static":
                    {
                        var r = await _standardRefactoringEngine.MakeMethodStaticAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"make_method_static: method '{methodName}' not found or not eligible in '{filePath}'. " +
                                    "The method must not access instance members. Use get_file_outline to verify the method exists.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "make_method_thread_safe":
                    {
                        var r = await _threadSafetyEngine.MakeMethodThreadSafeAsync(filePath, methodName, lockFieldName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>()
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"make_method_thread_safe: method '{methodName}' not found in '{filePath}'. " +
                                    "Use get_file_outline to verify the method name (case-sensitive).")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "optimize_independent_awaits":
                    {
                        var r = await _asyncOptimizationEngine.OptimizeIndependentAwaitsAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = $"No sequential independent awaits found to parallelize in '{methodName}' in '{filePath}'."
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "optimize_to_value_task":
                    {
                        var r = await _asyncOptimizationEngine.OptimizeToValueTaskAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"optimize_to_value_task: method '{methodName}' not found or not eligible in '{filePath}'. " +
                                    "The method must return Task or Task<T> and be async. Use get_file_outline to verify.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "reduce_block_depth":
                    {
                        var r = await _codeFlowEngine.ReduceBlockDepthAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = $"No deeply nested blocks found to flatten in '{methodName}' in '{filePath}'."
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "update_xml_docs_from_signature":
                    {
                        var r = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = $"No XML doc parameters out of sync with the signature of '{methodName}' in '{filePath}'."
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "use_exception_expressions":
                    {
                        var r = await _syntaxUpgradeEngine.UseExceptionExpressionsAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = $"No if-throw guard patterns eligible for exception expression conversion found in '{methodName}' in '{filePath}'."
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                default:
                    return new ToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"Unknown transform '{transform}'. Valid values: add_guard_clauses, convert_expression_body, " +
                         "convert_lock_to_semaphore_slim, convert_method_to_indexer, convert_out_params_to_value_tuple, " +
                         "convert_static_to_extension, convert_switch_to_expression, convert_to_async_enumerable, " +
                         "extension_to_static, generate_async_overload, make_method_static, make_method_thread_safe, " +
                         "optimize_independent_awaits, optimize_to_value_task, reduce_block_depth, " +
                         "update_xml_docs_from_signature, use_exception_expressions.")
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyMethodCodemod ({Transform}) failed for '{MethodName}'", transform, methodName);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"ApplyMethodCodemod ({transform}) failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    // ── 3. apply_class_codemod ────────────────────────────────────────────────

    [McpServerTool(Name = "ApplyClassCodemod")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Applies a class-scoped code transformation; returns updated file content as a string. transform: call describe_advanced_tool_options("apply_class_codemod") for valid values. direction: required for convert_property_safe — "ToFullProperty" or "ToAutoProperty". contextSnippet/lineBefore/lineAfter disambiguate convert_property_safe. Throws InvalidOperationException if file or class not found.
        """)]
    public async Task<ToolResult<object>> ApplyClassCodemod(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.ClassName)] string className,
        [ExternalInputRequired(DataTag.Transform)] string transform,
        [ExternalInputRequired(DataTag.PropertyName)] string? propertyName = null,
        [ToolOption(ToolOptionTag.Direction)] string? direction = null,
        [Consumes(DataTag.ContextSnippet, required: true)] string? contextSnippet = null,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        try
        {
            FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

            switch (transform)
            {
                case "add_validation_to_poco":
                    {
                        try
                        {
                            var r = await _apiIntegrationEngine.AddValidationToPocoAsync(filePath, className);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new ToolResult<object>
                                {
                                    Success = true,
                                    Data = $"No unvalidated properties found on '{className}' in '{filePath}'. Class may already have validation attributes or have no settable properties."
                                };
                            }

                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                            };
                        }
                        catch (InvalidOperationException ioe)
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"add_validation_to_poco: class '{className}' not found in '{filePath}'. {ioe.Message} " +
                                    "Use get_file_outline to verify the class name (case-sensitive).")
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "add_validation_to_poco unexpected exception for '{ClassName}' in '{FilePath}'", className, filePath);
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception, $"add_validation_to_poco for '{className}' in '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
                            };
                        }
                    }
                case "class_to_record":
                    {
                        var r = await _modernizationEngine.ClassToRecordAsync(filePath, className);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"class_to_record: class '{className}' not found or not eligible in '{filePath}'. " +
                                    "The class must have no custom methods beyond property accessors. Use get_file_outline to verify.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "convert_abstract_to_interface":
                    {
                        try
                        {
                            var r = await _advancedStructuralEngine.ConvertAbstractClassToInterfaceAsync(filePath, className);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new ToolResult<object>
                                {
                                    Success = false,
                                    Error = new ResultError(ToolErrorCode.Exception,
                                        $"convert_abstract_to_interface: class '{className}' not found or is not abstract in '{filePath}'. " +
                                        "The class must be declared with the 'abstract' keyword. Use get_file_outline to verify.")
                                };
                            }

                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                            };
                        }
                        catch (InvalidOperationException ioe)
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"convert_abstract_to_interface: class '{className}' not eligible in '{filePath}'. {ioe.Message}")
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "convert_abstract_to_interface unexpected exception for '{ClassName}' in '{FilePath}'", className, filePath);
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception, $"convert_abstract_to_interface for '{className}' in '{filePath}' failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
                            };
                        }
                    }
                case "convert_property_safe":
                    {
                        var propName = propertyName ?? className;
                        if (string.IsNullOrEmpty(direction))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.InvalidArgument, "direction is required for convert_property_safe. Valid values: ToFullProperty, ToAutoProperty.")
                            };
                        }

                        var r = await _codeGenerationEngine.ConvertPropertySafeAsync(filePath, propName, direction, contextSnippet, lineBefore, lineAfter);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"convert_property_safe ({direction}): property '{propName}' not found or not eligible in '{filePath}'. " +
                                    "Possible causes: property name is wrong (case-sensitive), property already has the target style, " +
                                    "or contextSnippet did not uniquely identify it. Use get_file_outline to list available properties.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "convert_property_to_methods":
                    {
                        var propName = propertyName ?? className;
                        var r = await _codeStyleEngine.ConvertPropertyToMethodsAsync(filePath, propName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"convert_property_to_methods: property '{propName}' not found in '{filePath}'. " +
                                    "Use get_file_outline to list available properties (name is case-sensitive).")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "convert_to_background_service":
                    {
                        var r = await _architecturalEngine.ConvertToBackgroundServiceAsync(filePath, className);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"convert_to_background_service: class '{className}' not found or not eligible in '{filePath}'. " +
                                    "The class must not already implement BackgroundService. Use get_file_outline to verify.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "convert_to_source_generated_logging":
                    {
                        var r = await _modernLoggingEngine.ConvertToSourceGeneratedLoggingAsync(filePath, className);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = $"No ILogger.Log calls found to convert to source-generated logging in '{className}' in '{filePath}'."
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "document_poco_fields":
                    {
                        var r = await _documentationEngine.DocumentPocoFieldsAsync(filePath, className);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = $"No undocumented fields/properties found on '{className}' in '{filePath}'. Class may already be documented or have no public fields."
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "make_class_immutable":
                    {
                        var r = await _immutabilityEngine.MakeClassImmutableAsync(filePath, className);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"make_class_immutable: class '{className}' not found or already immutable in '{filePath}'. " +
                                    "Use get_file_outline to verify the class exists and has mutable properties.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "record_to_class":
                    {
                        var r = await _modernizationEngine.RecordToClassAsync(filePath, className);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"record_to_class: record '{className}' not found in '{filePath}'. " +
                                    "The type must be declared as a 'record'. Use get_file_outline to verify.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "replace_constructor_with_factory":
                    {
                        var r = await _advancedStructuralEngine.ReplaceConstructorWithFactoryAsync(filePath, className);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"replace_constructor_with_factory: class '{className}' not found or not eligible in '{filePath}'. " +
                                    "Use get_file_outline to verify the class name (case-sensitive).")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "sort_members":
                    {
                        var r = await _refactoringEngine.SortMembersAsync(filePath, className);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = true,
                                Data = $"No members to reorder found in '{className}' in '{filePath}'. Type may be empty or already sorted."
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                case "upgrade_to_primary_constructor":
                    {
                        var r = await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync(filePath, className);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>
                            {
                                Success = false,
                                Error = new ResultError(ToolErrorCode.Exception,
                                    $"upgrade_to_primary_constructor: class '{className}' not found or not eligible in '{filePath}'. " +
                                    "The constructor must only assign parameters to readonly fields (no other logic). Use get_file_outline to verify the class exists.")
                            };
                        }

                        return new ToolResult<object>
                        {
                            Success = true,
                            Data = new SourceTransformResult(r.UpdatedText, false, false, filePath)
                        };
                    }
                default:
                    return new ToolResult<object>
                    {
                        Success = false,
                        Error = new ResultError(ToolErrorCode.Exception, $"Unknown transform '{transform}'. Valid values: add_validation_to_poco, class_to_record, " + "convert_abstract_to_interface, convert_property_safe, convert_property_to_methods, " + "convert_to_background_service, convert_to_source_generated_logging, document_poco_fields, " + "make_class_immutable, record_to_class, replace_constructor_with_factory, sort_members, " + "upgrade_to_primary_constructor.")
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyClassCodemod ({Transform}) failed for '{ClassName}'", transform, className);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"ApplyClassCodemod ({transform}) failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    // ── 4. generate ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "Generate")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Generates new code for a type or method. kind: call describe_advanced_tool_options("generate") for valid values, required parameters per kind, and return types. filePath required for all kinds except generate_decorator_class. decoratorPrefix defaults to "Logging". framework for test generation: "NUnit" (default), "xunit", or "mstest". disambiguateLine resolves overloaded method targets for generate_path_driven_tests.
        """)]
    public async Task<ToolResult<object>> Generate(
        string kind,
        [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null,
        [Consumes(DataTag.ClassName)] string? className = null,
        [Consumes(DataTag.MethodName)] string? methodName = null,
        [Consumes(DataTag.MemberName)] string? members = null,
        [ExternalInputRequired(DataTag.DecoratorPrefix)] string decoratorPrefix = "Logging",
        [ExternalInputRequired(DataTag.ProjectName)] string? projectName = null,
        [ExternalInputRequired(DataTag.Framework)] string framework = "NUnit",
        [ExternalInputRequired(DataTag.StartLine)] int? disambiguateLine = null,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        try
        {
            FilePath filePath = _workspaceManager.SetFilePath(filepath);

            switch (kind)
            {
                case "add_benchmark_stub":
                    {
                        if (!filePath.Validated)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath is required for add_benchmark_stub.") };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "className is required for add_benchmark_stub.") };
                        }

                        if (string.IsNullOrEmpty(methodName))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "methodName is required for add_benchmark_stub.") };
                        }

                        var r = await _testingEngine.AddBenchmarkStubAsync(filePath, className, methodName);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.Exception, $"add_benchmark_stub failed for '{className}.{methodName}' in '{filePath}': file not found, class not found, or method not found. Ensure the solution is loaded.") };
                        }

                        return new ToolResult<object>() { Success = true, Data = new SourceTransformResult(r.UpdatedText, false, false, filePath) };
                    }
                case "generate_constructor":
                    {
                        if (!filePath.Validated)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath is required for generate_constructor.") };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_constructor.") };
                        }

                        var result = await _codeGenerationEngine.GenerateConstructorAsync(filePath, className);
                        if (string.IsNullOrEmpty(result.UpdatedText))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.Exception, $"generate_constructor failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.") };
                        }

                        return new ToolResult<object>() { Success = true, Data = result.ToJsonSummary() };
                    }
                case "generate_decorator_class":
                    {
                        if (string.IsNullOrEmpty(className))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "className (the interface name) is required for generate_decorator_class.") };
                        }

                        var result = await _codeGenerationEngine.GenerateDecoratorClassAsync(className, decoratorPrefix, projectName);
                        if (result == null)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.Exception, $"generate_decorator_class: interface '{className}' not found in the solution{(projectName != null ? $" project '{projectName}'" : string.Empty)}. Ensure the interface name matches exactly (including leading 'I') and is part of the loaded solution.") };
                        }

                        return new ToolResult<object>() { Success = true, Data = result };
                    }
                case "generate_equality_overrides":
                    {
                        if (!filePath.Validated)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath is required for generate_equality_overrides.") };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_equality_overrides.") };
                        }

                        var result = await _analysisEngine.GenerateEqualityOverridesAsync(filePath, className);
                        if (string.IsNullOrEmpty(result.UpdatedText))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.Exception, $"generate_equality_overrides failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.") };
                        }

                        return new ToolResult<object>() { Success = true, Data = result.ToJsonSummary() };
                    }
                case "generate_fluent_builder":
                    {
                        if (!filePath.Validated)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath is required for generate_fluent_builder.") };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_fluent_builder.") };
                        }

                        try
                        {
                            var result = await _codeGenerationEngine.GenerateFluentBuilderAsync(filePath, className);
                            return new ToolResult<object>() { Success = true, Data = result };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "generate_fluent_builder failed for '{ClassName}' in '{FilePath}'", className, filePath);
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.Exception, $"{ex.GetType().Name}: {ex.Message}") };
                        }
                    }
                case "generate_path_driven_tests":
                    {
                        if (!filePath.Validated)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath is required for generate_path_driven_tests.") };
                        }

                        if (string.IsNullOrEmpty(methodName))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "methodName is required for generate_path_driven_tests.") };
                        }

                        var result = await _pathDrivenTestEngine.GeneratePathDrivenTestsAsync(filePath, methodName, framework, disambiguateLine);
                        return new ToolResult<object>() { Success = true, Data = result };
                    }
                case "generate_repository_interface":
                    {
                        if (filePath.Validated)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath is required for generate_repository_interface.") };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_repository_interface.") };
                        }

                        var result = await _codeGenerationEngine.GenerateRepositoryInterfaceAsync(filePath, className);
                        return new ToolResult<object>() { Success = true, Data = result };
                    }
                case "generate_test_scaffold":
                    {
                        if (!filePath.Validated)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath is required for generate_test_scaffold.") };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_test_scaffold.") };
                        }

                        var result = await _testingEngine.GenerateTestScaffoldAsync(filePath, className);
                        return new ToolResult<object>() { Success = true, Data = result };
                    }
                case "generate_test_skeleton":
                    {
                        if (!filePath.Validated)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath is required for generate_test_skeleton.") };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_test_skeleton.") };
                        }

                        var result = await _testingEngine.GenerateTestSkeletonAsync(filePath, className);
                        return new ToolResult<object>() { Success = true, Data = result };
                    }
                case "generate_to_string_safe":
                    {
                        if (!filePath.Validated)
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "filePath is required for generate_to_string_safe.") };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.InvalidArgument, "className (typeName) is required for generate_to_string_safe.") };
                        }

                        IList<string>? memberList = members is null ? null
                            : members.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(m => m.Trim())
                                     .Where(m => m.Length > 0)
                                     .ToList();
                        var result = await _msToolAugmentEngine.GenerateToStringSafeAsync(filePath, className, memberList);
                        return new ToolResult<object>() { Success = true, Data = result };
                    }
                default:
                    return new ToolResult<object>()
                    {
                        Error = new ResultError(ToolErrorCode.InvalidArgument,
                        $"Unknown kind '{kind}'. Valid values: add_benchmark_stub, generate_constructor, " +
                        "generate_decorator_class, generate_equality_overrides, generate_fluent_builder, " +
                        "generate_path_driven_tests, generate_repository_interface, generate_test_scaffold, " +
                        "generate_test_skeleton, generate_to_string_safe.")
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generate ({Kind}) failed", kind);
            return new ToolResult<object>() { Error = new ResultError(ToolErrorCode.Exception, $"Generate ({kind}) failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}") };
        }
    }

    internal static ToolOptionsResult ApplyFileCodemodOptions() => new()
    {
        Description = """
            apply_file_codemod — valid transform values:
              add_braces                        Adds braces to all brace-less control statements.
              cleanup_implicit_spans            Removes redundant implicit Span<T>→Span<byte> casts.
              convert_to_null_coalescing        Replaces null-conditional chains with ?? operators.
              convert_to_pattern                Converts is/as type-check+cast pairs to pattern matching.
              convert_to_switch                 Converts if-else chains to switch expressions.
              fix_mismatched_namespaces         Corrects namespace declarations to match folder structure.
              fix_thread_sleep                  Replaces Thread.Sleep with await Task.Delay in async methods.
              format_document_preview           Returns a FormatPreviewResult diff without writing.
              format_document_safe              Formats the document. preview=false writes to disk; preview=true returns content only.
              generate_xml_documentation_stubs  Generates XML doc stubs for all undocumented public methods.
              optimize_task_wait                Converts blocking Task.Wait/Result to async/await.
              preview_add_missing_usings        Returns AddUsingsPreview listing missing usings (read-only).
              add_configure_await_false         Adds .ConfigureAwait(false) to all awaits. libraryMode=true (default).
                                                Returns SourceTransformResult.
              remove_configure_await_false      Removes all .ConfigureAwait(x) calls. Returns SourceTransformResult.
              simplify_boolean_expressions      Simplifies redundant boolean expressions (x == true → x).
              simplify_member_access            Removes unnecessary this./base. qualifiers.
              simplify_verbosity                Removes redundant type names and default parameter values.
              sort_and_deduplicate_usings       Sorts and deduplicates using directives. preview=false writes to disk.
                                                Returns UsingsCleanupResult.
              upgrade_pattern_matching          Upgrades is/as casts to C# pattern-matching syntax.
              upgrade_thread_safety             Fixes dangerous double-checked locking patterns.
              upgrade_to_file_scoped_namespace  Converts block-scoped namespace to file-scoped.
              upgrade_to_modern_guards          Converts null-check guards to ArgumentNullException.ThrowIfNull.
              use_field_backed_properties       Converts auto-properties with backing fields to field-backed (C# 13).
              use_index_from_end                Converts array[array.Length - N] to array[^N].
              use_time_provider                 Replaces DateTime.Now/UtcNow with ITimeProvider calls.

            Additional parameters:
              libraryMode: for add_configure_await_false — true (default) adds .ConfigureAwait(false) to all awaits.
              preview: for format_document_safe and sort_and_deduplicate_usings — false (default) writes to disk.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["transforms"] = new[] {
                "add_braces", "cleanup_implicit_spans", "convert_to_null_coalescing", "convert_to_pattern",
                "convert_to_switch", "fix_mismatched_namespaces", "fix_thread_sleep", "format_document_preview",
                "format_document_safe", "generate_xml_documentation_stubs", "optimize_task_wait",
                "preview_add_missing_usings", "add_configure_await_false", "remove_configure_await_false",
                "simplify_boolean_expressions", "simplify_member_access", "simplify_verbosity",
                "sort_and_deduplicate_usings", "upgrade_pattern_matching", "upgrade_thread_safety",
                "upgrade_to_file_scoped_namespace", "upgrade_to_modern_guards", "use_field_backed_properties",
                "use_index_from_end", "use_time_provider"
            }
        }
    };

    internal static ToolOptionsResult ApplyMethodCodemodOptions() => new()
    {
        Description = """
            apply_method_codemod — valid transform values:
              add_guard_clauses              Adds ArgumentNullException.ThrowIfNull guards for reference params.
                                             Returns SourceTransformResult.
              convert_expression_body        Converts between block body and expression body.
                                             direction: "ToExpression" or "ToBlock".
                                             contextSnippet/lineBefore/lineAfter to disambiguate.
              convert_lock_to_semaphore_slim Converts lock statements to async SemaphoreSlim pattern.
                                             Returns SourceTransformResult.
              convert_method_to_indexer      Converts a single-parameter get/set method pair to an indexer.
              convert_out_params_to_value_tuple  Converts out-parameter methods to ValueTuple returns.
                                             Returns OutParamConversionResult.
              convert_static_to_extension    Converts a static method to an extension method.
              convert_switch_to_expression   Converts a switch statement to a switch expression.
              convert_to_async_enumerable    Converts a Task<List<T>>-returning method to IAsyncEnumerable<T>.
                                             Returns SourceTransformResult.
              extension_to_static            Converts an extension method back to a static method.
              generate_async_overload        Generates an async overload of a synchronous method via Task.Run.
              make_method_static             Removes implicit instance state and makes the method static.
              make_method_thread_safe        Adds a lock field and wraps the method body in a lock statement.
                                             lockFieldName: name for the lock object (default "_lock").
                                             Returns SourceTransformResult.
              optimize_independent_awaits    Batches sequential independent awaits into Task.WhenAll.
              optimize_to_value_task         Converts Task/Task<T> return type to ValueTask/ValueTask<T>.
              reduce_block_depth             Inverts conditions and uses early returns to reduce nesting depth.
              update_xml_docs_from_signature Regenerates XML <param> and <returns> tags from the method signature.
              use_exception_expressions      Replaces throw new ArgumentNullException(nameof(x)) with
                                             ArgumentNullException.ThrowIfNull(x), etc.

            Additional parameters:
              direction: required for convert_expression_body — "ToExpression" or "ToBlock".
              contextSnippet/lineBefore/lineAfter: for convert_expression_body disambiguation.
              lockFieldName: for make_method_thread_safe — name for the lock field (default "_lock").
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["transforms"] = new[] {
                "add_guard_clauses", "convert_expression_body", "convert_lock_to_semaphore_slim",
                "convert_method_to_indexer", "convert_out_params_to_value_tuple", "convert_static_to_extension",
                "convert_switch_to_expression", "convert_to_async_enumerable", "extension_to_static",
                "generate_async_overload", "make_method_static", "make_method_thread_safe",
                "optimize_independent_awaits", "optimize_to_value_task", "reduce_block_depth",
                "update_xml_docs_from_signature", "use_exception_expressions"
            }
        }
    };

    internal static ToolOptionsResult ApplyClassCodemodOptions() => new()
    {
        Description = """
            apply_class_codemod — valid transform values:
              add_validation_to_poco          Adds [Required] and [StringLength(100)] to all string properties.
              class_to_record                 Converts a class to a record type.
              convert_abstract_to_interface   Converts an abstract class to an interface.
              convert_property_safe           Converts a property between auto-property and full property.
                                              propertyName: the property to convert.
                                              direction: "ToFullProperty" or "ToAutoProperty".
                                              contextSnippet/lineBefore/lineAfter to disambiguate.
              convert_property_to_methods     Converts a property to a getter/setter method pair.
                                              propertyName: pass the property name via className or propertyName.
              convert_to_background_service   Adds BackgroundService base class and generates ExecuteAsync override.
              convert_to_source_generated_logging  Converts ILogger calls to source-generated logging.
              document_poco_fields            Adds [Description] XML comments to all fields in a POCO class.
              make_class_immutable            Converts mutable properties to init-only and adds a With method.
              record_to_class                 Converts a record type to a class.
              replace_constructor_with_factory  Replaces a constructor with a static factory method.
              sort_members                    Sorts members by convention (fields, ctors, props, methods).
              upgrade_to_primary_constructor  Converts a simple assignment-only constructor to a C# 12 primary constructor.

            Additional parameters:
              propertyName: for convert_property_safe and convert_property_to_methods.
              direction: required for convert_property_safe — "ToFullProperty" or "ToAutoProperty".
              contextSnippet/lineBefore/lineAfter: for convert_property_safe disambiguation.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["transforms"] = new[] {
                "add_validation_to_poco", "class_to_record", "convert_abstract_to_interface",
                "convert_property_safe", "convert_property_to_methods", "convert_to_background_service",
                "convert_to_source_generated_logging", "document_poco_fields", "make_class_immutable",
                "record_to_class", "replace_constructor_with_factory", "sort_members",
                "upgrade_to_primary_constructor"
            }
        }
    };
}
