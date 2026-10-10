namespace RoslynSentinel.Common;

public static class CodeTransformCatalogues
{
    public static ToolOptionsResult ApplyFileCodeTransformOptions() => new()
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

    public static ToolOptionsResult ApplyMethodCodeTransformOptions() => new()
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

    public static ToolOptionsResult ApplyClassCodeTransformOptions() => new()
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
