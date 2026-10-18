# TODO - open items only

Rules: (1) one entry = one line (`- [OPEN|IN-PROGRESS|BLOCKED] Title (YYYY-MM-DD if known) - one sentence - [doc]` followed by a relative link); (2) when an item is done, move it to [CLOSED.md](./CLOSED.md) with evidence (older months: [archive/](./archive/)); (3) design prose belongs in proposals/, ideas/, issues/ or design/ (see [templates/README.md](./templates/README.md)), never here. Full text of every shrunk entry lives in the linked doc under "Moved from TODO.md (2026-10-17)".

## P1 Now

Blocking or in-flight slices.

- [BLOCKED] MoveMember fold: no tool retypes an existing constructor parameter, so DI-chain call sites cannot be resolved (2026-09-29) - blocks the MoveMember fold of engine-reorg group 6 - [doc](blockers/blocking_error_movemember_no_retype_tool_for_di_chain_call_sites.md)
- [OPEN] ReplaceSnippet schema invites anchor-only edits (2026-10-12) - NotFound buries the culprit snippet and the description never points to Member - [doc](blockers/blocking_error_replacesnippet_schema_invites_anchor_only_edits.md)
- [OPEN] SubAgent fails with a bare InvalidOperationException and no findable run log (2026-10-12) - cause undiagnosed - [doc](blockers/blocking_error_subagent_invalidoperationexception_no_detail.md)
- [IN-PROGRESS] C5c-2 leftovers: unrouted EOL flatteners (2026-10-17) - plural `ReplaceNodesAndFormatReplacementsAsync` still raw `Formatter.FormatAsync` + whole-file `NormalizeEol`; `RemoveMethodParameterAsync` annotates the whole method - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [IN-PROGRESS] C5c-3: ExtractInterfaceAsync base-list layout leaves the colon at column 0, and files scaffolded by MoveTypeToFileAsync lack a trailing newline (2026-10-17) - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [IN-PROGRESS] C5c leftovers: ConvertExpressionBodyAsync whole-document format and the near-dead InlineVariableAsync fallback (2026-10-17) - route via `FormatAnnotatedSpansAsync`, decide the `NormalizeWholeSubtreeWhitespace` call under C6 - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [IN-PROGRESS] MoveMember private-state TOOL-GAP, mostly done (2026-10-17) - `fieldMap` and the ctor-assigned-field guard shipped; stale client schema, same-class calls, `keepDelegator`, subagent toolset visibility, stale class docs on WorkspaceTools/WorkspaceReadNavigationTools remain - [doc](proposals/proposal_movemember_keep_delegator_and_same_class_calls.md)

## P2 Next

Committed, not started.

- [OPEN] Wrap the 9 unwrapped `[McpServerTool]` returns in `SentinelCallToolResult` (2026-10-17) - plan is DRAFT and another session is implementing; do not begin until the user says so - [doc](plans/plan_wrap_unwrapped_tool_returns_in_sentinel_call_tool_result.md)
- [OPEN] C6 whole-subtree whitespace enforcement: reachability check of ~65 legacy root sites, migrate engine by engine (2026-10-15) - 73 call sites in 16 files; 3 sites deliberately left unfixed on 2026-08-27 (`RunMicroRefactoringAsync`, `ExtractConstantSafeAsync`, `GenerateToStringSafeAsync`) - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] TOOL-GAP `NormalizeNewRootWhitespaceKeepingComments` (2026-10-16) - no helper formats a newly built root that embeds existing code with comments kept (PinnedC6_* sites, ExtractInterface comment loss) - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] `NormalizeNewRootWhitespaceKeepingUsingHeaders` limitations (2026-10-16) - directive spanning usings gives CS1027 and a header on a usings-less file is not carried; fix only if the C6 variant does not subsume it - [doc](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- [OPEN] MoveMember keep-delegator, same-class calls and string-for-object coercion (2026-10-17) - proposal PROPOSED, slices 1-8 not built; also see the parallel-change wrappers proposal - [doc](proposals/proposal_movemember_keep_delegator_and_same_class_calls.md)
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
- [OPEN] Schema budget headroom is about 2 chars (2026-10-17) - claude-lean Search/Git schemas sit at the `CoreSurface` limits (~40498/40500); the next parameter must trim wording first - [doc](proposals/proposal_reduce_tool_schema_token_cost.md)
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

### Needs owner review (audit verdict UNSURE, original text left intact)

These four entries could not be verified or refuted from source on 2026-10-17. They stay verbatim until an owner decides to close, shrink or re-scope them.

### `SubAgentEval` child's full `RunTest` reported 1 failed test (2657 passed) in the live smoke run - unexplained

**Found:** 2026-10-01, live `SubAgentEval` smoke run (run `20261001-222319-107-295d110c`). The same
suite in the main checkout has 0 failures. `SubAgentEvalResult` carries counts only, so the failing
test's name was not captured. Candidates: a test that assumes the repo root has a `.git` directory
(a worktree has a `.git` file), a path-length issue under `RoslynSentinel-TestRuns\SubAgent\<runId>\subagent\Worktree`,
or a flake from running alongside other sessions. Next step: re-run `SubAgentEval` (or `RunTest` in a
manual worktree at that path) and capture which test fails; consider adding the first failing test
name to `SubAgentEvalResult` so this is visible without digging.

### `MoveMember`'s ledger `TryOpen` path still unexercised with entries actually present at apply time

_Note 2026-10-17: "same session as above" referred to the `"new"` sentinel entry, now closed (see CLOSED.md, found during end-to-end MoveMember testing)._

**Found:** 2026-09-27, same session as above. See
`docs/current/blockers/resolved/blocking_error_movemember_ledger_discarded_on_apply.md` (now FIXED —
the tool-layer discard bug is resolved) for full context. Every real (non-dry-run) `MoveMember` apply
attempted so far either had zero unresolved call sites by the time of apply (this session's real
apply resolved all 14 via `callSiteFixups` first) or was rejected before reaching the ledger-open
check. `((IScopedOperationLedger)_workspaceManager).TryOpen(...)` at
`AdvancedRefactoringTools.cs`'s `MoveMember` has therefore still never actually run.

**Not fixed / not yet exercised:** construct a real apply where `autoResolveCallSites` is true,
`callSiteFixups` deliberately covers fewer than all unresolved sites, and the apply still succeeds
overall (i.e. the *resolved* sites compile, leaving only the *deliberately-unfixed* sites as
`PendingLedgerEntries`) — confirm the ledger actually opens, blocks an unrelated write, and can be
resolved by a follow-up `callSiteFixups`-carrying retry.

### `Member(replace)` sub-findings left open by the blank-line/newline fix (2026-09-18)

Split out of `blocking_error_member_replace_strips_blank_line_between_adjacent_members.md` when that
doc's primary symptom (inserted members losing separating blank lines/newlines) was archived as
fixed via `RoslynFormattingHelper.InsertMemberFormattedAsync`. Two sub-findings from that doc's repro
were explicitly left unresolved and are carried forward here rather than lost with the archive:

1. **Silent no-op on `Member(replace)`**: a `replace` call against `ServerBuildInfo`'s constructor
   (`containerName`/`memberName`/`contextSnippet` targeting) returned `"status":"applied"`, but an
   immediate re-read showed no file content had changed at all (confirmed via the harness's own
   "file unchanged since your last Read" signal). Not traced to source - open question is whether
   `contextSnippet` matching silently matched nothing, or the write was computed correctly but never
   persisted. Needs someone to trace the `Member` write path (`ValidateAndApplyHelper.ValidateAndApplyAsync`
   -> `ApplyProposedChangesAsync`) with a minimal repro.
2. **`CS0542` on top-level-type addressing**: `Member(replace, containerName: <namespace>,
   memberName: <TopLevelClassName>, ...)` - i.e. addressing a top-level class by its containing
   namespace instead of a containing type - produced `'X': member names cannot be the same as their
   enclosing type` instead of replacing the class body. Not disambiguated whether this addressing
   combination is meant to be supported at all, or whether it's a genuine insertion/targeting bug
   that nests new content into the class instead of replacing it wholesale.

### `docCommentId` parameter audit across all tools — not started

**Found:** 2026-09-06, see [docCommentId_description_gap.md](./docCommentId_description_gap.md). `RenameSymbol`'s description
doesn't say how to obtain `docCommentId` (via `LocateSymbol`); confirmed as the direct cause of a
model fabricating a placeholder ID and getting rejected before self-correcting.

**Scope (two parts):**
1. Audit every tool parameter named `docCommentId` (`grep -rn docCommentId` across `[Description]`
   attributes/tool method signatures) and add "obtain this via `LocateSymbol`" (or equivalent) to
   each description that's missing it.
2. While auditing, identify every tool that takes `docCommentId` *and* also has other mandatory
   parameters (e.g. `filePath`) — flag these separately, since requiring both a resolved symbol ID
   and a hand-supplied file path is a second place a model can supply mismatched/fabricated values
   (the file path could point somewhere the docCommentId doesn't actually live), and the
   description should make clear which one is authoritative for locating the target.

**Not started** — deferred to its own session per the original memory's plan.

## By area

- Formatting, EOL and trivia: [proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md](proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- Structural refactoring (MoveType, MoveMember, IntroduceParameterObject): [issues/issue_structural_refactoring_open_defects.md](issues/issue_structural_refactoring_open_defects.md), [issues/issue_movemember_open_followups.md](issues/issue_movemember_open_followups.md)
- Tool surface and schema: [proposals/proposal_reduce_tool_schema_token_cost.md](proposals/proposal_reduce_tool_schema_token_cost.md), [tool-terminology-refinement-reference-v1.md](tool-terminology-refinement-reference-v1.md)
- Session, workspace and Git: [plans/plan_session_halt_recovery_and_git_gaps.md](plans/plan_session_halt_recovery_and_git_gaps.md), [issues/issue_tool_hygiene_open_items.md](issues/issue_tool_hygiene_open_items.md)
