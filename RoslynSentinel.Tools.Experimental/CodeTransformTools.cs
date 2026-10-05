using System.ComponentModel;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Experimental;

[McpServerToolType]
public class CodeTransformTools
{
    // ── shared engines ────────────────────────────────────────────────────────
    private readonly AntiPatternEngine _antiPatternEngine;
    private readonly ApiGenerationEngine _apiGenerationEngine;
    private readonly BasicRefactoringEngine _refactoringEngine;
    private readonly MemberRefactoringEngine _memberRefactoringEngine;
    private readonly LogicSimplificationEngine _logicOptimizationEngine;
    private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    private readonly SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private readonly CodeStyleEngine _codeStyleEngine;
    private readonly LogicSimplificationEngine _advancedLogicEngine;
    private readonly SyntaxModernizationEngine _modernizationEngine;
    private readonly CodeGenerationEngine _codeGenerationEngine;
    // ── apply_file_codetransform engines ────────────────────────────────────────────
    private readonly IDEStyleEngine _ideStyleEngine;
    private readonly CodeHealingEngine _codeHealingEngine;
    private readonly AdvancedRefactoringEngine _advancedRefactoringEngine;
    private readonly MsToolAugmentEngine _msToolAugmentEngine;
    private readonly DocumentationEngine _documentationEngine;
    private readonly SolutionStructureEngine _solutionStructureEngine;
    // ── apply_method_codetransform engines ──────────────────────────────────────────
    private readonly ThreadSafetyEngine _threadSafetyEngine;
    private readonly OutParamRefactoringEngine _outParamRefactoringEngine;
    private readonly LogicSimplificationEngine _codeFlowEngine;
    // ── apply_class_codetransform engines ───────────────────────────────────────────
    private readonly StructuralRefactoringEngine _advancedStructuralEngine;
    // ── generate engines ──────────────────────────────────────────────────────
    private readonly TestingEngine _testingEngine;
    private readonly PathDrivenTestEngine _pathDrivenTestEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger<CodeTransformTools> _logger;

    public CodeTransformTools(BasicRefactoringEngine refactoringEngine, MemberRefactoringEngine memberRefactoringEngine, LogicSimplificationEngine logicOptimizationEngine, AsyncOptimizationEngine asyncOptimizationEngine, SyntaxUpgradeEngine syntaxUpgradeEngine, CodeStyleEngine codeStyleEngine, LogicSimplificationEngine advancedLogicEngine, SyntaxModernizationEngine modernizationEngine, CodeGenerationEngine codeGenerationEngine, IDEStyleEngine ideStyleEngine, CodeHealingEngine codeHealingEngine, AdvancedRefactoringEngine advancedRefactoringEngine, MsToolAugmentEngine augmentEngine, DocumentationEngine documentationEngine, SolutionStructureEngine solutionStructureEngine, ThreadSafetyEngine threadSafetyEngine, OutParamRefactoringEngine outParamRefactoringEngine, LogicSimplificationEngine codeFlowEngine, StructuralRefactoringEngine advancedStructuralEngine, TestingEngine testingEngine, PathDrivenTestEngine pathDrivenTestEngine, IWorkspaceManager workspaceManager, ILogger<CodeTransformTools> logger, ApiGenerationEngine apiGenerationEngine, AntiPatternEngine antiPatternEngine)
    {
        _refactoringEngine = refactoringEngine;
        _memberRefactoringEngine = memberRefactoringEngine;
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
        _solutionStructureEngine = solutionStructureEngine;
        _threadSafetyEngine = threadSafetyEngine;
        _outParamRefactoringEngine = outParamRefactoringEngine;
        _codeFlowEngine = codeFlowEngine;
        _advancedStructuralEngine = advancedStructuralEngine;
        _testingEngine = testingEngine;
        _pathDrivenTestEngine = pathDrivenTestEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
        _apiGenerationEngine = apiGenerationEngine;
        _antiPatternEngine = antiPatternEngine;
    }

    // ── 1. apply_file_codetransform ─────────────────────────────────────────────────
    [McpServerTool(Name = "ApplyFileCodeTransform")]
    [Produces(DataTag.ResultOnly)]
    [Description("Applies a file-wide code transformation. Call DescribeAdvancedToolOptions(\"apply_file_codetransform\") for the list of transform values.")]
    public async Task<SentinelCallToolResult<object>> ApplyFileCodeTransform([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Description("The transformation to apply. See DescribeAdvancedToolOptions(\"apply_file_codetransform\") for valid values.")][ExternalInputRequired(DataTag.DataType)] string transform, [Description("Only used by add_configure_await_false: true (default) appends .ConfigureAwait(false) to all awaits.")][ExternalInputRequired(DataTag.LibraryMode)] bool libraryMode = true, [Description("Only used by format_document_safe / sort_and_deduplicate_usings: true returns updated content without writing to disk.")][ToolOption(ToolOptionTag.Preview)] bool preview = false, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
            switch (transform)
            {
                case "add_braces":
                    {
                        var r = await _syntaxUpgradeEngine.AddBracesAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No brace-less control flow statements found in '{resolvedFilePath}'. File already uses braces consistently."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = r.ToJsonSummary()
                        };
                    }

                case "cleanup_implicit_spans":
                    {
                        var r = await _syntaxUpgradeEngine.CleanupImplicitSpansAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No implicit Span/Memory conversion patterns found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = r.ToJsonSummary()
                        };
                    }

                case "convert_to_null_coalescing":
                    {
                        var r = await _logicOptimizationEngine.ConvertToNullCoalescingAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No null-check patterns eligible for ??/??= conversion found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = r.ToJsonSummary()
                        };
                    }

                case "convert_to_pattern":
                    {
                        var r = await _modernizationEngine.ConvertToPatternAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No if/switch chains eligible for pattern-matching conversion found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = r.ToJsonSummary()
                        };
                    }

                case "convert_to_switch":
                    {
                        var r = await _logicOptimizationEngine.ConvertToSwitchAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No if-else chains eligible for switch expression conversion found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = r.ToJsonSummary()
                        };
                    }

                case "fix_mismatched_namespaces":
                    {
                        var r = await _solutionStructureEngine.FixMismatchedNamespacesAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No namespace/folder mismatches found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = r.ToJsonSummary()
                        };
                    }

                case "fix_thread_sleep":
                    {
                        try
                        {
                            var r = await _codeHealingEngine.FixThreadSleepAsync(resolvedFilePath, cancellationToken);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new SentinelCallToolResult<object>()
                                {
                                    IsError = false,
                                    SuccessData = $"No Thread.Sleep calls eligible for async conversion found in '{resolvedFilePath}'."
                                };
                            }

                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = r.ToJsonSummary()
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "fix_thread_sleep unexpected exception for '{FilePathWrapper}'", resolvedFilePath);
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = true,
                                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"fix_thread_sleep for '{resolvedFilePath}'")
                            };
                        }
                    }

                case "format_document_preview":
                    var result = await _refactoringEngine.FormatDocumentPreviewAsync(resolvedFilePath, cancellationToken);
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = false,
                        SuccessData = result
                    };
                case "format_document_safe":
                    var result2 = await _msToolAugmentEngine.FormatDocumentSafeAsync(resolvedFilePath, preview, cancellationToken);
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = false,
                        SuccessData = result2
                    };
                case "generate_xml_documentation_stubs":
                    {
                        var r = await _documentationEngine.GenerateXmlDocumentationStubsAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No undocumented public members found in '{resolvedFilePath}'. File already has XML doc stubs."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = r.ToJsonSummary()
                        };
                    }

                case "optimize_task_wait":
                    {
                        var result3 = await _advancedRefactoringEngine.OptimizeTaskWaitAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result3.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No synchronous Task.Wait/.Result/.GetAwaiter().GetResult() patterns found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result3.Outcome
                        };
                    }

                case "preview_add_missing_usings":
                    var result4 = await _msToolAugmentEngine.PreviewAddMissingUsingsAsync(resolvedFilePath, cancellationToken);
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = false,
                        SuccessData = result4
                    };
                case "add_configure_await_false":
                    {
                        var result5 = await _asyncOptimizationEngine.AddConfigureAwaitFalseAsync(resolvedFilePath, libraryMode, cancellationToken: cancellationToken);
                        if (string.IsNullOrEmpty(result5.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No awaits missing .ConfigureAwait(false) found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(result5.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "remove_configure_await_false":
                    {
                        var result6 = await _asyncOptimizationEngine.RemoveConfigureAwaitFalseAsync(resolvedFilePath, cancellationToken: cancellationToken);
                        if (string.IsNullOrEmpty(result6.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No .ConfigureAwait(false) calls found to remove in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(result6.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "simplify_boolean_expressions":
                    {
                        var result7 = await _logicOptimizationEngine.SimplifyBooleanExpressionsAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result7.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No boolean expressions eligible for simplification found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result7
                        };
                    }

                case "simplify_member_access":
                    {
                        var result8 = await _ideStyleEngine.SimplifyMemberAccessAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result8.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No qualified member access patterns found to simplify in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result8
                        };
                    }

                case "simplify_verbosity":
                    {
                        var result9 = await _codeStyleEngine.SimplifyVerbosityAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result9.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No verbose patterns found to simplify in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result9
                        };
                    }

                case "sort_and_deduplicate_usings":
                    {
                        var result17 = await _msToolAugmentEngine.SortAndDeduplicateUsingsAsync(resolvedFilePath, !preview, cancellationToken);
                        if (result17 == null)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No unsorted or duplicate using directives found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result17
                        };
                    }

                case "upgrade_pattern_matching":
                    {
                        var result10 = await _syntaxUpgradeEngine.UpgradePatternMatchingAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result10.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No type-check/cast patterns eligible for modern pattern matching found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result10
                        };
                    }

                case "upgrade_thread_safety":
                    {
                        var result11 = await _codeStyleEngine.FixDangerousLockAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result11.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No dangerous lock patterns found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result11
                        };
                    }

                case "upgrade_to_file_scoped_namespace":
                    {
                        var result12 = await _syntaxUpgradeEngine.UpgradeToFileScopedNamespaceAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result12.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No block-scoped namespace declarations found in '{resolvedFilePath}'. File already uses file-scoped namespaces."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result12
                        };
                    }

                case "upgrade_to_modern_guards":
                    {
                        var result13 = await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result13.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No legacy null/argument guard patterns found in '{resolvedFilePath}'. File already uses modern guards."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result13
                        };
                    }

                case "use_field_backed_properties":
                    {
                        var result14 = await _syntaxUpgradeEngine.UseFieldBackedPropertiesAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result14.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No auto-properties eligible for field-backed conversion found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result14
                        };
                    }

                case "use_index_from_end":
                    {
                        var result15 = await _codeStyleEngine.UseIndexFromEndAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result15.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No array/list indexing patterns eligible for index-from-end (^n) syntax found in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result15
                        };
                    }

                case "use_time_provider":
                    {
                        var result16 = await _codeStyleEngine.UseTimeProviderAsync(resolvedFilePath, cancellationToken);
                        if (string.IsNullOrEmpty(result16.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No DateTime.Now/UtcNow calls found to replace with ITimeProvider in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result16
                        };
                    }

                default:
                    return new SentinelCallToolResult<object>()
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown transform '{transform}'. Valid values: add_braces, cleanup_implicit_spans, " + "convert_to_null_coalescing, convert_to_pattern, convert_to_switch, fix_mismatched_namespaces, " + "fix_thread_sleep, format_document_preview, format_document_safe, generate_xml_documentation_stubs, " + "optimize_task_wait, preview_add_missing_usings, add_configure_await_false, remove_configure_await_false, " + "simplify_boolean_expressions, simplify_member_access, simplify_verbosity, sort_and_deduplicate_usings, " + "upgrade_pattern_matching, upgrade_thread_safety, upgrade_to_file_scoped_namespace, " + "upgrade_to_modern_guards, use_field_backed_properties, use_index_from_end, use_time_provider.")
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyFileCodeTransform ({Transform}) failed", transform);
            return new SentinelCallToolResult<object>()
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyFileCodeTransform ({transform})")
            };
        }
    }

    // ── 2. apply_method_codetransform ───────────────────────────────────────────────
    [McpServerTool(Name = "ApplyMethodCodeTransform")]
    [Produces(DataTag.ResultOnly)]
    [Description("Applies a method-scoped code transformation. Call DescribeAdvancedToolOptions(\"apply_method_codetransform\") for the list of transform values.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: direction is required for transform=convert_expression_body ("ToExpression" or "ToBlock"); unused by other transforms. Enforced at runtime, not by the schema.
    public async Task<SentinelCallToolResult<object>> ApplyMethodCodeTransform([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [ExternalInputRequired(DataTag.MethodName, required: true)] string methodName, [Description("The transformation to apply. See DescribeAdvancedToolOptions(\"apply_method_codetransform\") for valid values.")][ExternalInputRequired(DataTag.Transform)] string transform, [Description("Required for transform=convert_expression_body: \"ToExpression\" or \"ToBlock\". Unused by other transforms.")][ToolOption(ToolOptionTag.Direction)] string? direction = null, [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null, [Consumes(DataTag.LineBefore)] string? lineBefore = null, [Consumes(DataTag.LineAfter)] string? lineAfter = null, [Description("Only used by transform=make_method_thread_safe: the lock field's name.")][ExternalInputRequired(DataTag.SymbolName)] string lockFieldName = "_lock", // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
            switch (transform)
            {
                case "add_guard_clauses":
                    {
                        var r = await _logicOptimizationEngine.AddGuardClausesAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No parameters eligible for guard clause insertion found in '{methodName}' in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "convert_expression_body":
                    {
                        if (string.IsNullOrEmpty(direction))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "direction is required for convert_expression_body. Valid values: ToExpression, ToBlock.")
                            };
                        }

                        var r = await _advancedStructuralEngine.ConvertExpressionBodyAsync(resolvedFilePath, methodName, direction, contextSnippet, lineBefore, lineAfter, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"convert_expression_body ({direction}) found nothing to convert for '{methodName}' in '{resolvedFilePath}'. " + "Possible causes: member not found (verify name and file are correct), member already has the target body style, " + "or contextSnippet did not uniquely match. Use GetFileOutline to confirm the member exists.")
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "convert_lock_to_semaphore_slim":
                    {
                        var r = await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = $"No lock statements found in '{methodName}' in '{resolvedFilePath}' to convert to SemaphoreSlim."
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "convert_method_to_indexer":
                    {
                        var r = await _advancedStructuralEngine.ConvertMethodToIndexerAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"convert_method_to_indexer: method '{methodName}' not found or not eligible in '{resolvedFilePath}'. " + "The method must have exactly one parameter and return a value. Use GetFileOutline to verify the method exists.")
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "convert_out_params_to_value_tuple":
                    var result = await _outParamRefactoringEngine.ConvertOutParamsToValueTupleAsync(resolvedFilePath, methodName, cancellationToken);
                    if (result is null || result.IsError || result.Changes is not { Count: > 0 })
                    {
                        return new SentinelCallToolResult<object>
                        {
                            IsError = true,
                            ErrorData = new ResultError(ToolErrorCode.Exception, $"convert_out_params_to_value_tuple failed for '{methodName}' in '{resolvedFilePath}': {result?.Message}")
                        };
                    }

                    return new SentinelCallToolResult<object>
                    {
                        IsError = false,
                        SuccessData = result
                    };
                case "convert_static_to_extension":
                    {
                        try
                        {
                            var r = await _logicOptimizationEngine.ConvertStaticToExtensionAsync(resolvedFilePath, methodName, cancellationToken);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new SentinelCallToolResult<object>()
                                {
                                    IsError = true,
                                    ErrorData = new ResultError(ToolErrorCode.Exception, $"convert_static_to_extension: method '{methodName}' not found or not eligible in '{resolvedFilePath}'. " + "The method must be static and have at least one parameter to become the 'this' parameter. Use GetFileOutline to verify.")
                                };
                            }

                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "convert_static_to_extension unexpected exception for '{MethodName}' in '{FilePathWrapper}'", methodName, resolvedFilePath);
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"convert_static_to_extension for '{methodName}' in '{resolvedFilePath}'")
                            };
                        }
                    }

                case "convert_switch_to_expression":
                    {
                        var r = await _syntaxUpgradeEngine.ConvertSwitchToExpressionAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = $"No switch statements eligible for switch expression conversion found in '{methodName}' in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "convert_to_async_enumerable":
                    {
                        var r = await _asyncOptimizationEngine.ConvertToAsyncEnumerableAsync(resolvedFilePath, methodName, cancellationToken: cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"convert_to_async_enumerable: method '{methodName}' not found or not eligible in '{resolvedFilePath}'. " + "The method must return Task<List<T>> or Task<IEnumerable<T>>. Use GetFileOutline to verify the method signature.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "extension_to_static":
                    {
                        var r = await _logicOptimizationEngine.ExtensionToStaticAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"extension_to_static: method '{methodName}' not found or not an extension method in '{resolvedFilePath}'. " + "The method must be in a static class and have a 'this' parameter. Use GetFileOutline to verify.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "generate_async_overload":
                    {
                        try
                        {
                            var r = await _asyncOptimizationEngine.GenerateAsyncOverloadAsync(resolvedFilePath, methodName, cancellationToken);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new SentinelCallToolResult<object>
                                {
                                    IsError = true,
                                    ErrorData = new ResultError(ToolErrorCode.Exception, $"generate_async_overload: method '{methodName}' not found or not eligible in '{resolvedFilePath}'. " + "The method must be synchronous and non-void. Use GetFileOutline to verify the method exists and its signature.")
                                };
                            }

                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "generate_async_overload unexpected exception for '{MethodName}' in '{FilePathWrapper}'", methodName, resolvedFilePath);
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"generate_async_overload for '{methodName}' in '{resolvedFilePath}'")
                            };
                        }
                    }

                case "make_method_static":
                    {
                        var r = await _memberRefactoringEngine.MakeMethodStaticAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"make_method_static: method '{methodName}' not found or not eligible in '{resolvedFilePath}'. " + "The method must not access instance members. Use GetFileOutline to verify the method exists.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "make_method_thread_safe":
                    {
                        var r = await _threadSafetyEngine.MakeMethodThreadSafeAsync(resolvedFilePath, methodName, lockFieldName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"make_method_thread_safe: method '{methodName}' not found in '{resolvedFilePath}'. " + "Use GetFileOutline to verify the method name (case-sensitive).")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "optimize_independent_awaits":
                    {
                        var r = await _asyncOptimizationEngine.OptimizeIndependentAwaitsAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = $"No sequential independent awaits found to parallelize in '{methodName}' in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "optimize_to_value_task":
                    {
                        var r = await _asyncOptimizationEngine.OptimizeToValueTaskAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"optimize_to_value_task: method '{methodName}' not found or not eligible in '{resolvedFilePath}'. " + "The method must return Task or Task<T> and be async. Use GetFileOutline to verify.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "reduce_block_depth":
                    {
                        var r = await _logicOptimizationEngine.ReduceBlockDepthAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = $"No deeply nested blocks found to flatten in '{methodName}' in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "update_xml_docs_from_signature":
                    {
                        var r = await _refactoringEngine.UpdateXmlDocsFromSignatureAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = $"No XML doc parameters out of sync with the signature of '{methodName}' in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "use_exception_expressions":
                    {
                        var r = await _syntaxUpgradeEngine.UseExceptionExpressionsAsync(resolvedFilePath, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = $"No if-throw guard patterns eligible for exception expression conversion found in '{methodName}' in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                default:
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown transform '{transform}'. Valid values: add_guard_clauses, convert_expression_body, " + "convert_lock_to_semaphore_slim, convert_method_to_indexer, convert_out_params_to_value_tuple, " + "convert_static_to_extension, convert_switch_to_expression, convert_to_async_enumerable, " + "extension_to_static, generate_async_overload, make_method_static, make_method_thread_safe, " + "optimize_independent_awaits, optimize_to_value_task, reduce_block_depth, " + "update_xml_docs_from_signature, use_exception_expressions.")
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyMethodCodeTransform ({Transform}) failed for '{MethodName}'", transform, methodName);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyMethodCodeTransform ({transform})")
            };
        }
    }

    /// <summary>
    /// Builds the "filePath is required for {kind}" error for a codetransform switch case, distinguishing
    /// "no solution is loaded" (a precondition failure independent of what filepath was passed) from
    /// "the filepath argument itself was invalid" -> see FilePathWrapper.FailureReason.
    /// </summary>
    private ResultError BuildFilePathRequiredError(FilePathWrapper filePath, string kind)
    {
        return filePath.FailureReason == FilePathFailureReason.NoSolutionLoaded ? new ResultError(ToolErrorCode.SolutionNotLoaded, SolutionNotLoadedMessage.ForFilePath(kind, _workspaceManager.LoadState)) : new ResultError(ToolErrorCode.InvalidArgument, $"filePath is required for {kind}.");
    }

    // ── 3. apply_class_codetransform ────────────────────────────────────────────────
    [McpServerTool(Name = "ApplyClassCodeTransform")]
    [Produces(DataTag.ResultOnly)]
    [Description("Applies a class-scoped code transformation. Call DescribeAdvancedToolOptions(\"apply_class_codetransform\") for the list of transform values.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: propertyName is required by transforms that target a specific property (e.g. convert_property_safe); unused by class-wide transforms. direction is required for transform=convert_property_safe ("ToFullProperty" or "ToAutoProperty"). Enforced at runtime, not by the schema.
    public async Task<SentinelCallToolResult<object>> ApplyClassCodeTransform([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [ExternalInputRequired(DataTag.ClassName)] string className, [Description("The transformation to apply. See DescribeAdvancedToolOptions(\"apply_class_codetransform\") for valid values.")][ExternalInputRequired(DataTag.Transform)] string transform, [Description("Required by transforms that target a single property (e.g. convert_property_safe). Unused by class-wide transforms.")][ExternalInputRequired(DataTag.PropertyName)] string? propertyName = null, [Description("Required for transform=convert_property_safe: \"ToFullProperty\" or \"ToAutoProperty\". Unused by other transforms.")][ToolOption(ToolOptionTag.Direction)] string? direction = null, [Description(ToolParams.ContextSnippet)][Consumes(DataTag.ContextSnippet)] string? contextSnippet = null, [Consumes(DataTag.LineBefore)] string? lineBefore = null, [Consumes(DataTag.LineAfter)] string? lineAfter = null, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
            switch (transform)
            {
                case "add_validation_to_poco":
                    {
                        try
                        {
                            var r = await _apiGenerationEngine.AddValidationToPocoAsync(resolvedFilePath, className, cancellationToken);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new SentinelCallToolResult<object>
                                {
                                    IsError = false,
                                    SuccessData = $"No unvalidated properties found on '{className}' in '{resolvedFilePath}'. Class may already have validation attributes or have no settable properties."
                                };
                            }

                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                            };
                        }
                        catch (InvalidOperationException ioe)
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"add_validation_to_poco: class '{className}' not found in '{resolvedFilePath}'. {ioe.Message} " + "Use GetFileOutline to verify the class name (case-sensitive).")
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "add_validation_to_poco unexpected exception for '{ClassName}' in '{FilePathWrapper}'", className, resolvedFilePath);
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"add_validation_to_poco for '{className}' in '{resolvedFilePath}'")
                            };
                        }
                    }

                case "class_to_record":
                    {
                        var r = await _modernizationEngine.ClassToRecordAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"class_to_record: class '{className}' not found or not eligible in '{resolvedFilePath}'. " + "The class must have no custom methods beyond property accessors. Use GetFileOutline to verify.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "convert_abstract_to_interface":
                    {
                        try
                        {
                            var r = await _advancedStructuralEngine.ConvertAbstractClassToInterfaceAsync(resolvedFilePath, className, cancellationToken);
                            if (string.IsNullOrEmpty(r.UpdatedText))
                            {
                                return new SentinelCallToolResult<object>
                                {
                                    IsError = true,
                                    ErrorData = new ResultError(ToolErrorCode.Exception, $"convert_abstract_to_interface: class '{className}' not found or is not abstract in '{resolvedFilePath}'. " + "The class must be declared with the 'abstract' keyword. Use GetFileOutline to verify.")
                                };
                            }

                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                            };
                        }
                        catch (InvalidOperationException ioe)
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"convert_abstract_to_interface: class '{className}' not eligible in '{resolvedFilePath}'. {ioe.Message}")
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "convert_abstract_to_interface unexpected exception for '{ClassName}' in '{FilePathWrapper}'", className, resolvedFilePath);
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"convert_abstract_to_interface for '{className}' in '{resolvedFilePath}'")
                            };
                        }
                    }

                case "convert_property_safe":
                    {
                        var propName = propertyName ?? className;
                        if (string.IsNullOrEmpty(direction))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "direction is required for convert_property_safe. Valid values: ToFullProperty, ToAutoProperty.")
                            };
                        }

                        var r = await _codeGenerationEngine.ConvertPropertySafeAsync(resolvedFilePath, propName, direction, contextSnippet, lineBefore, lineAfter, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.NotFound, $"convert_property_safe ({direction}): property '{propName}' not found or not eligible in '{resolvedFilePath}'. " + "Possible causes: property name is wrong (case-sensitive), property already has the target style, " + "or contextSnippet did not uniquely identify it. Use GetFileOutline to list available properties.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "convert_property_to_methods":
                    {
                        var propName = propertyName ?? className;
                        var r = await _codeStyleEngine.ConvertPropertyToMethodsAsync(resolvedFilePath, propName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.NotFound, $"convert_property_to_methods: property '{propName}' not found in '{resolvedFilePath}'. " + "Use GetFileOutline to list available properties (name is case-sensitive).")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "convert_to_background_service":
                    {
                        var r = await _solutionStructureEngine.ConvertToBackgroundServiceAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.NotFound, $"convert_to_background_service: class '{className}' not found or not eligible in '{resolvedFilePath}'. " + "The class must not already implement BackgroundService. Use GetFileOutline to verify.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "convert_to_source_generated_logging":
                    {
                        var r = await _modernizationEngine.ConvertToSourceGeneratedLoggingAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = $"No ILogger.Log calls found to convert to source-generated logging in '{className}' in '{resolvedFilePath}'."
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "document_poco_fields":
                    {
                        var r = await _documentationEngine.DocumentPocoFieldsAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = $"No undocumented fields/properties found on '{className}' in '{resolvedFilePath}'. Class may already be documented or have no public fields."
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "make_class_immutable":
                    {
                        var r = await _modernizationEngine.MakeClassImmutableAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"make_class_immutable: class '{className}' not found or already immutable in '{resolvedFilePath}'. " + "Use GetFileOutline to verify the class exists and has mutable properties.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "record_to_class":
                    {
                        var r = await _modernizationEngine.RecordToClassAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.NotFound, $"record_to_class: record '{className}' not found in '{resolvedFilePath}'. " + "The type must be declared as a 'record'. Use GetFileOutline to verify.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "replace_constructor_with_factory":
                    {
                        var r = await _advancedStructuralEngine.ReplaceConstructorWithFactoryAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"replace_constructor_with_factory: class '{className}' not found or not eligible in '{resolvedFilePath}'. " + "Use GetFileOutline to verify the class name (case-sensitive).")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "sort_members":
                    {
                        var r = await _memberRefactoringEngine.SortMembersAsync(resolvedFilePath, className, cancellationToken: cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = false,
                                SuccessData = $"No members to reorder found in '{className}' in '{resolvedFilePath}'. Type may be empty or already sorted."
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case "upgrade_to_primary_constructor":
                    {
                        var r = await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>
                            {
                                IsError = true,
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"upgrade_to_primary_constructor: class '{className}' not found or not eligible in '{resolvedFilePath}'. " + "The constructor must only assign parameters to readonly fields (no other logic). Use GetFileOutline to verify the class exists.")
                            };
                        }

                        return new SentinelCallToolResult<object>
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                default:
                    return new SentinelCallToolResult<object>
                    {
                        IsError = true,
                        ErrorData = new ResultError(ToolErrorCode.Exception, $"Unknown transform '{transform}'. Valid values: add_validation_to_poco, class_to_record, " + "convert_abstract_to_interface, convert_property_safe, convert_property_to_methods, " + "convert_to_background_service, convert_to_source_generated_logging, document_poco_fields, " + "make_class_immutable, record_to_class, replace_constructor_with_factory, sort_members, " + "upgrade_to_primary_constructor.")
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ApplyClassCodeTransform ({Transform}) failed for '{ClassName}'", transform, className);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"ApplyClassCodeTransform ({transform})")
            };
        }
    }

    // ── 4. generate ───────────────────────────────────────────────────────────
    [McpServerTool(Name = "Generate")]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates new code for a type or method. Call DescribeAdvancedToolOptions(\"generate\") for the list of kind values, their required parameters, and return types.")]
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: filepath is required for every kind except generate_decorator_class. className/methodName/members/disambiguateLine requirements vary per kind -> see DescribeAdvancedToolOptions("generate"). Enforced at runtime, not by the schema.
    public async Task<SentinelCallToolResult<object>> Generate([Description("The kind of code to generate. See DescribeAdvancedToolOptions(\"generate\") for valid values.")] CodeTransformKind kind, [Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: false)] string? filepath = null, [Consumes(DataTag.ClassName)] string? className = null, [Consumes(DataTag.MethodName)] string? methodName = null, [Consumes(DataTag.MemberName)] string? members = null, [Description("Only used by kind=generate_decorator_class: the prefix for the generated decorator class name.")][ExternalInputRequired(DataTag.DecoratorPrefix)] string decoratorPrefix = "Logging", [Description("Only used by kind=generate_decorator_class: scopes the interface lookup to one project.")][ExternalInputRequired(DataTag.ProjectName)] string? projectName = null, [Description("Only used by kind=generate_path_driven_tests: \"NUnit\" (default), \"xunit\", or \"mstest\".")][ExternalInputRequired(DataTag.Framework)] string framework = "NUnit", [Description("Only used by kind=generate_path_driven_tests: resolves an overloaded method target.")][ExternalInputRequired(DataTag.StartLine)] int? disambiguateLine = null, // RequestContext<CallToolRequestParams> requestParams = null,
 CancellationToken cancellationToken = default)
    {
        try
        {
            FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filepath);
            switch (kind)
            {
                case CodeTransformKind.add_benchmark_stub:
                    {
                        if (!resolvedFilePath.Validated)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = BuildFilePathRequiredError(resolvedFilePath, "add_benchmark_stub")
                            };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required for add_benchmark_stub.")
                            };
                        }

                        if (string.IsNullOrEmpty(methodName))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "methodName is required for add_benchmark_stub.")
                            };
                        }

                        var r = await _testingEngine.AddBenchmarkStubAsync(resolvedFilePath, className, methodName, cancellationToken);
                        if (string.IsNullOrEmpty(r.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"add_benchmark_stub failed for '{className}.{methodName}' in '{resolvedFilePath}': file not found, class not found, or method not found. Ensure the solution is loaded.")
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = new SourceTransformResult(r.UpdatedText, false, false, resolvedFilePath)
                        };
                    }

                case CodeTransformKind.generate_constructor:
                    {
                        if (!resolvedFilePath.Validated)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = BuildFilePathRequiredError(resolvedFilePath, "generate_constructor")
                            };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_constructor.")
                            };
                        }

                        var result = await _codeGenerationEngine.GenerateConstructorAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(result.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"generate_constructor failed for '{className}' in '{resolvedFilePath}': file not found or class not found. Ensure the solution is loaded.")
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result.ToJsonSummary()
                        };
                    }

                case CodeTransformKind.generate_decorator_class:
                    {
                        if (string.IsNullOrEmpty(className))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className (the interface name) is required for generate_decorator_class.")
                            };
                        }

                        var result = await _codeGenerationEngine.GenerateDecoratorClassAsync(className, decoratorPrefix, projectName, cancellationToken);
                        if (result == null)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"generate_decorator_class: interface '{className}' not found in the solution{(projectName != null ? $" project '{projectName}'" : string.Empty)}. Ensure the interface name matches exactly (including leading 'I') and is part of the loaded solution.")
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result
                        };
                    }

                case CodeTransformKind.generate_equality_overrides:
                    {
                        if (!resolvedFilePath.Validated)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = BuildFilePathRequiredError(resolvedFilePath, "generate_equality_overrides")
                            };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_equality_overrides.")
                            };
                        }

                        var result = await _antiPatternEngine.GenerateEqualityOverridesAsync(resolvedFilePath, className, cancellationToken);
                        if (string.IsNullOrEmpty(result.UpdatedText))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.Exception, $"generate_equality_overrides failed for '{className}' in '{resolvedFilePath}': file not found or class not found. Ensure the solution is loaded.")
                            };
                        }

                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result.ToJsonSummary()
                        };
                    }

                case CodeTransformKind.generate_fluent_builder:
                    {
                        if (!resolvedFilePath.Validated)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = BuildFilePathRequiredError(resolvedFilePath, "generate_fluent_builder")
                            };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_fluent_builder.")
                            };
                        }

                        try
                        {
                            var result = await _codeGenerationEngine.GenerateFluentBuilderAsync(resolvedFilePath, className, cancellationToken);
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = false,
                                SuccessData = result
                            };
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "generate_fluent_builder failed for '{ClassName}' in '{FilePathWrapper}'", className, resolvedFilePath);
                            return new SentinelCallToolResult<object>()
                            {
                                IsError = true,
                                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"generate_fluent_builder for '{className}' in '{resolvedFilePath}'")
                            };
                        }
                    }

                case CodeTransformKind.generate_path_driven_tests:
                    {
                        if (!resolvedFilePath.Validated)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = BuildFilePathRequiredError(resolvedFilePath, "generate_path_driven_tests")
                            };
                        }

                        if (string.IsNullOrEmpty(methodName))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "methodName is required for generate_path_driven_tests.")
                            };
                        }

                        var result = await _pathDrivenTestEngine.GeneratePathDrivenTestsAsync(resolvedFilePath, methodName, framework, disambiguateLine, cancellationToken);
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result
                        };
                    }

                case CodeTransformKind.generate_repository_interface:
                    {
                        if (!resolvedFilePath.Validated)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = BuildFilePathRequiredError(resolvedFilePath, "generate_repository_interface")
                            };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_repository_interface.")
                            };
                        }

                        var result = await _codeGenerationEngine.GenerateRepositoryInterfaceAsync(resolvedFilePath, className, cancellationToken);
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result
                        };
                    }

                case CodeTransformKind.generate_test_scaffold:
                    {
                        if (!resolvedFilePath.Validated)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = BuildFilePathRequiredError(resolvedFilePath, "generate_test_scaffold")
                            };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_test_scaffold.")
                            };
                        }

                        var result = await _testingEngine.GenerateTestScaffoldAsync(resolvedFilePath, className, cancellationToken);
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result
                        };
                    }

                case CodeTransformKind.generate_test_skeleton:
                    {
                        if (!resolvedFilePath.Validated)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = BuildFilePathRequiredError(resolvedFilePath, "generate_test_skeleton")
                            };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className is required for generate_test_skeleton.")
                            };
                        }

                        var result = await _testingEngine.GenerateTestSkeletonAsync(resolvedFilePath, className, cancellationToken: cancellationToken);
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result
                        };
                    }

                case CodeTransformKind.generate_to_string_safe:
                    {
                        if (!resolvedFilePath.Validated)
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = BuildFilePathRequiredError(resolvedFilePath, "generate_to_string_safe")
                            };
                        }

                        if (string.IsNullOrEmpty(className))
                        {
                            return new SentinelCallToolResult<object>()
                            {
                                ErrorData = new ResultError(ToolErrorCode.InvalidArgument, "className (typeName) is required for generate_to_string_safe.")
                            };
                        }

                        IList<string>? memberList = members?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
                        var result = await _msToolAugmentEngine.GenerateToStringSafeAsync(resolvedFilePath, className, memberList, cancellationToken);
                        return new SentinelCallToolResult<object>()
                        {
                            IsError = false,
                            SuccessData = result
                        };
                    }

                default:
                    return new SentinelCallToolResult<object>()
                    {
                        ErrorData = new ResultError(ToolErrorCode.InvalidArgument, $"Unknown kind '{kind}'. Valid values: add_benchmark_stub, generate_constructor, " + "generate_decorator_class, generate_equality_overrides, generate_fluent_builder, " + "generate_path_driven_tests, generate_repository_interface, generate_test_scaffold, " + "generate_test_skeleton, generate_to_string_safe.")
                    };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generate ({Kind}) failed", kind);
            return new SentinelCallToolResult<object>()
            {
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, $"Generate ({kind})")
            };
        }
    }

    internal static ToolOptionsResult ApplyFileCodeTransformOptions() => new()
    {
        Description = """
            apply_file_codetransform - valid transform values:
              add_braces                        Adds braces to all brace-less control statements.
              cleanup_implicit_spans            Removes redundant implicit Span<T>->Span<byte> casts.
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
              simplify_boolean_expressions      Simplifies redundant boolean expressions (x == true -> x).
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
              libraryMode: for add_configure_await_false - true (default) adds .ConfigureAwait(false) to all awaits.
              preview: for format_document_safe and sort_and_deduplicate_usings - false (default) writes to disk.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["transforms"] = new[]
            {
                "add_braces",
                "cleanup_implicit_spans",
                "convert_to_null_coalescing",
                "convert_to_pattern",
                "convert_to_switch",
                "fix_mismatched_namespaces",
                "fix_thread_sleep",
                "format_document_preview",
                "format_document_safe",
                "generate_xml_documentation_stubs",
                "optimize_task_wait",
                "preview_add_missing_usings",
                "add_configure_await_false",
                "remove_configure_await_false",
                "simplify_boolean_expressions",
                "simplify_member_access",
                "simplify_verbosity",
                "sort_and_deduplicate_usings",
                "upgrade_pattern_matching",
                "upgrade_thread_safety",
                "upgrade_to_file_scoped_namespace",
                "upgrade_to_modern_guards",
                "use_field_backed_properties",
                "use_index_from_end",
                "use_time_provider"
            }
        }
    };
    internal static ToolOptionsResult ApplyMethodCodeTransformOptions() => new()
    {
        Description = """
            apply_method_codetransform - valid transform values:
              add_guard_clauses              Adds ArgumentNullException.ThrowIfNull guards for reference params.
                                             Returns SourceTransformResult.
              convert_expression_body        Converts between block body and expression body.
                                             direction: "ToExpression" or "ToBlock".
                                             contextSnippet/lineBefore/lineAfter to disambiguate.
              convert_lock_to_semaphore_slim Converts lock statements to async SemaphoreSlim pattern.
                                             Returns SourceTransformResult.
              convert_method_to_indexer      Converts a single-parameter get/set method pair to an indexer.
              convert_out_params_to_value_tuple  Converts out-parameter methods to ValueTuple returns.
                                             Returns OutParamConversionResult; pass its Changes to
                                             ApplyDiff (changesetFormat=files) to write to disk.
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
              direction: required for convert_expression_body - "ToExpression" or "ToBlock".
              contextSnippet/lineBefore/lineAfter: for convert_expression_body disambiguation.
              lockFieldName: for make_method_thread_safe - name for the lock field (default "_lock").
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["transforms"] = new[]
            {
                "add_guard_clauses",
                "convert_expression_body",
                "convert_lock_to_semaphore_slim",
                "convert_method_to_indexer",
                "convert_out_params_to_value_tuple",
                "convert_static_to_extension",
                "convert_switch_to_expression",
                "convert_to_async_enumerable",
                "extension_to_static",
                "generate_async_overload",
                "make_method_static",
                "make_method_thread_safe",
                "optimize_independent_awaits",
                "optimize_to_value_task",
                "reduce_block_depth",
                "update_xml_docs_from_signature",
                "use_exception_expressions"
            }
        }
    };
    internal static ToolOptionsResult ApplyClassCodeTransformOptions() => new()
    {
        Description = """
            apply_class_codetransform - valid transform values:
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
              direction: required for convert_property_safe - "ToFullProperty" or "ToAutoProperty".
              contextSnippet/lineBefore/lineAfter: for convert_property_safe disambiguation.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["transforms"] = new[]
            {
                "add_validation_to_poco",
                "class_to_record",
                "convert_abstract_to_interface",
                "convert_property_safe",
                "convert_property_to_methods",
                "convert_to_background_service",
                "convert_to_source_generated_logging",
                "document_poco_fields",
                "make_class_immutable",
                "record_to_class",
                "replace_constructor_with_factory",
                "sort_members",
                "upgrade_to_primary_constructor"
            }
        }
    };
}