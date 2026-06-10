using System.ComponentModel;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

[McpServerToolType]
public class SentinelQualityTools
{
    private readonly TestingEngine _testingEngine;
    private readonly ControlFlowEngine _controlFlowEngine;
    private readonly AnalysisEngine _analysisEngine;
    private readonly AntiPatternEngine _antiPatternEngine;
    private readonly ThreadSafetyEngine _threadSafetyEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly CodeStyleAnalysisEngine _codeStyleAnalysisEngine;
    private readonly StackOverflowEngine _stackOverflowEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelQualityTools> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public SentinelQualityTools(
        TestingEngine testingEngine,
        ControlFlowEngine controlFlowEngine,
        AnalysisEngine analysisEngine,
        AntiPatternEngine antiPatternEngine,
        ThreadSafetyEngine threadSafetyEngine,
        DiagnosticEngine diagnosticEngine,
        CodeStyleAnalysisEngine codeStyleAnalysisEngine,
        StackOverflowEngine stackOverflowEngine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelQualityTools> logger)
    {
        _testingEngine = testingEngine;
        _controlFlowEngine = controlFlowEngine;
        _analysisEngine = analysisEngine;
        _antiPatternEngine = antiPatternEngine;
        _threadSafetyEngine = threadSafetyEngine;
        _diagnosticEngine = diagnosticEngine;
        _codeStyleAnalysisEngine = codeStyleAnalysisEngine;
        _stackOverflowEngine = stackOverflowEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool]
    [Produces(DataTag.Report)]
    [Description("Returns execution paths to cover and test methods that exercise a production method. Finds covering tests by name convention (test method name contains production method name) and by direct call-site presence. Returns BranchesToTest, CoveringTests (test file, method, line), and HasAnyCoverage flag.")]
    public async Task<ToolResult<object>> GetTestCoverageMap(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _controlFlowEngine.GetTestCoverageMapAsync(filePath, methodName);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTestCoverageMap failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetTestCoverageMap failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    [McpServerTool]
    [Produces(DataTag.Report)]
    [Description("Calculates cyclomatic complexity of a method: 1 + one per if/else/case/while/for/foreach/catch/&&/||/?? branch. Returns complexity score and contributing conditionals. Guide: 1–4 = Low, 5–7 = Medium, 8–10 = High (refactoring candidate), >10 = Very High.")]
    public async Task<ToolResult<object>> GetMethodComplexity(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _testingEngine.CalculateComplexityAsync(filePath, methodName);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMethodComplexity failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetMethodComplexity failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    private static ToolOptionsResult ApplyFileCodemodOptions() => new()
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

    private static ToolOptionsResult ApplyMethodCodemodOptions() => new()
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

    private static ToolOptionsResult ApplyClassCodemodOptions() => new()
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

    private static ToolOptionsResult GenerateOptions() => new()
    {
        Description = """
            generate — valid kind values:
              add_benchmark_stub           Adds a BenchmarkDotNet stub class for a method.
                                           Requires filePath, className, methodName.
                                           Returns SourceTransformResult.
              generate_constructor         Generates a constructor from private/readonly fields.
                                           Returns updated file content as a string.
              generate_decorator_class     Generates a Decorator pattern class for an interface.
                                           Pass the interface name as className (filePath not required).
                                           decoratorPrefix: prefix for the decorator class (default "Logging").
                                           projectName: optional project scope.
                                           Returns DecoratorResult.
              generate_equality_overrides  Generates Equals and GetHashCode overrides.
                                           Returns updated file content as a string.
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

            Additional parameters:
              filePath: required for all kinds except generate_decorator_class.
              className: target class name; for generate_decorator_class pass the interface name.
              methodName: required for add_benchmark_stub and generate_path_driven_tests.
              members: for generate_to_string_safe — optional comma-separated member list.
              decoratorPrefix: for generate_decorator_class (default "Logging").
              projectName: for generate_decorator_class — optional project scope.
              framework: for generate_path_driven_tests — "NUnit" (default), "xunit", or "mstest".
              disambiguateLine: for generate_path_driven_tests — disambiguates overloaded methods.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["kinds"] = new[] {
                "add_benchmark_stub", "generate_constructor", "generate_decorator_class",
                "generate_equality_overrides", "generate_fluent_builder", "generate_path_driven_tests",
                "generate_repository_interface", "generate_test_scaffold", "generate_test_skeleton",
                "generate_to_string_safe"
            }
        }
    };

    private static ToolOptionsResult ConvertSwitchOptions() => new()
    {
        Description = """
            convert_switch_to_pattern_safe — supported switch forms and rejection rules:

            SUPPORTED forms:
              1. All cases assign to the SAME variable:
                   case "g": factor = 1.0; break;
                   → factor = unit switch { "g" => 1.0, ... };
              2. All cases are return statements:
                   case "g": return 1.0;
                   → return unit switch { "g" => 1.0, ... };
              3. All cases are throw statements (or mixed with return).

            REJECTED (returned as error, not silently dropped):
              • Cases assigning to MULTIPLE different variables per case
              • Cases assigning to different variables across cases
              • Cases with complex multi-statement bodies

            Parameters:
              filePath       — absolute path to the .cs file.
              contextSnippet — verbatim substring from the switch keyword line, e.g. "switch (unit)".
              lineBefore/lineAfter — disambiguate when the snippet matches multiple locations.

            Run analyze_switch_for_pattern_conversion first if you are unsure whether conversion is safe.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["supportedForms"] = new[] { "single-variable assignment", "return statements", "throw statements" },
            ["rejectedForms"] = new[] { "multiple-variable assignment per case", "different variables across cases", "complex multi-statement bodies" }
        }
    };

    private static ToolOptionsResult AnalyzeSwitchOptions() => new()
    {
        Description = """
            analyze_switch_for_pattern_conversion — pre-flight analysis output fields:

            Returns SwitchConversionAnalysis with:
              IsSafeToConvert     — true when the standard tool or convert_switch_to_pattern_safe will produce correct output.
              CaseCount           — total number of cases analysed.
              Cases[]             — per-case detail: CaseLabel, AssignmentCount, VariablesAssigned[], IsSafe, BlockingReason.
              BlockingReason      — human-readable reason why IsSafeToConvert is false (null when safe).
              Recommendation      — suggested next step.

            WHY THIS TOOL EXISTS: The standard 'convert_to_pattern_matching' tool silently drops
            variable assignments in switch cases that assign to more than one variable, producing
            broken code without any warning. This tool detects that condition before conversion.

            Parameters:
              filePath       — absolute path to the .cs file.
              contextSnippet — verbatim substring from the switch keyword line.
              lineBefore/lineAfter — optional disambiguation.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["outputFields"] = new[] { "IsSafeToConvert", "CaseCount", "Cases", "BlockingReason", "Recommendation" }
        }
    };

    private static ToolOptionsResult AnalyzeForeachOptions() => new()
    {
        Description = """
            analyze_foreach_for_linq_conversion — pre-flight analysis output fields:

            Returns ForeachLinqAnalysis with:
              IsSafeToConvert          — true when convert_foreach_linq will produce correct output.
              CollectionVariableName   — the collection variable being built by the foreach.
              StatementsBeforeForeach  — list of statements that modify the collection BEFORE the loop
                                         (these would be discarded by the standard tool if present).
              BlockingReason           — human-readable reason when IsSafeToConvert is false.
              Recommendation           — suggested next step.

            WHY THIS TOOL EXISTS: The standard 'convert_foreach_linq' tool silently destroys data.
            When a collection is modified before the foreach (e.g., results.Add("header")), the
            standard tool re-initialises the variable with 'new List<T>()', discarding those
            pre-loop additions WITHOUT any warning.

            ALWAYS call this before convert_foreach_linq. Only proceed if IsSafeToConvert=true.

            Parameters:
              filePath        — absolute path to the .cs file.
              contextSnippet  — short snippet of the foreach statement (e.g., "foreach (var item in").
              lineBefore/lineAfter — optional disambiguation.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["outputFields"] = new[] { "IsSafeToConvert", "CollectionVariableName", "StatementsBeforeForeach", "BlockingReason", "Recommendation" }
        }
    };

    private static ToolOptionsResult ScanOptions()
    {
        // Single source of truth: derived from SentinelScanTools.scan_descriptors.
        // Adding, removing, or reclassifying a detector in scan_descriptors automatically
        // propagates here — no manual sync required.
        var byDomain = SentinelScanTools.scan_descriptors
            .GroupBy(d => d.Domain)
            .OrderBy(g => g.Key)
            .ToDictionary(
                g => g.Key,
                g => g.Select(d => d.Id).ToArray());

        int total = SentinelScanTools.scan_descriptors.Length;

        var sb = new StringBuilder();
        sb.AppendLine($"scan — valid detector IDs grouped by domain ({total} total):");
        sb.AppendLine();
        foreach (var (domain, ids) in byDomain)
        {
            sb.AppendLine($"{domain} ({ids.Length}):");
            // Wrap ids at ~80 chars, indented two spaces
            const int Indent = 2;
            const int WrapAt = 80;
            var line = new StringBuilder(new string(' ', Indent));
            foreach (var id in ids)
            {
                string candidate = line.Length == Indent ? id.ToString() : ", " + id.ToString();
                if (line.Length + candidate.Length > WrapAt && line.Length > Indent)
                {
                    sb.AppendLine(line.ToString());
                    line.Clear().Append(new string(' ', Indent)).Append(id.ToString());
                }
                else
                {
                    line.Append(candidate);
                }
            }
            if (line.Length > Indent)
            {
                sb.AppendLine(line.ToString());
            }
            sb.AppendLine();
        }
        sb.AppendLine("scope values: \"file\" | \"project\" | \"solution\"");
        sb.AppendLine("scopeName: filePath for scope=file; projectName for scope=project; omit for solution.");
        sb.AppendLine("  For duplicate_blocks_in_hierarchy, scopeName is the root type name.");
        sb.AppendLine("File-scope-only detectors require scope=\"file\". unused_references requires scope=\"project\".");
        sb.Append("Call describe_scan_detectors for per-detector scope hints and descriptions.");

        return new ToolOptionsResult
        {
            Description = sb.ToString(),
            StructuredOptions = new Dictionary<string, object>(
                byDomain.Select(kvp =>
                    new KeyValuePair<string, object>(kvp.Key, kvp.Value)))
        };
    }
}
// v2 — ScanOptions() now derived from SentinelScanTools.scan_descriptors (single source of truth)