Task: Update MCP tool names, method names, and descriptions in *Tools.cs files
You are updating tool metadata and method names in a C# MCP server codebase. You will modify [Description()] attribute text, [McpServerTool(Name = "...")] name overrides, and C# method names. Do not modify method signatures (parameters, return types), method bodies, logic, or any *Engine.cs files.

Setup
Before making any changes, call get_workspace_health to confirm a solution is loaded. If no solution is loaded, call load_solution first. Do not proceed until the workspace reports IsOperational=true and HasLoadedSolution=true.

Rules
Locating tools:

Target files matching the pattern *Tools.cs only.
Each tool is a method decorated with [McpServerTool] and [Description(...)].
Use find_by_name or search_solution_text to locate each method by name before modifying it.

Applying name changes — tools with a new name specified:

Rename the C# method from PascalCase of the original snake_case to PascalCase of the new snake_case (e.g. get_external_changes → list_external_disk_changes means renaming the method GetExternalChanges → ListExternalDiskChanges). Use rename_symbol to perform the rename so all references are updated.
Remove any existing Name = "..." property from [McpServerTool] if present — the method name now matches the tool name by convention and no override is needed.
If no Name property existed, no attribute change is needed.

Applying name changes — tools with "no change":

Do not rename the method.
Do not add or modify a Name property on [McpServerTool].

Applying description changes:

If the review specifies "no change" for Description, leave the [Description(...)] attribute untouched.
If a revised description is provided, replace the full string content of the [Description(...)] attribute with the revised text.
Preserve the existing string literal style: if the original uses """...""" (raw string literal), keep raw string literal; if it uses "...", keep that form. Do not change indentation style of surrounding code.
The new description text must be used verbatim — do not paraphrase, summarize, or reformat it.

Order of operations per tool:

Locate the method using search_solution_text.
If a name change is specified: rename the method with rename_symbol, then verify no Name = "..." property remains on [McpServerTool] and remove it if present.
If a description change is specified: replace the [Description(...)] content using replace_member or proposed_change.
Stage and apply changes with staged_change or proposed_change as appropriate.

Changes to apply
Process every entry below. "no change" entries are included for completeness — skip them.

acknowledge_sync
Name: acknowledge_sync → clear_external_drift
Description (revised): Clears the external-drift list after the AI has read the latest disk changes. No parameters.

add_constructor_parameter
Name: no change
Description (revised): Adds a DI constructor parameter in one step: private readonly field + parameter + body assignment. fieldName overrides the derived field name (defaults to _camelCase of paramName). Creates a constructor if none exists; converts expression-bodied constructors to block bodies. autoStage=true → ChangeId for staged_change.

add_enum_value
Name: no change
Description (revised): Adds a new value to an existing enum. explicitValue=99 → Archived = 99. If the enum is not found, the file is returned unchanged. autoStage=true → ChangeId.

add_member
Name: no change
Description (revised): Adds a new member to a type. position: null/end (append), after:MemberName, or before:MemberName. autoStage=true → ChangeId.

add_member_typed
Name: no change
Description (revised): Generates a typed member and adds it to a type. kind: property (auto-property) or field. Property defaults: hasSetter=true, accessibility=public. Field defaults: isReadonly=false, isStatic=false, accessibility=private; initializer sets optional field initializer expression. autoStage=true → ChangeId.

add_summary_comment
Name: no change
Description (revised): Adds or replaces a /// <summary> XML doc comment on a type or member. Replaces any existing summary. autoStage=true → ChangeId.

add_using_directive
Name: no change
Description (revised): Adds a using directive if not already present. Pass the namespace name (e.g. System.Linq); prefix with static  for static usings. File returned unchanged if directive already exists. autoStage=true → ChangeId.

analyze_foreach_for_linq_conversion
Name: no change
Description (revised): Pre-flight safety check before convert_foreach_linq. FIXES MS BUG: the standard tool silently destroys data when a collection is modified before the foreach. Only proceed with conversion if IsSafeToConvert=true. contextSnippet: short foreach snippet (e.g. "foreach (var item in"). lineBefore/lineAfter disambiguate multiple matches. Call describe_advanced_tool_options("analyze_foreach_for_linq_conversion") for full output field reference and safety rules.

analyze_method
Name: no change
Description (revised): Analyses a method from multiple angles. aspect values: controlFlow (return paths, throw sites, infinite loop detection → ControlFlowSummary), dataFlow (unassigned reads, written/read variables, closure captures → DataFlowSummary), pathCoverage (execution paths for test coverage → PathCoverageReport), unreachableCode (statements after unconditional return/throw → List<string>).

analyze_switch_for_pattern_conversion
Name: no change
Description (revised): Pre-flight safety check before converting a switch statement to a switch expression. FIXES MS BUG: the standard tool silently drops variable assignments in multi-variable cases. IsSafeToConvert=true means the standard tool or convert_switch_to_pattern_safe will produce correct output. contextSnippet: verbatim substring from the switch keyword line (e.g. "switch (unit)"). lineBefore/lineAfter disambiguate. Call describe_advanced_tool_options("analyze_switch_for_pattern_conversion") for full output field reference.

apply_class_codemod
Name: no change
Description (revised): Applies a class-scoped code transformation; returns updated file content as a string — pass to apply_proposed_changes to write to disk. transform: call describe_advanced_tool_options("apply_class_codemod") for valid values. direction: required for convert_property_safe — "ToFullProperty" or "ToAutoProperty". contextSnippet/lineBefore/lineAfter disambiguate convert_property_safe. Throws InvalidOperationException if file or class not found.

apply_file_codemod
Name: no change
Description (revised): Applies a file-wide code transformation; most transforms return updated file content — pass to apply_proposed_changes to write to disk. transform: call describe_advanced_tool_options("apply_file_codemod") for valid values. libraryMode=true → .ConfigureAwait(false) on all awaits (for add_configure_await_false). preview=true → returns updated content without writing (for format_document_safe / sort_and_deduplicate_usings). Some transforms return type-specific results (SourceTransformResult, UsingsCleanupResult, etc.). Throws InvalidOperationException if file not found or no changes needed.

apply_method_codemod
Name: no change
Description (revised): Applies a method-scoped code transformation; most transforms return updated file content — pass to apply_proposed_changes to write to disk. transform: call describe_advanced_tool_options("apply_method_codemod") for valid values. direction: required for convert_expression_body — "ToExpression" or "ToBlock". lockFieldName names the lock field for make_method_thread_safe (default "_lock"). contextSnippet/lineBefore/lineAfter disambiguate convert_expression_body. Some transforms return type-specific results (SourceTransformResult, OutParamConversionResult). Throws InvalidOperationException if file or method not found.

async_migrate
Name: no change
Description (revised): Unified dispatcher for six async-migration operations. All operations check the circuit breaker first and return BatchResultSummary. operation: call describe_advanced_tool_options("async_migrate") for valid values and required input fields per operation. Use get_operation_detail(changeId) for per-item details. Severity="halt" → circuit breaker opened; call get_breaker_status then reset_breaker. ErrorCode="SolutionNotLoaded" → call load_solution first. ErrorCode="InvalidArgument" → unknown operation name.

change_accessibility
Name: no change
Description (revised): Changes the accessibility modifier of a type or member. accessibility must be one of: public, private, internal, protected, protected internal, private protected. autoStage=true → ChangeId.

change_signature
Name: no change
Description (revised): Reorders method parameters and updates all call sites across the solution. newParameterOrder: zero-based index array specifying the new parameter order. autoStage=true → ChangeId.

convert_anonymous_to_named
Name: no change
Description (revised): Converts the first anonymous object creation expression in the file to a formal named class declaration.

convert_switch_to_pattern_safe
Name: no change
Description (revised): Converts a switch statement to a switch expression, rejecting unsafe cases instead of silently producing broken code. FIXES MS BUG: the standard tool drops variable assignments when a case sets more than one variable. contextSnippet: verbatim substring from the switch keyword line (e.g. "switch (unit)"). lineBefore/lineAfter disambiguate multiple matches. Run analyze_switch_for_pattern_conversion first if unsure. Returns MsAugmentResult with UpdatedContent on success or Error on rejection. Call describe_advanced_tool_options("convert_switch_to_pattern_safe") for supported switch forms and rejection rules.

create_project
Name: no change
Description (revised): Creates a new project and adds it to the current solution. projectType defaults to console.

describe_advanced_tool_options
Name: no change
Description (revised): Returns reference documentation for a named tool's valid input values — operation names, transform/kind/detector catalogues, and parameter defaults. Only covers tools whose valid values cannot be inferred from the schema alone. Covered tools: async_migrate, scan, scan_migration_candidates, apply_file_codemod, apply_method_codemod, apply_class_codemod, generate, convert_switch_to_pattern_safe, analyze_switch_for_pattern_conversion, analyze_foreach_for_linq_conversion. Returns ErrorCode="NoFurtherDocumentation" if the tool is not in the covered set — this does not mean the tool is invalid, only that its schema is self-describing.

describe_scan_detectors
Name: no change
Description (revised): Returns the catalogue of available scan detectors. domain filters by domain: async | concurrency | config | convention | correctness | dead-code | misc | performance | security | structure. detector returns info for a single detector by exact id. Both omitted → all 94 detectors. Each entry includes: Id, Domain, ScopeHint (file | project | solution | any combinations), Description.

diagnose
Name: no change
Description (revised): Checks Roslyn MCP server environment and workspace status. solutionPath re-checks a specific path. verbose=true → extended output. Prefer get_workspace_health — this tool has a known false-negative bug where healthy workspaces are reported as unhealthy.

encapsulate_field_safe
Name: no change
Description (revised): Encapsulates a public field into a private backing field + public property. FIXES standard encapsulate_field BUG: the standard tool creates a backing field and property with the same name, causing infinite recursion/compile error. This tool always renames the backing field to _camelCase. overridePropertyName provides a custom property name when the default PascalCase would conflict. Returns UpdatedContent.

extract_local_variable
Name: no change
Description (revised): Extracts an inline expression into a local variable declaration (e.g. return x + y; → var sum = x + y; return sum;). contextSnippet: verbatim substring containing the expression. lineBefore/lineAfter disambiguate multiple matches. Returns updated file content.

extract_members
Name: no change
Description (revised): Extracts members from a class into a new type. as values: interface (public API → new interface file, requires newTypeName), class (named members → new class, requires memberNames + newTypeName), partial (named members → new partial file, requires memberNames), superclass (common members → new base class, requires newTypeName; for multiple classes supply filePaths[] + classNames[]). autoStage=true → ChangeId where applicable.

extract_method_safe
Name: no change
Description (revised): Extracts selected statements into a new method with the correct return type. FIXES standard extract_method BUG: the standard tool declares void return type for selections ending with return <expression>, causing a compile error. This tool uses Roslyn's SemanticModel to infer the actual return type and DataFlowAnalysis to build the correct parameter list. Requires a loaded solution. contextSnippet: short unique code snippet identifying the selection. lineBefore/lineAfter disambiguate. Returns MsAugmentResult with Success=true and UpdatedContent, or Success=false and Error.

features
Name: no change
Description (revised): Queries or updates analysis/refactoring feature flags. action values: list (all features and current status; names/enabled ignored), get (enabled status of specific features by names), update (batch-update via enabled as [{ Key: featureName, Value: bool }] pairs).

find_by_name
Name: no change
Description (revised): Finds symbols by name using various lookup strategies. kind values: implementorsOf, attributeUsages, objectCreations, extensionsFor, typesWithAttribute, methodsByReturnType. projectName/filePath narrow scope where supported. sortByFrequency=true ranks by frequency (supported for objectCreations).

find_references
Name: no change
Description (revised): Finds all call sites or implementations for a symbol without requiring line/column. kind: callers (→ List<CallerInfo>) or implementations (→ List<ImplementationInfo>). contextSnippet disambiguates overloads; lineBefore/lineAfter provide further disambiguation.

generate
Name: no change
Description (revised): Generates new code for a type or method. kind: call describe_advanced_tool_options("generate") for valid values, required parameters per kind, and return types. filePath required for all kinds except generate_decorator_class. decoratorPrefix defaults to "Logging". framework for test generation: "NUnit" (default), "xunit", or "mstest". disambiguateLine resolves overloaded method targets for generate_path_driven_tests.

generate_classes_from_json
Name: no change
Description (revised): Generates C# class declarations from a JSON string using rootClassName as the top-level type name under the specified namespace.

generate_default_config_json
Name: no change
Description (revised): Scans a project for all config["Key"] and IConfiguration.GetValue<T>("Key") usages and returns a JSON skeleton with all keys and inferred default values.

generate_http_client
Name: no change
Description (revised): Generates a typed HttpClient wrapper for a Web API controller.

generate_mapping
Name: no change
Description (revised): Generates a mapping method between fromType and toType.

get_async_migration_progress
Name: no change
Description (revised): Returns async migration progress statistics for the solution or a single project. Reports: total async Task/ValueTask methods, how many have a CancellationToken parameter (and how many still need one), percentage coverage, Asyncify-bridge wrapper count ([Obsolete("Asyncify-bridge:...")]), bridge call sites pending migration (CS0618), and async void event handlers (informational — their signatures cannot be extended). projectName=null → entire solution.

get_best_insertion_point
Name: no change
Description (revised): Returns the best 1-based line number for inserting a new member of memberKind in a type, following standard C# ordering (fields → constructors → destructors → properties → events → methods → nested types).

get_breaker_status
Name: no change
Description (revised): Returns current circuit breaker state: severity (ok/caution/halt), trip-condition counters, and thresholds. Check before running large batch operations.

get_call_graph
Name: no change
Description (revised): Builds a call graph for a method. direction: forward (what the method calls → CallGraphNode tree), reverse (who calls this method → ReverseCallGraphNode tree), tree (markdown call-tree string). maxDepth defaults to 3.

get_code_inventory
Name: no change
Description (revised): Returns a structured report of all namespaces, classes, methods, and properties in a file.

get_comprehensive_health_report
Name: no change
Description (revised): Generates a paged health report across one or more engines: Structure, Modernization, Performance, Safety, Architecture. Null engines → all engines. projectName/filePath narrow scope. offset/limit page project results. timeoutSeconds defaults to 25.

get_di_registrations
Name: no change
Description (revised): Scans for all DI registrations (AddSingleton/AddScoped/AddTransient) across the solution or in a scoped project/file. Returns service type, implementation type, lifetime, and source location. lifetimeFilter: Singleton, Scoped, or Transient.

get_diagnostics
Name: no change
Description (revised): Gets compiler diagnostics. scope: file | project | solution. scopeName: filePath when scope=file, projectName when scope=project. summarize=true → grouped by diagnostic ID with counts. maxDetails caps raw detail list (default 50; error/warning totals are always complete). topN caps groups when summarize=true (default 20).

get_external_changes
Name: get_external_changes → list_external_disk_changes
Description (revised): Returns files modified on disk since the AI last synced. No parameters.

get_file_outline
Name: no change
Description (revised): Returns a structural outline of a file — namespaces, classes, interfaces, methods, and properties with 1-based line ranges. Member bodies are not included.

get_method_complexity
Name: no change
Description (revised): Calculates cyclomatic complexity of a method: 1 + one per if/else/case/while/for/foreach/catch/&&/||/?? branch. Returns complexity score and contributing conditionals. Guide: 1–4 = Low, 5–7 = Medium, 8–10 = High (refactoring candidate), >10 = Very High.

get_method_source
Name: no change
Description (revised): Returns the full source text of a named method. Case-sensitive match with case-insensitive fallback. Returns the first match for overloaded names.

get_operation_detail
Name: no change
Description (revised): Returns a filtered slice of an operation result blob by changeId. filter: failures, skipped, rolledback, file:<path>, or null for all items. maxItems caps the returned slice — never dumps the full document.

get_project_framework_summary
Name: get_project_framework_summary → list_project_framework_targets
Description (revised): Returns each project's TargetFramework value. Use before check_project_consistency to see the full framework landscape. No parameters.

get_public_api_surface
Name: no change
Description (revised): Returns the public API surface of a project. persistBaseline=false (default) → full List<ApiSurfaceEntry> with signatures, virtuality, and XML docs (for SDK documentation/API review). persistBaseline=true → compact List<PublicApiMember> baseline for passing to scan_breaking_changes. filePath scopes to a single file (persistBaseline=true only). includeMethods/includeProperties/includeTypes filter output (persistBaseline=false only).

get_scan_result
Name: no change
Description (revised): Pages through a large scan result written to disk when scan_migration_candidates payload exceeded the inline size threshold. Supply either changeId (resolves to .roslynsentinel/operations/scan_*_{changeId}.json) or filePath (must match the scan_*.json pattern). Returns ToolResult<object> with TotalRecords and HasMore.

get_solution_metrics
Name: no change
Description (revised): Returns deep metrics for the entire solution or a single project. projectName=null → solution-wide.

get_test_coverage_map
Name: no change
Description (revised): Returns execution paths to cover and test methods that exercise a production method. Finds covering tests by name convention (test method name contains production method name) and by direct call-site presence. Returns BranchesToTest, CoveringTests (test file, method, line), and HasAnyCoverage flag.

get_type_info
Name: no change
Description (revised): Returns type information. include: hierarchy (base class chain, interfaces, derived types → TypeHierarchyReport), members (all public/protected members with full metadata → List<TypeMemberDetail>), both (default → object with Hierarchy and Members). includeInherited=false excludes inherited members (applies to members and both).

get_workspace_health
Name: no change
Description (revised): Targeted workspace health check. FIXES MS BUG: the standard diagnose tool reports healthy:false even when all projects load successfully, because it tests MSBuild path existence rather than actual workspace state. This tool reads workspace state directly. Returns: IsOperational, HasLoadedSolution, LoadedSolutionPath, ProjectCount, DocumentCount, LoadErrors, Summary. IsOperational=true + HasLoadedSolution=false is normal — no solution loaded yet, not an error.

inline
Name: no change
Description (revised): Inlines a symbol by replacing all usages with its definition. kind: method (inline body at all call sites solution-wide — expression-body or single-return methods only), variable (inline local variable into usages), field (inline field value into usages), parameter (inline a constant parameter into method body — also supply methodName). targetName is the symbol name (parameterName when kind=parameter).

inline_class
Name: no change
Description (revised): Merges all members of a source class into a target class and removes the source class declaration. Works within the same file or across files. Updates all type references (variable declarations, constructor calls, casts, typeof, etc.) to the inlined class name throughout the solution. Returns a filePath → updatedContent dictionary for every affected file.

inspect_symbol
Name: no change
Description (revised): Inspects a symbol in depth. aspect: info (type, kind, accessibility, attributes, documentation → SymbolHoverInfo) or blastRadius (all call sites and affected projects if symbol changes → ImpactReport). contextSnippet: verbatim substring identifying the symbol. lineBefore/lineAfter disambiguate.

interpolate_string_safe
Name: no change
Description (revised): Converts a string.Format(...) call to an interpolated string. Fixes standard convert_to_interpolated_string: resolves const string format arguments via the semantic model, so it works when the format string is a named const rather than a literal. Handles {0:format} specifiers correctly. contextSnippet: verbatim substring identifying the string.Format call. lineBefore/lineAfter disambiguate. Returns updated file content.

introduce
Name: no change
Description (revised): Introduces a named symbol from an expression. as values: localVariable, field (private readonly), parameter (single-file), constant (→ MsAugmentResult). contextSnippet: verbatim substring identifying the expression. lineBefore/lineAfter disambiguate.

introduce_parameter_object
Name: no change
Description (revised): Encapsulates method parameters into a new C# 12 record type. Groups all non-CancellationToken parameters (or only parameterNames if specified) into public record {NewTypeName}(...). Rewrites parameter references in the method body to request.PropertyName. Appends the record to end of file. Adds a TODO comment to update call sites — call sites must be updated manually.

invert_assignments
Name: no change
Description (revised): Swaps left and right sides of all assignment statements within a 1-based line range.

invert_boolean_logic
Name: no change
Description (revised): Inverts all usages of a boolean identifier across the solution: wraps each usage with ! and removes double negations. Returns a file → content map of changed files.

list
Name: list → list_solution_items
Description (revised): Lists projects, files, or dependencies in the loaded solution. kind: projects (all projects), files (all source files in a project, requires projectName), dependencies (NuGet and project references for a project, requires projectName).

load_solution
Name: no change
Description (revised): Loads a .NET solution file into memory for persistent analysis. Must be called before any operation that returns ErrorCode="SolutionNotLoaded".

modify_attribute
Name: no change
Description (revised): Adds or removes an attribute on a type or member. action: add or remove. attribute/attributeSource/attributeName accept the attribute with or without brackets or Attribute suffix (e.g. "[ApiController]", "Required", "Obsolete"). autoStage=true → ChangeId.

modify_base_type
Name: no change
Description (revised): Adds or removes a base type or interface from a type declaration. action: add or remove. autoStage=true → ChangeId.

modify_modifier
Name: no change
Description (revised): Adds or removes a modifier keyword on a type or member. modifier: virtual, abstract, sealed, static, readonly, override, partial, async, new, extern, unsafe, volatile. action: add or remove. autoStage=true → ChangeId.

move_all_types_to_files
Name: no change
Description (revised): Moves all secondary types to their own files. scope=file → requires scopeName (file path), returns ChangeId + first-15-line content previews. scope=project → requires scopeName (project name), returns ChangeId + affected file list. scope=solution → scopeName ignored. autoStage=false → returns raw changes dictionary without staging.

move_file_to_namespace_folder
Name: no change
Description (revised): Returns the folder path where a file should reside based on its declared namespace. Read-only — use to plan file moves before executing them.

move_type
Name: no change
Description (revised): Moves a type to a new location. destination: ownFile (move to its own .cs file → ChangeId + content previews; autoStage=false → raw file dict) or outerScope (move nested type to containing namespace scope → updated file content).

preview_rename_impact
Name: no change
Description (revised): Previews the impact of renaming a symbol across the solution without applying changes. Returns affected files and location count. contextSnippet disambiguates overloads; lineBefore/lineAfter provide further disambiguation.

project_doc
Name: no change
Description (revised): Unified accessor for all project doc files under docs/. action × docType routing: list × documentation → lists all files in docs/ (name ignored); read/write × plan → docs/plans/<name>; read/write × handoff → docs/handoffs/<name>; read × completed_work → docs/completed/<name>; append × completed_work → append-only audit trail (no overwrite); read/write × documentation → docs/documentation/<name>; read/write × state → docs/migration-state.yaml (name ignored). name required for all file-scoped operations except state. content required for write and append.

proposed_change
Name: no change
Description (revised): Applies or validates a proposed change set. format=files → supply changes dict (filePath → newContent). format=diff → supply filePath + unifiedDiff. action: apply (write to disk) or validate (dry-run compiler diagnostics). validateOnApply=true (default) → runs delta compile before writing; returns validation errors without touching disk if new errors are introduced. Set false only for intentional intermediate-broken-state edits. On successful apply, returns ApplyChangesResult with UndoChangeId — pass to undo_last_apply to revert.

pull_up_member
Name: no change
Description (revised): Pulls a method or property from a derived class into its base class. Removes override, adds virtual (if not already abstract/virtual), and moves the declaration. Returns a two-file change dict (derived + base class). Requires the base class to have accessible source in the solution. autoStage=true → ChangeId.

remove_member
Name: no change
Description (revised): Removes a member from a class or interface by name. Does not check for usages — use safe_delete to guard against removing members that are still referenced.

rename_symbol
Name: no change
Description (revised): Renames a symbol across the entire solution. contextSnippet: verbatim substring from the source file, long enough to appear exactly once (typically the surrounding line). lineBefore/lineAfter disambiguate when the snippet could match multiple locations. Returns an error if the snippet matches zero or multiple locations. Returns per-file diff hunks (±2 lines context) and a staged ChangeId. Review FileChanges before calling staged_change.

replace_member
Name: no change
Description (revised): Surgically replaces a specific member (method, property, or class) by name with newSource. Targets the first member with a matching name.

reset_breaker
Name: no change
Description (revised): Resets the circuit breaker and all failure counters, re-enabling mutating tools. Only call after investigating and addressing the root cause of the failures that tripped the breaker.

retry_failed_changes
Name: no change
Description (revised): Retries committing previously failed file changes using server-side cached content — no need to re-send file contents. specificFiles limits the retry to a subset of files. retryCount defaults to 3.

safe_delete
Name: no change
Description (revised): Deletes a symbol only if it has zero usages in the entire codebase. Requires line and column (1-based) to identify the symbol at the declaration site.

scan
Name: scan → run_scan_detector
Description (revised): Dispatches a named detector across a file, project, or solution. detector: one of 94 detector IDs — call describe_advanced_tool_options("scan") for the full list grouped by domain. scope: file | project | solution. scopeName: filePath when scope=file; projectName when scope=project; root type name for duplicate_blocks_in_hierarchy. File-scope-only detectors require scope=file. unused_references requires scope=project. Call describe_scan_detectors for per-detector scope hints.

scan_breaking_changes
Name: no change
Description (revised): Compares a previously captured API surface baseline against current code and reports breaking changes: removed types, removed/renamed members, signature changes. Workflow: (1) call get_public_api_surface with persistBaseline=true to capture baseline, (2) make code changes, (3) call this tool with the baseline list. Scope with projectName/filePath matching step 1.

scan_duplicate_blocks_in_class
Name: no change
Description (revised): Finds duplicate statement sequences within the methods of a single class using structural hashing (SyntaxKind-based — matches regardless of variable names or literal values). Returns clone groups with: StatementCount, HasControlFlowExit (flag only, does not block finding), SnippetPreview, CapturedVariables (would become parameters if extracted), ProducedVariables (would need to be returned if extracted), and Occurrences (method, start line, end line, file). minStatements=3 for aggressive detection, 6+ for substantial clones only.

scan_migration_candidates
Name: no change
Description (revised): Returns [MigrationCandidate]-attributed methods added by flag_migration_candidate. Syntax-level — no compilation needed. pattern: call describe_advanced_tool_options("scan_migration_candidates") for valid values. summarize=true → guaranteed ≤2KB dashboard (byClass capped at 10, TopCandidates capped at 5 regardless of topN; ByClassTruncated=true when truncated). summarize=false + limit/offset → full paged candidate records. minScore filters in both modes; TotalRecords reflects post-filter count. A method flagged for two patterns appears twice. When results exceed the inline threshold, LargeResultInfo is populated instead of Data — call get_scan_result(changeId) to read in pages.

search_solution_text
Name: no change
Description (revised): Searches all source files in the loaded solution for a text pattern or regex. Returns file path, 1-based line and column, and a preview per match. isRegex=true treats pattern as a regular expression. fileGlob restricts to matching file paths. maxResults caps total matches (default 200).

split_project_by_folder
Name: no change
Description (revised): Moves all files under a specific folder from a source project to a new target project, preserving folder structure.

staged_change
Name: no change
Description (revised): Manages a staged change set. action: apply (write to disk), get (return file contents dict), validate (dry-run compiler diagnostics), discard (remove without applying). validateOnApply=true (default) → runs delta compile before writing; returns validation errors without touching disk if new errors are introduced. Set false only for intentional intermediate-broken-state edits. retryCount applies to action=apply only (default 3). On successful apply, the same changeId can be passed to undo_last_apply to revert.

sync_interface
Name: no change
Description (revised): Manages interface/class synchronization. action values: implement (generate stub implementations for all unimplemented interface members on className → returns updated file content), sync (add to interface any public members in className missing from interfaceName → returns updated interface file), verify (report coverage of all implementing classes → requires only interfaceName; use projectName to scope). filePath is the class file for implement/sync.

sync_type_and_filename
Name: no change
Description (revised): Renames the file to match the primary type declared in it.

trace_variable_lifetime
Name: no change
Description (revised): Traces a variable's complete lifetime from declaration through every read, write, ref/out pass, return, and closure capture, across all code paths (loops, conditionals, try/catch) in the enclosing scope. lineNumber: 1-based line of the declaration (disambiguates same-name variables). Returns: TypeName, DeclarationLine, ScopeDescription, IsDefinitelyAssigned, IsAlwaysAssigned, IsCapturedInClosure, and Accesses list with Line, Column, AccessKind (Declaration/Read/Write/Ref/Out/Return/Capture), ContextStack (method > if > for ancestry), IsInLoop, IsInConditional.

undo_last_apply
Name: no change
Description (revised): Reverts files from a previously applied batch to their pre-apply state using the forensic blob written at apply time. Covers all apply operations: proposed_change, staged_change, and batch-first tools.

wrap_range
Name: no change
Description (revised): Wraps a 1-based line range. wrapper values: tryCatch (wrap in try/catch; name = exceptionType, default Exception; catchVariableName defaults to ex; catchBody optional), using (wrap in using statement; name = disposal variable name, required), region (wrap in #region; name = region label, required). autoStage=true → ChangeId for tryCatch/region; using returns content string directly.

Verification
After completing all changes, output a table with these columns:
Tool (snake_case)FileMethod renamedName= removed/unchangedDescription updated
One row per tool. Mark each cell yes, no change, or skipped — not found.