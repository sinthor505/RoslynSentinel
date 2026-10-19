# TODO - open items only

Rules: (1) one entry = one line (`- [OPEN|IN-PROGRESS|BLOCKED] Title (YYYY-MM-DD if known) - one sentence - [doc]` followed by a relative link); (2) when an item is done, move it to [CLOSED.md](./CLOSED.md) with evidence (older months: [archive/](./archive/)); (3) design prose belongs in proposals/, ideas/, issues/ or design/ (see [templates/README.md](./templates/README.md)), never here. Full text of every shrunk entry lives in the linked doc under "Moved from TODO.md (2026-10-17)".

## P1 Now

Blocking or in-flight slices.

- [BLOCKED] MoveMember fold: no tool retypes an existing constructor parameter, so DI-chain call sites cannot be resolved (2026-09-29) - blocks the MoveMember fold of engine-reorg group 6 - [doc](blockers/blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md)
- [OPEN] ReplaceSnippet schema invites anchor-only edits (2026-10-12) - NotFound buries the culprit snippet and the description never points to Member - [doc](blockers/blocking_error_replacesnippet_schema_invites_anchor_only_edits.md)
- [OPEN] SubAgent fails with a bare InvalidOperationException and no findable run log (2026-10-12) - cause undiagnosed - [doc](blockers/blocking_error_subagent_invalidoperationexception_no_detail.md)
- [OPEN] `ConvertToBackgroundServiceAsync` (`Engines.Basic/ProjectStructureEngine.cs`) still replaces the whole base list when the class has a non-interface base class or an unresolvable base type (`class W : Foo, IDisposable` becomes `class W : BackgroundService`); decide: refuse with a clear error, or keep the interfaces and drop only the base class (2026-10-19)
- [OPEN] `SyncInterfaceToImplementation` same-file path goes through `ReplaceNodeAndFormatReplacementAsync`, which annotates the whole interface node; irregular existing members are probably re-spaced - not pinned (2026-10-18)
- [OPEN] Whitespace initiative follow-ups (2026-10-19; C1-C11 done and the family deleted in D1, see CLOSED.md): rename `StripAllTriviaForNormalize` (the only survivor; "ForNormalize" is now a misnomer); replace the `using static RoslynFormattingHelper` imports (D2 kept 45 call sites in AdvancedStructural/AsyncOptimization/CodeStyle/LogicOptimization/Modernization unchanged) with qualified calls once SemanticFindReplace is reachable from a subagent; async leftovers (the relative-path Asyncify fixtures were made absolute 2026-10-19; `MigrationScanResultTests.T3` stays under the 15 KB inline threshold only because its fixture root is a short drive-root folder, so it is sensitive to path length): `ApplyProposedChangesAsync` results still ignored in `RunUpliftBatchAsync`, `PropagateCancellationTokenBatchAsync` and other AsyncifyTools phases, and the CT-add/body-rewrite validation-failure skips in `RunBridgeBatchAsync` claim "flagged NeedsManualReview" without flagging. Defect noticed, not fixed (needs design; the other four from this list were fixed 2026-10-19): `InlineMethod` neither substitutes parameters nor adds parentheses (`Calc.Answer( ) * 2` becomes `40 + 2 * 2`) and picks the first same-named method in the defining file. - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [IN-PROGRESS] C5c leftovers: unrouted EOL flatteners (2026-10-17) - plural helper now routed (c327b75f); `RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync` (~:295) still annotation-format + whole-file `NormalizeEol`; `RemoveMethodParameterAsync` annotates the whole method - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] C5b-2c review leftovers (2026-10-18) - ExtractMethod annotates the whole containing block (narrow to call statement + new method, fold into C5c); add characterization pins for a CRLF-dominant file with a stray LF, CR-only/Unicode-EOL input, a stray CRLF on a blank line in the edit region, and the pre-existing trivia loss (SortUsings trailing comments and removed-duplicate leading trivia, ExtractMethod inter-statement comments) - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [IN-PROGRESS] C5c-3: ExtractInterfaceAsync base-list layout leaves the colon at column 0, and files scaffolded by MoveTypeToFileAsync lack a trailing newline (2026-10-17) - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [IN-PROGRESS] C5c leftovers: ConvertExpressionBodyAsync whole-document format and the near-dead InlineVariableAsync fallback (2026-10-17) - route via `FormatAnnotatedSpansAsync` (the `NormalizeWholeSubtreeWhitespace` call is gone; the helper was deleted in D1) - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [IN-PROGRESS] MoveMember private-state TOOL-GAP, mostly done (2026-10-17) - `fieldMap`, the ctor-assigned-field guard, `oldMember`/`forwarderReceiver`, same-class call rewrite and string-for-object coercion shipped (2026-10-18); a client-cached stale schema is only answered by the new hint, subagent toolset visibility and stale class docs on WorkspaceTools/WorkspaceReadNavigationTools remain - [doc](proposals/proposal_movemember_keep_delegator_and_same_class_calls.md)

## P2 Next

Committed, not started.

- [IN-PROGRESS] EOL policy and final-bytes write guard (2026-10-18) - 313 of 693 `.cs` are LF or mixed in the working tree (index all LF, so git hides it); Steps 0-1 done (`scripts/Get-EolAudit.ps1`), next Step 2 normalize - [doc](plans/plan_eol_policy_and_write_chokepoint.md)
- [OPEN] Wrap the 9 unwrapped `[McpServerTool]` returns in `SentinelCallToolResult` (2026-10-17) - plan is DRAFT and another session is implementing; do not begin until the user says so - [doc](plans/plan_wrap_unwrapped_tool_returns_in_sentinel_call_tool_result.md)
- [OPEN] C6 whole-subtree whitespace enforcement: reachability check of ~65 legacy root sites, migrate engine by engine (2026-10-15) - 73 call sites in 16 files; 3 sites deliberately left unfixed on 2026-08-27 (`RunMicroRefactoringAsync`, `ExtractConstantSafeAsync`, `GenerateToStringSafeAsync`) - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] MoveMember keep modes for instance moves into a static class or a new class, and for generic/extension methods (2026-10-18) - still `NotImplemented`; the rest of the proposal (slices 1-8) is built - [doc](proposals/proposal_movemember_keep_delegator_and_same_class_calls.md)
- [OPEN] `MoveTypeToOuterScopeAsync` does not adjust accessibility (2026-10-17) - private/protected nested type becomes CS1527; widen to internal or refuse; also an unreachable `_ => currentContainer` arm - [doc](issues/issue_structural_refactoring_open_defects.md)
- [OPEN] `IntroduceParameterObjectAsync` drops its TODO comment, hides the class path and reports a wrong success message (2026-10-17) - [doc](issues/issue_structural_refactoring_open_defects.md)
- [OPEN] `MoveType` cannot lift a nested type to top level (2026-10-14) - qualified `Outer.Nested` references are left behind (13 CS0426) - [doc](issues/issue_structural_refactoring_open_defects.md)
- [OPEN] `MoveType(destination: ownFile)` has no target-folder parameter (2026-10-14) - also adds an unused using and drops the type-level doc summary - [doc](issues/issue_structural_refactoring_open_defects.md)
- [OPEN] `MappingEngine.InvertAssignments` leaks leading trivia onto the right-hand side (2026-10-16) - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] `IDEStyleEngine.SimplifyMemberAccess` drops the leading trivia of the `this` token (2026-10-16) - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] `AddUsingDirective` simplifyExisting: `Simplifier.ReduceAsync` simplifies nothing because no node carries `Simplifier.Annotation` (2026-10-16) - re-verified 2026-10-17 at `BasicRefactoringEngine.cs:1553` - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] ExtractInterface drops comments and cannot warn (2026-10-16) - engine returns only a file dictionary; add a warnings field or a comment-keeping variant - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] DTO hook gap: `Member replace`, `ReplaceSnippet` and `ApplyDiff` are not inspected by `enforce-dto-justification.ps1` (2026-10-16) - [doc](issues/issue_tool_hygiene_open_items.md)
- [OPEN] `GitImpl` generic catch blocks put `ex.Message` into `Error` (2026-09-30) - about 16 sites; violates the no-raw-exception rule - [doc](issues/issue_tool_hygiene_open_items.md)
- [OPEN] `SessionHalted` external-drift latch: false positive from a timestamp-only touch, message does not self-report scope (2026-09-15) - [doc](plans/plan_session_halt_recovery_and_git_gaps.md)
- [OPEN] Path comparison: analyzer/source-scan guardrail for `FilePathWrapper` vs `PathComparison.Comparer` case traps, and `FilePathLock` platform-conditional comparer (2026-10-02) - [doc](issues/issue_path_comparison_case_sensitivity.md)
- [OPEN] `contextSnippet` "not found" wording, Step 5 (wrong-node-kind family, about 7 files) (2026-09-16) - categories A/B fixed; see the plan for steps 1-4 - [doc](finding_snippet_notfound_wording_confusion_audit.md)
- [OPEN] Scoped operation ledger: review and extend to more tools than MoveMember (2026-10-09) - [doc](proposals/proposal_journal_digest_followup_decisions.md)
- [OPEN] Follow-ups of the blocking-load and Build-result-shape plan (2026-10-09) - SubAgent tools do not wait for `SolutionLoadStatus`, embedded `BuildVerification` details not projected - [doc](plans/plan_blocking_solution_load_and_build_result_shape.md)
- [OPEN] `SyntaxTargetResolver` extraction follow-ups (2026-10-03) - `Describe*` formatters not unified, write-only `_symbolNavigationEngine`, RenameSymbol cannot merge into an existing signature, `Member(remove)` keyword resolution - [doc](proposals/proposal_syntax_target_resolver_extraction.md)
- [OPEN] `MoveMember` follow-ups left by the whole-file reformat fix (2026-10-03) - instance-move cref limitations and others - [doc](issues/issue_movemember_open_followups.md)
- [OPEN] Battery tests with bare relative file names leak files into the working directory (2026-10-17) - only BatteryTwentyFourTests got a cleanup; consider a shared test-base cleanup - [doc](issues/issue_tool_hygiene_open_items.md)

## P3 Later

Ideas and low priority.

- [OPEN] `NormalizeEolOfChangedLines` matches by line text, so repeated identical lines can take the wrong terminator (2026-10-16) - low risk, mixed-EOL files only - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] Old Advanced-side NormalizeWhitespace sweep list (2026-08-24) - SUPERSEDED by the C0-C6 enforcement proposal; full history kept there - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] Symbol/member lookup fragmentation across five resolvers plus three dispatch tables (2026-09-20) - proposal only, not implemented (see also the unify-member-lookup proposal) - [doc](proposal_universal_symbol_resolver.md)
- [OPEN] Future feature: `UsingDirective(operation: add, simplifyAllCallers: true)` solution-wide simplification (2026-10-17) - NOT DESIGNED - [doc](ideas/idea_using_directive_simplify_all_callers.md)
- [OPEN] `contextSnippet` deprecation tracking and `NearMissList`'s 3-candidate cap (2026-09-16) - deferred - [doc](finding_snippet_notfound_wording_confusion_audit.md)
- [OPEN] Read-tool metadata envelope (`isComplete`/truncation flag) for the read tools other than `GetMethodSource`/`GetFileOutline` (2026-08-24) - [doc](ideas/idea_read_tool_metadata_envelope_followups.md)
- [OPEN] Tool terminology/naming backlog (2026-08-24) - weak or ambiguous tool and parameter names, unactioned - [doc](tool-terminology-refinement-reference-v1.md)
- [OPEN] Model tool-choice gap: `ApplyDiff` used instead of `ChangeAccessibility` for `ReplaceBlockFormatted`, 67.5% of all ApplyDiff failures (2026-09-05) - unactioned - [doc](model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md)
- [OPEN] Server-side telemetry/metrics for tool call counts and error rates (2026-09-05) - idea, not implemented - [doc](ideas/server_telemetry_idea.md)
- [OPEN] `Plan:`/`Closes:` commit trailers plus a reconciliation script for plan/proposal/finding docs (2026-10-18) - convention first, `ProjectDoc` extension later - [doc](ideas/idea_doc_id_commit_trailers_and_reconciliation.md)

### Needs owner review (audit verdict UNSURE)

Could not be verified or refuted from source on 2026-10-17; an owner decides to close, shrink or re-scope. Full text lives in the linked doc.

- [OPEN] `SubAgentEval` child full `RunTest` reported 1 failed test (2657 passed), unexplained (2026-10-01) - failing test name not captured - [doc](issues/issue_tool_hygiene_open_items.md)
- [OPEN] `MoveMember` ledger `TryOpen` path never exercised with entries present at apply time (2026-09-27) - [doc](issues/issue_movemember_open_followups.md)
- [OPEN] `Member(replace)` sub-findings: silent no-op on a constructor replace, and CS0542 when a top-level type is addressed by namespace (2026-09-18) - [doc](member_replace_drops_leading_blank_line_and_verify_gap.md)
- [OPEN] `docCommentId` parameter audit across all tools (2026-09-06) - not started; add "obtain via LocateSymbol" to each description and flag tools that also require `filePath` - [doc](docCommentId_description_gap.md)

## By area

- Formatting, EOL and trivia: [proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- Structural refactoring (MoveType, MoveMember, IntroduceParameterObject): [issues/issue_structural_refactoring_open_defects.md](issues/issue_structural_refactoring_open_defects.md), [issues/issue_movemember_open_followups.md](issues/issue_movemember_open_followups.md)
- Tool surface and schema: [proposals/proposal_reduce_tool_schema_token_cost.md](proposals/proposal_reduce_tool_schema_token_cost.md), [tool-terminology-refinement-reference-v1.md](tool-terminology-refinement-reference-v1.md)
- Session, workspace and Git: [plans/plan_session_halt_recovery_and_git_gaps.md](plans/plan_session_halt_recovery_and_git_gaps.md), [issues/issue_tool_hygiene_open_items.md](issues/issue_tool_hygiene_open_items.md)
