# TODO - open items only

Rules: (1) one entry = one line (`- [OPEN|READY|IN-PROGRESS|BLOCKED|PARKED] Title (YYYY-MM-DD if known) - one sentence - [doc]` followed by a relative link; READY = decided and scoped by a todo review, decisions in the linked doc's `## Decisions`; PARKED = the sentence says why and what would unpark it); (2) when an item is done, move it to [CLOSED.md](./CLOSED.md) with evidence (older months: [archive/](./archive/)); (3) design prose belongs in proposals/, ideas/, issues/ or design/ (see [templates/README.md](./templates/README.md)), never here. Full text of every shrunk entry lives in the linked doc under "Moved from TODO.md (2026-10-17)".

## P1 Now

Blocking or in-flight slices.

- [PARKED] `SemanticFindReplace(replaceConstruction)` options (2026-11-05) - storing the full skipped-site list for `GetLargeResult` (conflicts with the response-pipeline rule) and option 1b (`Action<T>` configure argument); parked because no concrete usage needs them (1a covers initializers); unpark when a sweep skips more sites than the 3-per-reason echo shows or meets an initializer no factory parameter takes - [proposal](proposals/proposal_semantic_find_replace_construction_initializers_and_skip_echo.md)
- [IN-PROGRESS] Git-spawn test flake (2026-11-05; decided 2026-11-10) - all three slices landed 2026-11-10 (`GitProcess` dedicated-thread reads d0ceef51; shared `Tests.Support.Basic/GitTestRepo.cs` replaces the 8 copies e3cdc6df..44a2944f; known-flake class comments 93160409..919cdccf); left: acceptance = 5 consecutive full worktree runs with no git-spawn failure - [doc](issues/issue_process_spawn_test_flake.md)

## P2 Next

Committed, not started.

- [OPEN] Cross-session write journal (2026-11-05, revised 2026-11-09) - attribution layer for per-file "who/why" in StaleRead and drift messages; premises updated (ToolCallContext exists, blobs carry AppliedBy*); plan after the follow-ups plan lands - [doc](proposals/proposal_cross_session_write_journal.md)

- [OPEN] `NormalizeNewRootWhitespaceKeepingComments` does not exist (2026-11-08) - needs design (a comment-keeping NormalizeWhitespace replacement for new-root callers); the other "not started" whitespace entries were verified already fixed 2026-11-08 (plural `ReplaceNodesAndFormatReplacementsAsync` c327b75f, `simplifyExisting` 6eaace53, SimplifyMemberAccess, InvertAssignments, ExtractInterface) - [doc](proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)

- [OPEN] ModelEval tests mix difficulty axes and grade rigid outcomes (2026-11-09) - classify each test by one primary axis (tool use, multi-step state, ambiguity, adherence, recovery/efficiency, realistic plan), grade on that axis, split the 10-chain into explicit and referential variants, refresh PlanStepRunner from a recent real plan; start in a fresh session with the test inventory - [proposal](proposals/proposal_modeleval_test_axes_and_aligned_grading.md)
- [BLOCKED] Core split test-move remainder (2026-11-10) - mixed files done (444e5a53, fdeba2d2, c8d2dbfc, 4beb7a2b); left: move `MoveMemberSuccessTests` and `MoveMemberToolCollateralRefusalTests` (Tests.Tools.Advanced, both marked `// TODO`) whole to Tests.Tools.Basic, blocked on MoveFile slice 1; MoveType promotion is later - [doc](proposals/proposal_usage_based_core_split.md)
- [OPEN] Split Advanced into vertical cluster projects, Asyncify first, then Analysis (2026-11-09) - tools + engines + tests per project, namespaces kept; needs a plan doc - [doc](proposals/proposal_usage_based_core_split.md)
- [OPEN] `MoveMember` qualify-receivers pass and alias-using duplicates (2026-10-03) - needs design; the other follow-ups (copy-all-usings, wrapped call sites, global-using duplicates, D2/Q1/Q2a test-seam nits) were closed in the todo review 2026-11-08 - [doc](issues/issue_movemember_open_followups.md)


## P3 Later


Ideas and low priority.

- [PARKED] Stale-read ledger: per-snapshot hashes (2026-11-09) - follow-up F5 of the closed read-ledger plan; once staged writes make `Committed` and `IncludeStaged` text diverge, `_observedHashes` needs one hash per snapshot; parked until staged writes are built - [doc](plans/closed/plan_read_ledger_stale_read_guard.md)

- [PARKED] Dispatch-table unification (2026-09-20) - proposal section 6; the resolver scope is RESOLVED (2026-10-03); parked because no defect stems from the three tables diverging; unpark on such a bug - [doc](proposals/proposal_universal_symbol_resolver.md)
- [OPEN] Store `AfterSource` in chokepoint operation blobs (2026-11-09) - NOT DESIGNED; blobs hold only `BeforeSource`, so `UndoLastApply` cannot detect a later edit; needs an `ApplyChangesResult` written-text map plus the 2 shared blob builders; cost: blob size roughly doubles; not needed for stale-read follow-ups Step 9h - [doc](ideas/idea_store_aftersource_in_chokepoint_operation_blobs.md)
- [OPEN] TOOL-GAP: `RunTest`/`Build` cannot target a git worktree (2026-11-09) - NOT DESIGNED; no `repoPath` parameter (the `Git` tool has one for read operations), so a background test runner cannot test a committed slice in its own worktree (own `bin/`/`obj/`) while implementation continues in the main tree; today the per-batch full run blocks the next batch (~15 min for Tests.Tools.Basic). Suggested change: optional `repoPath` (absolute path to a worktree of this repo) on `RunTest` and `Build`, plus a runner agent that tests the latest committed slice in a fixed worktree under `c:\tmp` and reports failures with the commit hash. Watch CPU contention with build-timeout tests. Stopgap in use since 2026-11-09: `scripts/Invoke-WorktreeTests.ps1` run in the background by the main session (no longer blocks the next batch)
- [OPEN] MoveFile tool (TOOL-GAP, 2026-11-10) - design in a fresh chat; decided: any text file, `MoveFile(sourcePath, destinationPath, namespaceHandling: keep|update|copyAndObsolete)` all mandatory, slice 1 = keep only, claude-lean core beside CreateFile after the schema trim, no auto-staging; open: cross-project in slice 1, Git(status) rename hint, update/copyAndObsolete design (ScopedLedger, renamed class) - [doc](proposals/proposal_move_file_tool.md)
- [OPEN] `MoveType` ownFile silently ignores `targetFolder` when the type name already equals the file name (the no_changes guard in `BasicRefactoringEngine.MoveTypeToFileAsync` ~1197-1250 runs before the folder is considered) (2026-11-10) - found in the todo review; decide: honour targetFolder (a file move) or refuse naming MoveFile - [doc](proposals/proposal_move_file_tool.md)
- [OPEN] Split `Git` into read and write tools (2026-11-10) - NOT DESIGNED; raised during the schema trim: each tool ~2.3k chars instead of 4.3k with a shorter operation enum (weak models), but the claude-lean total grows ~300-600 chars (shared `reason`/`paths`/`ref`, two descriptions) unless the write tool is gated off surfaces that never commit (eval, PlanStepRunner); design in a fresh chat - [doc](proposals/proposal_reduce_tool_schema_token_cost.md)
- [OPEN] TOOL-GAP: no tool deletes a comment block or line range (2026-11-09) - NOT DESIGNED; removing the ~275-line commented-out `ApplyDiffWithConfirmationCode` block from `WorkspaceTools.cs` took one `ReplaceSnippet` batch of 7 verbatim chunks (~14 KB re-sent): `Member(replace)` keeps a member's leading trivia and `Member(remove)` refuses a method with callers. Owner decision: a NEW tool, not more optional params on `ReplaceSnippet` (weak models already struggle with it). Sketch: `ReplaceRange(filePath, firstLineText, lastLineText, newContent)`, all mandatory; `firstLineText`/`lastLineText` are verbatim line texts (not numbers; names chosen so models do not pass an int) that must each match exactly once (stale-line-number safe), the inclusive range is replaced, empty `newContent` deletes; same chokepoint, compile-before-write and undo as `ReplaceSnippet`; no size cap on the replaced range; on-demand toolset, not the claude-lean core; supersedes the parked EditLines idea. Follow-up once built: a model-eval A/B of weak-model success with `ReplaceRange` vs `ReplaceSnippet` on the same large-block delete/replace tasks (first-try success, retries, tokens sent)
- [OPEN] Peer coordination: shared holds file (2026-11-09) - NOT DESIGNED; `HOLD`/`RELEASE` live only in conversation text, so a hold survives compaction only if the session copies it into its own tracker, and a third session cannot see it. Proposal: a git-ignored `.claude/holds.json` (or one file per session under `.claude/holds/`), each entry = session name, files, reason, since, expected release; `HOLD`/`RELEASE` write it, `OWNS`/`WHOAREYOU` and the dispatch briefs read it; stale entries (session no longer in `ListAgents`) are ignored. Open: concurrent-write safety (one file per session avoids it), whether the `enforce-dogfood.ps1` hook should warn on an edit to a file another session holds. Update the `peer-coordination` skill and CLAUDE.md "Peer sessions" - [doc](plans/closed/plan_stale_read_guard_followups.md)
- [OPEN] Future feature: `UsingDirective(operation: add, simplifyAllCallers: true)` solution-wide simplification (2026-10-17) - NOT DESIGNED - [doc](ideas/idea_using_directive_simplify_all_callers.md)
- [PARKED] `GenerateConstructor` auto-properties (2026-11-09) - tool gated, 0 journal calls; rule recorded (fall back to auto-properties when no field qualifies); unpark when the tool is used - [doc](plans/closed/plan_pinned_tool_defect_fixes.md)
- [PARKED] ChangeSignature all-or-nothing refusal on virtual/override/abstract/interface members (2026-11-09) - partial apply superseded by the W19 `[Obsolete]` wrapper (ce901063); no wrapper is possible for these members and no usage needs it yet; unpark when a real session hits the refusal - [doc](proposals/proposal_journal_digest_followup_decisions.md)

### Needs owner review (audit verdict UNSURE)

Could not be verified or refuted from source on 2026-10-17; an owner decides to close, shrink or re-scope. Full text lives in the linked doc.


## By area

- Formatting, EOL and trivia: [proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md](proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)
- Structural refactoring (MoveType, MoveMember, IntroduceParameterObject): [issues/issue_structural_refactoring_open_defects.md](issues/issue_structural_refactoring_open_defects.md), [issues/issue_movemember_open_followups.md](issues/issue_movemember_open_followups.md)
- Tool surface and schema: [proposals/proposal_reduce_tool_schema_token_cost.md](proposals/proposal_reduce_tool_schema_token_cost.md), [reference_tool_terminology_refinement.md](references/reference_tool_terminology_refinement.md)
- Session, workspace and Git: [plans/closed/plan_session_halt_recovery_and_git_gaps.md](plans/closed/plan_session_halt_recovery_and_git_gaps.md), [issues/issue_tool_hygiene_open_items.md](issues/issue_tool_hygiene_open_items.md)
