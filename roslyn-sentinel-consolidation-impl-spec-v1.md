# RoslynSentinel — Tool Consolidation Implementation Spec
<!-- v1 -->
**Target server:** RoslynSentinel (rhale78) — C# / Microsoft.CodeAnalysis  
**Date:** 2026-05-31  
**For:** an implementing coding agent (Copilot/Claude in VS Code) working against the RoslynSentinel repo  
**Outcome:** reduce the exposed surface from **318 active tools** (430 incl. deprecated) to **90 tools**, all under the 128-tool agent cap in a single profile.

## 0. Read first — scope and rules

This spec has three workstreams: **(A) obsoletion** (delete dead tools), **(B) consolidation** (merge by shared input shape), **(C) the `scan` / `describe_scan_detectors` redesign**. Do them in the order in §6.

**Consolidation rule (do not violate):** merge several tools into one *only* when they share the same **required** input fields and differ by **one** axis (scope / direction / kind / action / target), plus at most one *optional* disambiguator. If two candidates have **disjoint required fields**, leave them separate — a single tool with conditionally-required fields produces a schema the model cannot fill reliably. The codemod family is intentionally **three** tools (file / method / class) for this reason, not one.

**C# style for this repo:** explicit types, always braces, no expression-bodied members.

## A. Obsoletion (delete)

### A1. Deprecated aliases — delete all 109 (110 entries; `find_circular_dependencies` is listed twice)

Every tool whose description begins `Deprecated: use … instead`. These are old Copilot-style names (`find_*`/`detect_*`) already superseded by `get_*`/`scan_*`. Delete the tool registrations and their handler methods.

<details><summary>Full list (109)</summary>

- `add_cancellation_token_to_method`
- `apply_cancellation_token_to_file`
- `convert_to_async_bridge_single`
- `detect_anti_patterns`
- `detect_breaking_changes`
- `detect_inefficient_string_comparisons`
- `detect_json_anti_patterns`
- `detect_layer_violations`
- `detect_long_parameter_lists`
- `detect_memory_leaks`
- `detect_mismatched_await`
- `detect_reflection_usage`
- `detect_unreachable_code`
- `detect_unused_local_variables`
- `detect_unused_private_fields`
- `detect_value_task_misuse`
- `find_all_implementations`
- `find_all_throw_sites`
- `find_async_in_constructor`
- `find_async_over_sync`
- `find_async_void_without_try_catch`
- `find_attribute_usages`
- `find_best_insertion_point`
- `find_blocking_calls_in`
- `find_boxing_allocations`
- `find_callers_safe`
- `find_cancellation_token_not_forwarded`
- `find_cas_loop_without_backoff`
- `find_check_then_act_on_dictionary`
- `find_circular_dependencies`
- `find_circular_type_references`
- `find_concurrent_collection_opportunities`
- `find_configure_await_missing`
- `find_di_registrations`
- `find_double_checked_locking`
- `find_duplicate_blocks_in_class`
- `find_duplicate_blocks_in_hierarchy`
- `find_duplicate_methods`
- `find_extension_methods`
- `find_finalizer_on_disposable`
- `find_hardcoded_paths`
- `find_implementations_safe`
- `find_implicit_nullable_boxing`
- `find_inconsistent_async_suffix`
- `find_interface_extraction_candidates`
- `find_internal_classes_that_could_be_private`
- `find_large_methods`
- `find_large_switch_statements`
- `find_large_types`
- `find_linq_n1_patterns`
- `find_linq_redundant_where`
- `find_long_parameter_list`
- `find_methods_by_return_type`
- `find_migration_candidates`
- `find_misbound_overload_chains`
- `find_missing_cancellation_tokens`
- `find_missing_generic_constraints`
- `find_multiple_enumeration`
- `find_multiple_out_parameter_methods`
- `find_mutable_public_collection_properties`
- `find_mutable_public_properties`
- `find_namespace_path_mismatches`
- `find_naming_violations`
- `find_non_exhaustive_enum_switches`
- `find_object_creation_sites`
- `find_obsolete_callers`
- `find_possible_deadlocks`
- `find_possible_infinite_loops`
- `find_primitive_obsession`
- `find_re_do_s_patterns`
- `find_readonly_field_candidates`
- `find_regex_new_in_loop`
- `find_sequential_independent_awaits`
- `find_services_not_registered`
- `find_string_format_in_loops`
- `find_string_magic_values`
- `find_structural_smells`
- `find_task_delay_usage`
- `find_task_delay_zero_usage`
- `find_task_run_in`
- `find_task_void_usage`
- `find_task_when_all_usage`
- `find_task_yield_usage`
- `find_todo_fixme_comments`
- `find_types_by_attribute`
- `find_unawaited_fire_and_forget`
- `find_unawaked_dispose`
- `find_unbounded_recursion`
- `find_unbounded_static_collections`
- `find_uninstantiated_types`
- `find_unobserved_task_in_field`
- `find_unsafe_lazy_init`
- `find_unsafe_lazy_init_thread`
- `find_unused_constructors`
- `find_unused_interfaces`
- `find_unused_private_members`
- `find_unused_references`
- `find_unvalidated_regex_source`
- `find_use_frozen_collections`
- `find_value_type_mutation_intent`
- `flag_migration_candidate`
- `flag_migration_candidates_batch`
- `flag_migration_candidates_in_project`
- `propagate_cancellation_token_batch`
- `propagate_cancellation_token_in_file`
- `propagate_cancellation_token_in_method`
- `run_bridge_batch`
- `run_uplift_batch`
- `run_uplift_batch_multi`

</details>

### A2. Redundant twins — delete 9 (keep the canonical)

| Delete | Keep (canonical) |
|---|---|
| `analyze_method_control_flow` | `analyze_control_flow` |
| `analyze_method_data_flow` | `analyze_data_flow` |
| `convert_string_format_to_interpolated_smart` | `interpolate_string_safe` |
| `extract_constant` | `extract_constant_safe` |
| `extract_local_variable` | `introduce (as=localVariable)` |
| `extract_method` | `extract_method_safe` |
| `generate_to_string` | `generate_to_string_safe` |
| `safe_delete_symbol` | `safe_delete` |
| `scan_long_parameter_lists` | `scan_long_parameter_list` |

### A3. Name collisions (server bug) — fix

These names are registered **twice** for *different* functions. Rename one of each pair:

- `find_circular_dependencies`
- `scan_circular_dependencies`
- `sync_type_and_filename`

`scan_circular_dependencies`: one detects project-reference cycles, the other type-dependency cycles — rename to `circular_project_references` and `circular_type_references` as detector ids. `sync_type_and_filename`: true duplicate, drop one. `find_circular_dependencies`: deprecated (removed in A1).

## C. The `scan` / `describe_scan_detectors` redesign

Collapse **96 pattern-sweep detectors** into one tool whose `detector` argument is an **enum of bare names with NO prose**. Move the descriptions into a companion lookup tool. This removes ~1.5k tokens of detector docs from every prefill while keeping constrained decoding (the enum still grammar-restricts the value, so the model cannot emit an invalid detector).

### C1. `scan` tool

```
scan(
  detector:  enum[ <~96 bare ids, see C4> ],   # names only, no descriptions
  scope:     enum[ file | project | solution ],
  scopeName: string?                            # filePath / projectName; omit for solution
)
```
- Map `scope`+`scopeName` to the old per-detector required shape: solution-wide detectors ignore it; file-level detectors require `scopeName=<filePath>`; the two class/type-scoped detectors take a type/file name. Validate server-side and return a clear error if a detector needs a scope it wasn't given.

- **Normalisation:** strip the `scan_` / `check_for_` / `check_` / `analyze_` / `optimize_` prefix to form the detector id (e.g. `scan_sql_injection`→`sql_injection`, `check_for_empty_catch_blocks`→`empty_catch_blocks`). Verified: no id collisions after stripping.

### C2. `describe_scan_detectors` tool (companion)

```
describe_scan_detectors(
  domain:   enum[ concurrency | config | convention | correctness | dead-code | misc | performance | security | structure ]?,
  detector: string?                              # a single detector id
)
  # no args   -> all detectors (id, domain, description)
  # domain    -> that slice (disambiguation case)
  # detector  -> that one (confirm-a-name case)
```
- **The description text is not new content** — it is the *current* first-line description of each obsoleted `scan_*`/`check_*` tool. When you fold a detector in, move its existing description string into this tool's data table keyed by detector id. Keep them one line each.

- Agent flow: the ~96 names are visible in the `scan` enum every turn, so for a self-explanatory intent the agent calls `scan` directly with no lookup. It calls `describe_scan_detectors` only to choose by *meaning* (e.g. picking among the concurrency detectors).

### C3. Relocate deep per-method analyzers OUT of `scan`

These are not pattern sweeps — they deeply analyze ONE method and return a structured result. Put them in their own tool, not the `scan` enum:

```
analyze_method(filePath, methodName, aspect: enum[ controlFlow | dataFlow | pathCoverage | unreachableCode ])
```
Folds: `analyze_control_flow`, `analyze_data_flow`, `analyze_path_coverage`, `scan_unreachable_code`.

### C4. Proposed detector ids by domain (review the cross-cutting ones)

Domain is a **soft taxonomy** used only for the `describe_scan_detectors` filter — it does not affect `scan` behaviour. The assignments below are derived from tool names as a starting point; verify the cross-cutting cases. Each detector belongs to exactly one domain here for filter simplicity.

**concurrency** (26): `async_in_constructor`, `async_over_sync`, `async_void_without_try_catch`, `cancellation_token_not_forwarded`, `cas_loop_without_backoff`, `check_then_act_on_dictionary`, `concurrent_collection_opportunities`, `configure_await_missing`, `double_checked_locking`, `inconsistent_async_suffix`, `mismatched_await`, `missing_cancellation_tokens`, `possible_deadlocks`, `semaphore_usage`, `sequential_independent_awaits`, `task_delay_usage`, `task_delay_zero_usage`, `task_run_in`, `task_void_usage`, `task_when_all_usage`, `task_yield_usage`, `unawaited_fire_and_forget`, `unobserved_task_in_field`, `unsafe_lazy_init`, `unsafe_lazy_init_thread`, `value_task_misuse`

**config** (3): `json_anti_patterns`, `package_inconsistency`, `project_consistency`

**convention** (6): `mutable_public_collection_properties`, `mutable_public_properties`, `naming_violations`, `readonly_field_candidates`, `string_magic_values`, `todo_fixme_comments`

**correctness** (17): `all_throw_sites`, `empty_catch_blocks`, `exception_handling`, `memory_leaks`, `misbound_overload_chains`, `missing_generic_constraints`, `multiple_out_parameter_methods`, `non_exhaustive_enum_switches`, `possible_infinite_loops`, `redundant_cast`, `resource_disposal`, `services_not_registered`, `stack_overflow_risks`, `unawaked_dispose`, `unbounded_recursion`, `unbounded_static_collections`, `value_type_mutation_intent`

**dead-code** (9): `obsolete_callers`, `uninstantiated_types`, `unused_constructors`, `unused_event_subscriptions`, `unused_interfaces`, `unused_local_variables`, `unused_private_fields`, `unused_private_members`, `unused_references`

**misc** (3): `anti_patterns`, `blocking_calls_in`, `finalizer_on_disposable`

**performance** (11): `boxing_allocations`, `implicit_nullable_boxing`, `inefficient_string_comparisons`, `linq_n1_patterns`, `linq_redundant_where`, `multiple_enumeration`, `performance`, `re_do_s_patterns`, `regex_new_in_loop`, `string_format_in_loops`, `use_frozen_collections`

**security** (5): `hardcoded_paths`, `reflection_usage`, `security`, `sql_injection`, `unvalidated_regex_source`

**structure** (16): `circular_dependencies`, `circular_type_references`, `duplicate_blocks_in_class`, `duplicate_blocks_in_hierarchy`, `duplicate_methods`, `interface_extraction_candidates`, `internal_classes_that_could_be_private`, `large_methods`, `large_switch_statements`, `large_types`, `layer_violations`, `long_parameter_list`, `namespace_path_mismatches`, `primitive_obsession`, `structural_smells`, `type_cohesion`

Genuinely cross-cutting (pick one home, or list under both in the description text):

- `value_task_misuse` — concurrency+performance
- `regex_new_in_loop` — performance+security
- `implicit_nullable_boxing` — correctness+performance
- `unused_event_subscriptions` — dead-code+correctness
- `unawaked_dispose` — concurrency+correctness

## B. Consolidation map (the other merges)

Each row collapses the listed tools into one. Implement each merged tool's handler to dispatch on the axis argument to the existing per-tool logic (the underlying Roslyn operations are unchanged — this is an API-surface refactor, not a behaviour change).

| New tool | Axis | Folds (count) |
|---|---|---|
| `scan(detector, scope, scopeName?)` | detector enum (~96 bare names, NO prose) + scope | 96 detectors (see §C) |
| `describe_scan_detectors(domain?, detector?)` | companion: returns detector descriptions; optional narrowing | new companion |
| `analyze_method(filePath, methodName, aspect)` | aspect = controlFlow|dataFlow|pathCoverage|unreachableCode (deep per-method analysis, relocated out of scan) | 4 |
| `apply_file_codemod(filePath, transform)` | transform enum (file-wide rewrites) | 25 |
| `apply_method_codemod(filePath, methodName, transform)` | transform enum (method-scoped) | 17 |
| `apply_class_codemod(filePath, className, transform)` | transform enum (class-scoped) | 13 |
| `generate(filePath, className, artifact)` | artifact enum (class/type generators) | 10 |
| `find_references(filePath, symbolName, kind)` | kind = callers|implementations|overrides | 2 |
| `find_by_name(name, kind, scope?)` | kind = implementorsOf|attributeUsages|objectCreations|extensionsFor|typesWithAttribute|methodsByReturnType | 6 |
| `get_type_info(typeName, include)` | include = hierarchy|members|both | 2 |
| `get_diagnostics(scope, scopeName?, summarize?)` | scope = file|project|solution | 4 |
| `get_call_graph(filePath, methodName, direction, maxDepth?, format?)` | direction = forward|reverse | 3 |
| `get_public_api_surface(scope, persistBaseline?)` | persistBaseline flag (surface vs snapshot) | 2 |
| `staged_change(action, changeId)` | action = apply|get|validate|discard | 4 |
| `proposed_change(format, action, payload)` | format(files|diff) x action(apply|validate) | 4 |
| `inspect_symbol(filePath, contextSnippet, aspect)` | aspect = info|blastRadius | 2 |
| `list(kind, projectName?)` | kind = projects|files|dependencies | 3 |
| `modify_attribute(filePath, targetName, attribute, action)` | action = add|remove | 2 |
| `modify_modifier(filePath, targetName, modifier, action)` | action = add|remove | 2 |
| `modify_base_type(filePath, typeName, baseTypeName, action)` | action = add|remove | 2 |
| `introduce(filePath, contextSnippet, newName, as)` | as = localVariable|field|parameter|constant | 4 |
| `extract_members(filePath, className, memberNames, newTypeName, as)` | as = interface|class|partial|superclass | 4 |
| `sync_interface(filePath, className, interfaceName, action)` | action = implement|sync|verify | 3 |
| `inline(filePath, targetName, kind)` | kind = method|variable|field|parameter | 4 |
| `add_member(filePath, containerName, newMemberSource, position?)` | position = end|before:X|after:X | 3 |
| `add_member_typed(filePath, containerName, name, type, kind)` | kind = property|field | 2 |
| `wrap_range(filePath, startLine, endLine, wrapper, name?)` | wrapper = tryCatch|using|region | 3 |
| `move_type(filePath, typeName, destination)` | destination = ownFile|outerScope | 2 |
| `move_all_types_to_files(scope, scopeName?)` | scope = file|project|solution | 3 |
| `async_migrate(operation, input)` | operation enum (shared BatchTargetInput) — WEAKEST MERGE, input sub-schema varies | 6 |
| `project_doc(action, docType, name?, content?)` | docType x action(read|write|append|list) | 11 |
| `features(action, names?, enabled?)` | action = get|list|update | 3 |

<details><summary>Full fold lists for each merged tool</summary>

**`scan`** ← `analyze_exception_handling`, `analyze_performance`, `analyze_security`, `analyze_semaphore_usage`, `analyze_stack_overflow_risks`, `analyze_type_cohesion`, `check_for_empty_catch_blocks`, `check_for_redundant_cast`, `check_for_sql_injection`, `check_for_unused_event_subscriptions`, `check_package_inconsistency`, `check_project_consistency`, `optimize_resource_disposal`, `scan_all_throw_sites`, `scan_anti_patterns`, `scan_async_in_constructor`, `scan_async_over_sync`, `scan_async_void_without_try_catch`, `scan_blocking_calls_in`, `scan_boxing_allocations`, `scan_cancellation_token_not_forwarded`, `scan_cas_loop_without_backoff`, `scan_check_then_act_on_dictionary`, `scan_circular_dependencies`, `scan_circular_type_references`, `scan_concurrent_collection_opportunities`, `scan_configure_await_missing`, `scan_double_checked_locking`, `scan_duplicate_blocks_in_class`, `scan_duplicate_blocks_in_hierarchy`, `scan_duplicate_methods`, `scan_finalizer_on_disposable`, `scan_hardcoded_paths`, `scan_implicit_nullable_boxing`, `scan_inconsistent_async_suffix`, `scan_inefficient_string_comparisons`, `scan_interface_extraction_candidates`, `scan_internal_classes_that_could_be_private`, `scan_json_anti_patterns`, `scan_large_methods`, `scan_large_switch_statements`, `scan_large_types`, `scan_layer_violations`, `scan_linq_n1_patterns`, `scan_linq_redundant_where`, `scan_long_parameter_list`, `scan_memory_leaks`, `scan_misbound_overload_chains`, `scan_mismatched_await`, `scan_missing_cancellation_tokens`, `scan_missing_generic_constraints`, `scan_multiple_enumeration`, `scan_multiple_out_parameter_methods`, `scan_mutable_public_collection_properties`, `scan_mutable_public_properties`, `scan_namespace_path_mismatches`, `scan_naming_violations`, `scan_non_exhaustive_enum_switches`, `scan_obsolete_callers`, `scan_possible_deadlocks`, `scan_possible_infinite_loops`, `scan_primitive_obsession`, `scan_re_do_s_patterns`, `scan_readonly_field_candidates`, `scan_reflection_usage`, `scan_regex_new_in_loop`, `scan_sequential_independent_awaits`, `scan_services_not_registered`, `scan_string_format_in_loops`, `scan_string_magic_values`, `scan_structural_smells`, `scan_task_delay_usage`, `scan_task_delay_zero_usage`, `scan_task_run_in`, `scan_task_void_usage`, `scan_task_when_all_usage`, `scan_task_yield_usage`, `scan_todo_fixme_comments`, `scan_unawaited_fire_and_forget`, `scan_unawaked_dispose`, `scan_unbounded_recursion`, `scan_unbounded_static_collections`, `scan_uninstantiated_types`, `scan_unobserved_task_in_field`, `scan_unsafe_lazy_init`, `scan_unsafe_lazy_init_thread`, `scan_unused_constructors`, `scan_unused_interfaces`, `scan_unused_local_variables`, `scan_unused_private_fields`, `scan_unused_private_members`, `scan_unused_references`, `scan_unvalidated_regex_source`, `scan_use_frozen_collections`, `scan_value_task_misuse`, `scan_value_type_mutation_intent`

**`analyze_method`** ← `analyze_control_flow`, `analyze_data_flow`, `analyze_path_coverage`, `scan_unreachable_code`

**`apply_file_codemod`** ← `add_braces`, `convert_to_null_coalescing`, `convert_to_pattern`, `convert_to_switch`, `simplify_boolean_expressions`, `simplify_member_access`, `simplify_verbosity`, `sort_and_deduplicate_usings`, `use_field_backed_properties`, `use_index_from_end`, `use_time_provider`, `upgrade_pattern_matching`, `upgrade_to_file_scoped_namespace`, `upgrade_to_modern_guards`, `upgrade_thread_safety`, `cleanup_implicit_spans`, `optimize_task_wait`, `add_configure_await_false`, `remove_configure_await_false`, `fix_thread_sleep`, `generate_xml_documentation_stubs`, `preview_add_missing_usings`, `format_document_safe`, `format_document_preview`, `fix_mismatched_namespaces`

**`apply_method_codemod`** ← `convert_lock_to_semaphore_slim`, `convert_method_to_indexer`, `convert_out_params_to_value_tuple`, `convert_static_to_extension`, `convert_switch_to_expression`, `convert_to_async_enumerable`, `extension_to_static`, `generate_async_overload`, `make_method_static`, `make_method_thread_safe`, `optimize_independent_awaits`, `optimize_to_value_task`, `reduce_block_depth`, `use_exception_expressions`, `add_guard_clauses`, `update_xml_docs_from_signature`, `convert_expression_body`

**`apply_class_codemod`** ← `class_to_record`, `record_to_class`, `convert_abstract_to_interface`, `convert_to_background_service`, `convert_to_source_generated_logging`, `make_class_immutable`, `upgrade_to_primary_constructor`, `replace_constructor_with_factory`, `add_validation_to_poco`, `document_poco_fields`, `sort_members`, `convert_property_to_methods`, `convert_property_safe`

**`generate`** ← `generate_constructor`, `generate_equality_overrides`, `generate_fluent_builder`, `generate_repository_interface`, `generate_test_scaffold`, `generate_test_skeleton`, `generate_to_string_safe`, `generate_decorator_class`, `add_benchmark_stub`, `generate_path_driven_tests`

**`find_references`** ← `get_callers`, `get_implementations`

**`find_by_name`** ← `get_all_implementations`, `get_object_creation_sites`, `get_extension_methods`, `get_attribute_usages`, `scan_types_by_attribute`, `scan_methods_by_return_type`

**`get_type_info`** ← `get_type_hierarchy`, `get_type_members_detail`

**`get_diagnostics`** ← `get_file_diagnostics`, `get_project_diagnostics`, `get_solution_diagnostics`, `get_diagnostics_summary`

**`get_call_graph`** ← `get_call_graph`, `get_reverse_call_graph`, `generate_call_tree`

**`get_public_api_surface`** ← `get_public_api_surface`, `get_public_api_surface_snapshot`

**`staged_change`** ← `apply_staged_changes`, `get_staged_changes`, `validate_staged_changes`, `discard_staged_changes`

**`proposed_change`** ← `apply_proposed_changes`, `validate_proposed_changes`, `apply_proposed_diff`, `validate_proposed_diff`

**`inspect_symbol`** ← `get_symbol_info`, `get_blast_radius`

**`list`** ← `list_projects`, `list_files`, `list_dependencies`

**`modify_attribute`** ← `add_attribute`, `remove_attribute`

**`modify_modifier`** ← `add_modifier`, `remove_modifier`

**`modify_base_type`** ← `add_base_type`, `remove_base_type`

**`introduce`** ← `introduce_field`, `introduce_variable`, `introduce_parameter`, `extract_constant_safe`

**`extract_members`** ← `extract_interface`, `extract_class`, `extract_members_to_partial`, `extract_superclass`

**`sync_interface`** ← `implement_interface_safe`, `sync_interface_to_implementation`, `verify_interface_completeness`

**`inline`** ← `inline_method`, `inline_variable`, `inline_field`, `inline_parameter`

**`add_member`** ← `add_member_to_class`, `insert_member_after`, `insert_member_before`

**`add_member_typed`** ← `add_property`, `add_field`

**`wrap_range`** ← `wrap_in_try_catch`, `wrap_in_using`, `wrap_in_region`

**`move_type`** ← `move_type_to_file`, `move_type_to_outer_scope`

**`move_all_types_to_files`** ← `move_all_types_to_files`, `move_all_types_to_files_in_project`, `move_all_types_to_files_in_solution`

**`async_migrate`** ← `asyncify`, `add_cancellation_token`, `propagate_cancellation_token`, `convert_to_async_bridge`, `run_uplift`, `flag_migration_candidates`

**`project_doc`** ← `read_plan`, `update_plan`, `read_handoff`, `write_handoff`, `read_completed_work`, `append_completed_work`, `read_project_documentation`, `update_project_documentation`, `list_project_documentation`, `read_current_state`, `update_current_state`

**`features`** ← `get_feature_status`, `list_features`, `update_features`

</details>

## D. Weakest merges — verify, split back if the model misfills

- **`async_migrate`** — the six folded ops share `BatchTargetInput` at the top level but each operation's `input` *sub-object* differs. This is the only merge that bends the no-conditional-schema rule (at the sub-object level). If the agent misfills it, split these six back out — low cost.

- **`scan` detector ↔ scope validity** — not every detector supports every scope. Encode the valid scope(s) per detector server-side and fail fast with a message naming the allowed scope, rather than silently scanning nothing.

- **`generate` vs generator singletons** — `generate_classes_from_json`, `generate_http_client`, `generate_mapping`, `generate_default_config_json` keep their own tools (disjoint required inputs). Do **not** fold them into `generate`.

## E. Revised profile grouping (optional)

All **90** tools fit under 128 in one profile, so `--mode` gating is now optional (token economy only). If you keep profiles, expose Core+Refactor by default and the rest behind a startup `--mode` arg.

### Core (always on) — 32
`acknowledge_sync`, `diagnose`, `find_by_name`, `find_references`, `get_best_insertion_point`, `get_breaker_status`, `get_call_graph`, `get_code_inventory`, `get_di_registrations`, `get_diagnostics`, `get_external_changes`, `get_file_outline`, `get_method_complexity`, `get_method_source`, `get_operation_detail`, `get_project_framework_summary`, `get_public_api_surface`, `get_solution_metrics`, `get_test_coverage_map`, `get_type_info`, `get_workspace_health`, `inspect_symbol`, `list`, `load_solution`, `preview_rename_impact`, `proposed_change`, `reset_breaker`, `retry_failed_changes`, `search_solution_text`, `staged_change`, `trace_variable_lifetime`, `undo_last_apply`

### Common refactor (default) — 28
`add_constructor_parameter`, `add_enum_value`, `add_member`, `add_member_typed`, `add_summary_comment`, `add_using_directive`, `change_accessibility`, `change_signature`, `convert_anonymous_to_named`, `encapsulate_field_safe`, `extract_members`, `extract_method_safe`, `inline`, `inline_class`, `introduce`, `introduce_parameter_object`, `modify_attribute`, `modify_base_type`, `modify_modifier`, `move_file_to_namespace_folder`, `move_type`, `pull_up_member`, `remove_member`, `rename_symbol`, `replace_member`, `safe_delete`, `sync_interface`, `wrap_range`

### `--mode analysis` — 7
`analyze_foreach_for_linq_conversion`, `analyze_method`, `analyze_switch_for_pattern_conversion`, `describe_scan_detectors`, `get_comprehensive_health_report`, `scan`, `scan_breaking_changes`

### `--mode modernize` — 9
`apply_class_codemod`, `apply_file_codemod`, `apply_method_codemod`, `convert_switch_to_pattern_safe`, `interpolate_string_safe`, `invert_assignments`, `invert_boolean_logic`, `modernize_exceptions`, `upgrade_unbound_nameof`

### `--mode codegen` — 5
`generate`, `generate_classes_from_json`, `generate_default_config_json`, `generate_http_client`, `generate_mapping`

### `--mode async` — 3
`async_migrate`, `get_async_migration_progress`, `scan_migration_candidates`

### `--mode project` — 6
`create_project`, `features`, `move_all_types_to_files`, `project_doc`, `split_project_by_folder`, `sync_type_and_filename`

**Default (Core + Refactor) = 60.** Each opt-in mode = Core + that group, all under 128.

## F. Implementation order

Incremental, lowest-risk first (matches the repo's guard→read→write→complex convention):

1. **A1–A3 obsoletion.** Delete deprecated aliases + twins, fix collisions. Pure removal; verify the build and that no remaining tool references a deleted handler. Biggest immediate surface drop.
2. **Read-only merges first:** `get_diagnostics`, `find_references`, `find_by_name`, `get_type_info`, `get_call_graph`, `inspect_symbol`, `list`, `get_public_api_surface`. No mutation risk; easy to test by comparing output to the old tools.
3. **`scan` + `describe_scan_detectors` + `analyze_method` (§C).** The headline change. Build `describe_scan_detectors` data table from the existing descriptions as you remove each standalone detector.
4. **Write-pipeline merges:** `staged_change`, `proposed_change`.
5. **Refactor/edit merges:** `modify_*`, `introduce`, `inline`, `add_member`, `add_member_typed`, `extract_members`, `sync_interface`, `wrap_range`, `move_type`.
6. **Codemod merges:** `apply_file_codemod`, `apply_method_codemod`, `apply_class_codemod`, `generate`.
7. **Gated families:** `project_doc`, `features`, `move_all_types_to_files`, and last `async_migrate` (the weakest merge — validate carefully).
8. **(Optional) profiles:** add the `--mode` startup arg and gate per §E.

## G. Acceptance criteria

- [ ] `tools/list` returns ≤ 90 tools (≤128 in any single `--mode`).
- [ ] No tool description begins with `Deprecated:`.
- [ ] No duplicate tool names in the manifest.
- [ ] `scan` enum carries detector **names only**, no per-value prose; total scan-related schema < ~250 tokens.
- [ ] Every old detector id is reachable via `scan(detector=…)` and described via `describe_scan_detectors`.
- [ ] Each merged tool reproduces the output of every tool it replaced (spot-check one axis value per fold).
- [ ] Build passes; no orphaned handler references.

*Generated 2026-05-31 — v1*