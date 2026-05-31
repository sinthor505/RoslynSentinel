using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

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
        as a string; pass the result to apply_proposed_changes to write to disk. Exceptions are
        noted below.

        transform values:
          add_braces                        Adds braces to all brace-less control statements.
          cleanup_implicit_spans            Removes redundant implicit Span<T>→Span<byte> casts.
          convert_to_null_coalescing        Replaces null-conditional chains with ?? operators.
          convert_to_pattern                Converts is/as type-check+cast pairs to pattern matching.
          convert_to_switch                 Converts if-else chains to switch expressions.
          fix_mismatched_namespaces         Corrects namespace declarations to match folder structure.
          fix_thread_sleep                  Replaces Thread.Sleep with await Task.Delay in async methods.
          format_document_preview           Returns a FormatPreviewResult diff without writing.
          format_document_safe              Formats the document. preview=false (default) writes to disk
                                            and updates the workspace; preview=true returns content only.
          generate_xml_documentation_stubs  Generates XML doc stubs for all undocumented public methods.
          optimize_task_wait                Converts blocking Task.Wait/Result to async/await.
          preview_add_missing_usings        Returns AddUsingsPreview listing missing usings (read-only).
          add_configure_await_false         Adds .ConfigureAwait(false) to all awaits. libraryMode=true
                                            (default) processes all awaits. Returns SourceTransformResult.
          remove_configure_await_false      Removes all .ConfigureAwait(x) calls. Returns SourceTransformResult.
          simplify_boolean_expressions      Simplifies redundant boolean expressions (x == true → x).
          simplify_member_access            Removes unnecessary this./base. qualifiers.
          simplify_verbosity                Removes redundant type names and default parameter values.
          sort_and_deduplicate_usings       Sorts and deduplicates using directives. preview=false (default)
                                            writes to disk; preview=true returns content without writing.
                                            Returns UsingsCleanupResult.
          upgrade_pattern_matching          Upgrades is/as casts to C# pattern-matching syntax.
          upgrade_thread_safety             Fixes dangerous double-checked locking patterns.
          upgrade_to_file_scoped_namespace  Converts block-scoped namespace to file-scoped.
          upgrade_to_modern_guards          Converts null-check guards to ArgumentNullException.ThrowIfNull.
          use_field_backed_properties       Converts auto-properties with backing fields to field-backed (C# 13).
          use_index_from_end                Converts array[array.Length - N] to array[^N].
          use_time_provider                 Replaces DateTime.Now/UtcNow with ITimeProvider calls.

        libraryMode: for add_configure_await_false — true (default) adds .ConfigureAwait(false) to all awaits.
        preview: for format_document_safe and sort_and_deduplicate_usings — false (default) writes to disk;
                 true returns updated content without writing.
        """)]
    public async Task<object> ApplyFileCodemod(
        string filePath,
        string transform,
        bool libraryMode = true,
        bool preview = false)
    {
        switch (transform)
        {
            case "add_braces":
            {
                var r = await _syntaxUpgradeEngine.AddBracesAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"add_braces failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "cleanup_implicit_spans":
            {
                var r = await _syntaxUpgradeEngine.CleanupImplicitSpansAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"cleanup_implicit_spans failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "convert_to_null_coalescing":
            {
                var r = await _logicOptimizationEngine.ConvertToNullCoalescingAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_to_null_coalescing failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "convert_to_pattern":
            {
                var r = await _modernizationEngine.ConvertToPatternAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_to_pattern failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "convert_to_switch":
            {
                var r = await _logicOptimizationEngine.ConvertToSwitchAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_to_switch failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "fix_mismatched_namespaces":
            {
                var r = await _projectStructureEngine.FixMismatchedNamespacesAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"fix_mismatched_namespaces failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "fix_thread_sleep":
            {
                try
                {
                    var r = await _codeHealingEngine.FixThreadSleepAsync(filePath);
                    if (string.IsNullOrEmpty(r))
                        throw new InvalidOperationException($"fix_thread_sleep failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
                    return r;
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "fix_thread_sleep unexpected exception for '{FilePath}'", filePath);
                    throw new InvalidOperationException($"fix_thread_sleep for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
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
                    throw new InvalidOperationException($"generate_xml_documentation_stubs failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
                return r;
            }
            case "optimize_task_wait":
            {
                var r = await _advancedRefactoringEngine.OptimizeTaskWaitAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"optimize_task_wait failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "preview_add_missing_usings":
                return await _augmentEngine.PreviewAddMissingUsingsAsync(filePath);
            case "add_configure_await_false":
            {
                var r = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync(filePath, libraryMode);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"add_configure_await_false failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
                return new SourceTransformResult(r, false, false, filePath);
            }
            case "remove_configure_await_false":
            {
                var r = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"remove_configure_await_false failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
                return new SourceTransformResult(r, false, false, filePath);
            }
            case "simplify_boolean_expressions":
            {
                var r = await _logicOptimizationEngine.SimplifyBooleanExpressionsAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"simplify_boolean_expressions failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "simplify_member_access":
            {
                var r = await _ideStyleEngine.SimplifyMemberAccessAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"simplify_member_access failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "simplify_verbosity":
            {
                var r = await _codeStyleEngine.SimplifyVerbosityAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"simplify_verbosity failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "sort_and_deduplicate_usings":
                return await _augmentEngine.SortAndDeduplicateUsingsAsync(filePath, !preview);
            case "upgrade_pattern_matching":
            {
                var r = await _syntaxUpgradeEngine.UpgradePatternMatchingAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"upgrade_pattern_matching failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "upgrade_thread_safety":
            {
                var r = await _codeStyleEngine.FixDangerousLockAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"upgrade_thread_safety failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "upgrade_to_file_scoped_namespace":
            {
                var r = await _syntaxUpgradeEngine.UpgradeToFileScopedNamespaceAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"upgrade_to_file_scoped_namespace failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "upgrade_to_modern_guards":
            {
                var r = await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"upgrade_to_modern_guards failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "use_field_backed_properties":
            {
                var r = await _syntaxUpgradeEngine.UseFieldBackedPropertiesAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"use_field_backed_properties failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "use_index_from_end":
            {
                var r = await _codeStyleEngine.UseIndexFromEndAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"use_index_from_end failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            case "use_time_provider":
            {
                var r = await _codeStyleEngine.UseTimeProviderAsync(filePath);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"use_time_provider failed for '{filePath}': file not found or no changes needed. Ensure the solution is loaded.");
                return r;
            }
            default:
                throw new ArgumentException(
                    $"Unknown transform '{transform}'. Valid values: add_braces, cleanup_implicit_spans, " +
                    "convert_to_null_coalescing, convert_to_pattern, convert_to_switch, fix_mismatched_namespaces, " +
                    "fix_thread_sleep, format_document_preview, format_document_safe, generate_xml_documentation_stubs, " +
                    "optimize_task_wait, preview_add_missing_usings, add_configure_await_false, remove_configure_await_false, " +
                    "simplify_boolean_expressions, simplify_member_access, simplify_verbosity, sort_and_deduplicate_usings, " +
                    "upgrade_pattern_matching, upgrade_thread_safety, upgrade_to_file_scoped_namespace, " +
                    "upgrade_to_modern_guards, use_field_backed_properties, use_index_from_end, use_time_provider.");
        }
    }

    // ── 2. apply_method_codemod ───────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Applies a method-scoped code transformation. Most transforms return the updated file
        content as a string; pass the result to apply_proposed_changes to write to disk.
        Transforms that return SourceTransformResult are noted.

        transform values:
          add_guard_clauses              Adds ArgumentNullException.ThrowIfNull guards for reference params.
                                         Returns SourceTransformResult.
          convert_expression_body        Converts between block body and expression body. direction: "ToExpression"
                                         or "ToBlock". contextSnippet/lineBefore/lineAfter to disambiguate.
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

        direction: required for convert_expression_body — "ToExpression" or "ToBlock".
        contextSnippet/lineBefore/lineAfter: for convert_expression_body disambiguation.
        lockFieldName: for make_method_thread_safe — name for the lock field (default "_lock").
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
        switch (transform)
        {
            case "add_guard_clauses":
            {
                var r = await _logicOptimizationEngine.AddGuardClausesAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"add_guard_clauses failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                return new SourceTransformResult(r, false, false, filePath);
            }
            case "convert_expression_body":
            {
                if (string.IsNullOrEmpty(direction))
                    throw new ArgumentException("direction is required for convert_expression_body. Valid values: ToExpression, ToBlock.");
                var r = await _refactoringEngine.ConvertExpressionBodyAsync(filePath, methodName, direction, contextSnippet, lineBefore, lineAfter);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_expression_body failed for '{methodName}' ({direction}) in '{filePath}': file not found, member not found, or context snippet did not match. Ensure the solution is loaded.");
                return r;
            }
            case "convert_lock_to_semaphore_slim":
            {
                var r = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_lock_to_semaphore_slim failed for '{methodName}' in '{filePath}': file not found, method not found, or no lock statements found. Ensure the solution is loaded.");
                return new SourceTransformResult(r, false, false, filePath);
            }
            case "convert_method_to_indexer":
            {
                var r = await _granularRefactoringEngine.ConvertMethodToIndexerAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_method_to_indexer failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
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
                        throw new InvalidOperationException($"convert_static_to_extension failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                    return r;
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "convert_static_to_extension unexpected exception for '{MethodName}' in '{FilePath}'", methodName, filePath);
                    throw new InvalidOperationException($"convert_static_to_extension for '{methodName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                }
            }
            case "convert_switch_to_expression":
            {
                var r = await _syntaxUpgradeEngine.ConvertSwitchToExpressionAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_switch_to_expression failed for '{methodName}' in '{filePath}': file not found, method not found, or no switch statements found. Ensure the solution is loaded.");
                return r;
            }
            case "convert_to_async_enumerable":
            {
                var r = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_to_async_enumerable failed for '{methodName}' in '{filePath}': file not found, method not found, or method does not return Task<List<T>>. Ensure the solution is loaded.");
                return new SourceTransformResult(r, false, false, filePath);
            }
            case "extension_to_static":
            {
                var r = await _advancedLogicEngine.ExtensionToStaticAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"extension_to_static failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                return r;
            }
            case "generate_async_overload":
            {
                try
                {
                    var r = await _asyncOptimizationEngine.GenerateAsyncOverloadAsync(filePath, methodName);
                    if (string.IsNullOrEmpty(r))
                        throw new InvalidOperationException($"generate_async_overload failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                    return r;
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "generate_async_overload unexpected exception for '{MethodName}' in '{FilePath}'", methodName, filePath);
                    throw new InvalidOperationException($"generate_async_overload for '{methodName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                }
            }
            case "make_method_static":
            {
                var r = await _standardRefactoringEngine.MakeMethodStaticAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"make_method_static failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                return r;
            }
            case "make_method_thread_safe":
            {
                var r = await _threadSafetyEngine.MakeMethodThreadSafeAsync(filePath, methodName, lockFieldName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"make_method_thread_safe failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                return new SourceTransformResult(r, false, false, filePath);
            }
            case "optimize_independent_awaits":
            {
                var r = await _asyncOptimizationEngine.OptimizeIndependentAwaitsAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"optimize_independent_awaits failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                return r;
            }
            case "optimize_to_value_task":
            {
                var r = await _asyncOptimizationEngine.OptimizeToValueTaskAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"optimize_to_value_task failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                return r;
            }
            case "reduce_block_depth":
            {
                var r = await _codeFlowEngine.ReduceBlockDepthAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"reduce_block_depth failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                return r;
            }
            case "update_xml_docs_from_signature":
            {
                var r = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"update_xml_docs_from_signature failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                return r;
            }
            case "use_exception_expressions":
            {
                var r = await _syntaxUpgradeEngine.UseExceptionExpressionsAsync(filePath, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"use_exception_expressions failed for '{methodName}' in '{filePath}': file not found or method not found. Ensure the solution is loaded.");
                return r;
            }
            default:
                throw new ArgumentException(
                    $"Unknown transform '{transform}'. Valid values: add_guard_clauses, convert_expression_body, " +
                    "convert_lock_to_semaphore_slim, convert_method_to_indexer, convert_out_params_to_value_tuple, " +
                    "convert_static_to_extension, convert_switch_to_expression, convert_to_async_enumerable, " +
                    "extension_to_static, generate_async_overload, make_method_static, make_method_thread_safe, " +
                    "optimize_independent_awaits, optimize_to_value_task, reduce_block_depth, " +
                    "update_xml_docs_from_signature, use_exception_expressions.");
        }
    }

    // ── 3. apply_class_codemod ────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Applies a class-scoped code transformation and returns the updated file content as a string.
        Pass the result to apply_proposed_changes to write to disk.

        transform values:
          add_validation_to_poco          Adds [Required] and [StringLength(100)] to all string properties.
          class_to_record                 Converts a class to a record type.
          convert_abstract_to_interface   Converts an abstract class to an interface.
          convert_property_safe           Converts a property between auto-property and full property.
                                          propertyName: the property to convert.
                                          direction: "ToFullProperty" or "ToAutoProperty".
                                          contextSnippet/lineBefore/lineAfter to disambiguate.
          convert_property_to_methods     Converts a property to a getter/setter method pair.
                                          propertyName: the property name (pass via className parameter).
          convert_to_background_service   Adds BackgroundService base class and generates ExecuteAsync override.
          convert_to_source_generated_logging  Converts ILogger calls to source-generated logging.
          document_poco_fields            Adds [Description] XML comments to all fields in a POCO class.
          make_class_immutable            Converts mutable properties to init-only and adds a With method.
          record_to_class                 Converts a record type to a class.
          replace_constructor_with_factory  Replaces a constructor with a static factory method.
          sort_members                    Sorts members by convention (fields, ctors, props, methods).
          upgrade_to_primary_constructor  Converts a simple assignment-only constructor to a C# 12 primary constructor.

        propertyName: for convert_property_safe and convert_property_to_methods — the property name
                      (pass the property name as the className parameter, or use this dedicated parameter).
        direction: required for convert_property_safe — "ToFullProperty" or "ToAutoProperty".
        contextSnippet/lineBefore/lineAfter: for convert_property_safe disambiguation.
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
        switch (transform)
        {
            case "add_validation_to_poco":
            {
                try
                {
                    var r = await _apiIntegrationEngine.AddValidationToPocoAsync(filePath, className);
                    if (string.IsNullOrEmpty(r))
                        throw new InvalidOperationException($"add_validation_to_poco failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                    return r;
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "add_validation_to_poco unexpected exception for '{ClassName}' in '{FilePath}'", className, filePath);
                    throw new InvalidOperationException($"add_validation_to_poco for '{className}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                }
            }
            case "class_to_record":
            {
                var r = await _modernizationEngine.ClassToRecordAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"class_to_record failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                return r;
            }
            case "convert_abstract_to_interface":
            {
                try
                {
                    var r = await _advancedStructuralEngine.ConvertAbstractClassToInterfaceAsync(filePath, className);
                    if (string.IsNullOrEmpty(r))
                        throw new InvalidOperationException($"convert_abstract_to_interface failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                    return r;
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "convert_abstract_to_interface unexpected exception for '{ClassName}' in '{FilePath}'", className, filePath);
                    throw new InvalidOperationException($"convert_abstract_to_interface for '{className}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
                }
            }
            case "convert_property_safe":
            {
                var propName = propertyName ?? className;
                if (string.IsNullOrEmpty(direction))
                    throw new ArgumentException("direction is required for convert_property_safe. Valid values: ToFullProperty, ToAutoProperty.");
                var r = await _codeGenerationEngine.ConvertPropertySafeAsync(filePath, propName, direction, contextSnippet, lineBefore, lineAfter);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_property_safe failed for property '{propName}' ({direction}) in '{filePath}': file not found, property not found, or context snippet did not match. Ensure the solution is loaded.");
                return r;
            }
            case "convert_property_to_methods":
            {
                var propName = propertyName ?? className;
                var r = await _codeStyleEngine.ConvertPropertyToMethodsAsync(filePath, propName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_property_to_methods failed for property '{propName}' in '{filePath}': file not found or property not found. Ensure the solution is loaded.");
                return r;
            }
            case "convert_to_background_service":
            {
                var r = await _architecturalEngine.ConvertToBackgroundServiceAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_to_background_service failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                return r;
            }
            case "convert_to_source_generated_logging":
            {
                var r = await _modernLoggingEngine.ConvertToSourceGeneratedLoggingAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"convert_to_source_generated_logging failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                return r;
            }
            case "document_poco_fields":
            {
                var r = await _documentationEngine.DocumentPocoFieldsAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"document_poco_fields failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                return r;
            }
            case "make_class_immutable":
            {
                var r = await _immutabilityEngine.MakeClassImmutableAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"make_class_immutable failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                return r;
            }
            case "record_to_class":
            {
                var r = await _modernizationEngine.RecordToClassAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"record_to_class failed for '{className}' in '{filePath}': file not found or record not found. Ensure the solution is loaded.");
                return r;
            }
            case "replace_constructor_with_factory":
            {
                var r = await _advancedStructuralEngine.ReplaceConstructorWithFactoryAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"replace_constructor_with_factory failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                return r;
            }
            case "sort_members":
            {
                var r = await _refactoringEngine.SortMembersAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"sort_members failed for '{className}' in '{filePath}': file not found or type not found. Ensure the solution is loaded.");
                return r;
            }
            case "upgrade_to_primary_constructor":
            {
                var r = await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"upgrade_to_primary_constructor failed for '{className}' in '{filePath}': file not found, class not found, or constructor is not a simple field-assignment constructor. Ensure the solution is loaded.");
                return r;
            }
            default:
                throw new ArgumentException(
                    $"Unknown transform '{transform}'. Valid values: add_validation_to_poco, class_to_record, " +
                    "convert_abstract_to_interface, convert_property_safe, convert_property_to_methods, " +
                    "convert_to_background_service, convert_to_source_generated_logging, document_poco_fields, " +
                    "make_class_immutable, record_to_class, replace_constructor_with_factory, sort_members, " +
                    "upgrade_to_primary_constructor.");
        }
    }

    // ── 4. generate ───────────────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Generates new code for a type or method. Returns a type-specific result object.

        kind values:
          add_benchmark_stub           Adds a BenchmarkDotNet stub class for a method.
                                       Requires filePath, className, methodName.
                                       Returns SourceTransformResult.
          generate_constructor         Generates a constructor from private/readonly fields.
                                       Returns the updated file content as a string.
          generate_decorator_class     Generates a Decorator pattern class for an interface.
                                       Pass the interface name as className (filePath not required).
                                       decoratorPrefix: prefix for the decorator class (default "Logging").
                                       projectName: optional project scope.
                                       Returns DecoratorResult.
          generate_equality_overrides  Generates Equals and GetHashCode overrides.
                                       Returns the updated file content as a string.
          generate_fluent_builder      Generates a fluent builder class with With{Property}() methods.
                                       Returns FluentBuilderResult.
          generate_path_driven_tests   Generates test stubs for each execution path in a method.
                                       Requires filePath, methodName.
                                       framework: "NUnit" (default), "xunit", or "mstest".
                                       disambiguateLine: line number to resolve overloaded methods.
                                       Returns PathDrivenTestReport.
          generate_repository_interface  Extracts an interface from a class with DI and Moq snippets.
                                       Returns RepositoryInterfaceResult.
          generate_test_scaffold       Generates an xUnit+Moq test scaffold with mock fields and test stubs.
                                       Returns TestScaffoldResult.
          generate_test_skeleton       Generates a test class skeleton with one test stub per public method.
                                       Returns TestSkeletonReport.
          generate_to_string_safe      Generates a ToString() override with correctly escaped interpolated strings.
                                       members: optional comma-separated list of property/field names.
                                       Returns MsAugmentResult.

        filePath: required for all kinds except generate_decorator_class.
        className: the target class name; for generate_decorator_class, pass the interface name here.
        methodName: required for add_benchmark_stub and generate_path_driven_tests.
        members: for generate_to_string_safe — optional comma-separated member list.
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
        switch (kind)
        {
            case "add_benchmark_stub":
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new ArgumentException("filePath is required for add_benchmark_stub.");
                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className is required for add_benchmark_stub.");
                if (string.IsNullOrEmpty(methodName))
                    throw new ArgumentException("methodName is required for add_benchmark_stub.");
                var r = await _testingEngine.AddBenchmarkStubAsync(filePath, className, methodName);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"add_benchmark_stub failed for '{className}.{methodName}' in '{filePath}': file not found, class not found, or method not found. Ensure the solution is loaded.");
                return new SourceTransformResult(r, false, false, filePath);
            }
            case "generate_constructor":
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new ArgumentException("filePath is required for generate_constructor.");
                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className is required for generate_constructor.");
                var r = await _codeGenerationEngine.GenerateConstructorAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"generate_constructor failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                return r;
            }
            case "generate_decorator_class":
            {
                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className (the interface name) is required for generate_decorator_class.");
                var result = await _codeGenerationEngine.GenerateDecoratorClassAsync(className, decoratorPrefix, projectName);
                if (result == null)
                    throw new InvalidOperationException($"generate_decorator_class: interface '{className}' not found in the solution{(projectName != null ? $" project '{projectName}'" : string.Empty)}. Ensure the interface name matches exactly (including leading 'I') and is part of the loaded solution.");
                return result;
            }
            case "generate_equality_overrides":
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new ArgumentException("filePath is required for generate_equality_overrides.");
                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className is required for generate_equality_overrides.");
                var r = await _analysisEngine.GenerateEqualityOverridesAsync(filePath, className);
                if (string.IsNullOrEmpty(r))
                    throw new InvalidOperationException($"generate_equality_overrides failed for '{className}' in '{filePath}': file not found or class not found. Ensure the solution is loaded.");
                return r;
            }
            case "generate_fluent_builder":
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new ArgumentException("filePath is required for generate_fluent_builder.");
                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className is required for generate_fluent_builder.");
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
                    throw new ArgumentException("filePath is required for generate_path_driven_tests.");
                if (string.IsNullOrEmpty(methodName))
                    throw new ArgumentException("methodName is required for generate_path_driven_tests.");
                return await _pathDrivenTestEngine.GeneratePathDrivenTestsAsync(filePath, methodName, framework, disambiguateLine);
            }
            case "generate_repository_interface":
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new ArgumentException("filePath is required for generate_repository_interface.");
                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className is required for generate_repository_interface.");
                return await _codeGenerationEngine.GenerateRepositoryInterfaceAsync(filePath, className);
            }
            case "generate_test_scaffold":
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new ArgumentException("filePath is required for generate_test_scaffold.");
                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className is required for generate_test_scaffold.");
                return await _testingEngine.GenerateTestScaffoldAsync(filePath, className);
            }
            case "generate_test_skeleton":
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new ArgumentException("filePath is required for generate_test_skeleton.");
                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className is required for generate_test_skeleton.");
                return await _testingEngine.GenerateTestSkeletonAsync(filePath, className);
            }
            case "generate_to_string_safe":
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new ArgumentException("filePath is required for generate_to_string_safe.");
                if (string.IsNullOrEmpty(className))
                    throw new ArgumentException("className (typeName) is required for generate_to_string_safe.");
                IList<string>? memberList = members is null ? null
                    : members.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .Select(m => m.Trim())
                             .Where(m => m.Length > 0)
                             .ToList();
                return await _augmentEngine.GenerateToStringSafeAsync(filePath, className, memberList);
            }
            default:
                throw new ArgumentException(
                    $"Unknown kind '{kind}'. Valid values: add_benchmark_stub, generate_constructor, " +
                    "generate_decorator_class, generate_equality_overrides, generate_fluent_builder, " +
                    "generate_path_driven_tests, generate_repository_interface, generate_test_scaffold, " +
                    "generate_test_skeleton, generate_to_string_safe.");
        }
    }
}
