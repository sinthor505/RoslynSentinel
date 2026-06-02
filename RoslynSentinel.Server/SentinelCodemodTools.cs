using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

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
    private readonly MsToolAugmentEngine _augmentEngine;
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
        _augmentEngine = augmentEngine;
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
        _logger = logger;
    }

    // ── 1. apply_file_codemod ─────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Applies a file-wide code transformation. Most transforms return the updated file content
        as a string; pass the result to apply_proposed_changes to write to disk.

        filePath:  the .cs file to transform.
        transform: the codemod to apply — call describe_advanced_tool_options("apply_file_codemod") for
                   valid values and per-transform notes.
        libraryMode: for add_configure_await_false — true (default) adds .ConfigureAwait(false) to all awaits.
        preview: for format_document_safe and sort_and_deduplicate_usings — false (default) writes to disk;
                 true returns updated content without writing.

        Returns the updated file content as a string, or a type-specific result for some transforms
        (SourceTransformResult, UsingsCleanupResult, FormatPreviewResult, AddUsingsPreview).
        Throws InvalidOperationException when the file is not found or no changes are needed.
        """)]
    public async Task<object> ApplyFileCodemod(
        string filePath,
        string transform,
        bool libraryMode = true,
        bool preview = false)
    {
        try
        {
            switch (transform)
            {
                case "add_braces":
                    {
                        var r = await _syntaxUpgradeEngine.AddBracesAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"add_braces failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "cleanup_implicit_spans":
                    {
                        var r = await _syntaxUpgradeEngine.CleanupImplicitSpansAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"cleanup_implicit_spans failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_to_null_coalescing":
                    {
                        var r = await _logicOptimizationEngine.ConvertToNullCoalescingAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_to_null_coalescing failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_to_pattern":
                    {
                        var r = await _modernizationEngine.ConvertToPatternAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_to_pattern failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_to_switch":
                    {
                        var r = await _logicOptimizationEngine.ConvertToSwitchAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_to_switch failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "fix_mismatched_namespaces":
                    {
                        var r = await _projectStructureEngine.FixMismatchedNamespacesAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"fix_mismatched_namespaces failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "fix_thread_sleep":
                    {
                        try
                        {
                            var r = await _codeHealingEngine.FixThreadSleepAsync(filePath);
                            if (string.IsNullOrEmpty(r))
                                return ($"fix_thread_sleep failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
                            return r;
                        }
                        catch (InvalidOperationException) { return ($"fix_thread_sleep failed for '{filePath}': invalid operation. Ensure the solution is loaded."); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "fix_thread_sleep unexpected exception for '{FilePath}'", filePath);
                            return ($"fix_thread_sleep for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                        }
                    }
                case "format_document_preview":
                    return await _refactoringEngine.FormatDocumentPreviewAsync(filePath);
                case "format_document_safe":
                    return await _augmentEngine.FormatDocumentSafeAsync(filePath, preview);
                case "generate_xml_documentation_stubs":
                    {
                        var r = await _documentationEngine.GenerateXmlDocumentationStubsAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"generate_xml_documentation_stubs failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
                        return r;
                    }
                case "optimize_task_wait":
                    {
                        var r = await _advancedRefactoringEngine.OptimizeTaskWaitAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"optimize_task_wait failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "preview_add_missing_usings":
                    return await _augmentEngine.PreviewAddMissingUsingsAsync(filePath);
                case "add_configure_await_false":
                    {
                        var r = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync(filePath, libraryMode);
                        if (string.IsNullOrEmpty(r))
                            return ($"add_configure_await_false failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
                        return new SourceTransformResult(r, false, false, filePath);
                    }
                case "remove_configure_await_false":
                    {
                        var r = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"remove_configure_await_false failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
                        return new SourceTransformResult(r, false, false, filePath);
                    }
                case "simplify_boolean_expressions":
                    {
                        var r = await _logicOptimizationEngine.SimplifyBooleanExpressionsAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"simplify_boolean_expressions failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "simplify_member_access":
                    {
                        var r = await _ideStyleEngine.SimplifyMemberAccessAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"simplify_member_access failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "simplify_verbosity":
                    {
                        var r = await _codeStyleEngine.SimplifyVerbosityAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"simplify_verbosity failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "sort_and_deduplicate_usings":
                    return await _augmentEngine.SortAndDeduplicateUsingsAsync(filePath, !preview);
                case "upgrade_pattern_matching":
                    {
                        var r = await _syntaxUpgradeEngine.UpgradePatternMatchingAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"upgrade_pattern_matching failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "upgrade_thread_safety":
                    {
                        var r = await _codeStyleEngine.FixDangerousLockAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"upgrade_thread_safety failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "upgrade_to_file_scoped_namespace":
                    {
                        var r = await _syntaxUpgradeEngine.UpgradeToFileScopedNamespaceAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"upgrade_to_file_scoped_namespace failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "upgrade_to_modern_guards":
                    {
                        var r = await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"upgrade_to_modern_guards failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "use_field_backed_properties":
                    {
                        var r = await _syntaxUpgradeEngine.UseFieldBackedPropertiesAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"use_field_backed_properties failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "use_index_from_end":
                    {
                        var r = await _codeStyleEngine.UseIndexFromEndAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"use_index_from_end failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                case "use_time_provider":
                    {
                        var r = await _codeStyleEngine.UseTimeProviderAsync(filePath);
                        if (string.IsNullOrEmpty(r))
                            return ($"use_time_provider failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                        return r;
                    }
                default:
                    return (
                        $"Unknown transform '{transform}'. Valid values: add_braces, cleanup_implicit_spans, " +
                        "convert_to_null_coalescing, convert_to_pattern, convert_to_switch, fix_mismatched_namespaces, " +
                        "fix_thread_sleep, format_document_preview, format_document_safe, generate_xml_documentation_stubs, " +
                        "optimize_task_wait, preview_add_missing_usings, add_configure_await_false, remove_configure_await_false, " +
                        "simplify_boolean_expressions, simplify_member_access, simplify_verbosity, sort_and_deduplicate_usings, " +
                        "upgrade_pattern_matching, upgrade_thread_safety, upgrade_to_file_scoped_namespace, " +
                        "upgrade_to_modern_guards, use_field_backed_properties, use_index_from_end, use_time_provider.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyFileCodemod ({Transform}) failed", transform);
            return $"ApplyFileCodemod ({transform}) failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── 2. apply_method_codemod ───────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Applies a method-scoped code transformation. Most transforms return the updated file
        content as a string; pass the result to apply_proposed_changes to write to disk.

        filePath:   the .cs file containing the method.
        methodName: the method to transform.
        transform:  the codemod to apply — call describe_advanced_tool_options("apply_method_codemod") for
                    valid values and per-transform notes.
        direction: required for convert_expression_body — "ToExpression" or "ToBlock".
        contextSnippet/lineBefore/lineAfter: for convert_expression_body disambiguation.
        lockFieldName: for make_method_thread_safe — name for the lock field (default "_lock").

        Returns the updated file content as a string, or a type-specific result for some transforms
        (SourceTransformResult, OutParamConversionResult).
        Throws InvalidOperationException when the file or method is not found.
        """)]
    public async Task<object> ApplyMethodCodemod(
        string filePath,
        string methodName,
        string transform,
        string? direction = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        string lockFieldName = "_lock")
    {
        try
        {
            switch (transform)
            {
                case "add_guard_clauses":
                    {
                        var r = await _logicOptimizationEngine.AddGuardClausesAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"add_guard_clauses failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return new SourceTransformResult(r, false, false, filePath);
                    }
                case "convert_expression_body":
                    {
                        if (string.IsNullOrEmpty(direction))
                            return ("direction is required for convert_expression_body. Valid values: ToExpression, ToBlock.");
                        var r = await _refactoringEngine.ConvertExpressionBodyAsync(filePath, methodName, direction, contextSnippet, lineBefore, lineAfter);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_expression_body failed for '{methodName}' ({direction}) in '{filePath}': file not found, member not found, or context snippet did not match. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_lock_to_semaphore_slim":
                    {
                        var r = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_lock_to_semaphore_slim failed for '{methodName}' in '{filePath}': file not found, method not found, or no lock statements found. Ensure the solution is loaded.");
                        return new SourceTransformResult(r, false, false, filePath);
                    }
                case "convert_method_to_indexer":
                    {
                        var r = await _granularRefactoringEngine.ConvertMethodToIndexerAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_method_to_indexer failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_out_params_to_value_tuple":
                    return await _outParamRefactoringEngine.ConvertOutParamsToValueTupleAsync(filePath, methodName);
                case "convert_static_to_extension":
                    {
                        try
                        {
                            var r = await _advancedLogicEngine.ConvertStaticToExtensionAsync(filePath, methodName);
                            if (string.IsNullOrEmpty(r))
                                return ($"convert_static_to_extension failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                            return r;
                        }
                        catch (InvalidOperationException) { return ($"convert_static_to_extension failed for '{methodName}' in '{filePath}': invalid operation. Ensure the solution is loaded."); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "convert_static_to_extension unexpected exception for '{MethodName}' in '{FilePath}'", methodName, filePath);
                            return ($"convert_static_to_extension for '{methodName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                        }
                    }
                case "convert_switch_to_expression":
                    {
                        var r = await _syntaxUpgradeEngine.ConvertSwitchToExpressionAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_switch_to_expression failed for '{methodName}' in '{filePath}': file not found, method not found, or no switch statements found. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_to_async_enumerable":
                    {
                        var r = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_to_async_enumerable failed for '{methodName}' in '{filePath}': file not found, method not found, or method does not return Task<List<T>>. Ensure the solution is loaded.");
                        return new SourceTransformResult(r, false, false, filePath);
                    }
                case "extension_to_static":
                    {
                        var r = await _advancedLogicEngine.ExtensionToStaticAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"extension_to_static failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "generate_async_overload":
                    {
                        try
                        {
                            var r = await _asyncOptimizationEngine.GenerateAsyncOverloadAsync(filePath, methodName);
                            if (string.IsNullOrEmpty(r))
                                return ($"generate_async_overload failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                            return r;
                        }
                        catch (InvalidOperationException) { return ($"generate_async_overload failed for '{methodName}' in '{filePath}': invalid operation. Ensure the solution is loaded."); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "generate_async_overload unexpected exception for '{MethodName}' in '{FilePath}'", methodName, filePath);
                            return ($"generate_async_overload for '{methodName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                        }
                    }
                case "make_method_static":
                    {
                        var r = await _standardRefactoringEngine.MakeMethodStaticAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"make_method_static failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "make_method_thread_safe":
                    {
                        var r = await _threadSafetyEngine.MakeMethodThreadSafeAsync(filePath, methodName, lockFieldName);
                        if (string.IsNullOrEmpty(r))
                            return ($"make_method_thread_safe failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return new SourceTransformResult(r, false, false, filePath);
                    }
                case "optimize_independent_awaits":
                    {
                        var r = await _asyncOptimizationEngine.OptimizeIndependentAwaitsAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"optimize_independent_awaits failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "optimize_to_value_task":
                    {
                        var r = await _asyncOptimizationEngine.OptimizeToValueTaskAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"optimize_to_value_task failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "reduce_block_depth":
                    {
                        var r = await _codeFlowEngine.ReduceBlockDepthAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"reduce_block_depth failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "update_xml_docs_from_signature":
                    {
                        var r = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"update_xml_docs_from_signature failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "use_exception_expressions":
                    {
                        var r = await _syntaxUpgradeEngine.UseExceptionExpressionsAsync(filePath, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"use_exception_expressions failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                        return r;
                    }
                default:
                    return (
                         $"Unknown transform '{transform}'. Valid values: add_guard_clauses, convert_expression_body, " +
                         "convert_lock_to_semaphore_slim, convert_method_to_indexer, convert_out_params_to_value_tuple, " +
                         "convert_static_to_extension, convert_switch_to_expression, convert_to_async_enumerable, " +
                         "extension_to_static, generate_async_overload, make_method_static, make_method_thread_safe, " +
                         "optimize_independent_awaits, optimize_to_value_task, reduce_block_depth, " +
                         "update_xml_docs_from_signature, use_exception_expressions.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyMethodCodemod ({Transform}) failed for '{MethodName}'", transform, methodName);
            return $"ApplyMethodCodemod ({transform}) failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── 3. apply_class_codemod ────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Applies a class-scoped code transformation and returns the updated file content as a string.
        Pass the result to apply_proposed_changes to write to disk.

        filePath:  the .cs file containing the class.
        className: the target class name.
        transform: the codemod to apply — call describe_advanced_tool_options("apply_class_codemod") for
                   valid values and per-transform notes.
        propertyName: for convert_property_safe and convert_property_to_methods — the property name.
        direction: required for convert_property_safe — "ToFullProperty" or "ToAutoProperty".
        contextSnippet/lineBefore/lineAfter: for convert_property_safe disambiguation.

        Returns the updated file content as a string.
        Throws InvalidOperationException when the file or class is not found.
        """)]
    public async Task<object> ApplyClassCodemod(
        string filePath,
        string className,
        string transform,
        string? propertyName = null,
        string? direction = null,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null)
    {
        try
        {
            switch (transform)
            {
                case "add_validation_to_poco":
                    {
                        try
                        {
                            var r = await _apiIntegrationEngine.AddValidationToPocoAsync(filePath, className);
                            if (string.IsNullOrEmpty(r))
                                return ($"add_validation_to_poco failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                            return r;
                        }
                        catch (InvalidOperationException) { return ($"add_validation_to_poco failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded."); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "add_validation_to_poco unexpected exception for '{ClassName}' in '{FilePath}'", className, filePath);
                            return ($"add_validation_to_poco for '{className}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                        }
                    }
                case "class_to_record":
                    {
                        var r = await _modernizationEngine.ClassToRecordAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"class_to_record failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_abstract_to_interface":
                    {
                        try
                        {
                            var r = await _advancedStructuralEngine.ConvertAbstractClassToInterfaceAsync(filePath, className);
                            if (string.IsNullOrEmpty(r))
                                return ($"convert_abstract_to_interface failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                            return r;
                        }
                        catch (InvalidOperationException) { return ($"convert_abstract_to_interface failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded."); }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "convert_abstract_to_interface unexpected exception for '{ClassName}' in '{FilePath}'", className, filePath);
                            return ($"convert_abstract_to_interface for '{className}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                        }
                    }
                case "convert_property_safe":
                    {
                        var propName = propertyName ?? className;
                        if (string.IsNullOrEmpty(direction))
                            return ("direction is required for convert_property_safe. Valid values: ToFullProperty, ToAutoProperty.");
                        var r = await _codeGenerationEngine.ConvertPropertySafeAsync(filePath, propName, direction, contextSnippet, lineBefore, lineAfter);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_property_safe failed for property '{propName}' ({direction}) in '{filePath}': file not found, property not found, or context snippet did not match. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_property_to_methods":
                    {
                        var propName = propertyName ?? className;
                        var r = await _codeStyleEngine.ConvertPropertyToMethodsAsync(filePath, propName);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_property_to_methods failed for property '{propName}' in '{filePath}': file not found or property not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_to_background_service":
                    {
                        var r = await _architecturalEngine.ConvertToBackgroundServiceAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_to_background_service failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "convert_to_source_generated_logging":
                    {
                        var r = await _modernLoggingEngine.ConvertToSourceGeneratedLoggingAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"convert_to_source_generated_logging failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "document_poco_fields":
                    {
                        var r = await _documentationEngine.DocumentPocoFieldsAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"document_poco_fields failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "make_class_immutable":
                    {
                        var r = await _immutabilityEngine.MakeClassImmutableAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"make_class_immutable failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "record_to_class":
                    {
                        var r = await _modernizationEngine.RecordToClassAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"record_to_class failed for '{className}' in '{filePath}': file not found or record not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "replace_constructor_with_factory":
                    {
                        var r = await _advancedStructuralEngine.ReplaceConstructorWithFactoryAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"replace_constructor_with_factory failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "sort_members":
                    {
                        var r = await _refactoringEngine.SortMembersAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"sort_members failed for '{className}' in '{filePath}': file not found or type not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "upgrade_to_primary_constructor":
                    {
                        var r = await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"upgrade_to_primary_constructor failed for '{className}' in '{filePath}': file not found, class not found, or constructor is not a simple field-assignment constructor. Ensure the solution is loaded.");
                        return r;
                    }
                default:
                    return ($"Unknown transform '{transform}'. Valid values: add_validation_to_poco, class_to_record, " + "convert_abstract_to_interface, convert_property_safe, convert_property_to_methods, " + "convert_to_background_service, convert_to_source_generated_logging, document_poco_fields, " + "make_class_immutable, record_to_class, replace_constructor_with_factory, sort_members, " + "upgrade_to_primary_constructor.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyClassCodemod ({Transform}) failed for '{ClassName}'", transform, className);
            return $"ApplyClassCodemod ({transform}) failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── 4. generate ───────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Generates new code for a type or method. Returns a kind-specific result object.

        kind:      the artefact to generate — call describe_advanced_tool_options("generate") for valid
                   values, required parameters per kind, and return types.
        filePath:  required for all kinds except generate_decorator_class.
        className: target class name; for generate_decorator_class, pass the interface name.
        methodName: required for add_benchmark_stub and generate_path_driven_tests.
        members:   for generate_to_string_safe — optional comma-separated member list.
        decoratorPrefix: for generate_decorator_class (default "Logging").
        projectName: for generate_decorator_class — optional project scope.
        framework: for generate_path_driven_tests — "NUnit" (default), "xunit", or "mstest".
        disambiguateLine: for generate_path_driven_tests — disambiguates overloaded methods.
        """)]
    public async Task<object> Generate(
        string kind,
        string? filePath = null,
        string? className = null,
        string? methodName = null,
        string? members = null,
        string decoratorPrefix = "Logging",
        string? projectName = null,
        string framework = "NUnit",
        int? disambiguateLine = null)
    {
        try
        {
            switch (kind)
            {
                case "add_benchmark_stub":
                    {
                        if (string.IsNullOrEmpty(filePath))
                            return ("filePath is required for add_benchmark_stub.");
                        if (string.IsNullOrEmpty(className))
                            return ("className is required for add_benchmark_stub.");
                        if (string.IsNullOrEmpty(methodName))
                            return ("methodName is required for add_benchmark_stub.");
                        var r = await _testingEngine.AddBenchmarkStubAsync(filePath, className, methodName);
                        if (string.IsNullOrEmpty(r))
                            return ($"add_benchmark_stub failed for '{className}.{methodName}' in '{filePath}': file not found, class not found, or method not found. Ensure the solution is loaded.");
                        return new SourceTransformResult(r, false, false, filePath);
                    }
                case "generate_constructor":
                    {
                        if (string.IsNullOrEmpty(filePath))
                            return ("filePath is required for generate_constructor.");
                        if (string.IsNullOrEmpty(className))
                            return ("className is required for generate_constructor.");
                        var r = await _codeGenerationEngine.GenerateConstructorAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"generate_constructor failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "generate_decorator_class":
                    {
                        if (string.IsNullOrEmpty(className))
                            return ("className (the interface name) is required for generate_decorator_class.");
                        var result = await _codeGenerationEngine.GenerateDecoratorClassAsync(className, decoratorPrefix, projectName);
                        if (result == null)
                            return ($"generate_decorator_class: interface '{className}' not found in the solution{(projectName != null ? $" project '{projectName}'" : string.Empty)}. Ensure the interface name matches exactly (including leading 'I') and is part of the loaded solution.");
                        return result;
                    }
                case "generate_equality_overrides":
                    {
                        if (string.IsNullOrEmpty(filePath))
                            return ("filePath is required for generate_equality_overrides.");
                        if (string.IsNullOrEmpty(className))
                            return ("className is required for generate_equality_overrides.");
                        var r = await _analysisEngine.GenerateEqualityOverridesAsync(filePath, className);
                        if (string.IsNullOrEmpty(r))
                            return ($"generate_equality_overrides failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                        return r;
                    }
                case "generate_fluent_builder":
                    {
                        if (string.IsNullOrEmpty(filePath))
                            return ("filePath is required for generate_fluent_builder.");
                        if (string.IsNullOrEmpty(className))
                            return ("className is required for generate_fluent_builder.");
                        try
                        {
                            return await _codeGenerationEngine.GenerateFluentBuilderAsync(filePath, className);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "generate_fluent_builder failed for '{ClassName}' in '{FilePath}'", className, filePath);
                            return new FluentBuilderResult(className, string.Empty, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                case "generate_path_driven_tests":
                    {
                        if (string.IsNullOrEmpty(filePath))
                            return ("filePath is required for generate_path_driven_tests.");
                        if (string.IsNullOrEmpty(methodName))
                            return ("methodName is required for generate_path_driven_tests.");
                        return await _pathDrivenTestEngine.GeneratePathDrivenTestsAsync(filePath, methodName, framework, disambiguateLine);
                    }
                case "generate_repository_interface":
                    {
                        if (string.IsNullOrEmpty(filePath))
                            return ("filePath is required for generate_repository_interface.");
                        if (string.IsNullOrEmpty(className))
                            return ("className is required for generate_repository_interface.");
                        return await _codeGenerationEngine.GenerateRepositoryInterfaceAsync(filePath, className);
                    }
                case "generate_test_scaffold":
                    {
                        if (string.IsNullOrEmpty(filePath))
                            return ("filePath is required for generate_test_scaffold.");
                        if (string.IsNullOrEmpty(className))
                            return ("className is required for generate_test_scaffold.");
                        return await _testingEngine.GenerateTestScaffoldAsync(filePath, className);
                    }
                case "generate_test_skeleton":
                    {
                        if (string.IsNullOrEmpty(filePath))
                            return ("filePath is required for generate_test_skeleton.");
                        if (string.IsNullOrEmpty(className))
                            return ("className is required for generate_test_skeleton.");
                        return await _testingEngine.GenerateTestSkeletonAsync(filePath, className);
                    }
                case "generate_to_string_safe":
                    {
                        if (string.IsNullOrEmpty(filePath))
                            return ("filePath is required for generate_to_string_safe.");
                        if (string.IsNullOrEmpty(className))
                            return ("className (typeName) is required for generate_to_string_safe.");
                        IList<string>? memberList = members is null ? null
                            : members.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(m => m.Trim())
                                     .Where(m => m.Length > 0)
                                     .ToList();
                        return await _augmentEngine.GenerateToStringSafeAsync(filePath, className, memberList);
                    }
                default:
                    return (
                        $"Unknown kind '{kind}'. Valid values: add_benchmark_stub, generate_constructor, " +
                        "generate_decorator_class, generate_equality_overrides, generate_fluent_builder, " +
                        "generate_path_driven_tests, generate_repository_interface, generate_test_scaffold, " +
                        "generate_test_skeleton, generate_to_string_safe.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generate ({Kind}) failed", kind);
            return $"Generate ({kind}) failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
