# Plan: reorganize the docs folder into genre folders with closed/ history

**Status:** IMPLEMENTED 2026-11-08. Moves, merges, link and .cs reference rewrites, index and memory updates are done and the build was clean; the commit is pending.

## Progress (2026-11-08)

- Owner decisions applied: docs/research files are now tracked at their new location (the `/docs/research/` rule is removed from `.gitignore`); all 11 merge candidates merged (23 source files folded into 11 docs, sources deleted with `DeleteFile`); LOW-confidence rows applied as written; `blockers/resolved/` renamed to `blockers/closed/`.
- Moved: 274 move rows (271 files plus 3 step-set folders holding 31 files); `docs/obsolete`, `docs/research`, `docs/testing`, `docs/current/specs` and `docs/current/upstream` are removed; `docs/current/` root holds only `TODO.md`, `CLOSED.md`, `DOCUMENTATION_INDEX.md`.
- Link rewrite: 229 non-.cs files rewritten (md, ps1, json, CLAUDE.md, .claude agents/hooks/skills, scripts). Relative Markdown links under docs/, .claude/ and CLAUDE.md all resolve except 68 that were already broken before the move (listed by the verify pass).
- .cs references: 110 rows in 76 .cs files rewritten in place (comments and one `[Obsolete]` message string, doc paths only; encoding, BOM and line endings preserved byte for byte). Record: `c:\tmp\cs_doc_ref_rewrites.tsv`. Not yet built. The server's drift watcher lists those 76 .cs files as external changes.
- Merge notes: `plan_scoped_operation_ledger.md` absorbed both proposals (cross-genre, into plans/closed); the nine test-review findings are `findings/finding_comprehensive_review_tests.md` (open); the pointer `plan-eval-defect-remediation-v2-steps.md` is now `evals/closed/eval_defect_remediation_v2_steps/README.md`. `eval_*` docs and the merged docs carry a generic "CLOSED 2026-11-08" Status line where they had none.
- Template README: `evals` genre row added, rule 8 generalized to `<genre>/closed/`, rule 9 replaced by the docs/current root rule.
- Left for the owner: `DOCUMENTATION_INDEX.md` still describes the removed folders; memory files and `MEMORY.md` still link old doc names (hygiene reports 26 MEM-WIKILINK findings for renamed docs); `docs/testing/` is also a runtime path of the ProjectDoc tool in testing mode (code, unchanged).

The sections below are the original manifest (paths are the pre-move sources and the planned targets).

All paths are relative to `docs/`; targets are under `docs/current/`.

## Problem

About 400 Markdown files sit in `docs/current/` root, `obsolete/`, `research/`, `testing/`, `specs/` and `upstream/`, and many open-genre docs are actually finished. Link rules and the template README (rule 9) tell agents to leave legacy docs in place, so the tree keeps growing. Target layout: `docs/current/` root keeps only `TODO.md`, `CLOSED.md`, `DOCUMENTATION_INDEX.md`; genre folders `blockers findings proposals issues design plans ideas references evals`, each with `closed/`; `archive/` for frozen pre-taxonomy docs.

## Decision (summary of the manifest)

### Counts per target folder (rows in the table below, incl. renames of docs that stay open)

| Target folder | Rows |
| --- | --- |
| `current/archive/` | 18 |
| `current/blockers/closed/` | 41 |
| `current/design/` | 3 |
| `current/design/closed/` | 3 |
| `current/evals/closed/` | 42 |
| `current/findings/` | 17 |
| `current/findings/closed/` | 30 |
| `current/ideas/` | 2 |
| `current/ideas/closed/` | 3 |
| `current/issues/` | 1 |
| `current/issues/closed/` | 4 |
| `current/plans/closed/` | 76 |
| `current/proposals/` | 17 |
| `current/proposals/closed/` | 28 |
| `current/references/` | 12 |

Rows that stay put (Source == Target, listed separately): blockers 3, design 1, findings 7, ideas 5, issues 4, plans 12, proposals 21, references 15, plus 5 keepers (3 root files, 2 archive rollups).

- **Total manifest rows:** 365 (table 297, stay-put list 68); keepers not counted.
- **LOW-confidence rows:** 48 (marked LOW in the table, or LOW in the stay-put list).
- **Inbound references (rows in the table, excluding rows marked * below):** 1693, of which 323 are in .cs files. Counts are occurrences of the old file name in 1628 scanned files (md, cs, ps1, json, yml, txt, csproj, etc.); `.claude/{journal,handoff,usage,worktrees,projects}`, `.roslynsentinel`, `ModelTestingResults`, `lmstudio_logs`, `_scratchbuild*`, `bin*`, `obj` and Worktree folders are excluded as logs/artifacts. Rows marked `*` share a basename with another doc or have a generic name (README.md, 00-index.md, 01-baseline.md); their count is not attributable and is excluded from the total (4 rows).
- **Collisions:** see "Collisions" below.
- **Rename proposed (one line):** `blockers/resolved/` -> `blockers/closed/` (every existing file under `docs/current/blockers/resolved/`; the 9 files from `obsolete/blockers/resolved/` already target `blockers/closed/`); optional, equivalent either way.
- **Binaries and baselines (out of scope):** `docs/CodeMetrics.csv`, `CodeMetrics.xls`, `CodeMetrics.xlsx`, `neurosymbolic-codegen-originals-2026-05-29.zip`, `roslyn-sentinel-originals-2026-06-04.zip` stay; `known-*.txt` baselines stay.

### Decisions and surprises the owner should look at

1. **`docs/research/` is gitignored** (`.gitignore`: `/docs/research/`; its README says "untracked by design"). The 22 files there are not in git. Moving them to `current/evals/closed/` makes them tracked (22 new files in the next commit) unless `evals/closed/` gets an ignore rule. Decide first; the move script must use `Move-Item`, not `git mv`, for them.
2. **`evals/closed/` takes almost everything** from `research/testing`, the `project_*` memory mirrors in `obsolete/`, the `current/model_eval_*` analyses and the three PlanStepRunner step-set folders, because finished experiments have no "open" state. If you prefer a flat `evals/`, change the state column; nothing else depends on it.
3. **Eval filename prefix:** the brief gave no prefix for the new genre; this manifest uses `eval_<slug>.md` and `eval_<slug>/` for step-set folders, and drops `project_`/`model_eval_` prefixes. Add the row to `templates/README.md`.
4. **Many open-folder docs are closed:** counts are in the table; every Status line that says IMPLEMENTED/FIXED/RESOLVED/SUPERSEDED/DONE moved. Docs with no status or a stale OPEN were checked against symbol existence (Search symbol mode: `IRateLimiter`, `IAutomaticCircuitBreaker`, `ScopedOperationLedgerEngine`, `SnippetSymbolLocator`, `SymbolResolver`, `IWorkspaceReader`, `StructuralRefactoringEngine` exist; `DocumentChangeSet`, `SplitFacadeMember`, `CompilationCache`, `BulkComment` do not), CLOSED.md/TODO.md mentions, and the memory index. No per-doc git log search was done (the `Git` tool has no message grep); the 100-commit window checked only shows 2026-11 work.
5. **PROPOSED docs that CLOSED.md lists** (`proposal_build_full_build_ignores_project_scope`, `proposal_load_solution_hint_for_unrestored_project`) are marked closed with LOW confidence; their Status lines still say PROPOSED and should be updated by whoever closes them.
6. **Name-only moves of living docs:** hyphenated/`-v1` references (`reference-code-file-write-paths-v1.md`, `reference-roslynsentinel-mcp-launch-v1.md`, `reference-vscode-control-script-v1.md`) are cited from scripts and `.cs` comments; their ref counts are in the table.

### Docs that look like merge candidates (not merged)

- `research/testing/project_replaceblockformatted_accessibility_cost_2026_09_05.md` is a short mirror of `current/model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md` (same target name: collision).
- `research/testing/project_modeleval_fixture_test_suite_redesign.md` mirrors `current/modeleval_fixture_test_suite_redesign.md` (same target name: collision).
- `obsolete/project_external_drift_hard_blocker_idea.md` duplicates `current/ideas/external-drift-hard-blocker.md` (same target name: collision).
- `obsolete/project_readfile_createfile_path_inconsistency_bug.md` and `obsolete/project_readfile_createfile_disk_fallback_fixed.md` describe the same bug and its fix.
- `obsolete/finding_git_tool_worktree_resolution_wrong_repo_root.md` is a pointer to `blockers/resolved/blocking_error_git_tool_commit_reports_clean_tree_worktree.md`.
- `obsolete/plans/plan-eval-defect-remediation-v2-steps.md` is a pointer to the step tree (fold into the step-set folder as its README).
- `current/plans/plan_rename_locator_step1..3_*.md` fold into `plan_rename_symbol_snippet_locator.md` (and into `evals/closed/eval_rename_locator_steps_runner/`, the runner copy of the same steps).
- `proposal_scoped_operation_ledger.md`, `proposal_movemember_instance_callsite_resolution.md` and `plan_scoped_operation_ledger.md` describe one feature.
- `roslyn-duplication-audit-v1.md`, `obsolete/codebase-consistency-audit-v1.md` and `obsolete/plans/plan-codebase-consistency-review-v1.md` are one audit series.
- The nine `20261020-Comprehensive-Review_Tests.*` findings could become one finding with a section per project (renamed here, not merged).
- `obsolete/plan-tool-rename-v1.md`, `VERIFICATION_TABLE.md` and `obsolete/plan-migration-scan-*` / `spec-migration-scan-*` pairs are plan+spec pairs of the same work.

### Collisions

- RESOLVED by suffix: obsolete/specs/spec-eval-defect-remediation-v2.md -> current/plans/closed/plan_eval_defect_remediation_spec.md (otherwise collides with the matching plan-* file)
- RESOLVED by suffix: obsolete/spec-migration-scan-result-handling-v2.md -> current/plans/closed/plan_migration_scan_result_handling_spec.md (otherwise collides with the matching plan-* file)
- OPEN COLLISION: current/modeleval_fixture_test_suite_redesign.md + research/testing/project_modeleval_fixture_test_suite_redesign.md -> current/evals/closed/eval_modeleval_fixture_test_suite_redesign.md (likely duplicates; merge or rename)
- OPEN COLLISION: current/model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md + research/testing/project_replaceblockformatted_accessibility_cost_2026_09_05.md -> current/evals/closed/eval_replaceblockformatted_accessibility_cost_2026_09_05.md (likely duplicates; merge or rename)
- OPEN COLLISION: current/ideas/external-drift-hard-blocker.md + obsolete/project_external_drift_hard_blocker_idea.md -> current/ideas/closed/idea_external_drift_hard_blocker.md (likely duplicates; merge or rename)

## Manifest

Source and Target are relative to `docs/`. Status found is the status word quoted from the first lines (`none` = no status line; `title:X` = taken from the title). Refs = inbound references to the old file name (in parentheses: how many are in .cs files). `*` = name shared with another doc or generic, count not attributable.

### docs/current (root, 76 files) - 73 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/blocking_error_group4_refactoringengine_absorption_false_duplicates.md` | `current/blockers/closed/blocking_error_group4_refactoringengine_absorption_false_duplicates.md` | OPEN | OPEN but engine reorg since landed; sibling group6 resolved | LOW | 2 (0) |
| `current/blocking_error_group6_getpublicapisurface_duplicate_false_premise.md` | `current/blockers/closed/blocking_error_group6_getpublicapisurface_duplicate_false_premise.md` | RESOLVED | Status RESOLVED, not a defect | HIGH | 0 (0) |
| `current/bugreport_applydiff_escape_sequence_and_linecount_header.md` | `current/findings/finding_applydiff_escape_sequence_and_linecount_header.md` | none | No status; bug report, no fix evidence found | LOW | 0 (0) |
| `current/decisions_autonomous_run_2026-11-05.md` | `current/archive/decisions_autonomous_run_2026_11_05.md` | none | Run decision log; fits no genre; companion of in-progress plan | MED | 6 (0) |
| `current/design_mcp_schema_nullable_anyof_fix.md` | `current/design/design_mcp_schema_nullable_anyof_fix.md` | deferred | Front matter says deferred; still a live design topic | MED | 0 (0) |
| `current/design_read_chokepoint.md` | `current/design/design_read_chokepoint.md` | none | IWorkspaceReader exists; design still describes it | MED | 52 (8) |
| `current/design_subagent_tool.md` | `current/design/design_subagent_tool.md` | none | SubAgent built; design still describes it | MED | 4 (0) |
| `current/docCommentId_description_gap.md` | `current/issues/issue_doc_comment_id_description_gap.md` | none | No status; "Not started yet"; memory says tracked in TODO | LOW | 2 (1) |
| `current/finding_ambiguous_plan_instruction_replacesnippetbatch_oscillation.md` | `current/findings/closed/finding_ambiguous_plan_instruction_replacesnippetbatch_oscillation.md` | confirmed | ReplaceSnippetBatch tool no longer exists; follow-ups stale | MED | 2 (1) |
| `current/finding_applyunifieddiff_anchor_collision_duplicate_trivia.md` | `current/findings/finding_applyunifieddiff_anchor_collision_duplicate_trivia.md` | confirmed | Not fixed per memory index | MED | 2 (0) |
| `current/finding_build_quickbuild_spurious_error_cascade.md` | `current/findings/finding_build_quickbuild_spurious_error_cascade.md` | DECISION | Status DECISION; no fix evidence | MED | 5 (0) |
| `current/finding_constructorparameter_methodsignature_no_retype_operation.md` | `current/findings/finding_constructorparameter_methodsignature_no_retype_operation.md` | confirmed | Retype still missing; blocker only partly resolved | MED | 0 (0) |
| `current/finding_git_commit_commithash_length_unconfirmed_no_source_mechanism.md` | `current/findings/closed/finding_git_commit_commithash_length_unconfirmed_no_source_mechanism.md` | RESOLVED | Status RESOLVED | HIGH | 2 (2) |
| `current/finding_matchstrictness_internal_param_evaluation.md` | `current/findings/finding_matchstrictness_internal_param_evaluation.md` | Not implemented | Not implemented evaluation; no later evidence | LOW | 2 (0) |
| `current/finding_member_add_traceability_comment_wording.md` | `current/findings/closed/finding_member_add_traceability_comment_wording.md` | RESOLVED | Status RESOLVED | HIGH | 4 (0) |
| `current/finding_member_addtopleveltype_addmember_comment_lands_in_file_content.md` | `current/findings/closed/finding_member_addtopleveltype_addmember_comment_lands_in_file_content.md` | confirmed | Sibling finding says the comment injection was disabled | MED | 0 (0) |
| `current/finding_multifile_signature_change_sequencing_friction.md` | `current/findings/finding_multifile_signature_change_sequencing_friction.md` | confirmed | Friction pattern; parallel-change proposal still open | LOW | 2 (0) |
| `current/finding_normalizewhitespace_container_reformat_risk_inventory.md` | `current/findings/closed/finding_normalizewhitespace_container_reformat_risk_inventory.md` | Historical | Status Historical; helper family deleted | HIGH | 7 (0) |
| `current/finding_snippet_notfound_wording_confusion_audit.md` | `current/findings/finding_snippet_notfound_wording_confusion_audit.md` | Category | Doc says it stays open; listed in TODO | HIGH | 5 (0) |
| `current/finding_synctypeandfilename_wrong_type_selection.md` | `current/findings/closed/finding_synctypeandfilename_wrong_type_selection.md` | FIXED | Status FIXED 2026-09-19 | HIGH | 0 (0) |
| `current/finding_writepath_noop_fabricated_success_formatting_only_edits.md` | `current/findings/closed/finding_writepath_noop_fabricated_success_formatting_only_edits.md` | RESOLVED | Status RESOLVED | HIGH | 3 (0) |
| `current/interrogate_script_added.md` | `current/references/reference_interrogate_script.md` | none | Living description of a script, not an event | MED | 0 (0) |
| `current/issue_conditional_required_param_audit_followup.md` | `current/issues/closed/issue_conditional_required_param_audit_followup.md` | title:CLOSED | Title says CLOSED 2026-09-06 | HIGH | 5 (0) |
| `current/issue_di_engine_grouping_open_question.md` | `current/issues/closed/issue_di_engine_grouping_open_question.md` | title:OPEN | Title OPEN but memory says moot (Tools/Impl split shipped) | LOW | 0 (0) |
| `current/issue_member_containername_conditional_required_gap.md` | `current/issues/closed/issue_member_containername_conditional_required_gap.md` | title:FIXED | Title says FIXED 2026-09-06 | HIGH | 8 (0) |
| `current/lmstudio_streaming_json_truncation_investigation.md` | `current/findings/closed/finding_lmstudio_streaming_json_truncation.md` | none | Root cause RESOLVED 2026-09-07 (upstream bug) | HIGH | 0 (0) |
| `current/member_replace_drops_leading_blank_line_and_verify_gap.md` | `current/findings/finding_member_replace_drops_leading_blank_line_and_verify_gap.md` | none | No status; memory index lists it open | MED | 3 (0) |
| `current/model_eval_pattern_analysis_2026_09_02.md` | `current/evals/closed/eval_pattern_analysis_2026_09_02.md` | none | Dated analysis report, no status | MED | 3 (1) |
| `current/model_eval_pattern_analysis_prompt.md` | `current/evals/closed/eval_pattern_analysis_prompt.md` | none | Prompt for the 2026-09-02 analysis | MED | 2 (0) |
| `current/model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md` | `current/evals/closed/eval_replaceblockformatted_accessibility_cost_2026_09_05.md` **COLLISION** | none | CLOSED.md entry resolved 2026-11-07 | MED | 4 (0) |
| `current/modeleval_fixture_test_suite_redesign.md` | `current/evals/closed/eval_modeleval_fixture_test_suite_redesign.md` **COLLISION** | none | Text says IMPLEMENTED 2026-09-07 | HIGH | 12 (7) |
| `current/proposal_batch_modifier_tools.md` | `current/proposals/proposal_batch_modifier_tools.md` | none | Drafted not implemented; batchEdits state unverified | LOW | 2 (0) |
| `current/proposal_batch_replacesnippet.md` | `current/proposals/closed/proposal_batch_replacesnippet.md` | none | ReplaceSnippet batchEdits now exists | MED | 16 (1) |
| `current/proposal_centralized_large_result_filter.md` | `current/proposals/closed/proposal_centralized_large_result_filter.md` | none | Marshaller plan owns offload now | MED | 7 (3) |
| `current/proposal_changesignature_full_roslyn_service.md` | `current/proposals/closed/proposal_changesignature_full_roslyn_service.md` | none | CLOSED.md: Roslyn API internal, option abandoned | HIGH | 10 (0) |
| `current/proposal_changesymboltype_tool.md` | `current/proposals/proposal_changesymboltype_tool.md` | none | No ChangeSymbolType tool in tool table | MED | 9 (0) |
| `current/proposal_codemodtools_decomposition.md` | `current/proposals/proposal_codemodtools_decomposition.md` | none | Drafted; TODO shows partial conversion since | LOW | 2 (0) |
| `current/proposal_compilation_cache.md` | `current/proposals/proposal_compilation_cache.md` | none | No cache type exists in solution | MED | 10 (3) |
| `current/proposal_cs0138_lookup_helper.md` | `current/proposals/proposal_cs0138_lookup_helper.md` | none | Memory index: designed, not built | HIGH | 0 (0) |
| `current/proposal_datatag_chaining_contract.md` | `current/proposals/proposal_datatag_chaining_contract.md` | none | No status; DataTag still opt-in only | LOW | 5 (2) |
| `current/proposal_envelope_field_promotion.md` | `current/proposals/proposal_envelope_field_promotion.md` | none | No status; pilot 2 abandoned, rest unverified | LOW | 16 (0) |
| `current/proposal_findreferences_resolver_and_suggestions.md` | `current/proposals/closed/proposal_findreferences_resolver_and_suggestions.md` | Implemented | Status Implemented 2026-09-20 | HIGH | 2 (1) |
| `current/proposal_first_person_turn_injection.md` | `current/proposals/proposal_first_person_turn_injection.md` | none | Memory index: proposal, unbuilt | MED | 4 (0) |
| `current/proposal_identifier_clarity_audit_tool.md` | `current/proposals/proposal_identifier_clarity_audit_tool.md` | none | No such tool in tool table | MED | 0 (0) |
| `current/proposal_movemember_instance_callsite_resolution.md` | `current/proposals/closed/proposal_movemember_instance_callsite_resolution.md` | none | CLOSED.md entry; ledger and keep modes shipped | MED | 26 (3) |
| `current/proposal_outparam_tuple_construction_syntaxfactory.md` | `current/proposals/proposal_outparam_tuple_construction_syntaxfactory.md` | none | Proposed, no status; not verified in source | LOW | 2 (1) |
| `current/proposal_per_call_correlation_id.md` | `current/proposals/proposal_per_call_correlation_id.md` | none | Undecided; no later evidence | LOW | 0 (0) |
| `current/proposal_per_session_mcp_server.md` | `current/proposals/closed/proposal_per_session_mcp_server.md` | none | Memory: per-session isolation shipped 2026-10-23 | MED | 7 (0) |
| `current/proposal_redirect_calls_tool.md` | `current/proposals/proposal_redirect_calls_tool.md` | none | No RedirectCalls tool in tool table | LOW | 3 (0) |
| `current/proposal_replacesnippet_result_dto.md` | `current/proposals/closed/proposal_replacesnippet_result_dto.md` | Implemented | Status Implemented 2026-09-20 | HIGH | 0 (0) |
| `current/proposal_scoped_operation_ledger.md` | `current/proposals/closed/proposal_scoped_operation_ledger.md` | none | ScopedOperationLedgerEngine exists | MED | 17 (3) |
| `current/proposal_solution_relative_paths.md` | `current/proposals/closed/proposal_solution_relative_paths.md` | none | Stage 1 plan IMPLEMENTED; doc says shipped | LOW | 5 (0) |
| `current/proposal_splitfacademember_tool.md` | `current/proposals/proposal_splitfacademember_tool.md` | none | No tool built; facade split may be finished | LOW | 4 (0) |
| `current/proposal_staged_writes.md` | `current/proposals/proposal_staged_writes.md` | Superseded | No staged-write type found | LOW | 11 (0) |
| `current/proposal_structuredcontent_rollout.md` | `current/proposals/closed/proposal_structuredcontent_rollout.md` | none | Per-branch DTO rollout mostly landed per later doc | LOW | 28 (10) |
| `current/proposal_tool_error_code_taxonomy.md` | `current/proposals/proposal_tool_error_code_taxonomy.md` | none | Memory: partial, numeric scheme unbuilt | MED | 17 (0) |
| `current/proposal_unified_search_tool.md` | `current/proposals/closed/proposal_unified_search_tool.md` | none | Search tool with mode selector exists | MED | 2 (1) |
| `current/proposal_unify_autostage_return_shape.md` | `current/proposals/proposal_unify_autostage_return_shape.md` | none | No status; no evidence either way | LOW | 6 (0) |
| `current/proposal_unify_member_lookup_paths.md` | `current/proposals/closed/proposal_unify_member_lookup_paths.md` | Superseded | Doc says superseded 2026-09-24 | HIGH | 11 (0) |
| `current/proposal_universal_symbol_resolver.md` | `current/proposals/proposal_universal_symbol_resolver.md` | none | Mostly landed 2026-09-27 but still named in TODO | LOW | 28 (5) |
| `current/reference_known_failing_tests.md` | `current/references/reference_known_failing_tests.md` | CURRENT | Status CURRENT | HIGH | 6 (0) |
| `current/reference_lmstudio_loaded_models_endpoint.md` | `current/references/reference_lmstudio_loaded_models_endpoint.md` | none | Living lookup | MED | 2 (0) |
| `current/reference_lmstudio_reasoning_budget_message.md` | `current/references/reference_lmstudio_reasoning_budget_message.md` | none | Living lookup | MED | 0 (0) |
| `current/reference_log_sources_and_cross_referencing.md` | `current/references/reference_log_sources_and_cross_referencing.md` | none | Living lookup, updated 2026-10-09 | MED | 7 (0) |
| `current/reference_model_eval_procedure.md` | `current/references/reference_model_eval_procedure.md` | none | Living procedure | MED | 7 (0) |
| `current/reference_parse_agent_log_script.md` | `current/references/reference_parse_agent_log_script.md` | none | Living script doc; script fixed 2026-11-07 | MED | 6 (0) |
| `current/reference-code-file-write-paths-v1.md` | `current/references/reference_code_file_write_paths.md` | CURRENT | Status CURRENT; rename drops -v1 | HIGH | 25 (2) |
| `current/reference-roslynsentinel-mcp-launch-v1.md` | `current/references/reference_roslynsentinel_mcp_launch.md` | Living | Status Living reference; rename | HIGH | 6 (0) |
| `current/reference-vscode-control-script-v1.md` | `current/references/reference_vscode_control_script.md` | Living | Status Living reference; rename | HIGH | 5 (0) |
| `current/roslyn-duplication-audit-v1.md` | `current/findings/closed/finding_roslyn_duplication_audit.md` | none | Audit; CLOSED.md cites it; actions executed | MED | 14 (0) |
| `current/sequential_edit_habit_vs_compiler_checks_theory.md` | `current/evals/closed/eval_sequential_edit_habit_vs_compiler_checks_theory.md` | none | Theory from eval review; fixes landed 2026-09-05 | LOW | 2 (1) |
| `current/test_result_archiver_added.md` | `current/references/reference_model_testing_results_archiver.md` | IMPLEMENTED | IMPLEMENTED; describes live archiver | MED | 0 (0) |
| `current/tool-terminology-refinement-reference-v1.md` | `current/references/reference_tool_terminology_refinement.md` | none | Reference; items still open in TODO | MED | 6 (0) |

### docs/current/specs - 1 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/specs/spec-applydiff-subagent-wrapper-v1.md` | `current/plans/closed/plan_applydiff_subagent_wrapper.md` | none | Option A wrapper never built; ApplyDiff behaviour changed | LOW | 0 (0) |

### docs/current/upstream - 2 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/upstream/UNFINISHED_FEATURES.md` | `current/archive/unfinished_features.md` | All | Frozen 2026-05-12 backlog; pre-taxonomy | MED | 8 (0) |
| `current/upstream/UNFINISHED.md` | `current/archive/unfinished.md` | none | Frozen 2026-05 backlog; pre-taxonomy | MED | 5 (0) |

### docs/current/design (existing folder) - 1 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/design/searchsolutiontext_dual_mode_search.md` | `current/design/closed/design_searchsolutiontext_dual_mode_search.md` | none | Tool replaced by Search; CLOSED.md cites it | MED | 2 (1) |

### docs/current/blockers (open folder) - 2 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/blockers/blocking_error_subagent_invalidoperationexception_no_detail.md` | `current/blockers/closed/blocking_error_subagent_invalidoperationexception_no_detail.md` | OPEN | OPEN but cause found; CLOSED.md has later SubAgent fixes | LOW | 0 (0) |
| `current/blockers/blocking_error_toolset_enable_not_visible_in_client_tool_list.md` | `current/blockers/closed/blocking_error_toolset_enable_not_visible_in_client_tool_list.md` | NOT REPRODUCED | NOT REPRODUCED 2026-11-05 | MED | 4 (0) |

### docs/current/findings (open folder) - 17 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/findings/20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Advanced.md` | `current/findings/finding_comprehensive_review_tests_advanced.md` | OPEN | Status OPEN; rename | MED | 0 (0) |
| `current/findings/20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Asyncify.md` | `current/findings/finding_comprehensive_review_tests_asyncify.md` | OPEN | Status OPEN; rename | MED | 0 (0) |
| `current/findings/20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Basic.md` | `current/findings/finding_comprehensive_review_tests_basic.md` | OPEN | Status OPEN; rename | MED | 0 (0) |
| `current/findings/20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Battery.Advanced.md` | `current/findings/finding_comprehensive_review_tests_battery_advanced.md` | OPEN | Status OPEN; rename | MED | 0 (0) |
| `current/findings/20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Battery.Basic.md` | `current/findings/finding_comprehensive_review_tests_battery_basic.md` | OPEN | Status OPEN; rename | MED | 0 (0) |
| `current/findings/20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.md` | `current/findings/finding_comprehensive_review_tests_core.md` | OPEN | No Status line; audit of RoslynSentinel.Tests; rename | MED | 0 (0) |
| `current/findings/20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Server.md` | `current/findings/finding_comprehensive_review_tests_server.md` | OPEN | Status OPEN; rename | MED | 0 (0) |
| `current/findings/20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Tools.Advanced.md` | `current/findings/finding_comprehensive_review_tests_tools_advanced.md` | OPEN | Status OPEN; rename | MED | 0 (0) |
| `current/findings/20261020-Comprehensive-Review_Tests.RoslynSentinel.Tests.Tools.Basic.md` | `current/findings/finding_comprehensive_review_tests_tools_basic.md` | OPEN | Status OPEN; rename | MED | 0 (0) |
| `current/findings/finding_blastradius_failure_reported_as_success.md` | `current/findings/closed/finding_blastradius_failure_reported_as_success.md` | FIXED | Status FIXED 2026-10-04 | HIGH | 2 (1) |
| `current/findings/finding_build_offload_hides_error_count.md` | `current/findings/closed/finding_build_offload_hides_error_count.md` | RESOLVED | Status RESOLVED 2026-10-09 | HIGH | 3 (0) |
| `current/findings/finding_dogfood_hook_blocks_markdown_write_that_mentions_cs.md` | `current/findings/closed/finding_dogfood_hook_blocks_markdown_write_that_mentions_cs.md` | FIXED | Status FIXED 2026-10-24 | HIGH | 2 (1) |
| `current/findings/finding_dogfood_hook_matches_cs_in_scratchpad_commands.md` | `current/findings/closed/finding_dogfood_hook_matches_cs_in_scratchpad_commands.md` | SUPERSEDED | Status SUPERSEDED 2026-10-24 | HIGH | 2 (0) |
| `current/findings/finding_engine_result_shapes_primitives_and_wrapper_usage.md` | `current/findings/closed/finding_engine_result_shapes_primitives_and_wrapper_usage.md` | DECISION | DECISION adopted; IResultStatus plan DONE 2026-10-11 | MED | 6 (0) |
| `current/findings/finding_member_add_member_flattens_expression_body_continuation_indent.md` | `current/findings/closed/finding_member_add_member_flattens_expression_body_continuation_indent.md` | NOT REPRODUCED | NOT REPRODUCED 2026-11-05 | HIGH | 3 (1) |
| `current/findings/finding_result_status_vocabularies_fragmented.md` | `current/findings/closed/finding_result_status_vocabularies_fragmented.md` | FIXED | Status FIXED 2026-10-11 | HIGH | 4 (0) |
| `current/findings/finding_solution_path_lost_across_server_restart.md` | `current/findings/closed/finding_solution_path_lost_across_server_restart.md` | RESOLVED | Status RESOLVED 2026-10-08 | HIGH | 2 (1) |

### docs/current/ideas (open folder) - 4 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/ideas/editlines-and-anchor-attributes.md` | `current/ideas/idea_editlines_and_anchor_attributes.md` | parked | Status parked; rename | HIGH | 0 (0) |
| `current/ideas/external-drift-hard-blocker.md` | `current/ideas/closed/idea_external_drift_hard_blocker.md` **COLLISION** | finalized | ExternalFileDrift tool shipped; obsolete memory says implemented | MED | 25 (9) |
| `current/ideas/server_telemetry_idea.md` | `current/ideas/closed/idea_server_telemetry.md` | IMPLEMENTED | Status IMPLEMENTED 2026-11-05 | HIGH | 4 (0) |
| `current/ideas/tool_attribution_idea.md` | `current/ideas/idea_tool_attribution.md` | raised | Raised, not implemented; rename | HIGH | 4 (0) |

### docs/current/issues (open folder) - 1 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/issues/issue_battery_tests_leak_files.md` | `current/issues/closed/issue_battery_tests_leak_files.md` | OPEN | OPEN 2026-10-20; fixed by Battery reorg proposal 2026-10-21 | LOW | 5 (0) |

### docs/current/plans (open folder) - 48 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/plans/Plan — Replace full MSBuild reload.md` | `current/plans/closed/plan_replace_full_msbuild_reload.md` | none | No status; reload visibility work shipped 2026-11-05 | LOW | 0 (0) |
| `current/plans/plan_blocking_solution_load_and_build_result_shape.md` | `current/plans/closed/plan_blocking_solution_load_and_build_result_shape.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-10 | HIGH | 5 (0) |
| `current/plans/plan_constructor_parameter_retype_cascade.md` | `current/plans/closed/plan_constructor_parameter_retype_cascade.md` | none | Text: Implemented 2026-10-01; no status line | MED | 0 (0) |
| `current/plans/plan_cref_qualifier_helper_and_tool.md` | `current/plans/closed/plan_cref_qualifier_helper_and_tool.md` | DONE | Status DONE 2026-10-15 | HIGH | 6 (2) |
| `current/plans/plan_eol_policy_and_write_chokepoint.md` | `current/plans/closed/plan_eol_policy_and_write_chokepoint.md` | DONE | DONE; follow-ups live in TODO | MED | 17 (7) |
| `current/plans/plan_external_file_drift_tool_and_halt_stamping.md` | `current/plans/closed/plan_external_file_drift_tool_and_halt_stamping.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-10 | HIGH | 13 (3) |
| `current/plans/plan_extract_automatic_circuit_breaker.md` | `current/plans/closed/plan_extract_automatic_circuit_breaker.md` | none | No status; IAutomaticCircuitBreaker exists | MED | 3 (0) |
| `current/plans/plan_extract_manual_circuit_breaker.md` | `current/plans/closed/plan_extract_manual_circuit_breaker.md` | none | No status; sibling extraction done | MED | 6 (0) |
| `current/plans/plan_extract_rate_limiter.md` | `current/plans/closed/plan_extract_rate_limiter.md` | none | No status; IRateLimiter exists | MED | 2 (1) |
| `current/plans/plan_extract_symbol_resolver.md` | `current/plans/closed/plan_extract_symbol_resolver.md` | none | No status; SymbolResolver class exists | MED | 7 (0) |
| `current/plans/plan_extract_unrecoverable_circuit_breaker.md` | `current/plans/closed/plan_extract_unrecoverable_circuit_breaker.md` | none | No status; sibling extraction done | MED | 2 (1) |
| `current/plans/plan_file_normalization_tool.md` | `current/plans/closed/plan_file_normalization_tool.md` | PARTLY SUPERSEDED | PARTLY SUPERSEDED by EOL plan 2026-10-26 | LOW | 6 (0) |
| `current/plans/plan_git_tool_listed_scope_and_shell_parity.md` | `current/plans/closed/plan_git_tool_listed_scope_and_shell_parity.md` | IMPLEMENTED | Status IMPLEMENTED 2026-09-30 | HIGH | 2 (0) |
| `current/plans/plan_git_tool_tag_stash_worktree_hunks.md` | `current/plans/closed/plan_git_tool_tag_stash_worktree_hunks.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-10 | HIGH | 3 (1) |
| `current/plans/plan_launcher_autoload_solution.md` | `current/plans/closed/plan_launcher_autoload_solution.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-09 | HIGH | 2 (1) |
| `current/plans/plan_linebefore_lineafter_single_line_contract.md` | `current/plans/closed/plan_linebefore_lineafter_single_line_contract.md` | none | No status; lineBefore/lineAfter live; unverified | LOW | 4 (0) |
| `current/plans/plan_mcp_tool_experience_journal.md` | `current/plans/closed/plan_mcp_tool_experience_journal.md` | IN | IN PROGRESS 2026-10-01 but journal is in daily use | LOW | 6 (0) |
| `current/plans/plan_mutating_and_test_tool_result_noise.md` | `current/plans/closed/plan_mutating_and_test_tool_result_noise.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-08 | HIGH | 7 (1) |
| `current/plans/plan_mutating_tools_compact_result_default.md` | `current/plans/closed/plan_mutating_tools_compact_result_default.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-08 | HIGH | 9 (1) |
| `current/plans/plan_read_block_http_launcher_retention_fileinfo.md` | `current/plans/closed/plan_read_block_http_launcher_retention_fileinfo.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-09 | HIGH | 2 (0) |
| `current/plans/plan_read_tool_inline_limits_and_no_solution_reads.md` | `current/plans/closed/plan_read_tool_inline_limits_and_no_solution_reads.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-08 | HIGH | 0 (0) |
| `current/plans/plan_refactoring_gaps_callsites_static_testoutput.md` | `current/plans/closed/plan_refactoring_gaps_callsites_static_testoutput.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-09 | HIGH | 0 (0) |
| `current/plans/plan_remove_dead_symbol_session_check.md` | `current/plans/closed/plan_remove_dead_symbol_session_check.md` | none | No status; sibling extract plan done | LOW | 8 (0) |
| `current/plans/plan_rename_locator_step1_encode_decode.md` | `current/plans/closed/plan_rename_locator_step1_encode_decode.md` | none | SnippetSymbolLocator exists; step file of rename plan | MED | 2 (1) |
| `current/plans/plan_rename_locator_step2_locate_symbol_snippet_mode.md` | `current/plans/closed/plan_rename_locator_step2_locate_symbol_snippet_mode.md` | none | Step file of rename-locator plan; built | MED | 2 (1) |
| `current/plans/plan_rename_locator_step3_rename_symbol_decode.md` | `current/plans/closed/plan_rename_locator_step3_rename_symbol_decode.md` | none | Step file of rename-locator plan; built | MED | 2 (1) |
| `current/plans/plan_rename_symbol_snippet_locator.md` | `current/plans/closed/plan_rename_symbol_snippet_locator.md` | none | SnippetSymbolLocator exists | MED | 2 (1) |
| `current/plans/plan_replacesnippet_whitespace_tolerant_match.md` | `current/plans/closed/plan_replacesnippet_whitespace_tolerant_match.md` | none | No status; whitespace tolerance shipped | LOW | 11 (1) |
| `current/plans/plan_runtest_failure_projection.md` | `current/plans/closed/plan_runtest_failure_projection.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-17 | HIGH | 0 (0) |
| `current/plans/plan_scoped_operation_ledger.md` | `current/plans/closed/plan_scoped_operation_ledger.md` | none | Ledger engine built | MED | 21 (2) |
| `current/plans/plan_search_exclude_test_projects_by_default.md` | `current/plans/closed/plan_search_exclude_test_projects_by_default.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-10 | HIGH | 2 (0) |
| `current/plans/plan_search_kind_mode_query_filter.md` | `current/plans/closed/plan_search_kind_mode_query_filter.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-08 | HIGH | 4 (0) |
| `current/plans/plan_semantic_find_replace.md` | `current/plans/closed/plan_semantic_find_replace.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-04 | HIGH | 0 (0) |
| `current/plans/plan_session_halt_recovery_and_git_gaps.md` | `current/plans/closed/plan_session_halt_recovery_and_git_gaps.md` | PARTIALLY IMPLEMENTED | PARTIALLY IMPLEMENTED; rest done by drift-tool plan | MED | 18 (0) |
| `current/plans/plan_shared_json_serializer_options.md` | `current/plans/closed/plan_shared_json_serializer_options.md` | IMPLEMENTED | Status IMPLEMENTED 2026-09-24 | HIGH | 3 (3) |
| `current/plans/plan_shared_result_status_interface.md` | `current/plans/closed/plan_shared_result_status_interface.md` | DONE | Status DONE 2026-10-11 | HIGH | 11 (0) |
| `current/plans/plan_split_refactoring_engine.md` | `current/plans/closed/plan_split_refactoring_engine.md` | none | No status; StructuralRefactoringEngine exists | LOW | 2 (1) |
| `current/plans/plan_split_workspace_refactoring_tools_for_di.md` | `current/plans/closed/plan_split_workspace_refactoring_tools_for_di.md` | none | Status updates say complete; CLOSED.md cites it | MED | 36 (12) |
| `current/plans/plan_stateless_http_toolset_enablement.md` | `current/plans/closed/plan_stateless_http_toolset_enablement.md` | DRAFT | DRAFT 2026-10-11; HTTP copy since retired | LOW | 2 (1) |
| `current/plans/plan_subagent_tool_implementation.md` | `current/plans/closed/plan_subagent_tool_implementation.md` | none | SubAgent shipped; CLOSED.md cites it | MED | 2 (0) |
| `current/plans/plan_tool_call_echo.md` | `current/plans/closed/plan_tool_call_echo.md` | approved | Approved 2026-10-01; toolCall envelope is live | MED | 3 (3) |
| `current/plans/plan_typed_large_result_offload.md` | `current/plans/closed/plan_typed_large_result_offload.md` | SUPERSEDED | Status SUPERSEDED 2026-11-02 | HIGH | 9 (0) |
| `current/plans/plan_wire_relative_paths_backstop_filter.md` | `current/plans/closed/plan_wire_relative_paths_backstop_filter.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-08 | HIGH | 6 (4) |
| `current/plans/plan_zero_match_is_not_an_error.md` | `current/plans/closed/plan_zero_match_is_not_an_error.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-08 | HIGH | 3 (0) |
| `current/plans/plan-9b-model-test-step1.md` | `current/evals/closed/eval_9b_model_test_step1.md` | none | Model-test step prompt, not a repo plan | MED | 2 (0) |
| `current/plans/plan-9b-model-test-step2.md` | `current/evals/closed/eval_9b_model_test_step2.md` | none | Model-test step prompt, not a repo plan | MED | 15 (3) |
| `current/plans/plan-manual-selfrun-20260914-remediation-v1.md` | `current/plans/closed/plan_manual_selfrun_20260914_remediation.md` | none | Phases done per CLOSED.md | MED | 0 (0) |
| `current/plans/plan-open-blockers-remediation-v1.md` | `current/plans/closed/plan_open_blockers_remediation.md` | title:open | Phases marked DONE; CLOSED.md cites it | HIGH | 3 (0) |

### docs/current/proposals (open folder) - 13 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `current/proposals/proposal_battery_test_reorganisation_in_memory_and_shared_sources.md` | `current/proposals/closed/proposal_battery_test_reorganisation_in_memory_and_shared_sources.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-21 | HIGH | 6 (0) |
| `current/proposals/proposal_build_full_build_ignores_project_scope.md` | `current/proposals/closed/proposal_build_full_build_ignores_project_scope.md` | PROPOSED | PROPOSED but CLOSED.md lists it | LOW | 4 (0) |
| `current/proposals/proposal_central_large_result_offload.md` | `current/proposals/closed/proposal_central_large_result_offload.md` | SUPERSEDED | Status SUPERSEDED 2026-11-02 | HIGH | 11 (0) |
| `current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md` | `current/proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-19 | HIGH | 34 (25) |
| `current/proposals/proposal_load_solution_hint_for_unrestored_project.md` | `current/proposals/closed/proposal_load_solution_hint_for_unrestored_project.md` | PROPOSED | PROPOSED but CLOSED.md lists it | LOW | 11 (0) |
| `current/proposals/proposal_movemember_keep_delegator_and_same_class_calls.md` | `current/proposals/closed/proposal_movemember_keep_delegator_and_same_class_calls.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-18 | HIGH | 6 (1) |
| `current/proposals/proposal_remove_ambient_solution_root.md` | `current/proposals/closed/proposal_remove_ambient_solution_root.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-02 | HIGH | 0 (0) |
| `current/proposals/proposal_search_exclude_test_projects_by_default.md` | `current/proposals/closed/proposal_search_exclude_test_projects_by_default.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-10 | HIGH | 2 (1) |
| `current/proposals/proposal_shared_result_status_interface.md` | `current/proposals/closed/proposal_shared_result_status_interface.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-11 | HIGH | 8 (1) |
| `current/proposals/proposal_side_stores_never_inline.md` | `current/proposals/closed/proposal_side_stores_never_inline.md` | IMPLEMENTED | Status IMPLEMENTED 2026-11-07 | HIGH | 11 (9) |
| `current/proposals/proposal_snippet_match_selectors_withinmember_matchline.md` | `current/proposals/closed/proposal_snippet_match_selectors_withinmember_matchline.md` | Phases | Phases 1-3 done; phase 4 is a decision, not work | MED | 4 (0) |
| `current/proposals/proposal_syntax_target_resolver_extraction.md` | `current/proposals/closed/proposal_syntax_target_resolver_extraction.md` | IMPLEMENTED | IMPLEMENTED; follow-ups live in TODO | HIGH | 5 (0) |
| `current/proposals/proposal_tool_hint_builder.md` | `current/proposals/closed/proposal_tool_hint_builder.md` | IMPLEMENTED | Status IMPLEMENTED 2026-10-25 | HIGH | 3 (0) |

### docs/obsolete (root) - 58 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `obsolete/Asyncify-Audit-Locks_v1_agent.md` | `current/archive/asyncify_audit_locks_agent.md` | none | Agent prompt for another repo; frozen | MED | 0 (0) |
| `obsolete/codebase-consistency-audit-v1.md` | `current/findings/closed/finding_codebase_consistency_audit.md` | none | Audit; actions executed later | MED | 3 (2) |
| `obsolete/design_applyunifieddiff_replace_snippet_v1.md` | `current/design/closed/design_applyunifieddiff_replace_snippet.md` | drafted | ReplaceSnippet built since; design drafted 2026-09-08 | MED | 12 (3) |
| `obsolete/design_createfile_tool_v1.md` | `current/design/closed/design_createfile_tool.md` | Implemented | Status Implemented 2026-09-09 (historical) | HIGH | 0 (0) |
| `obsolete/finding_battery_suite_intermittent_hang_or_crash.md` | `current/findings/closed/finding_battery_suite_intermittent_hang_or_crash.md` | OPEN | OPEN not reproduced; Battery suite reorganised since | LOW | 0 (0) |
| `obsolete/finding_git_tool_array_param_and_invalid_operation_crash.md` | `current/findings/closed/finding_git_tool_array_param_and_invalid_operation_crash.md` | confirmed | Not fixed per status; remediation plan since ran | LOW | 4 (1) |
| `obsolete/finding_git_tool_worktree_resolution_wrong_repo_root.md` | `current/findings/closed/finding_git_tool_worktree_resolution_wrong_repo_root.md` | confirmed | Pointer to a resolved blocker doc | MED | 3 (0) |
| `obsolete/finding_member_typedkind_missing_method_and_type_add_paths.md` | `current/findings/closed/finding_member_typedkind_missing_method_and_type_add_paths.md` | confirmed | Not fixed per status; remediation plan since ran | LOW | 4 (0) |
| `obsolete/implementation-plan-batch-first-v1.md` | `current/plans/closed/plan_batch_first.md` | none | Phases marked DONE (2026-05) | MED | 0 (0) |
| `obsolete/implementation-plan-consolidation-v1.md` | `current/plans/closed/plan_consolidation.md` | none | 2026-05 consolidation plan; pre-taxonomy | MED | 0 (0) |
| `obsolete/new-file-validation-gap-scope.md` | `current/findings/closed/finding_new_file_validation_gap_scope.md` | none | CLOSED.md cites it; memory: was edit-ordering | MED | 2 (0) |
| `obsolete/plan-build-verification-tool-v1.md` | `current/plans/closed/plan_build_verification_tool.md` | none | Build tool exists; CLOSED.md cites it | HIGH | 3 (0) |
| `obsolete/plan-migration-scan-result-handling-v2.md` | `current/plans/closed/plan_migration_scan_result_handling.md` | none | 2026-05 plan for retired migration tools | MED | 2 (1) |
| `obsolete/plan-normalize-whitespace-full-sweep-v1.md` | `current/plans/closed/plan_normalize_whitespace_full_sweep.md` | none | Whitespace family deleted 2026-10-19 | MED | 2 (1) |
| `obsolete/plan-symbol-tool-hardening-v1.md` | `current/plans/closed/plan_symbol_tool_hardening.md` | none | Text says already implemented; CLOSED.md cites it | HIGH | 15 (0) |
| `obsolete/plan-tool-disambiguation-remediation-v1.md` | `current/plans/closed/plan_tool_disambiguation_remediation.md` | none | contextSnippet disambiguation shipped | MED | 7 (1) |
| `obsolete/plan-tool-disambiguation-survey-v1.md` | `current/plans/closed/plan_tool_disambiguation_survey.md` | none | Survey plan; executed | MED | 2 (1) |
| `obsolete/plan-tool-rename-v1.md` | `current/plans/closed/plan_tool_rename.md` | none | VERIFICATION_TABLE says Implementation Complete | HIGH | 7 (0) |
| `obsolete/project_applydiff_capable_agent_feedback.md` | `current/evals/closed/eval_applydiff_capable_agent_feedback.md` | none | Memory mirror; eval feedback record | MED | 2 (0) |
| `obsolete/project_cs0122_fix_confirmed_2run_batch.md` | `current/evals/closed/eval_cs0122_fix_confirmed_2run_batch.md` | none | Memory mirror; run result | MED | 2 (1) |
| `obsolete/project_cs0122_lookup_helper_proposal.md` | `current/proposals/closed/proposal_cs0122_lookup_helper.md` | none | Implemented d4397b3 per memory index | HIGH | 1 (1) |
| `obsolete/project_dispose_waithandle_deadlock_found.md` | `current/findings/closed/finding_dispose_waithandle_deadlock.md` | none | Memory mirror; defect found and fixed | MED | 5 (2) |
| `obsolete/project_external_drift_hard_blocker_idea.md` | `current/ideas/closed/idea_external_drift_hard_blocker.md` **COLLISION** | none | Implemented 2026-09-01; duplicates the ideas/ doc | MED | 0 (0) |
| `obsolete/project_git_status_rev_parse_hang_confirmed.md` | `current/findings/closed/finding_git_status_rev_parse_hang.md` | none | Fix applied 2026-09-01 | HIGH | 0 (0) |
| `obsolete/project_model_eval_streaming_responses_api.md` | `current/evals/closed/eval_model_eval_streaming_responses_api.md` | none | Memory mirror; confirmed working | MED | 0 (0) |
| `obsolete/project_modeleval_testhost_hang_gotcha.md` | `current/evals/closed/eval_modeleval_testhost_hang_gotcha.md` | none | Memory mirror; superseded by memory testhost gotchas | MED | 0 (0) |
| `obsolete/project_planimplementverify_5run_result_2.md` | `current/evals/closed/eval_planimplementverify_5run_result_2.md` | none | Memory mirror; run result | MED | 0 (0) |
| `obsolete/project_planimplementverify_5run_result.md` | `current/evals/closed/eval_planimplementverify_5run_result.md` | none | Memory mirror; run result | MED | 4 (0) |
| `obsolete/project_readfile_createfile_disk_fallback_fixed.md` | `current/findings/closed/finding_readfile_createfile_disk_fallback.md` | title:fixed | Title says fixed 2026-09-02 | HIGH | 2 (0) |
| `obsolete/project_readfile_createfile_path_inconsistency_bug.md` | `current/findings/closed/finding_readfile_createfile_path_inconsistency.md` | none | Memory mirror; bug fixed per sibling doc | MED | 6 (3) |
| `obsolete/project_scriptedplan_5run_result.md` | `current/evals/closed/eval_scriptedplan_5run_result.md` | none | Memory mirror; run result | MED | 0 (0) |
| `obsolete/project_seed_investigation_result.md` | `current/evals/closed/eval_seed_investigation_result.md` | none | Memory mirror; investigation result | MED | 0 (0) |
| `obsolete/project-handoff-roslyn-sentinel-2026-06-04.md` | `current/archive/project_handoff_roslyn_sentinel_2026_06_04.md` | none | Handoff, 2026-06-04 | HIGH | 0 (0) |
| `obsolete/PROJECT.md` | `current/archive/project.md` | none | Pre-taxonomy project doc | HIGH | 2 (1)* |
| `obsolete/propagate-cancellation-token-tools-spec-v1.md` | `current/plans/closed/plan_propagate_cancellation_token_tools_spec.md` | none | 2026-05 spec for retired tools | MED | 0 (0) |
| `obsolete/proposal_nonblocking_validation_mode.md` | `current/proposals/closed/proposal_nonblocking_validation_mode.md` | Superseded | Superseded 2026-09-28 by staged-writes proposal | HIGH | 16 (0) |
| `obsolete/PROPOSED_TOOLS.md` | `current/archive/proposed_tools.md` | none | 2026-05 tool wish list; pre-taxonomy | HIGH | 2 (0) |
| `obsolete/README.md` | `current/archive/readme_2026_05.md` | none | Old product README; pre-taxonomy | HIGH | 86 (66)* |
| `obsolete/remaining-tasks-sentinelqualitytools-v1.md` | `current/archive/remaining_tasks_sentinelqualitytools.md` | none | 2026-05 task list; frozen | MED | 0 (0) |
| `obsolete/REQUIREMENTS.md` | `current/archive/requirements.md` | none | Pre-taxonomy | HIGH | 2 (1) |
| `obsolete/ROADMAP.md` | `current/archive/roadmap.md` | none | Pre-taxonomy | HIGH | 2 (0) |
| `obsolete/roslyn-sentinel-consolidation-impl-spec-v1.md` | `current/plans/closed/plan_roslyn_sentinel_consolidation_impl_spec.md` | none | 2026-05 consolidation spec; pre-taxonomy | MED | 3 (0) |
| `obsolete/roslyn-sentinel-engine-consolidation-impl-spec-v1.md` | `current/plans/closed/plan_roslyn_sentinel_engine_consolidation_impl_spec.md` | none | 2026-06 engine consolidation spec | MED | 0 (0) |
| `obsolete/roslyn-sentinel-files-summary-2026-06-04.md` | `current/archive/roslyn_sentinel_files_summary_2026_06_04.md` | none | File summary, 2026-06-04 | HIGH | 3 (0) |
| `obsolete/roslyn-sentinel-tools-review-v1.md` | `current/archive/roslyn_sentinel_tools_review.md` | Substantive | 2026-05 tool review; frozen | HIGH | 2 (1) |
| `obsolete/spec-batch-first-tool-architecture-v1.md` | `current/plans/closed/plan_batch_first_tool_architecture.md` | none | 2026-05 spec; implementation plan marks it DONE | MED | 3 (0) |
| `obsolete/spec-find-namespace-path-mismatches-v1.md` | `current/proposals/closed/proposal_find_namespace_path_mismatches.md` | none | Spec of a proposed tool, never listed in tool table | MED | 2 (0) |
| `obsolete/spec-migration-scan-result-handling-v2.md` | `current/plans/closed/plan_migration_scan_result_handling_spec.md` | none | 2026-05 spec; paired with its plan (name collides with its plan; _spec suffix) | MED | 6 (0) |
| `obsolete/spec-migration-scan-summary-patch-v1.md` | `current/plans/closed/plan_migration_scan_summary_patch.md` | none | 2026-06 spec for retired migration tools | MED | 2 (0) |
| `obsolete/spec-operation-outcome-routing-v1.md` | `current/plans/closed/plan_operation_outcome_routing.md` | Ready to | "Ready to implement" in 2026; later result-status work supersedes | LOW | 5 (0) |
| `obsolete/spec-scoped-documentation-tools-and-hardening-v1.md` | `current/plans/closed/plan_scoped_documentation_tools_and_hardening.md` | none | 2026-05 spec; pre-taxonomy | MED | 0 (0) |
| `obsolete/spec-symbol-handle-impl-v1.md` | `current/plans/closed/plan_symbol_handle_impl.md` | Ready for | 2026-06 spec; docCommentId wire in use | MED | 2 (1) |
| `obsolete/spec-tool-description-compression-v1.md` | `current/plans/closed/plan_tool_description_compression.md` | none | 2026-05 spec; later schema-cost work supersedes | MED | 3 (0) |
| `obsolete/STATE.md` | `current/archive/state.md` | Starting | Pre-taxonomy | HIGH | 0 (0)* |
| `obsolete/TOOL_DOCUMENTATION.md` | `current/archive/tool_documentation.md` | none | Generated 2026-05-12 tool docs; stale | HIGH | 3 (0) |
| `obsolete/tool_review_v1.md` | `current/archive/tool_review.md` | none | Old tool review; frozen | HIGH | 2 (1) |
| `obsolete/tool-disambiguation-survey-v1.md` | `current/findings/closed/finding_tool_disambiguation_survey.md` | none | Survey 2026-08-18; remediation shipped | MED | 9 (0) |
| `obsolete/VERIFICATION_TABLE.md` | `current/archive/verification_table.md` | Implementation | 2026-06 tool-rename verification; frozen | HIGH | 0 (0) |

### docs/obsolete/blockers - 33 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `obsolete/blockers/blocking_error_applydiff_silent_noop_false_success.md` | `current/blockers/closed/blocking_error_applydiff_silent_noop_false_success.md` | FIXED | Status FIXED 2026-09-01 | HIGH | 6 (2) |
| `obsolete/blockers/blocking_error_buildps1_relocated_reporoot_path_math_broken.md` | `current/blockers/closed/blocking_error_buildps1_relocated_reporoot_path_math_broken.md` | RESOLVED | Status RESOLVED 2026-09-15 | HIGH | 2 (1) |
| `obsolete/blockers/blocking_error_changesignature_internal_roslyn_api.md` | `current/blockers/closed/blocking_error_changesignature_internal_roslyn_api.md` | CLOSED | Status CLOSED 2026-09-19 | HIGH | 8 (0) |
| `obsolete/blockers/blocking_error_findattributeusages_first_throws_on_unresolved_target.md` | `current/blockers/closed/blocking_error_findattributeusages_first_throws_on_unresolved_target.md` | OPEN - | OPEN but CLOSED.md lists it | MED | 4 (2) |
| `obsolete/blockers/blocking_error_findreferences_field_contextsnippet_resolves_to_ctorparam.md` | `current/blockers/closed/blocking_error_findreferences_field_contextsnippet_resolves_to_ctorparam.md` | FIXED | Status FIXED | HIGH | 1 (1) |
| `obsolete/blockers/blocking_error_formattinghelper_cs0103_missing_formatting_using.md` | `current/blockers/closed/blocking_error_formattinghelper_cs0103_missing_formatting_using.md` | AMENDED | AMENDED 2026-09-12; CLOSED.md cites it | MED | 3 (0) |
| `obsolete/blockers/blocking_error_getdiagnostics_10_phase3_diffhunkanalyzer.md` | `current/blockers/closed/blocking_error_getdiagnostics_10_phase3_diffhunkanalyzer.md` | none | Auto-filed run failure; CLOSED.md cites it | MED | 2 (1) |
| `obsolete/blockers/blocking_error_getlargeresult_typed_branch_reoffload_loop.md` | `current/blockers/closed/blocking_error_getlargeresult_typed_branch_reoffload_loop.md` | OPEN | OPEN; marshaller plan replaced the paging path | LOW | 8 (4) |
| `obsolete/blockers/blocking_error_git_commit_files_param_does_not_scope_commit.md` | `current/blockers/closed/blocking_error_git_commit_files_param_does_not_scope_commit.md` | FIXED | Status FIXED 2026-09-12 | HIGH | 2 (0) |
| `obsolete/blockers/blocking_error_git_requires_loaded_solution.md` | `current/blockers/closed/blocking_error_git_requires_loaded_solution.md` | PARTIALLY FIXED | PARTIALLY FIXED; remediation plan Phase 1 DONE | MED | 5 (0) |
| `obsolete/blockers/blocking_error_git_stage_scope_schema_stale_server.md` | `current/blockers/closed/blocking_error_git_stage_scope_schema_stale_server.md` | OPEN | OPEN informational; CLOSED.md cites it | MED | 6 (0) |
| `obsolete/blockers/blocking_error_git_status_timeout.md` | `current/blockers/closed/blocking_error_git_status_timeout.md` | none | No status; rev-parse hang fixed 2026-09-01 | MED | 5 (1) |
| `obsolete/blockers/blocking_error_git_tool_cannot_target_other_worktree.md` | `current/blockers/closed/blocking_error_git_tool_cannot_target_other_worktree.md` | OPEN | OPEN; Git now has repoPath param | MED | 2 (1) |
| `obsolete/blockers/blocking_error_git_tool_no_soft_reset_operation.md` | `current/blockers/closed/blocking_error_git_tool_no_soft_reset_operation.md` | OPEN | OPEN; Git reset (soft/mixed) exists now | HIGH | 2 (0) |
| `obsolete/blockers/blocking_error_list_tools_misreports_tool_surface.md` | `current/blockers/closed/blocking_error_list_tools_misreports_tool_surface.md` | OPEN | OPEN; plan says fixed by cd8c8a9 | MED | 5 (2) |
| `obsolete/blockers/blocking_error_live_server_stale_after_enum_addition.md` | `current/blockers/closed/blocking_error_live_server_stale_after_enum_addition.md` | OPEN | OPEN informational; CLOSED.md cites it | MED | 7 (0) |
| `obsolete/blockers/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md` | `current/blockers/closed/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md` | FIXED | Status FIXED | HIGH | 8 (3) |
| `obsolete/blockers/blocking_error_methodsignature_add_rejects_required_trailing_cancellationtoken.md` | `current/blockers/closed/blocking_error_methodsignature_add_rejects_required_trailing_cancellationtoken.md` | closed-upstream | Front matter status closed-upstream | HIGH | 2 (1) |
| `obsolete/blockers/blocking_error_movemember_nested_class_dependency_not_moved.md` | `current/blockers/closed/blocking_error_movemember_nested_class_dependency_not_moved.md` | FIXED | Status FIXED 2026-09-28 | HIGH | 6 (0) |
| `obsolete/blockers/blocking_error_persistentworkspacemanager_dispose_race_crashes_process.md` | `current/blockers/closed/blocking_error_persistentworkspacemanager_dispose_race_crashes_process.md` | FIXED | Status FIXED, commit 14f8229 | HIGH | 3 (1) |
| `obsolete/blockers/blocking_error_plan_step_unreachable_verification_gate.md` | `current/blockers/closed/blocking_error_plan_step_unreachable_verification_gate.md` | AMENDED | AMENDED; CLOSED.md cites it | MED | 4 (0) |
| `obsolete/blockers/blocking_error_replacesnippet_03_locate_symbol_snippet_mode.md` | `current/blockers/closed/blocking_error_replacesnippet_03_locate_symbol_snippet_mode.md` | none | Auto-filed run failure; run finished | MED | 0 (0) |
| `obsolete/blockers/blocking_error_runtest_01_baseline.md` | `current/blockers/closed/blocking_error_runtest_01_baseline.md` | none | Auto-filed run failure; CLOSED.md cites it | MED | 2 (1) |
| `obsolete/blockers/blocking_error_searchsolutiontext_02_phase1_types_and_engine_fix.md` | `current/blockers/closed/blocking_error_searchsolutiontext_02_phase1_types_and_engine_fix.md` | none | Auto-filed run failure; run finished | MED | 2 (0) |
| `obsolete/blockers/blocking_error_searchsolutiontext_11_final_verification.md` | `current/blockers/closed/blocking_error_searchsolutiontext_11_final_verification.md` | none | Auto-filed run failure; CLOSED.md cites it | MED | 2 (1) |
| `obsolete/blockers/blocking_error_self_inflicted_drift_halt_from_edit_tool_on_tracked_cs_file.md` | `current/blockers/closed/blocking_error_self_inflicted_drift_halt_from_edit_tool_on_tracked_cs_file.md` | none | Self-caused halt; CLOSED.md cites it | MED | 2 (0) |
| `obsolete/blockers/blocking_error_self_inflicted_write_tool_triggered_drift_halt.md` | `current/blockers/closed/blocking_error_self_inflicted_write_tool_triggered_drift_halt.md` | OPEN | OPEN self-caused; drift tool reworked since | MED | 2 (1) |
| `obsolete/blockers/blocking_error_session_halt_from_out_of_band_rm_mid_spike.md` | `current/blockers/closed/blocking_error_session_halt_from_out_of_band_rm_mid_spike.md` | LATCH CLEARED | Status LATCH CLEARED 2026-09-14 | HIGH | 4 (0) |
| `obsolete/blockers/blocking_error_sessionhalted_persists_after_stray_file_removed.md` | `current/blockers/closed/blocking_error_sessionhalted_persists_after_stray_file_removed.md` | OPEN | OPEN; drift tool reworked since | MED | 0 (0) |
| `obsolete/blockers/blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md` | `current/blockers/closed/blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md` | OPEN | OPEN; finding says FIXED 2026-09-19 | MED | 4 (2) |
| `obsolete/blockers/blocking_error_taskpolling_test_responseid_mismatch.md` | `current/blockers/closed/blocking_error_taskpolling_test_responseid_mismatch.md` | FIXED | Status FIXED 2026-09-20 | HIGH | 2 (1) |
| `obsolete/blockers/finding_agent_no_terminal_step8_deviation.md` | `current/findings/closed/finding_agent_no_terminal_step8_deviation.md` | informational | Informational; CLOSED.md cites it | MED | 2 (1) |
| `obsolete/blockers/finding_applydiff_size_threshold_local_model.md` | `current/findings/closed/finding_applydiff_size_threshold_local_model.md` | informational | Informational eval finding; CLOSED.md cites it | MED | 5 (2) |

### docs/obsolete/blockers/resolved - 9 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `obsolete/blockers/resolved/blocking_error_git_stage_ignores_untracked_files.md` | `current/blockers/closed/blocking_error_git_stage_ignores_untracked_files.md` | RESOLVED | Status RESOLVED 2026-09-14 | HIGH | 5 (0) |
| `obsolete/blockers/resolved/blocking_error_git_tool_commit_reports_clean_tree_worktree.md` | `current/blockers/closed/blocking_error_git_tool_commit_reports_clean_tree_worktree.md` | none | Already in resolved/ | HIGH | 7 (1) |
| `obsolete/blockers/resolved/blocking_error_model_loadsolution_wipes_fixture_state.md` | `current/blockers/closed/blocking_error_model_loadsolution_wipes_fixture_state.md` | RESOLVED | Status RESOLVED 2026-08-29 | HIGH | 0 (0) |
| `obsolete/blockers/resolved/blocking_error_readfile_scanid_threshold_mismatch.md` | `current/blockers/closed/blocking_error_readfile_scanid_threshold_mismatch.md` | none | Original status bug; in resolved/ | HIGH | 0 (0) |
| `obsolete/blockers/resolved/blocking_error_searchmode_literal_override_and_iserror_flag.md` | `current/blockers/closed/blocking_error_searchmode_literal_override_and_iserror_flag.md` | none | Already in resolved/ | HIGH | 5 (2) |
| `obsolete/blockers/resolved/blocking_error_searchsolutiontext_regex_warning_salience.md` | `current/blockers/closed/blocking_error_searchsolutiontext_regex_warning_salience.md` | none | Already in resolved/ | HIGH | 4 (0) |
| `obsolete/blockers/resolved/finding_planstep_run_398_A_tool_defects.md` | `current/evals/closed/eval_planstep_run_398_a_tool_defects.md` | RESOLVED | RESOLVED 2026-09-10; PlanStepRunner run report | HIGH | 2 (0) |
| `obsolete/blockers/resolved/finding_planstep_run_398_B_harness.md` | `current/evals/closed/eval_planstep_run_398_b_harness.md` | RESOLVED | RESOLVED 2026-09-10; PlanStepRunner run report | HIGH | 4 (0) |
| `obsolete/blockers/resolved/finding_planstep_run_398_C_plan_content.md` | `current/evals/closed/eval_planstep_run_398_c_plan_content.md` | RESOLVED | RESOLVED 2026-09-10; PlanStepRunner run report | HIGH | 2 (0) |

### docs/obsolete (step folder) - 1 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `obsolete/plan-eval-defect-remediation-v2-steps/` | `current/evals/closed/eval_defect_remediation_v2_steps/` | none | Step tree of the implemented v2 plan; move as a folder | MED | 17 (2) |

### docs/obsolete/plans - 6 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `obsolete/plans/plan-codebase-consistency-review-v1.md` | `current/plans/closed/plan_codebase_consistency_review.md` | none | Audit plan; executed | MED | 2 (1) |
| `obsolete/plans/plan-eval-defect-remediation-v2-steps.md` | `current/plans/closed/plan_eval_defect_remediation_v2_steps.md` | none | Pointer to step tree; merge into folder | MED | 4 (0) |
| `obsolete/plans/plan-eval-defect-remediation-v2.md` | `current/plans/closed/plan_eval_defect_remediation.md` | none | Plan implemented (run 398 and later) | MED | 6 (0) |
| `obsolete/plans/plan-orientation-breaker.md` | `current/plans/closed/plan_orientation_breaker.md` | none | Orientation breaker shipped (CLOSED.md era) | MED | 8 (4) |
| `obsolete/plans/plan-reason-parameter-rollout-v1.md` | `current/plans/closed/plan_reason_parameter_rollout.md` | none | reason param is mandatory on tools today | HIGH | 2 (1) |
| `obsolete/plans/plan-runtest-tool-v1.md` | `current/plans/closed/plan_runtest_tool.md` | none | RunTest tool exists | HIGH | 7 (1) |

### docs/obsolete/specs - 4 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `obsolete/specs/spec-bulk-comment-orchestrator-v1.md` | `current/plans/closed/plan_bulk_comment_orchestrator.md` | none | No BulkComment symbol; never built | MED | 2 (1) |
| `obsolete/specs/spec-eval-defect-remediation-v2.md` | `current/plans/closed/plan_eval_defect_remediation_spec.md` | Ready for | Spec behind implemented plan v2 (name collides with its plan; _spec suffix) | MED | 1 (0) |
| `obsolete/specs/spec-read-tool-metadata-envelope-v1.md` | `current/plans/closed/plan_read_tool_metadata_envelope.md` | none | GetMethodSource/GetFileOutline done 2026-08-27 | MED | 9 (3) |
| `obsolete/specs/spec-replace-throws-in-mcp-tools-v1.md` | `current/plans/closed/plan_replace_throws_in_mcp_tools.md` | none | CLOSED.md cites it; typed-throw work superseded | MED | 3 (0) |

### docs/research/testing - 22 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `research/testing/overnight-run-2026-08-30.md` | `current/evals/closed/eval_overnight_run_2026_08_30.md` | none | Overnight run summary | HIGH | 3 (2) |
| `research/testing/piv_batch2_8of10_new_high_2026_09_07.md` | `current/evals/closed/eval_piv_batch2_8of10_new_high_2026_09_07.md` | none | Batch result | HIGH | 0 (0) |
| `research/testing/project_disambiguated_prompt_n20_result.md` | `current/evals/closed/eval_disambiguated_prompt_n20_result.md` | none | Prompt-variant result | HIGH | 0 (0) |
| `research/testing/project_disambiguated_v3_result_solution_preloaded_line.md` | `current/evals/closed/eval_disambiguated_v3_result_solution_preloaded_line.md` | none | Prompt-variant result | HIGH | 0 (0) |
| `research/testing/project_granite42_8b_findings.md` | `current/evals/closed/eval_granite42_8b_findings.md` | none | Per-model findings | HIGH | 0 (0) |
| `research/testing/project_modeleval_fixture_test_suite_redesign.md` | `current/evals/closed/eval_modeleval_fixture_test_suite_redesign.md` **COLLISION** | none | IMPLEMENTED 2026-09-07; mirror of current/ doc | HIGH | 0 (0) |
| `research/testing/project_overnight_50run_sweep_2026_08_31.md` | `current/evals/closed/eval_overnight_50run_sweep_2026_08_31.md` | none | Sweep result | HIGH | 0 (0) |
| `research/testing/project_overnight_todo_run_2026_08_27.md` | `current/evals/closed/eval_overnight_todo_run_2026_08_27.md` | none | Run summary | HIGH | 0 (0) |
| `research/testing/project_plan_before_edit_disambiguated_v2_result.md` | `current/evals/closed/eval_plan_before_edit_disambiguated_v2_result.md` | none | Prompt-variant result | HIGH | 0 (0) |
| `research/testing/project_planimplementverify_0of20_root_causes_2026_09_02.md` | `current/evals/closed/eval_planimplementverify_0of20_root_causes_2026_09_02.md` | none | Root-cause record, fixed | HIGH | 0 (0) |
| `research/testing/project_planimplementverify_promptcontext_solution_preloaded.md` | `current/evals/closed/eval_planimplementverify_promptcontext_solution_preloaded.md` | none | Prompt fix record | HIGH | 0 (0) |
| `research/testing/project_reason_param_enforcement_result.md` | `current/evals/closed/eval_reason_param_enforcement_result.md` | none | A/B result | HIGH | 0 (0) |
| `research/testing/project_reason_param_reveals_toolchoice_and_selfcorrection.md` | `current/evals/closed/eval_reason_param_reveals_toolchoice_and_selfcorrection.md` | none | Run review | HIGH | 0 (0) |
| `research/testing/project_reasoning_divergence_not_recurring.md` | `current/evals/closed/eval_reasoning_divergence_not_recurring.md` | none | Run review | HIGH | 0 (0) |
| `research/testing/project_repeat_penalty_ab_test.md` | `current/evals/closed/eval_repeat_penalty_ab_test.md` | none | A/B result | HIGH | 0 (0) |
| `research/testing/project_replaceblockformatted_accessibility_cost_2026_09_05.md` | `current/evals/closed/eval_replaceblockformatted_accessibility_cost_2026_09_05.md` **COLLISION** | none | Shorter mirror of current/model_eval_* doc | HIGH | 0 (0) |
| `research/testing/project_run398_c1c2_closed_by_b1_side_effect.md` | `current/evals/closed/eval_run398_c1c2_closed_by_b1_side_effect.md` | none | Run record, closed | HIGH | 0 (0) |
| `research/testing/project_run398_defect_remediation_a1_a5.md` | `current/evals/closed/eval_run398_defect_remediation_a1_a5.md` | none | Run record, fixed 2026-09-10 | HIGH | 2 (1) |
| `research/testing/project_system_prompt_v1_result_and_modifymodifier_gap.md` | `current/evals/closed/eval_system_prompt_v1_result_and_modifymodifier_gap.md` | none | Prompt result | HIGH | 0 (0) |
| `research/testing/qwen36_35b_ladder_preferred_branch_extract_bug.md` | `current/evals/closed/eval_qwen36_35b_ladder_preferred_branch_extract_bug.md` | none | Batch finding, root-caused | HIGH | 2 (1) |
| `research/testing/README.md` | `current/archive/research_testing_readme.md` | none | Describes the folder being emptied | HIGH | 88 (66)* |
| `research/testing/run-summary-manual-selfrun-20260914-004605.md` | `current/evals/closed/eval_run_summary_manual_selfrun_20260914_004605.md` | none | Run summary; remediation plan done | HIGH | 2 (1) |

### docs/testing (step folders) - 2 rows

| Source | Target | Status found | Reason | Conf | Refs (cs) |
| --- | --- | --- | --- | --- | --- |
| `testing/plan-eval-defect-remediation-v2-steps-runner/` | `current/evals/closed/eval_defect_remediation_v2_steps_runner/` | none | PlanStepRunner variant of the implemented v2 plan; move as a folder | MED | 24 (1) |
| `testing/plan-rename-locator-steps-runner/` | `current/evals/closed/eval_rename_locator_steps_runner/` | none | PlanStepRunner steps of the built rename-locator plan; move as a folder | MED | 0 (0) |

## Already correctly filed (Source == Target, open)

Not in the table. `(LOW)` = open/closed call is uncertain and worth a glance. Names are inside the folder shown.

- Keepers: `current/TODO.md`, `current/CLOSED.md`, `current/DOCUMENTATION_INDEX.md`, `current/archive/closed_2026-08.md`, `current/archive/closed_2026-09.md`.
- `current/blockers/` (3): blocking_error_gitremote_not_callable_after_toolset_enable.md, blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md (LOW), blocking_error_replacesnippet_schema_invites_anchor_only_edits.md (LOW)
- `current/design/` (1): design_test_category_tagging_tool.md
- `current/findings/` (7): finding_anonymous_and_untyped_return_shapes.md (LOW), finding_batch_edit_errors_lose_tool_exception_code.md, finding_battery_basic_slow_test_classification.md, finding_compile_gate_rejection_omits_source_line.md, finding_engine_layer_anonymous_and_untyped_returns.md, finding_member_view_signature_truncated_at_attribute_line.md (LOW), finding_nochange_producer_classification.md (LOW)
- `current/ideas/` (5): idea_doc_id_commit_trailers_and_reconciliation.md, idea_member_addmember_batch_multi_declaration.md, idea_modifyenum_error_should_surface_implied_removals.md (LOW), idea_read_tool_metadata_envelope_followups.md, idea_using_directive_simplify_all_callers.md
- `current/issues/` (4): issue_movemember_open_followups.md (LOW), issue_path_comparison_case_sensitivity.md, issue_structural_refactoring_open_defects.md, issue_tool_hygiene_open_items.md
- `current/plans/` (12): plan_agent_tooling_hooks_and_small_ergonomics.md, plan_ambiguity_selected_hash.md, plan_document_changeset_type.md, plan_error_codes_follow_cause.md, plan_ilspy_derived_capabilities.md, plan_implementer_lanes_tiers_and_tool_gap_reporting.md, plan_in_memory_test_tier_for_battery.md, plan_read_ledger_stale_read_guard.md, plan_replacesnippet_cross_file_edit_guidance.md, plan_unknown_parameter_rejection_reduction.md, plan_wire_result_marshaller.md, plan_wrap_unwrapped_tool_returns_in_sentinel_call_tool_result.md
- `current/proposals/` (21): proposal_create_project_register_own_writes.md, proposal_cross_session_write_journal.md (LOW), proposal_dto_ownership_role_lineage.md, proposal_expose_dormant_codetransform_engines.md, proposal_ilspy_derived_capabilities.md, proposal_inline_method_redesign.md, proposal_inspectsymbol_source_aspect.md, proposal_instance_move_preview_position_map_classification.md, proposal_internal_whitespace_tolerance_contract_for_snippet_helpers.md, proposal_journal_digest_followup_decisions.md, proposal_parallel_change_deprecated_wrappers.md (LOW), proposal_reduce_tool_schema_token_cost.md, proposal_result_status_and_code_on_the_wire.md, proposal_runtest_tests_for_members.md, proposal_semantic_edit_impact_diagnostics.md, proposal_semantic_find_replace_construction_initializers_and_skip_echo.md, proposal_sequence_pager.md, proposal_throw_typed_exceptions_not_failure_results.md, proposal_tool_role_attribute.md, proposal_usage_based_core_split.md, proposal_workspace_reload_failure_visibility.md (LOW)
- `current/references/` (15): reference_architecture_map.md, reference_autonomous_run_conventions.md, reference_blocker_workflow.md, reference_changing_claude_lean_tools.md, reference_edit_tool_gotchas.md, reference_file_watcher_and_drift_detection.md, reference_filepath_schema_true_detection.md, reference_hook_bypass.md, reference_live_server_freshness.md, reference_not_found_recovery_ladder.md, reference_nunit5_test_conventions.md, reference_server_telemetry.md, reference_tool_comparison_harness.md, reference_tool_response_pipeline.md, reference_working_conventions.md

## Execution rules

- Run one step at a time; commit after the move, after the link rewrite, and after the .cs sweep.
- Do not touch `*/Worktree/` folders.

## Steps

### Step 1 - Pre-checks
- Run `ExternalFileDrift(operation: Status)`; move ONE Markdown file first (a LOW-risk row with 0 refs) and check that no SessionHalted latch appears. Moves of `.md` files are expected not to trip the .cs drift watcher; verify before the bulk move.
- Decide the `docs/research/` gitignore question (surprise 1) and the eval prefix; resolve the OPEN COLLISION rows above.

### Step 2 - Move script
- A `scripts/Move-DocsReorg.ps1` reads the table (Source, Target), creates `closed/` and `evals/` folders, uses `git mv` for tracked files and `Move-Item` for the untracked `research/` files, and writes an old-path -> new-path map to `docs/current/archive/docs_reorg_map.csv`. Never delete with a shell command (use the map, then `DeleteFile` for any leftovers).
- Rename `blockers/resolved/` to `blockers/closed/` in the same pass (optional).

### Step 3 - Link rewrite for non-.cs files
- Using the map, rewrite references in `.md`, `.ps1`, `.json`, `.yml`, `.txt`, `.csproj`, `.claude/{agents,hooks,skills,commands}` and `CLAUDE.md`: bare file names, `docs/current/<name>` paths and relative links (`../plans/x.md`, `./x.md`). Fix relative links inside every moved file (depth changes by one for `closed/`). Re-run the reference scan; expect zero stale names.

### Step 4 - .cs comment rewrite (separate subagent sweep)
- Dispatch `implementer-sweep` with the map restricted to rows whose cs-count is above 0 (see the Refs column). Use MCP tools only (`Search` text mode for the old name, `ReplaceSnippet` per comment); no shell edits to `.cs`. Build once afterwards (0 errors).

### Step 5 - Template README and guidance
- Replace rule 9 in `docs/current/templates/README.md` (legacy docs are no longer left in place; the old layout is retired) and add an `eval` row to the genre table (`evals/`, `eval_<slug>.md`, closed history in `evals/closed/`). Add `closed/` to the lifecycle rule for every genre, not only blockers and plans.
- Update `DOCUMENTATION_INDEX.md`, `reference_working_conventions.md` and CLAUDE.md pointers if they name moved paths.
- Run `pwsh scripts/Test-GuidanceHygiene.ps1` (CLAUDE.md budget, TRAILER check, link check) and fix findings.

### Step 6 - Verify
- `Build` 0 errors, one `RunTest` run against the baselines, empty `docs/obsolete`, `docs/research`, `docs/testing`, `docs/current/specs`, `docs/current/upstream` (then remove the empty folders with `DeleteFile`/an owner-run command), and a final reference scan.
