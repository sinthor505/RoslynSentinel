# CLOSED — fixed/resolved history

Permanent record of issues that were found and fixed (or confirmed as an upstream bug outside
RoslynSentinel's control). Split out of TODO.md on 2026-09-10 to keep that file open-items-only;
entries below are otherwise unchanged from when they were closed. Newly-fixed TODO.md items should
be moved here going forward, not deleted.
Only the newest month (2026-10) is kept here. Older entries are archived unchanged:
[archive/closed_2026-09.md](archive/closed_2026-09.md) and [archive/closed_2026-08.md](archive/closed_2026-08.md).
Entries moved here from TODO.md on 2026-10-17 carry their original text plus an "Evidence" line.

## EditLines tool and anchor attributes - resolved (2026-11-10)

EditLines tool and anchor attributes (2026-08-31) - never built; parked because ApplyDiff/ReplaceSnippet cover line edits today; unpark when a model-eval run shows ApplyDiff is still a bottleneck - [doc](ideas/idea_editlines_and_anchor_attributes.md)

Evidence: dropped by owner 2026-11-10: superseded by the ReplaceRange TOOL-GAP entry (TODO.md, owner decision for a new mandatory-param tool)

## ProjectDoc trailer extension (trailers, validate, close) - resolved (2026-11-10)

`ProjectDoc` trailer extension in one slice (2026-11-09) - list docs with their `Plan:`/`Closes:` commits, validate a trailer value, perform the closure (Status, move to `closed/`, CLOSED.md line, link fixes); then update templates/README.md rule 11; Medium - [doc](ideas/closed/idea_doc_id_commit_trailers_and_reconciliation.md)

Evidence: 6252fdd2 (DocTrailerEngine scan/validate), b4378c41 (CloseDoc), 8dee86f2 (ProjectDoc actions); DocTrailerEngineTests, DocCloseTests, DocumentationToolsTests; ClaudeLean budget tests unchanged (ProjectDoc is on demand)

## D1 leftovers on gated tools - resolved (2026-11-10)

D1 leftovers on gated tools (2026-11-09; active write tools done in 2c319c1e and aa26d049) - ConvertStaticToExtension class pick, BuildAutoProperty, a `VariableLifetimeReport` warning field, BuildRecoveryHintAsync, the Asyncify uplift batch (carry symbol plus line); verify each site first and record any with no real ambiguity; Medium - [doc](findings/finding_snippet_notfound_wording_confusion_audit.md)

Evidence: 6352fbc7 (ConvertStaticToExtension, BuildAutoProperty verdict no-change, TraceVariableLifetime warning, recovery hint), 5b9b1348 (Asyncify uplift batch); outcome recorded in findings/finding_snippet_notfound_wording_confusion_audit.md

## IntroduceParameterObject updates interface implementers - resolved (2026-11-10)

IntroduceParameterObject updates interface implementers (2026-11-09) - return `Changes` for the interface and every implementer (positional parameter map, explicit/partial included, a using added when namespaces differ, expression-bodied methods handled); callers stay manual; Medium - [doc](issues/issue_structural_refactoring_open_defects.md)

Evidence: 57e1d0cb; AdvancedStructuralFormatCharacterizationTests, IntroduceInlineTests, Bug55_78BatchRegressionTests BUG_57 (the three issue-doc defects were already fixed earlier and are now pinned by exact tests)

## Full build waits on process exit, not pipe EOF - resolved (2026-11-10)

`BuildEngine.RunFullBuildAsync` waits on process exit, not pipe EOF (2026-11-09) - await exit, give the reads a 3-5 s drain grace, then cancel them and keep partial text; the timeout message reports whether the root exited; the test engine factory sets `MSBUILDDISABLENODEREUSE=1`; same for the `RunTest` spawn if it has the pattern; Medium - [doc](issues/closed/issue_full_build_waits_for_pipe_eof_held_by_msbuild_node.md)

Evidence: ef4301b6 (ChildProcessCapture helper; ChildProcessCaptureTests, BuildScopeFullBuildTests, RunTestTests)

## Failed batch errors stay readable - resolved (2026-11-10)

Failed batch errors stay readable (2026-11-09) - `ReplaceSnippetBatch` gives full detail for the first 3 failures and one line per remaining index under a total cap, and `GetLargeResult` pages a whole-stored `errorData.message` by lines; Small-Medium - [doc](issues/closed/issue_batch_edit_error_unreadable_when_offloaded.md)

Evidence: 30a07449 (BatchFailureMessage helper; BatchFailureMessageTests, ReplaceSnippetBatchTests 15-edit test, GetLargeResultTests errorData.message paging)

## Live-check of the stale-workspace stamp - resolved (2026-11-10)

Live-check the stale-workspace stamp once with `LaunchTestInstance` and a broken `.csproj` (2026-11-09) - record the outcome in the proposal; Small - [doc](proposals/proposal_workspace_reload_failure_visibility.md)

Evidence: commit titled "#7 ..." (git log); outcome recorded in proposal_workspace_reload_failure_visibility.md Decisions (iii): stamp, reason, recovery and reloadFailure seen on a live McpServerStatus result, self-clears after the file is repaired

## Post-write fold failure queues one bounded reload - resolved (2026-11-10)

A post-write fold failure queues one bounded `ReloadWorkspaceFromDiskAsync` (2026-11-09) - like the debounce-retry path; the latch stays if it fails; Small-Medium - [doc](proposals/proposal_workspace_reload_failure_visibility.md)

Evidence: commit titled "#6 ..." (git log); WorkspaceDebounceReloadTests PostWriteFoldFails_*; PostWriteFoldOverride test seam

## Startup auto-load records StartupLoad; staleness from last successful load - resolved (2026-11-10)

Startup auto-load records trigger `StartupLoad`, and `GetWorkspaceStatus` staleness uses `_lastSuccessfulLoadUtc` (2026-11-09) - Small-Medium - [doc](proposals/proposal_workspace_reload_failure_visibility.md)

Evidence: commit titled "#5 ..." (git log); WorkspaceReloadFailureStatusTests (StartupLoad trigger, fold of A does not hide stale B, reload clears stale count)

## --toolsets startup flag pre-enables toolsets before the first tools/list (#12) - resolved (2026-11-10)

`--toolsets=<set,...>` startup flag pre-enables toolsets before the first `tools/list` (2026-11-09) - so a launch config starts with the toolsets it needs (Claude Code never refreshes its list after an enable); Medium - [doc](blockers/closed/blocking_error_toolset_enable_not_visible_in_client_tool_list.md)

Evidence: this commit (batch B item #12); McpToolsetControlTests.StartupToolsets_Declarations_ListsTheSetInTheFirstToolsList and four sibling tests (156 surface-fixture tests pass). ServerStartupHelpers.ParseToolsets/ApplyStartupToolsets, wired in ServerStdio and ServerHttp. Unknown name = logged startup warning (matches --hide-tools). Real-client check with ~/.mcp.json left to the live window.

## Claude-lean startup surface widened by five tools (#9) - resolved (2026-11-10)

Widen the claude-lean startup surface (2026-11-09) - move SemanticFindReplace, InspectSymbol, MoveMember, UsingDirective and GetDiagnostics from `ToolsetCatalog` into `ClaudeLeanToolNames` and add `SemanticFindReplaceTools` to the claude mode, raising the budget tests and fixing the pinned on-demand asserts; Small-Medium - [doc](blockers/closed/blocking_error_toolset_enable_not_visible_in_client_tool_list.md)

Evidence: this commit (batch B item #9); ClaudeLeanModeTests (CoreTools oracle, 21), McpToolsetControlTests, ClaudeLeanSchemaBudgetTests (88 pass). Lean schema grew 33000 -> 45000 chars (Advanced), 31100 -> 40000 (Basic); trimming SemanticFindReplace (5450) and MoveMember (3356) schemas is a // TODO in ClaudeLeanSchemaBudgetTests.cs. Fresh-client ToolSearch check left to the live window.

## McpToolsetControl response no longer claims enabled tools are callable by name (#1) - resolved (2026-11-10)

`McpToolsetControl` response stops claiming enabled tools "can still be called by name" (2026-11-09) - `ToolsetControlTools.cs` ~79: say a client that does not refresh needs `/mcp` reconnect or a startup surface with the tool; Small - [doc](blockers/closed/blocking_error_toolset_enable_not_visible_in_client_tool_list.md)

Evidence: McpToolsetControlTests.Enable_StatusMessage_TellsNonRefreshingClientsToReconnect (this commit, batch B item #1)

## TypedMember optional isConst - resolved (2026-11-10)

`TypedMember` gets an optional `isConst` (2026-11-10, decided 2026-11-09) - field only, with a comment saying why it is optional; refuse it combined with `isStatic`/`isReadonly` or without an `initializer` as InvalidArgument with a next step (qwen3.5-9b got a silent `private static int` in run `20261110-020405-782` T20); check: field + isConst + initializer "1" gives `private const int LocalZone = 1;`; Small - no doc

Evidence: MemberRefactoringTests.AddField_PrivateConstWithInitializer; UsingDocsTypedMemberTests 21/21 (const emits `private const int LocalZone = 1;`, refusals for isStatic/isReadonly/no initializer/property); Tests.Server lean/alias 61/61

## MsToolAugmentEngine throw-migration finished - resolved (2026-11-10)

Finish the throw-migration in `MsToolAugmentEngine` (2026-11-09) - the three file lookups behind `BatteryEighteenTests.cs:76,94,205` throw NotFound via `RequireDocument`, and the tests assert `ToolNotFoundException`; `MsAugmentResult` is untouched; Small - [doc](plans/plan_error_codes_follow_cause.md)

Evidence: BatteryEighteenTests/RegressionTests/AugmentToolsTests 123/123; SwitchConversionAdvancedTests 6/6; callers QualityTools and CodeTransformTools catch at the tool boundary (ToolErrorMapper)

## Peer COMMITTED announcement (sender side) - resolved (2026-11-10)

Peer coordination: `COMMITTED <hash> <files>` message (2026-11-09) - NOT DESIGNED; after a commit, a session tells peers which files it wrote; the receiver runs `ExternalFileDrift(operation: Acknowledge)` for exactly those files, so its drift list does not fill with other sessions' committed writes (seen 2026-11-09: a peer's server latched on our committed writes and it acknowledged only some of them by hand). Add the line to the standing authorizations in CLAUDE.md "Peer sessions" and to `.claude/skills/peer-coordination/SKILL.md`; the halt itself goes away with plan_stale_read_guard_followups Step 8c - [doc](plans/closed/plan_stale_read_guard_followups.md)

Evidence: skill only per owner ruling 2026-11-10: .claude/skills/peer-coordination/SKILL.md section 7; no CLAUDE.md change

## GenerateConstructor skips const and static fields - resolved (2026-11-10)

`GenerateConstructor` excludes `const` and `static` fields (2026-11-09) - the `GenerateConstructorAsync` filter checks only private/readonly, so a `private const` would be assigned (CS0131, read in source: test first); Small - [doc](plans/closed/plan_pinned_tool_defect_fixes.md)

Evidence: CodeGenerationTests.GenerateConstructor_ClassWithConstAndStaticFields_SkipsThem; CodeGenerationTests 38/38

## VS Code client MCP tool list goes stale after toolset enable - resolved (2026-11-09)

The VS Code client's MCP tool list goes stale after restarts (2026-11-06) - `McpToolsetControl(declarations)` reports on, but `ChangeSignature` stays absent from `ToolSearch` in the main session and in subagents, even when enabled first on a fresh server or toggled with 10 s gaps, while `SemanticFindReplace` stays listed with `moveExtract` off; so neither `tools/list_changed` nor the reconnect refreshed the list; it reproduces the earlier gap that did not reproduce on 2026-11-05; reproduced again 2026-11-07 with `modelEval`/`SubAgent` right after a restart, and a stateless HTTP test instance answers Unknown tool after an enable, so launch it with mode `SubAgent` instead; a fresh session or `/mcp` reconnect is the untested remedy; workaround: `LaunchTestInstance` over HTTP - [blocker](blockers/closed/blocking_error_toolset_enable_not_visible_in_client_tool_list.md)

Evidence: client-side cache, not fixable in the server (Claude Code issues 66084, 60626, 50515, 13646: deferred-tool index built at connect, not refreshed on tools/list_changed); todo review 2026-11-09 replaced it with READY lines: widen claude-lean core, --toolsets startup flag, McpToolsetControl text fix (blockers/closed/blocking_error_toolset_enable_not_visible_in_client_tool_list.md Decisions)

## SessionHalted drift latch after another session's edits - resolved (2026-11-09)

SessionHalted after another session's real edits stays latched (2026-11-07; owner rule given 2026-11-09: fixed by plan_stale_read_guard_followups F1 option (d)) - Not a spurious flag: 27 of the 30 listed files are exactly the files of the marshaller session's four commits (d9fd0ec6, c6215b34, 7f00ecb9, af7e210f), written 35-50 min before the halt; a clean tree proved nothing because that session had committed them. The latch is sticky: OnFileSystemChanged adds the path to _externalChanges (PersistentWorkspaceManager.cs ~1214), ~500 ms later ApplyInMemoryDocumentUpdatesAsync folds the disk text into memory and re-baselines the hash but does not remove the path, and ApplyProposedChangesAsync (~1857-1862) halts on path membership alone. Acknowledge with ConfirmWithListedFiles clears only the named files (AdminTools.cs ~168-175, by design), so 29 stayed flagged until LoadSolutionAsync (ClearExternalFileChanges, ~834) ran on restart. Candidate fix: before latching in ApplyProposedChangesAsync, drop a target whose CurrentSolution text equals the disk text (DocumentTextMatchesDisk / IsTrackedDocumentUnchangedOnDisk ~511). Why deferred: the halt also guards an agent that read the file before the other session wrote it (line-based edits would clobber), so unlatching after a fold trades safety for fewer halts; needs the owner's rule for concurrent sessions. Test idea: write a file externally, wait for the version bump, apply to it (halts today); with stale text via SetTestSolution it must still halt. Not checked: server logs of the halting instance d87a019a-fefadd08 were deleted by the 03:20 sweep; two flagged files outside the commits (WorkspaceReadNavigationImpl.cs, SearchSolutionTextIncludeTestsTests.cs) are unexplained; Tests.Server.csproj probably edited by a non-server tool.

Evidence: 3e4ed6fa (drift halt removed; per-agent StaleRead + FoldPending), plan_stale_read_guard_followups Step 8c

## Stale-read guard follow-ups (F3, F4, F1, F7, 9h) - resolved (2026-11-09)

Stale-read guard follow-ups (2026-11-09) - plan drafted, owner decisions recorded: F1 option (d) (drift refuses only files the agent read or edited, via per-agent StaleRead; session halt kept for watcher overflow), F3 anchor + engine tools opt in, F4 GetFileOutline/InspectSymbol record, F7 cross-process write lock + debounce lock + fold-on-read; run with /implement-plan in a fresh chat - [doc](plans/closed/plan_stale_read_guard_followups.md)

Evidence: 20440d38..f97b1b62; plans/closed/plan_stale_read_guard_followups.md

## File not-found after a type rename should name the declaring file - resolved (2026-11-10)

After RenameSymbol renamed `OrderPricingCalculator` to `StandardOrderPricingCalculator`, qwen3.5-9b called ReadFile on `StandardOrderPricingCalculator.cs` (run `20261110-002934-654` T15) and got the generic not-found.
Evidence: `fe1cc725` (RenameSymbol renames a same-named file with the type); this session's round-6 commit (FileNotFoundMessage leads with "Type 'X' is declared in <path>; read that file." for ReadFile, GetMethodSource, GetFileOutline and document lookup; `FileNotFoundDeclaringTypeTests`).

## Tool-call hint verbatim-placeholder check - resolved (2026-11-08)

Tool-call hint verbatim-placeholder check (2026-10-25) - unmeasured whether a model pastes `<why you call this>` verbatim; parked because it needs a model eval; unpark at the next model-eval round (count verbatim placeholders in its logs) - [doc](proposals/closed/proposal_tool_hint_builder.md)

Evidence: ModelEval 2026-11-08, unsloth/qwen3.5-9b on host 113, 12 tests: 12 placeholder-hint results, 0 verbatim pastes (0/12); host 112 unreachable

## AppliedChangeSummary.From factory and migration - resolved (2026-11-08)

`AppliedChangeSummary.From` factory (2026-11-08) - add `AppliedChangeSummary.From(ApplyOutcome, ...)` that fills `LineChanges` and `Findings`, then migrate in slices the 53 of 82 production `new AppliedChangeSummary(` sites that omit `LineChanges` (their dry-run `lineChanges` is null although the Note promises it); done = no production site constructs the record without `LineChanges`, and a dry-run test per tool family shows non-null `lineChanges` - [doc](issues/issue_movemember_open_followups.md)

Evidence: 25f440fe (factory, ComputeLineChangesAsync, tests), 40bd5fd4, 93f0db87, 0ac39a4a, f7163659, 58f6f761, caf751fc (82 production sites migrated, 0 TODO-marked; Search text finds no production new AppliedChangeSummary(), enforced by AppliedChangeSummaryConstructionRatchetTests), 4cb71605 (DryRunLineChangesTests per family); full RunTest 7526 total, 0 failed, 32 skipped. Primary constructor left non-Obsolete (19 test sites, positional deconstruction). The autoStage=false preview branches are dormant (autoStage hardcoded true) but now compute LineChanges.

## Path comparison: caller-supplied path sites - resolved (2026-11-08)

Path comparison: caller-supplied path sites (2026-11-08) - route the 6 sites that compare a caller-supplied path with a document path (MemberRefactoringEngine ~509 and ~3406, ProjectStructureEngine ~91 and ~1147, SyntaxTargetResolver ~171, SemanticReplaceEngine ~620) and the 2 comparer-less `Equals` sites through `DocumentLookup` / `PathComparison.Comparer` (first check whether upstream normalization already fixes the case); the other baseline sites stay ratchet-guarded; done = a differently cased path test per site passes and the `PathComparisonRatchetTests` baseline drops by the fixed sites - [doc](issues/issue_path_comparison_case_sensitivity.md)

Evidence: 509705be (SyntaxTargetResolver), fe762aa3 (MemberRefactoringEngine 509, 3411), 044978ea (ProjectStructureEngine), 17fe2c6f (SemanticReplaceEngine 620), e9fb9e00 (NamedArguments file scope, the comparer-less Equals), fba1092c (SyncInterface sync, the other comparer-less Equals); PathCaseCallerSuppliedTests plus PathComparisonRatchetTests/FirstMatchRatchetTests baselines lowered. Measured: MemberRef ~509, ProjectStructure ~91 and both Equals sites were already case-insensitive at runtime (FilePathWrapper operators), so those tests are characterization tests; no differently-cased seam for MemberRef 3411 or SemanticReplace 620 (both strings come from the workspace). Remaining baseline kept: SemanticReplaceEngine.cs::c=1 (the d.Name fallback scan) and NamedArgumentsEngine.cs::c=1

## GetMemberName: events and indexers - resolved (2026-11-08)

`GetMemberName`: events and indexers (2026-11-08) - `SyntaxTargetResolver.GetMemberName` returns null for Event and Indexer (SyntaxTargetResolver.cs ~430); handle both; done = an awaited test that an event and an indexer target resolve or return an actionable error - [doc](proposals/proposal_universal_symbol_resolver.md)

Evidence: 16deb019; MemberNameEventIndexerAnchorTests (events and the indexer, named "this", now resolve as insert anchors; ResolveInsertAnchorIndex, SortMembers tiebreak and GetContainerMembers now name them instead of null)

## Failure messages without a next step - resolved (2026-11-08)

Failure messages without a next step (2026-11-08) - per site, list the valid values or end with a `ToolCallHint.Next`: ListSolutionItems unknown kind (WorkspaceProjectManagementImpl.cs ~173), Generate unknown kind (CodeTransformTools.cs ~1345), ModifyModifier invalid modifier, ReplaceSnippet invalid action, ListAll invalid kind and the 2 LoadSolution messages (locate with `Get-NotFoundRecoveryReport.ps1 -AllErrors`), and a completed RunTest run with failing tests (WorkspaceBuildTestImpl.cs ~159, name the first failing test in a re-run call if the engine has it); done = one test per site asserting the suffix - [doc](references/reference_not_found_recovery_ladder.md)

Evidence: 8b9b4bc9 (ListSolutionItems unknown kind lists valid values; LoadSolution timeout range and load failures end with a next call; FailureMessageNextStepTests), fe0ce1c2 (RunTest failing run ends with a re-run next step; RunTestTests.SummarizeTestRun_FailedTests_EndsWithRerunNextStep, the row carries no project so the hint passes only filter). Stale: Generate unknown kind (CodeTransformTools ~1345 already lists valid values); ModifyModifier modifier, ReplaceSnippet action and ListAll kind are enum parameters rejected before the tool runs by ToolArgumentValidator, whose message already says "Valid values are: ..." and suggests the closest member (only unreachable "Unhandled action" fallbacks remain); RunTest rate-limit text already says what to do; RunTest ~334 and ~345 already end with ToolCallHint.Next

## Error codes follow cause: Step 4 leftovers - resolved (2026-11-08)

Error codes follow cause: Step 4 leftovers (2026-11-08) - fix the codes at DeleteFile missing file (WholeFileWriteTools.cs ~133, to NotFound), RetryFailedChanges "File not found." (WorkspaceTools.cs ~376, NotFound), InlineClass not-found (AdvancedRefactoringTools.cs ~319, NotFound) and SyncTypeAndFilename "already exists" (RefactoringStructuralImpl.cs ~1447, TargetIneligible), and split the 4 conflated engine "not found or" messages (AsyncOptimizationEngine ~141, LogicOptimizationEngine ~1192, MemberRefactoringEngine ~2291, ThreadSafetyEngine ~35); done = one test per code and no "not found or" text left - [doc](plans/plan_error_codes_follow_cause.md)

Evidence: d619b9b4, 1314b913 (4 engine splits + ErrorCodeFollowsCauseEngineTests), 9e210285 (DeleteFile NotFound), abc9b5b0 (InlineClass: engine returned an unconsumed "__error__" dictionary key, now throws NotFound/TargetIneligible; BatteryThirtyOneTests), 05ce8aaf (SyncTypeAndFilename TargetIneligible; SyncTypeAndFilenameTests). WorkspaceTools.cs ~376 was inside a block-commented dead ApplyDiffWithConfirmationCode copy: stale, nothing to fix

## Bare "Could not parse file." - resolved (2026-11-08)

Bare "Could not parse file." (2026-11-08) - `AdvancedStructuralEngine.cs` ~1154 throws it without the path; name the path like its siblings (AdvancedRefactoringEngine ~58 and ~240, StructuralRefinementEngine ~166); done = an awaited `Assert.ThrowsAsync` test asserts the path is in the message - [doc](proposals/proposal_result_status_and_code_on_the_wire.md)

Evidence: 56bb885d; IntroduceParameterObject_NullSyntaxRoot_ErrorNamesThePath (root==null unreachable from a fixture, so the test goes through the new public static RequireSyntaxRoot seam)

## Tool terminology: enums and migration-state rename - resolved (2026-11-08)

Tool terminology: enums and migration-state rename (2026-11-08) - type `WrapRange` `wrapper` and `ModifyEnum` `values` as enums, and rename `GetMigrationLedger`/`ResetMigrationLedger` to `GetAsyncMigrationState`/`ResetAsyncMigrationState` (descriptions, prompts, docs and generated tables follow); the rest of the backlog (Introduce, bare Generate) stays reference only; done = the generated schema shows the enum values and no old tool name remains in docs/generated or prompts - [doc](references/reference_tool_terminology_refinement.md)

Evidence: 9fe3f2d3 (WrapRange wrapper enum), 5bb484f2 (ModifyEnum values description per owner amended ruling; GetAsyncMigrationState/ResetAsyncMigrationState rename; docs/generated regenerated); Tests.Server 694 passed 0 failed, WrapRange/BlobIntegrity 23 passed, Tests.Asyncify 123 passed. Not done by design: ModifyEnum values stays a string.

## Tool-call hint static descriptions - resolved (2026-11-08)

Tool-call hint static descriptions (2026-11-08) - add the required parameters to the ~5 `// hint-ok` `[Description]` mentions (ToolParams.cs ~19 and ~24 incl. the Build/RunTest descriptions, GitTools.cs ~82 `hunkIds`); the lean budget (32000 Advanced / 30500 Basic, ~28117 used) now has room; done = `ClaudeLeanSchemaBudgetTests` and `ToolHintRatchetTests` green - [doc](proposals/closed/proposal_tool_hint_builder.md)

Evidence: 88b369e8; ClaudeLeanSchemaBudgetTests + ToolHintRatchetTests green. ToolParams consts keep the hint-ok marker (reworded): the ratchet scans consts, which must stay const for [Description]; the GitTools marker was removed. Advanced budget raised to 32500 (premise of ~28117 used was stale: 31964 before, 32298 after).

## Battery leak guard sweep - resolved (2026-11-08)

Battery leak guard sweep (2026-11-08) - add `WorkingDirectoryLeakGuard` + `[NonParallelizable]` to the remaining source-writing battery fixtures (about 20 Battery.Advanced files incl. Eleven/Twelve/Fourteen, TwentyFive to TwentyEight, ThirtyFive/ThirtySix; Tools.Basic BatteryTwenty has `[NonParallelizable]` but no guard) by implementer-sweep in slices; out of scope: the two on-disk RenameSymbol tests, optional Parts C/D and leaks outside Battery (issue doc); done = every source-writing battery file calls the guard and a full run leaves no leaked files - [doc](issues/closed/issue_battery_tests_leak_files.md)

Evidence: 17c2f43a, f1db6658, e47ac380, 9722c450, 876d9b08, d18683bb; guard + NonParallelizable on 84 Battery.Advanced fixtures (B18 pure SyntaxFactory without SetUp left alone) plus BatteryTwenty guard calls; Battery.Advanced 439/439, BatteryTwentyTests 82/82; BatteryTwentyTwo and BatteryFour not included (they write only to explicit temp paths, no cwd leak)

## Test factory rollout sweep - resolved (2026-11-08)

Test factory rollout sweep (2026-11-08) - replace the 15 `new PersistentWorkspaceManager(...)` test sites (Tests.Server 8 in 7 files, Tests.Tools.Basic 7 in 4 files incl. 4 in `ApplyDiffSizeGuardTests`) with `TestEngines.PersistentWorkspaceManager(logger, solutionPath: ...)` by implementer-sweep hand edits (owner choice 2026-11-08: `SemanticFindReplace` is not visible in the client, see the first P1 entry); done = Build green, `Search(mode: text)` for `new PersistentWorkspaceManager(` finds none outside Common and the factory, touched test classes pass - [proposal](proposals/proposal_semantic_find_replace_construction_initializers_and_skip_echo.md)

Evidence: 83f39ebe, 1c9e0e52; 15 sites in 11 files now use TestEngines.PersistentWorkspaceManager, text search finds no remaining construction, solution Build 0 errors, Tests.Server 68/68 and Tests.Tools.Basic 61/61 touched classes pass (4 ApplyDiffSizeGuardTests sites sit in a commented-out block and were rewritten as text only; 11 of 15 sites used an initializer, not 3)

## NewlineLiteralRatchetTests: exempt ContentHasher - resolved (2026-11-08)

NewlineLiteralRatchetTests: exempt ContentHasher (2026-11-08) - move `ContentHasher.ComputeHash` (a hash input, never output) from the baseline to `ExemptFiles` (26 to 25 keys); the rest of the baseline is accepted (it only affects returned preview text and the ratchet blocks growth); done = `NewlineLiteralRatchetTests` green - [doc](plans/closed/plan_eol_policy_and_write_chokepoint.md)

Evidence: 41385b2a; NewlineLiteralRatchetTests green (baseline 26 to 25 keys)

## Hidden-test-hit hints (symbol search with results) - resolved (2026-11-08)

Hidden-test-hit hints: `typesWithAttribute` and a `symbol` search with zero results now count and name hidden test hits (2026-11-07); `symbol` with some results still skips test projects (counting there means compiling them on every call, so only do it if timing allows). The `attributeUsages`/`hasAttribute`/broaden-on-empty gaps and `includeTests` for `implementorsOf`/`objectCreations`/`extensionsFor`/`methodsByReturnType` are fixed (2026-11-05) - [doc](plans/closed/plan_search_text_scan_all_projects_and_recovery_hints.md)

Evidence: Owner ruling 2026-11-08 (todo punchlist): keep the current status text for a symbol search with results ("N test project(s) were excluded ... pass includeTests: true"); naming hits per project would cost an extra scan. Zero-result paths already name hidden hits.

## Build-isolation follow-ups (prod-instances launcher) - resolved (2026-11-08)

Build-isolation follow-ups (2026-10-23) - flip the prod-instances launcher on for live windows (env `ROSLYNSENTINEL_INSTANCES_ROOT` in `C:\Users\Administrator\.mcp.json`, startup timeout above 60 s) and check the first cold build (item 19(c) test-instance job object done 2026-11-05: `KillOnCloseJob`, live-checked - `taskkill /F` on test instance A killed its child instance B, no `[job]` warning) - [doc](plans/closed/plan_build_isolation_prod_instances_and_test_servers.md)

Evidence: ROSLYNSENTINEL_INSTANCES_ROOT is set in both server blocks of .mcp.json; the live server runs from RoslynSentinelInstances\builds\1f1740a3 (McpServerStatus 2026-11-08); job object done 2026-11-05; todo review 2026-11-08

## Whitespace initiative follow-ups (Asyncify write-refusal fall-through, InlineMethod) - resolved (2026-11-08)

Whitespace initiative follow-ups (2026-10-19; C1-C11 done and the family deleted in D1, see CLOSED.md): the `using static RoslynFormattingHelper` imports are gone (47 sites qualified 2026-11-05, see CLOSED.md); async leftovers (the relative-path Asyncify fixtures were made absolute 2026-10-19; `MigrationScanResultTests.T3` stays under the 15 KB inline threshold only because its fixture root is a short drive-root folder, so it is sensitive to path length): `ApplyProposedChangesAsync` results are now checked in `AsyncBatchEngine` (uplift, propagate-CT; the uplift flag write is pinned by `RunUpliftBatch_ValidationFails_FlagWrite{Refused,Succeeds}_*`, the propagate flag write by `PropagateCtBatch_ValidationFails_FlagWriteRefused_*`), but the original 7 bare calls in `AsyncifyTools.cs` are now all checked (2026-10-21: ClearAsyncMigrationCandidateFlags and the handler extract phase; the 5 cleanup/catch-path writes in `RunHandlerConvertPhaseAsync` append "cleanup write refused: ..." to the item `Reason` and the `FailureDetail` without changing the outcome; `T22_Asyncify_HandlerConvert_StaleFlagStripWriteRefused_ReasonCarriesCleanupNote` pins the already-async branch via the operation blob, the generic-catch handler `FailureDetail` branch is pinned by `T23_Asyncify_HandlerConvert_NonVoidHandler_CountsAsFailedWithFailureDetail` and a refused handler write by `T24`/`T25` 2026-11-07; `T25` proved Phase 3b counted a ledger-refused write (`IsError` with a null `ValidationResult`) as Succeeded, now fixed, while the same `IsError && ValidationResult != null` fall-through at the flag/bridge sites `applyResult1126`, `1223`, `1317`, `1421` and `3a` is still open, `// TODO` in `AsyncifyTools.cs`). Defect noticed, not fixed (the other four from this list were fixed 2026-10-19): `InlineMethod` (parameter substitution and precedence parentheses fixed 2026-10-21, a same-name overload or same-named method in another type of the defining file is now refused 2026-10-22, a call site where the inlined names would bind differently or be inaccessible, or an instance-state template called on another instance, is refused 2026-10-22, see CLOSED.md; call sites in another project, the needs-a-using case and the arrow-body and field-initializer speculative-binding fallbacks are pinned by the `BUG_InlineMethod_*` tests added 2026-11-07). - [doc](proposals/proposal_inline_method_redesign.md) - [doc](proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)

Evidence: a98f89f7 fixed sites 1126/1223/1317/1421 (tests T27-T30); site 3a treats any IsError as a failure (AsyncifyTools.cs ~3125); the BUG_InlineMethod_* tests pin fixed or refused behaviour, not open gaps; the trivia backlog moved to its own TODO line (todo review 2026-11-08)

## Wire result marshaller - resolved (2026-11-08)

Wire result marshaller follow-ups (plan IMPLEMENTED 2026-11-07, all steps done): (1) the marshaller composing the echo and stamps so `EchoReserveChars` can go - build only if a "Result marshaller miss" is seen - [proposal](proposals/proposal_marshaller_owns_echo_and_stamps.md); (2) `ChangeSignature` cannot edit record primary-constructor parameters (TOOL-GAP seen removing `ChangedContentResultId`, 52 named-argument sites; wanted: rewrite the record parameter list and update named/positional `new T(...)` and `with` call sites); (3) the plan and the two superseded docs still need moving to `closed/` (docs reorganization plan); (4) `SemanticFindReplace` `replaceConstruction` apply for `new PersistentWorkspaceManager(...)` -> `TestEngines.PersistentWorkspaceManager` factory (preview: 8 sites in Tests.Server, 11 solution-wide; check the `using` after apply) - [plan](plans/closed/plan_wire_result_marshaller.md)

Evidence: plan status IMPLEMENTED 2026-11-07, Steps 0-11 done; 9230e2fe (Closes: plan_wire_result_marshaller); todo review 2026-11-08

## Parse-AgentLog.ps1 returns 0 turns - resolved (2026-11-07)

`Parse-AgentLog.ps1` returns 0 turns on current logs (2026-11-07) - its turn regex needs an em-dash but logs use " - ", and its category filter expects `...Tests.ModelEval.AgentLoop...` while logs say `RoslynSentinel.Common.AgentLoop.ModelAgentRunner`; a patched copy at `C:\tmp\parsefix\Parse-AgentLog-patched.ps1` parses turns but its per-tool grouping comes out blank; also the eval script archives each run folder into a nested duplicate of the same name, which doubles what the parser sees

Evidence: scripts/Parse-AgentLog.ps1 parses 105 runs / 1689 turns on ModelTestingResults/113/Model_FixesWholeFileRewriteBug_MinimalGuidance with per-tool grouping (pwsh 7.5 and 5.1); modeleval archive copy fixed; fac89fdd

## Edit attribution ledger for parallel subagents - resolved (2026-11-07)

Edit attribution ledger for parallel subagents (2026-11-05, approved) - per-server record of which agent wrote each file, same-file conflict refusal, fixed per-process operating mode (`--operating-mode`); identity injected by a PreToolUse hook - [plan](plans/closed/plan_edit_attribution_ledger.md)

Evidence: Steps 0-11 done. On 2026-11-07 every running server was at b6ccd5e5 or newer and the hook helper was republished. Its output carried `_sessionId` when run by hand, and a test instance with hand-passed `_agentId` and `_sessionId` gave the right note for another session's `main`, the caller's own write, a subagent and a name that never existed. Not run across two real windows; the Git note is covered by tests only.

## Model tool-choice gap: ApplyDiff used instead of ChangeAccessibility - resolved (2026-11-07)

Model tool-choice gap: `ApplyDiff` used instead of `ChangeAccessibility` for `ReplaceBlockFormatted`, 67.5% of all ApplyDiff failures (2026-09-05) - lever 3 done 2026-11-05 (the CS0122 hint now names ChangeAccessibility); levers 1-2 (fixture prompt, `ChangeAccessibility` description) untested, re-run the model-eval batch to measure before pulling them - [doc](evals/closed/eval_replaceblockformatted_accessibility_cost_2026_09_05.md)

Evidence: Rerun 2026-11-07, 10 MinimalGuidance runs on unsloth/qwen3.5-9b: ApplyDiff is no longer in the surface, ChangeAccessibility called in 7 of 10 runs, and in both runs hitting CS0122 the hint led to it on the next turn. Levers 1-2 not pulled; numbers appended to the doc.

## MoveMember keep modes: remaining gaps - resolved (2026-11-07)

MoveMember keep modes: remaining gaps (2026-11-05; generic methods DONE per owner ruling D7: forwarder mirrors type parameters and constraints and passes explicit type arguments, tests in MoveMemberSuccessTests/MoveMemberForwarderTests) - keep modes on extension methods stay refused by design (`TargetIneligible`) until a use case appears, and a plain move is refused only when a receiver-style call `"a".Foo(1)` exists; a private moved method keeps a forwarder to a now-inaccessible member (CS0122, caught by the compile gate, no eligibility refusal yet); a generic containing type (`class Foo<T>`) gets no refusal, the dry-run compile gate catches a moved body that uses the containing `T` - [doc](proposals/closed/proposal_movemember_keep_delegator_and_same_class_calls.md)

Evidence: RequireKeepModeEligible refuses private and protected methods in keep modes (forwarder would be CS0122) and RequireNoContainingTypeParameters refuses a member using the source class type parameter unless the target declares the same name; MoveMemberRefusalTests +4, MoveMember tests 75/75 Tools.Advanced, 39/39 Basic, 112/112 Battery.Basic. Extension methods stay refused by design.

## ExtractInterface dropped signature comments silently - resolved (2026-11-07)

ExtractInterface still drops comments inside signatures and cannot warn about it (2026-10-21) - member doc comments are kept now; an inline comment in a parameter list is silently lost; a warnings field would need a new result type - [doc](proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)

Evidence: ExtractInterfaceAsync now adds one warning per member with a comment in its signature, surfaced as WarningDetails by ExtractMembersTool; ExtractInterfaceNewRootTests 11/11. Stripping stays (preserving a // comment in a parameter list would need multi-line interface signatures). Not covered: comments inside a property accessor list, which is rebuilt rather than stripped.

## SubAgent child Build reported thousands of false CS0246 errors - resolved (2026-11-07)

SubAgent child `Build` reports thousands of CS0246 errors (2026-11-07) - in the child worktree `Build(quickBuild)` returned 63116 errors because the test projects have no restored packages, so a model that builds there sees a failure it did not cause; restore in the worktree before the model runs or scope the child build to the production projects

Evidence: SubAgentChildServerLauncher.RestoreSolutionAsync (9718feab) restores the worktree solution before the child loads it. Live check 2026-11-07 on 192.168.1.113 with unsloth/qwen3.5-9b: child Build(quickBuild) Succeeded, whole run 57 s including the restore; transcript under RoslynSentinel-TestRuns/SubAgent/20261107-074448-514-abbddd64.

## SyncInterfaceToImplementation same-file path re-spaced irregular members - resolved (2026-11-07)

`SyncInterfaceToImplementation` same-file path goes through `ReplaceNodeAndFormatReplacementAsync`, which annotates the whole interface node, so irregular existing members are re-spaced (`int  A( ) ;` becomes `int A();`) - pinned 2026-10-21 by `AdvancedRefactoringNormalizeCharacterizationTests.SyncInterface_SameFile_RespacesIrregularMembers_Pinned`, fix (annotate only the added members) deliberately deferred 2026-10-21 (shared formatting code, cosmetic gain; revisit on a diff-noise report); the cross-file defect is fixed (CLOSED.md)

Evidence: Stale entry: already fixed in 13899fc2 (2026-10-22). AdvancedRefactoringEngine.SyncInterfaceToImplementationAsync annotates only the new members (comment at line 359); the pin was renamed and flipped to AdvancedRefactoringNormalizeCharacterizationTests.SyncInterface_SameFile_KeepsIrregularMembers.

## SubAgent returned an empty output when the answer was in reasoning - resolved (2026-11-07)

SubAgent returns an empty `output` when the model puts its final answer in reasoning (2026-11-07) - a SubAgent child run (qwen3.5-9b-coder) converged but `Content` was null and the answer sat in `ReasoningContent` (transcript turn 4), so the tool returned `output: ""`; fall back to the last reasoning text or report that the final message was empty (`ModelAgentRunner` / SubAgent result mapping, untraced)

Evidence: AgentTranscript.FinalMessage falls back from Content to ReasoningContent on the last turn; SubAgent adds a Caution finding when Output is reasoning or empty; SubAgentEvalResult and PlanStepRunner use the same helper. AgentTranscriptFinalMessageTests 6/6. No live child run yet.

## SessionHalted false positive from a timestamp-only touch of a file with no baseline - resolved (2026-11-07)

`SessionHalted` external-drift latch: false positive from a timestamp-only touch of a file with no recorded baseline (2026-09-15) - the message now self-reports scope (`DriftMessages.DriftHaltMessage`) and a content-hash gate covers files that have a baseline (`PersistentWorkspaceManager.cs:908-913`); 2026-10-20: the no-baseline path skips a Changed event when the document's loaded text equals disk (`IsTrackedDocumentUnchangedOnDisk`), project/solution files are hashed at load, and a folded document gets a baseline (`RecordFoldedDocumentBaseline`); 2026-10-21: a loaded `.csproj` edit now takes the full-solution reload (the per-project `OpenProjectAsync` branch always threw "already part of the workspace" and is deleted) and that reload re-records project/solution hashes (`RecordStructuralFileHashes`), pinned by `ExternalTouch_ProjectFileAfterDebounceReload_IsNotFlaggedAsDriftAsync`; 2026-11-05: `ExternalEdit_FoldedDocument_GetsBaselineHashThenTouchIgnoredAndEditFlaggedAsync` now also pins that the folded document holds recoverable text equal to the folded content (the case where Roslyn releases that text before a real edit cannot be forced deterministically, recorded in the test comment); the `.slnx` fixture tests were added 2026-10-21 (touch not flagged; edit flagged, re-baselined, later touch not flagged); failed reloads are now a sticky stale record that refuses source writes (the remaining visibility gaps are in the reload-failure TODO entry) - [doc](plans/closed/plan_session_halt_recovery_and_git_gaps.md)

Evidence: OnFileSystemChanged returns early when IsTrackedDocumentUnchangedOnDisk says the loaded text equals disk, and folded documents get a baseline (RecordFoldedDocumentBaseline); pinned by five CreateFileDeleteFileTests cases. Paths that still flag are deliberate (every doubt returns false): text not materialized, file locked or unreadable, non-document files, Created/Renamed/Deleted events. Partial-acknowledge policy and per-session halt granularity stay in plans/closed/plan_session_halt_recovery_and_git_gaps.md.

## MoveMember private-state TOOL-GAP - resolved (2026-11-07)

MoveMember private-state TOOL-GAP, mostly done (2026-10-17) - `fieldMap`, the ctor-assigned-field guard, `oldMember`/`forwarderReceiver`, same-class call rewrite and string-for-object coercion shipped (2026-10-18); a client-cached stale schema is only answered by the new hint. The mid-session toolset visibility gap is tracked in the P1 stale tool list entry. The class docs on WorkspaceTools/WorkspaceReadNavigationTools were verified current and need no change - [doc](proposals/closed/proposal_movemember_keep_delegator_and_same_class_calls.md)

Evidence: proposals/closed/proposal_movemember_keep_delegator_and_same_class_calls.md status IMPLEMENTED 2026-10-18 (slices 1-8), generic methods added later under D7. Remaining keep-mode gaps stay in the TODO.md "MoveMember keep modes" entry and client-side visibility in the stale tool list entry.

## MoveMember collateral-change tripwire leftovers - resolved (2026-11-07)

MoveMember collateral-change tripwire leftovers (2026-10-19) - only by-design gaps remain: the new-class file has no pre-image so is unchecked, and the compile gate skips a new file whose path maps to no project (`SolutionProjectLocator.FindContainingProject`); extending the tripwire to other tools is deferred (plan closed: [doc](plans/closed/plan_movemember_collateral_change_tripwire.md)); the dry run now returns `editSpans` (done 2026-10-19); see the collateral item in [issues/issue_movemember_open_followups.md](issues/issue_movemember_open_followups.md)

Evidence: Only by-design gaps remain: issues/issue_movemember_open_followups.md collateral item records that the new-class file has no pre-image and that a new file mapping to no project skips the compile gate. Extending the tripwire to other tools stays a deferral recorded in plans/closed/plan_movemember_collateral_change_tripwire.md (lines 85, 104).

## claude-lean could not write a new file in one call - resolved (2026-11-07)

Was: an implementer building a 174-line file seeded an empty class with `CreateFile`, then `ReplaceSnippet` refused the fill (over the size limit), its error advice offered `Member(replace)` for content holding several members, and `WriteFile`/`ChangeSignature` were on demand ([plan](plans/closed/plan_claude_lean_core_write_tools.md)).
Evidence: `CreateFile(content)` 25be5c3e; shape-aware oversize advice 046deb59; `--hide-tools` (the SubAgent child hides `WriteFile`) abdf8774; `WriteFile` and `ChangeSignature` always-on in claude-lean af0e830f (21 core tools). Live check 2026-11-07: a 6793-char `CreateFile(content)` landed in one call; a SubAgent child (qwen3.5-9b-coder) listed 20 tools without `WriteFile` and created its file in one `CreateFile(content)` call. ModelEval fixture A at host 113: 3/3 at 563f5ead, 3/3 at 046deb59, 2/3 at af0e830f; one 0/3 run in a dirty working tree is put down to another session's uncommitted edits (hypothesis). Not run: the Chain7 AFTER-only eval (weak baseline).

## Malformed `.csproj` reload was silent - resolved (2026-11-06)

Was: a syntax-broken `.csproj` reloaded with `workspaceStale: false`, `projectCount` unchanged and the project holding zero documents, because the reload-failure record only fired when the load threw, not when MSBuildWorkspace reported a failed project as a diagnostic ([doc](proposals/proposal_workspace_reload_failure_visibility.md)).
Evidence: `ProjectLoadFailureCollector` (`Common/WorkspaceReloadFailure.cs`) and `PersistentWorkspaceManager.cs`, W22 commit `10179fa8f7cd2aef71630cc1f4549f38de22b654`. Live check on 2026-11-06 at `facc36f9`: `workspaceStale: true` with `reloadFailure` about 4.7 s after the bad write, cleared on its own after the repair. Left as code TODOs: the post-write reload does not re-baseline after reverting a broken project; the collector matches on message text.

## `LoadSolution` unrestored-project hint looped - resolved (2026-11-06)

Was: `BuildUnrestoredHint` flagged projects whose `obj/project.assets.json` was older than the `.csproj`; a no-op restore does not rewrite assets.json, so the advice repeated forever.
Evidence: `FindUnrestoredProjects` compares restore inputs with `project.assets.json` and does not repeat advice, W22 commit `10179fa8f7cd2aef71630cc1f4549f38de22b654`. Live check on 2026-11-06: hint on a deleted `obj`, its advice worked, gone after restore. A project still unrestored on a repeat load gets no second hint, by design of the fix.

## Dry runs returned the full changed file by default - resolved (2026-11-06)

Was: `MoveMember(dryRun: true)` returned `changedContent` for a whole 3385-line file (190 KB), and `MoveType(dryRun, returnDiff: true)` returned both the diff and the full file.
Evidence: `AppliedChangeSummary` dry runs are compact, W22 commit `10179fa8f7cd2aef71630cc1f4549f38de22b654`. Live check on 2026-11-06: `MoveMember` and `ExtractMembers` dry runs return `lineChanges` and a note with no diff unless `returnDiff` is true. `Member` add dry runs still return a diff by default.

## Server-side telemetry for tool call counts and error rates - built per owner ruling D5 (2026-11-05)

Was: "Server-side telemetry/metrics for tool call counts and error rates (2026-09-05) - idea, not implemented; owner decision pending on exposure and persistence" ([idea](ideas/closed/idea_server_telemetry.md)).
Evidence: `ToolTelemetry` singleton (Common) updated by one call-tool filter (`ToolTelemetryRecorder`, registered 2nd, right after Echo, so it wraps ToolError and both breakers); exposed as `toolTelemetry` on `McpServerStatus`; `ToolTelemetryFlushService` writes `server-telemetry_<instanceId>.json` in place into explicit instance folders every 60 s when dirty and on shutdown; `roslynsentinel-mcp-launch.ps1` copies it to `<root>\telemetry\` before sweeping a stale instance; guidance-adherence counters (issued/followed/succeeded/ignored) via `ToolCallHint`; `scripts/Get-TelemetryRollup.ps1`. Tests: `ToolTelemetryTests`, `ToolTelemetryFlusherTests`, `ToolGuidanceTrackingTests`, `ToolTelemetryFilterOrderTests` (pins the filter order); `RoslynSentinel.Tests.Server` 617 passed, 0 failed, 9 skipped; `scripts/Test-TelemetryRollup.ps1` 109 checks on PS 5.1 and 7; Build 0 errors. Doc: [reference_server_telemetry.md](references/reference_server_telemetry.md). Live check 2026-11-06: the status field works, the launcher sweep copied a file to `telemetry\`, and at `4f511a8c` the `StopServer` flush wrote the file with the `McpServerControl` row (it did not before W24; the flush now runs on `ProcessExit`).

## `SyntaxTargetResolver` extraction follow-ups - closed by owner rulings (2026-11-05)

Was: "RenameSymbol cannot merge into an existing signature (design: a merge mode retargeting callers to the existing member; owner decision); `Describe*` formatters unification; `Member(remove)` refusal list size; keyword-resolution symptom" ([proposal](proposals/closed/proposal_syntax_target_resolver_extraction.md)).
Evidence: owner ruling D6 - no RenameSymbol merge mode; a new name colliding with an existing member signature stays a refusal, pinned by `RenameSymbolTests.RenameSymbol_NewNameCollidesWithExistingMemberSignature_IsRefusedAndWritesNothing` (CS0111 plus the "collides with an existing declaration" hint). The three `Describe*` candidate formatters in `SymbolNavigationEngine.cs` stay separate and now carry cross-referencing XML docs (inputs, caps, wording, when to use which). Owner ruling D9 - `MemberRemoveTests.RemoveMember_FifteenHundredCallers_RefusalStaysBounded` and `RemoveMember_OneMethodWithFifteenHundredCalls_RefusalStaysBounded` pin the old 425 KB "1006 caller(s)" report (measured refusal ~2.6 KB / ~2.4 KB for 1500 callers, 10 listed). The keyword-resolution symptom did not reproduce (name-directed `SnippetSymbolLocator` overload). Build 0 errors; `RoslynSentinel.Tests.Tools.Advanced` 558/558 pass.

## Read-tool metadata envelope for other tools - closed by owner decision (2026-11-05)

Was: "Read-tool metadata envelope (`isComplete`/truncation flag) for the read tools other than `ReadFile`/`GetMethodSource`/`GetFileOutline` ... owner decision pending on the `GetMethodSource` not-found contract and any other tool."
Evidence: owner ruling D4 (decisions_autonomous_run_2026_11_05.md "Owner rulings"): no further tools get `ReadEnvelope` (Search/ListAll already report `hasMoreData`/`totalRecords`), and `GetMethodSource` not-found stays an `isError` `MethodNotFound` result with candidates; the old spec's `MethodFound=false` success shape is rejected because it would hide misses from `isError`-based handling and telemetry.

## `MoveType` could not lift a nested type to top level - resolved (2026-11-05)

`MoveType(destination: ownFile)` on a nested type now rewrites the qualified `Outer.Nested` references (member access, qualified names, crefs, fully qualified forms) solution-wide in the same atomic change, via `SymbolFinder` and the semantic model (`NestedTypeReferenceLifter`, called from `BasicRefactoringEngine.MoveTypeToFileAsync`), and adds the namespace using where needed. Known limits: references through a derived type, `global::`-qualified names and generic containing types are not rewritten; a private/protected nested type is not widened.
Evidence: `MoveTypeToFileTests` (multi-file CS0426 fixture + no-outside-references case) pass; Build 0 errors.

## `MoveMember` did not carry needed usings to the target file - resolved (2026-11-05)

`MoveMemberUsings.BuildMissingUsingsEdit` adds the plain `using` directives the moved code needs (found semantically; carried only when the source file has them and the target lacks them) to the target document's edits on the existing-class, base-type and instance-to-existing-class routes. The new-class routes already copied all usings.
Evidence: `RoslynSentinel.Tests.Basic/MoveMemberCarriesUsingsTests.cs` (9 tests); 116 related Battery.Basic MoveMember/instance-move tests pass; Build 0 errors.
## `using static RoslynFormattingHelper` imports in five Advanced engines - resolved (2026-11-05)

The 47 unqualified helper calls in AdvancedStructural/AsyncOptimization/CodeStyle/LogicOptimization/Modernization engines are now `RoslynFormattingHelper.X(...)`, and the imports plus their "deliberate exception" comments are removed. Done with per-file `ReplaceSnippet` batches (two sweep dispatches): `SemanticFindReplace` has no operation that qualifies static-import calls (only invertBoolean, replaceConstruction, removeNullForgiving).
Evidence: `Search` for `using static .*RoslynFormattingHelper` returns 0; Build 0 errors; full solution run 6605 tests, 0 failed.

## `ChangeSignature` and `GetCallGraph` refused an ambiguous name but could not narrow it - resolved (2026-11-05)

Both tools now take `contextSnippet` (plus `lineBefore`/`lineAfter`, shared `ToolParams` descriptions). `GetCallGraph` resolves overloads through `ResolveCallGraphMethod` (no first-overload pick, `// TODO overloads` removed); `NameTargetLookup.RequireSingle` gained an optional `retryCall` so the Ambiguous hint is a valid `ToolCallHint.Call` naming the real parameter. Other `RequireSingle` callers keep the old wording. Live check after restart still pending.
Evidence: `RoslynSentinel.Tests.Tools.Advanced/AmbiguousNameTargetToolTests.cs` (new ChangeSignature and GetCallGraph cases); full solution run 6604 tests, 0 failed; Build 0 errors.

## `Member(addMember)` flattening the continuation indent of expression-bodied methods - not reproducible (2026-11-05)

A CRLF fixture with an inserted two-line `=>` method (`AddMemberAsync` and `InsertMemberAfterAsync`) keeps the 8-space continuation and leaves siblings unchanged. Untested: LF files, tab indents, a caller body indent that differs. Reopen with the exact before/after text if it recurs.
Evidence: `RoslynSentinel.Tests.Tools.Basic/MemberAddExpressionBodyIndentTests.cs` (two passing tests); [finding](findings/closed/finding_member_add_member_flattens_expression_body_continuation_indent.md).

## `Member` dispatcher was a 317-line method - resolved (2026-11-05)

`RefactoringStructuralImpl.Member` is now a 54-line switch over private `MemberViewAsync`, `MemberReplaceAsync`, `MemberRemoveAsync`, `MemberAddTopLevelTypeAsync`, `MemberAddEnumMemberAsync` and `MemberAddToContainerAsync` (bodies moved verbatim), so `GetMethodSource` no longer offloads it. Also: the `Member(remove)` refusal now lists at most 10 callers and 10 implementations with a "(+N more)" marker.
Evidence: `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs`, `RoslynSentinel.Tests.Tools.Advanced/MemberRemoveTests.cs` (`RemoveMember_ManyCallers_RefusalListsAtMostTenAndMarksTheRest`); 257 `Member` tests in Tests.Basic pass.

## SubAgent tools did not wait for the solution load; embedded `BuildVerification` was not projected - resolved (2026-11-05)

`SubAgent` / `SubAgentEval` now call `SubAgentModelRun.AwaitSolutionLoadAsync` (same bounded `SolutionLoadWait.DefaultTimeout` wait as `GetCurrentSolutionAsync`; `SolutionLoadTimeout` error if still loading) before reading `SolutionPath`. `GetDiagnostics` (verify != noBuild) projects its embedded `BuildVerification` through `BuildResultProjector` with the dependency graph and offloads full diagnostics like `Build`; `GetWorkspaceHealth` projects with cap 20 and no graph (`// TODO` for graph + offload).
Evidence: `RoslynSentinel.Tools.Advanced/SubAgentModelRun.cs` (`AwaitSolutionLoadAsync`), `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs` (`GetDiagnostics`), `RoslynSentinel.Tools.Basic/WorkspaceHealthMiscImpl.cs`, `RoslynSentinel.Tests.Tools.Advanced/SubAgentModelRunTests.cs` (no-wait paths only; the Loading/timeout path is untested, `// TODO` in file), no test for the projected `BuildVerification` (no failing-build fixture).

## Snippet wrong-node-kind leftovers (hover-info fallback) - resolved (2026-11-05)

`SymbolNavigationEngine.GetSymbolInfoAsync` now words "snippet matched but no symbol there" with `SnippetNodeKindError.Describe` (same helper as the other sites); the old raw "No symbol found at snippet" text remains only as the fallback when the syntax root is unavailable.
Evidence: `RoslynSentinel.Tests.Battery.Advanced/BatteryTwentyFiveTests.cs` (`GetSymbolInfoAsync_SnippetMatchesKeywordOnly_ErrorNamesNodeKindAndSaysNoSymbol`).

## `Search` / `FindReferences` failures and zero results lacked a next step - resolved (2026-11-05)

`Search` "query is required for mode" now ends with a ready `Search(mode, query)` example, and the fileGlob-matched-nothing warning ends with a rerun-without-fileGlob call and a project-rooted glob example (the 297 historic InvalidArgument events were mostly that glob case, now a warning). `FindReferences` zero-result status messages (callers, implementations, all) now state the scope searched (tests included or not) and a `Search(mode: text, query: <symbol>)` call. The rest of the "Failure messages without a next step" entry stays open in TODO.md.
Evidence: `RoslynSentinel.Tests.Tools.Basic/SearchModeTests.cs`, `SearchSolutionTextRecoveryHintTests.cs`, `FindReferencesIncludeTestsTests.cs` (`Callers_SymbolWithNoCallers_StatusMessageExplainsScopeAndOffersSearch`).

## `FindReferences` failures lacked a ready-to-run next call - resolved (2026-11-05)

Snippet-miss and name-miss messages from `FindCallersAsync` / `FindImplementationsForMemberAsync` (the `FindReferences` engine path) now end with a `ToolCallHint` `Next: FindReferences(...)` call using the best candidate's filePath plus verbatim declaration line (a type gets filePath only). The file-pinned case wraps the `SnippetSymbolLocator` snippet-miss exception and appends the call; the Did-you-mean list ranks exact-name matches first, then non-test types, non-test members, then test code (`RankCandidate`). Re-measure with `scripts/Get-NotFoundRecoveryReport.ps1 -Tool FindReferences` after the next restart.
Evidence: `RoslynSentinel.Tests.Advanced/BugFixTests.cs` (`*_EndsWithFindReferencesNextCall` x5, `FindCallersAsync_DidYouMean_RanksTypeAheadOfSameNamedTestMethod`), `FirstMatchRatchetTests` baseline entry for `BuildFindReferencesNextCallForFile`.

## File-not-found messages checked loaded documents only - resolved (2026-11-05)

The three copies (`WorkspaceReadNavigationImpl`, `WorkspaceFileEditImpl`, `DocumentLookupResult.Describe`) now call one helper, `RoslynSentinel.Common/FileNotFoundMessage.Build`. It reports a directory passed as a file, a `.csproj`/`.sln`/`.slnx` passed as a source file, and a file that exists on disk outside the loaded solution (naming the nearest `.sln`/`.slnx`, else `.csproj`, as a `LoadSolution(...)` call when callable), before the closest-path and no-match wording.
Evidence: `RoslynSentinel.Tests.Tools.Basic/FileNotFoundMessageTests.cs` (12 tests, one per branch), `CallableToolHintTests` and `ToolHintRatchetTests` pass.
## `RenameSymbol` could not rename a namespace - resolved (2026-11-05)

Roslyn's Renamer already rewrote namespace declarations, usings and qualified names across files and projects; the gaps were an accepted dotted `newName` (wrong rewrite) and a null updated handle. Now `docCommentId` `N:Ns.Name` with `newName` = the new last segment works; a dotted or non-identifier `newName` and the global namespace are refused with an actionable error; the updated handle is `N:<parent>.<newName>`. Follow-ups (Search(namespace) docCommentId, noisy residualMentions) are in TODO.md.
Evidence: `RoslynSentinel.Tests.Tools.Advanced/RenameNamespaceTests.cs` (5 tests, Lib + App fixture, namespace in 3+3 files), claude-lean schema budget tests pass.

## `CreateProject` drifted its own files and halted the session - resolved (2026-11-05)

`CreateProjectAsync` now calls the new `IWorkspaceHealthReporter.RegisterOwnWrites(paths)` (csproj, solution file(s), every non-bin/obj file under the project dir) after the child process and the EOL pass: it records content-hash baselines (BOM for `.cs`) and clears those paths from the drift list; unrelated drift stays. A non-zero child exit now throws `ToolInvalidArgumentException` (a `$LASTEXITCODE` guard between `dotnet new` and `dotnet sln add` makes a failed `dotnet new` visible). Live check still open: whether the new project becomes visible to Roslyn without `LoadSolution` (the hash-match path no longer arms the debounce reload; events during the child run still do).
Evidence: `RoslynSentinel.Tests.Tools.Basic/CreateProjectOwnWritesTests.cs` (3 tests: no halt after csproj write + template delete, unrelated drift still flagged, invalid template throws).

## Record-parameter gaps after the locator fix - resolved (2026-11-05)

FindImplementationsForMemberAsync and DescribeNameOnlyCandidates now handle positional record parameters (FindPositionalRecordParameters helper; no-snippet filePath lookup falls back to the synthesized property; snippet-miss message lists the parameter). The // TODO in SnippetSymbolLocator is removed. Finding: [finding](findings/closed/finding_snippet_locator_misses_record_positional_parameters.md).
Evidence: tests FindImplementationsForMemberAsync_PositionalRecordParameter_* and DescribeNameOnlyCandidates_PositionalRecordParameter_ListsParameterLine in RoslynSentinel.Tests.Advanced/BugFixTests.cs; full-solution RunTest 6556 total, 0 failed.

## `Build(fullBuild)` ignored `scope` - resolved (2026-11-05)

`RunFullBuildAsync` now takes `scope`/`scopeName`: project scope runs `dotnet build <project.csproj>`, file scope builds the containing project, unknown names return `BuildNotRun` with a `ListAll` hint, `ProjectsCompiled` lists the project plus its references; solution scope unchanged. The "scope was ignored" StatusMessage warning and the description text were removed. Evidence: `BuildScopeFullBuildTests` (6 tests, two independent Good/Bad projects); design [proposal](proposals/closed/proposal_build_full_build_ignores_project_scope.md) Option 1. Live check after server restart pending.

## `RunTest` teardown-failure entries: parser tests against real output - resolved (2026-11-05)

- `TestRunEngine.ParseFixtureTearDownFailures` entries are prepended (2026-10-22); golden-file parser tests over real `dotnet test -v quiet` output (`Resources/Golden/dotnet-test-teardown.txt`: ordinary fixture and `[SetUpFixture]`) landed 2026-11-02. The hand-written `ParseFixtureTearDownFailures_OrdinaryFixtureOneTimeTearDown_ReturnsOneEntry` was redundant with `..._RealDotnetTestOutput_OrdinaryFixtureCarriesTeardownMessage` (same prefix, message, ExampleRef, count assertions) and was deleted 2026-11-05. Known quirk kept: a SetUpFixture message can pick up the preceding "One or more child tests had errors" line. The aggregate `RunAsync` path end to end is deliberately untested (prepend tests cover the merge).
- Evidence: `TestRunEngineLockDetectionTests.cs` outline; Build 0 errors; full solution RunTest 0 failures (2026-11-05).

## Wrap the 9 unwrapped `[McpServerTool]` returns in `SentinelCallToolResult` - resolved (2026-11-05)

- McpServerStatus, GenerateClassesFromJson, GenerateHttpClient, GenerateDefaultConfigJson, InterpolateStringSafe, DescribeAdvancedToolOptions, ProjectDoc, ExternalFileDrift and McpServerControl now return the wrapper. Commit 295c2227; full solution run 6542 tests, 0 failed. Live check after restart: unconfirmed `StopServer` refused (`ConfirmationRequired`), `ExternalFileDrift(Status)` and `McpServerControl(ListTestInstances)` return `successData`. Wire shapes moved under `successData`/`errorData`. Open code TODOs: ProjectDoc missing-file read reports `Exception` not `NotFound`; legacy string wrappers `AcknowledgeDrift`/`ControlServer` removable once their direct-call tests move. Decisions: `decisions_autonomous_run_2026_11_05.md`.

## `contextSnippet` "not found" wording, Step 5 (wrong-node-kind family) - resolved (2026-11-02)

- The "position resolved but wrong node kind" messages were unlabeled raw text at 11 sites. They now all call `SnippetNodeKindError.Describe(text, root, match, expectedNode, passInstead)` in `RoslynSentinel.Common/SnippetMatching`, which names the node kind actually found at the contextSnippet; exception types and error codes are unchanged. Commits: c96442fe (helper), 58964bbe (`AdvancedStructuralEngine` x3), f413b117 (`MappingEngine`), 980d9004 (`CodeGenerationEngine`, `SemanticRefactoringEngine`), 02d1692f (`MsToolAugmentEngine` x5). Steps 1-4 of the plan shipped 2026-09-15/16; `ContextErrorBuilder` and `DiagnoseNoMatch` were later deleted in 361263f4 (replaced by `Common/SnippetMatching`).
- Remaining gaps (open in TODO.md P2 "Snippet wrong-node-kind leftovers"): optional sites `ImpactAnalyzer.cs:73` and `SymbolNavigationEngine.cs:445` deferred; the `ExtractMethodSafeAsync` site (~1534) is untested; `RealProjectPatternTests` asserts only the prefix. Deprecation tracking and the `NearMissList` 3-candidate cap stay a separate P3 item.
- Docs: plan [plan_contexterrorbuilder_orienting_guidance.md](plans/closed/plan_contexterrorbuilder_orienting_guidance.md) (IMPLEMENTED, moved to closed/); audit [finding_snippet_notfound_wording_confusion_audit.md](findings/finding_snippet_notfound_wording_confusion_audit.md) stays open.

## `LoadSolution` gave no hint when a project loaded degraded for lack of a restore - resolved (2026-11-02)

- An unrestored project yields 0 `WorkspaceDiagnostic`s (MSBuildWorkspace 5.9.0, SDK 10.0.401); the missing package shows only as CS0103/CS0246 compile errors, so there was no load text to match. `BuildPostLoadHint` now flags projects structurally: `obj/project.assets.json` missing, or older than the `.csproj` or the nearest `Directory.Packages.props` (`FindUnrestoredProjects`, `BuildUnrestoredHint` in `WorkspaceProjectManagementImpl`), and points at `Build(fullBuild)` then `LoadSolution(forceReload)`. Evidence: `WorkspaceUnrestoredProjectHintTests` (2 tests). Not covered: the full sentence through a live `LoadSolution`; `GetSolutionAsync` in `LoadSolution` is not try/caught. Proposal: [proposal_load_solution_hint_for_unrestored_project.md](proposals/closed/proposal_load_solution_hint_for_unrestored_project.md).

## Small tool fixes: Member(remove) self-recursion, Build fullBuild scope note, stale comments - resolved (2026-11-02)

- `Member(remove)` refused a self-recursive private helper (its own recursive call counted as a usage). `RemoveMemberAsync` now ignores references inside the member's own span. Evidence: `RemoveMember_SelfRecursivePrivateHelper_IsRemoved` and `RemoveMember_SelfRecursiveHelperAlsoCalledElsewhere_StillRefused` pass. A `cref` in the member's own doc comment is leading trivia and still counts (not handled).
- `Build(level: fullBuild, scope: project)` silently built the whole solution. Not changed to honour scope; the descriptions now say so and a full build with a non-solution scope appends "Scope '...' was ignored" to `StatusMessage` (`WorkspaceBuildTestImpl.Build`, `BuildDescription`, `BuildTool` scope description). Also fires for `fullBuildThrowaway`.
- Stale header comment in `SearchSolutionTextRecoveryHintTests.cs` reworded (advice is in `StatusMessage`, `WarningDetails` stays null). Stale `// TODO throw-migration` in `BatteryTwentyOneTests.UpgradeToPrimaryConstructor_MissingFile_ThrowsNotFound` removed (assertion was already typed).
- `ConvertMethodToPropertyAsync` and `MakeMethodStaticAsync` use `DocumentLookup.RequireDocument` instead of an inline "Document not found." throw.
- Evidence: Tests.Basic 1030/1030, Tests.Tools.Basic 1322 passed + 1 skipped, Tests.Tools.Advanced 527/527.

## Test construction factories: Tests.Support.Basic / Tests.Support.Advanced rolled out to every test project - resolved (2026-10-28)

About 1,450 `new Engine(...)` / `new Tools(...)` sites in 9 test projects now call static factories in two non-test
projects (`RoslynSentinel.Tests.Support.Basic`, `RoslynSentinel.Tests.Support.Advanced`) built on
`ActivatorUtilities` + a fresh container per call, so a new resolvable constructor parameter is absorbed in one place.
Supersedes the earlier BLOCKED entry for the pilot Step 4 (`SemanticFindReplace` unreachable until a fresh session).
Evidence: trial `ValidationEngine` constructor change broke 0 test files (2 production sites only); `Tests.Tools.Basic` 158 s
vs 159 s and `Tests.Advanced` 27.5 s vs 28.3 s against the pre-pilot commit `171037b6`. Plans:
[plans/closed/plan_tests_support_projects_factory_rollout.md](plans/closed/plan_tests_support_projects_factory_rollout.md),
[plans/closed/plan_test_construction_factories_pilot.md](plans/closed/plan_test_construction_factories_pilot.md).
Remaining `new` sites and follow-ups are listed in the rollout plan's Step 5e/6 notes and the open TODO.md entry on object initializers.

## Server.Basic folded into Server.Advanced; engine and tool-dependency registration moved to Engines.*/Tools.* - resolved (2026-10-27)

Evidence: commits 4b9d998d, b875d27a, 9f6860d5 (fold; [proposal](proposals/closed/proposal_unify_server_projects.md)) and 8417587a
(registration move-down; [proposal](proposals/closed/proposal_registration_move_down.md)). Build 0 errors, Tests.Server 472, Tests.Advanced 1500,
Tests.Battery.Basic 179/179, new tool-class resolve tests 4/4 and 3/3; tool surface identical after restart.

## `.cs` files kept acquiring LF and mixed line endings; no EOL policy, guard checked pre-normalization text - resolved (2026-10-26)

All 693 tracked files were `i/lf` and the working tree held 233 LF and 80 mixed files, invisible to `git diff`. Fixed by
`plan_eol_policy_and_write_chokepoint.md` Steps 0-8: `scripts/Get-EolAudit.ps1` and a working tree normalized to CRLF; `--eol auto|crlf|lf`
(`EolPolicyOptions`), `EolResolver.Resolve` and `EditorConfigEolReader`; the chokepoint converts the whole file to the resolved EOL and refuses only
a non-uniform result (`EolWritePolicy`, `a4a6e114` through `df0ad2d9`, cleanup `77b4d801`); `EolFileWriter` for the two workspace writes outside the chokepoint
(`64c5d590`); `IEolResolver.ResolveEol` / `ResolveEolAsync` and the required-`eol` `RoslynFormattingHelper` overloads with all engines moved (`f9bed9f9`
through `8cc62db6`); `DocumentationTools` doc writes no longer keep LF (`92fbac4e`). `InsertMemberDeclarationAndFormatInsertionAsync` no longer does a
whole-file `NormalizeEol` (verified 2026-10-26): it now takes a required `eol` and applies it to the inserted trivia only. Guards:
`BareFileWriteRatchetTests`, `NewlineLiteralRatchetTests` (`798b0098`), `EolAuditRatchetTests`, and the `build.ps1` EOL audit.
Evidence: `Search`/`GetMethodSource` on the 2026-10-26 source; `docs/current/references/reference_code_file_write_paths.md` ("EOL policy").
Closed 2026-10-26 afterwards: `Tools.Basic` pre-normalisation uses `ResolveEol` (`bc9727ef`); `ApplyOutcome.Findings` (`f00dc7a0`) and the direct-write
tools (WriteFile, DeleteFile, ApplyDiff, CreateFile, ReplaceSnippet/Batch) lift chokepoint findings to the response `findings` array (`2a65e4e2`);
Step 9 shipped as a `ReadFile` finding that reports mixed line-ending counts and minority line numbers, no new tool class (`1d63c3e2`). Decided not to
add a `CreateFile` EOL parameter or description note (a parameter breaks the single `--eol` policy, a note costs prompt tokens every session; the chokepoint
already converts new files).
Open follow-ups stay in TODO.md ("EOL policy follow-ups").

## Tool defects pinned by the Battery tests (#1-#24) - resolved or accepted (2026-10-25)

Fixed: #1-#11 #13-#19 #21 #23 plus the #12 comment part and the #20 disk reads (hashes in the plan's Status line). #12 stray CRLF on a blank line is superseded by `plan_eol_policy_and_write_chokepoint.md` Step 5 (whole-file EOL conversion at the chokepoint in every mode; the pin comment is reworded, 82a8771f). ACCEPTED, marked inline and not to be re-litigated: `FormatDocumentAsync` as dead code kept for its 11 tests; `InlineParameter` and `ExtractSuperclass` refuse with NotImplemented; #22 and #24 ride the throw-migration (see the `throw-migration` TODO). The three remaining #20 feature gaps stay open in TODO.md. Evidence: `plans/closed/plan_pinned_tool_defect_fixes.md`, `findings/closed/finding_pinned_tool_defects_from_battery_reorganisation.md`.

## No tool removed a redundant null-forgiving `!` across files - resolved (2026-10-25)

`DocumentEditResult.UpdatedText` became non-nullable, leaving 241 redundant `UpdatedText!` in 47 test files and no
semantic tool to strip them. Fixed by a third `SemanticFindReplace` operation, `removeNullForgiving` (1934c0f3),
applied in 600251b0 (205 sites, 46 files) and to `MemberRefactoringTests.cs` (36 sites) once its owning session
committed it; 53 redundant `UpdatedText ... Is.Not.Null` asserts were then simplified (47e03a6b and the last file).
Doc: [plan_semantic_find_replace_remove_null_forgiving.md](plans/closed/plan_semantic_find_replace_remove_null_forgiving.md).
Evidence: solution-wide `Search` for `UpdatedText!` and `UpdatedText.*Is.Not.Null` returns 0 matches. Open follow-up: the
claude-lean client does not list on-demand tools enabled mid-session (blockers/closed/blocking_error_toolset_enable_not_visible_in_client_tool_list.md).

## Tool-call hints omit required parameters - resolved (2026-10-25)

Every server-emitted hint naming a tool lacked `reason` (all 137 tools) and often `filePath`/`query`/`solutionPath`.
Fixed by `ToolCallHint.Call` (RoslynSentinel.Common), validated against `ToolSignatureCatalog`, with a ratchet scan
test (`ToolHintRatchetTests`) and an empty baseline (`docs/known-handwritten-tool-hints.txt`). Conversion slices by
project: Common, Engines.Basic, Engines.Advanced, Server.Basic, Tools.Advanced, Tools.Basic, GitImpl/GitTools (26ee119f),
CodeTransformTools, PlanStepRunner. Doc: [proposal_tool_hint_builder.md](proposals/closed/proposal_tool_hint_builder.md).
Evidence: the validator also caught a stale tool name (`flag_migration_candidates`) and an invalid enum value
(`ListAll(kind: types)`) that no test had flagged. Open follow-ups are in TODO.md.

## Retire EditOutcome.Unset - resolved (2026-10-24)

Re-parented `BatchResultSummary` (keeps `ChangeId`), `BridgeBatchResult`, `BestInsertionResult` (keeps `FilePath`) and `GenerateToStringResult` off `EngineResultBase` (`743283d6`), made `Outcome` `required` on `EngineResultBase` and `ProjectEditResult` (`1daccad1`), then removed `Unset`; 0 is now unnamed and maps to Failed through `ToResultStatus`'s `_` arm, and `GuardEngineResult`'s `default` arm stays for undefined values (`d120b7b9`). Evidence: full build 0 errors, test failures limited to the known baseline set. Wire change: `outcome`, `message`, `isCommitted` (and `filePath` on the batch types) no longer appear in the output of the batch tools, `GetBestInsertionPoint` and `GenerateToString`.

## Failed respawn build was invisible to the agent - resolved (2026-10-24)

The launcher fell back to the newest complete build when the new HEAD failed to build, visible only in `launch.log`. It now writes `instances\<id>\launch-outcome.json`; `McpServerStatus.lastLaunch` and a one-shot `launchWarning` report it, and StopServer warns about uncommitted server source. Evidence: e592a4e2; [plan](plans/closed/plan_surface_failed_launch_build.md); [blocker](blockers/closed/blocking_error_restart_reuses_stale_commit_keyed_build.md). Live check: respawn reported `lastLaunch.buildFailed:false` and StopServer returned the warning; the failed-build path is unit-tested only.

## `Search(text)` recovery hint duplicated, and positional-record snippets did not resolve - resolved (2026-10-24)

The zero-match hint no longer appears in `warningDetails` or a third `Findings` copy, and the long generic paragraph is dropped when a specific hint exists. `ListAll` (declaration kinds) and `attributeUsages` now scan test projects and report hidden hits per project with an `includeTests: true` hint. `SnippetSymbolLocator` skips snippet indentation and maps a positional-record parameter to its synthesized property; `FindCallers` resolves a record parameter by name and drops the internal "FindCallers:" prefix. Evidence: `RoslynSentinel.Tests.Tools.Basic` 1179 of 1183 passed (the one failure is a git-environment test), `BugFixTests` record-parameter tests 4/4, `RoslynSentinel.Tests.Tools.Advanced` attribute tests 65/65. Plan: [plan_search_text_scan_all_projects_and_recovery_hints.md](plans/closed/plan_search_text_scan_all_projects_and_recovery_hints.md); finding: [finding_snippet_locator_misses_record_positional_parameters.md](findings/closed/finding_snippet_locator_misses_record_positional_parameters.md).

## `GetMethodSource` MethodNotFound was a one-line dead end - resolved (2026-10-24)

The message gave no next step (22 transcript cases, 0% guided, 18% recovered). It now adds, on the failure path only, a qualified-name hint, a same-file other-kind note, a solution-wide list of matching methods and constructors (cap 5, `FindDeclarationsAsync`), a no-match statement with a `Search` fallback, and the methods the file declares. Evidence: f543fa51 (`WorkspaceReadNavigationImpl.BuildMethodNotFoundErrorAsync`); `GetMethodSourceTests` 11/11 and `RoslynSentinel.Tests.Tools.Basic` 1184 passed, 0 failed, 1 skipped. Remaining offenders: TODO.md (file-not-found copies, `FindReferences`, `Member`).

## `Inline(method)` changed meaning at call sites where the template's names bind differently - resolved (2026-10-22)

- `InlineMethodAsync` Pass 1 now runs `CollectInlineTemplateNames` (defining model) and `ThrowIfInlinedNamesDoNotRebind` (per call site): explicit non-`this` receivers are refused when the template needs the defining instance or is not a plain name chain (receiver dropped), every used member must be accessible at the call position, and the substituted expression is bound speculatively so each name must resolve to the same symbol; failures throw `ToolTargetIneligibleException`. Removes the `// TODO no accessibility check`. Evidence: seven `BUG_InlineMethod_*` tests in `BugFixTests.cs` (instance field from another type / other instance, private static from another type, unqualified static from another type, call-receiver, plus the two positive same-type and stateless cases).

## `Inline(method)` silently picked the first same-named method - resolved (2026-10-22)

- `StructuralRefactoringEngine.InlineMethodAsync` now refuses with `ToolTargetIneligibleException` ("declared N times in 'File.cs' (Type.Name(params), ...)") when the defining file declares the name more than once (overloads, or the same name in two types); a unique name behaves as before. Evidence: `BUG_InlineMethod_OverloadedName_IsRefused`, `BUG_InlineMethod_SameNameInTwoTypes_IsRefused` in `BugFixTests.cs`; the tool has no overload selector, so refusal is the safe answer.

## Pinned tool defects from the Battery reorganisation, first batch - resolved (2026-10-22)

- Fixed under `plans/plan_pinned_tool_defect_fixes.md`; each fix flipped its pinning test and removed the `// TODO`. `SyncTypeAndFilename` on a matching file is success without a write (a51fb3bd); `UpgradeToPrimaryConstructor` keeps the parameter list on the header line (1ddc6f79); `ConvertToSwitch` and `ConvertToPattern` return `AlreadyInTargetState` when nothing converts (05a0831c, 7a0d8fbe); `WrapRange wrapper=using` wraps from the range's first `IDisposable` declaration and otherwise refuses with `TargetIneligible` (5c99ee20); `ExtractMembers newType=superclass` refuses with `NotImplemented` (7728fbd0); `ExtractMembers newType=partial` removes the moved members from the source and adds `partial` (c33aeec8); `GenerateMapping` writes a new `{From}To{To}Mapper.cs` and never overwrites the source (9e5bc5ff). Earlier in the same plan: #1, #5, #6, #11, #13, #14, #15, #17 (63c46bfc, 92567696, 4da6d32c, cc95ff2d, 81d25534, 02ca2fb6, 668cc3d2). Open items: TODO.md "Tool defects pinned by the Battery tests".

## Pinned tool defects from the Battery reorganisation, second batch - resolved (2026-10-22)

- `ExtractMethodSafe` keeps comments between the selected statements (13a5333b); `FormatDocumentSafe`, `AnalyzeForeachForLinq`, `ExtractConstantSafe` and `GenerateToStringSafe` read the source from the workspace first via `ReadSourceAsync`, so in-memory documents work (1fc7ccfe); `RunMicroRefactoring` and `FormatDocument` return `AlreadyInTargetState` for a no-op (24e3b2b4, 4e651693); `ConvertAnonymousToNamed` infers property types from the semantic model, rewrites the anonymous expression to `new Class { ... }` and returns two files (8813f43a). Open items: TODO.md "Tool defects pinned by the Battery tests".

## Fake-write audit: unexplained `Tools.Basic` failures - resolved, flake (2026-10-22)

- The shell `dotnet test` audit run (2026-10-21) showed 2 `Tools.Basic` failures that `RunTest` did not reproduce. Rerun through `RunTest` with `environment: ROSLYNSENTINEL_FAKE_WRITE_AUDIT` (1101/1102): the audit file was never created (no write attempt on a `ReadOnly` fake) and the one failure, `BuildCascadeFixtureTests.FullBuild_UpstreamProjectFails_DownstreamProjectsReportNoErrors` (5 min real build, "No data on Failure result"), passed 10/10 in isolation. Treated as a slow-build flake under load. Evidence: [proposal](proposals/closed/proposal_battery_test_reorganisation_in_memory_and_shared_sources.md).

## `RunTest` surfaces a failing fixture `[OneTimeTearDown]` - resolved (2026-10-21)

- `TestRunEngine.ParseFixtureTearDownFailures(stderr)` turns the "TearDown failed for test fixture <Name>" stderr block (name, message up to `StackTrace:`) into `failureSummary` entries, wired into `RunOneProjectAsync` and the aggregate in `RunAsync`; counts and `RunSucceeded` are unchanged. Evidence: five tests in `TestRunEngineLockDetectionTests` (real sample captured from a scratch NUnit 5.0.0 project). Remaining gaps are in TODO.md.
- 2026-10-22: the teardown entries are now prepended (`TestRunEngine.PrependTearDownFailures`) in both the per-project and aggregate paths, so `TestRunResultProjector.MaxFailureGroups` (10) cannot drop them; pinned by two `PrependTearDownFailures_*` tests.

## `Inline(kind: method)` argument binding and precedence parentheses - resolved (2026-10-21)

- `StructuralRefactoringEngine.InlineMethodAsync` now binds each call's arguments to the method's parameters (named/reordered arguments included), parenthesizes non-simple substituted arguments, and parenthesizes the inlined expression by parent context (same-operator left operand stays bare, so `40 + 2 + 1` is byte-stable). It refuses with `ToolTargetIneligibleException` for ref/out/in, params, optional, extension, generic and async methods, non-explicit arguments, method-group/`nameof`/conditional-access references, and a parameter used 0 or 2+ times with a non-identifier, non-literal argument. Evidence: `BugFixTests` (named+reordered, two parameters, ref/optional/method-group refusals, twice-used parameter, `1 + Answer()`, unary and cast parents) and `AdvancedStructuralNormalizeCharacterizationTests.InlineMethod_WithCallers_ByteExact` (`(40 + 2) * 2`). Open: private-member accessibility from another type, cross-project call sites, first-same-named-method lookup (TODO.md).

## `CreateFile` global namespace, `Inline` description, InlineParameter test name - resolved (2026-10-21)

- `CreateFile` for a `.cs` file no longer requires `namespaceName`: omitted, null or whitespace creates the type in the global namespace (`WorkspaceFileEditImpl.CreateFile`; both tool declarations' `namespaceName` descriptions say so). Evidence: `CreateFileDeleteFileTests.CreateFileTool_NoNamespaceForCsFile_CreatesGlobalNamespaceTypeAsync` replaced the old refusal pin.
- The `Inline` tool's `kind`/`targetName`/`methodName` descriptions no longer advertise `kind: parameter` as usable (it refuses with `NotImplemented`; the real implementation stays open in TODO.md).
- `BatteryTwentyFiveTests.InlineParameterAsync_MethodWithParam_ReturnsNonNull` renamed `..._ThrowsToolNotImplemented` (it asserts the exception, not a result).

## RunTest environment passthrough, ExtractMembers superclass crash, pull-up `virtual` - resolved (2026-10-21)

- `RunTest` gained an optional `environment` map (name -> value) copied into the `dotnet test` child process before the `useScratchDir` isolation variables (`TestRunEngine.RunOneProjectAsync`; tool, facade and `WorkspaceBuildTestImpl.RunTest` thread it). Evidence: `RunTestTests.RunTest_EnvironmentParameter_ReachesTestProcessAsync`; the claude-lean schema budget was raised 28000 -> 28500 for it (parameter costs about 365 chars).
- `ExtractMembers(newType: superclass)` without `memberFilePaths` threw `IndexOutOfRange` (a dead `??` fallback on an empty array in `AdvancedRefactoringTools`, not a superclass problem); it now falls back to the target file. Evidence: `ExtractSuperclass_DogWithAnimalBase_NoMemberFilePaths_SucceedsWithEmptyBaseAndDoesNotRebaseDerived`. The stub behaviour behind it stays open in TODO.md.
- MoveMember pull-up emitting `virtual` is intended (`MoveMemberTextEdits.BuildPullUpModifierEdits`); the pinning test's TODO became a comment.

## Punchlist batch 2026-10-21: SyncInterface cross-file write, debounce .csproj reload, Plan:/Closes: convention

- `SyncInterfaceToImplementation` cross-file defect fixed: `SyncInterfaceTool` wrote the interface's text (prefixed `// Updated file: ...`) over the class file and never wrote the interface file (it ignored `syncResult.FilePath`). The engine no longer prefixes the text; the tool writes to the result's file and its `AppliedChangeSummary` names it. Evidence: `WrapRangeAndGenerationTests.SyncInterfaceToImplementation_InterfaceInOtherFile_UpdatesInterfaceFileOnly` (red before, green after), 7 of 7 `SyncInterface` tests pass.
- `OnDebounceTimerElapsed` per-project reload removed: `OpenProjectAsync` on an open project always threw "already part of the workspace" (logged only), leaving the workspace stale and the `.csproj` hash stale (the SessionHalted false positive on later touches). A loaded `.csproj` now takes the full-solution reload, which re-records project/solution hashes. Evidence: `ExternalTouch_ProjectFileAfterDebounceReload_IsNotFlaggedAsDriftAsync`, 30 of 30 `CreateFileDeleteFileTests` pass. Open parts stay in TODO.md.
- `Member`/`UsingDirective` hardcoded `autoStage = true` on the tool methods: closed as by design (not in the schema; the no-autoStage failure path is tested through the Impl in `MemberOutcomeFollowsCauseTests`).
- `Plan:`/`Closes:` commit trailer convention written down in `templates/README.md` rule 11; the reconciliation script stays open in TODO.md.

## Punchlist items 17-20: Member(replace), MoveType ownFile, ExtractInterface comments - resolved (2026-10-21)

- `MoveType(destination: ownFile)`: new optional `targetFolder` parameter (engine `MoveTypeToFileAsync` and tool); the moved type keeps its `///` doc comment; unused usings (CS8019) are dropped from the new file in both `MoveTypeToFileAsync` and `MoveAllTypesToFilesForDocumentAsync` (`RemoveUnusedUsingsAsync`). Shared byte-exact pins now use `Int32` so `using System;` stays used.
- `ExtractInterface`: each interface member keeps its leading `///` doc comment. Comments inside a signature are still dropped (pinned); a warnings field was not added (it needs a new result type), see TODO.md.
- `Member(replace)`: a `newMemberSource` that declares a type over a non-type target (the same-named constructor reached through a namespace `containerName`, CS0542) is refused with `SourceInvalid` and a ReplaceSnippet/ApplyDiff hint. The reported silent no-op on a constructor replace does not reproduce: `ReplaceMember_Constructor_ReplacesConstructorBody` passes.
- Item 20 (C5c-3: base-list colon, trailing newline) was already fixed in commits 7517b6d1 and 1aab6edf; only the proposal status line was stale.
- Evidence: solution run 2026-10-21, 5533 passed, 5 failed: four `Flags_*` tests in `SentinelAccuracyTests.cs` (file edited by another session, uncommitted before this work) and `FullBuild_UpstreamProjectFails_DownstreamProjectsReportNoErrors` (passes on re-run).

## TODO punchlist leftovers, `docCommentId` audit and C5b-2c leftovers - resolved (2026-10-20)

- Async batch: `FlagBridgeCandidateForReviewAsync` now passes the cancellation token to `ApplyProposedChangesAsync`; a cancelled flag write is still logged and skipped (best-effort helper). The bridge CT-add and body-rewrite flag tests exist (`AsyncBatchEngineFlagOutcomeTests`).
- `GitImpl`: the 15 remaining `catch (Exception ex)` clauses now carry `when (ex is not OperationCanceledException)`, matching the other Git operations.
- `IntroduceParameterObject`: the interface-only branch returns a WARNING that implementations were not updated; BUG_57 test tightened and renamed; tool-level test added. `docCommentId` descriptions: `DeleteUnusedSymbol` (both declarations), `NamedArguments` `targetSymbol` and `SemanticFindReplace` `symbol`/`factory` now say "from Search(mode: symbol), pass exactly as returned, do not construct"; `DeleteUnusedSymbol.filePath` says it is still required; `DocCommentIdWordingTests` pins it.
- C5b-2c: `ExtractMethodSafeAsync` annotates only the call statement and the new method (untouched code keeps its spacing); pins added for a stray LF in a CRLF document, a stray CRLF on a blank line, CR-only and U+2028 separators, and the known trivia loss (SortUsings comments, ExtractMethod inter-statement comments; the last two carry TODOs).
- `RemoveMethodParameterAsync` span formatting (item 21) was already done earlier (commit 7912b15e).
- Evidence: solution test run 2026-10-20, 5529 passed, 0 failed (`Tests.ModelEval` did not build: CS2012 file lock).

## TODO punchlist small items 1-8, 11, 12 - resolved (2026-10-20)

**Evidence (2026-10-20):** ReplaceSnippet split-class schema wording (832b2ddc); SubAgent failure message now ends with the absolute run-log path (05001466); async batch bridge/uplift skips write the NeedsManualReview flag via `FlagBridgeCandidateForReviewAsync` and say "flagged" only on a successful write, dead `fallbackHandled` stores removed (05001466); `InlineVariableAsync` fallback routed through container-scoped removal (d77a039f); `InvertAssignments` trivia leak (`SwapAssignmentOperands`) and `SimplifyMemberAccess` `this` trivia fixed with byte-exact tests (05001466); `AddUsingDirective` simplifyExisting annotates qualified names so the Simplifier shortens them (6eaace53); all 15 `GitImpl` `ex.Message` sites use `UnexpectedFailure<T>` (05001466); MoveMember ledger `TryOpen`/already-open tool-layer paths pinned (5470d393); `NormalizeEolOfChangedLines` repeated-line limitation accepted and pinned as a test case (c1a4bbd7). The bridge skip flag tests and `IntroduceParameterObject` defect 2 landed later the same day (1cad63e5, 6382d48e, 9aac88fd, b96db444).

Original TODO.md text (moved 2026-10-20, shortened to the titles):

- ReplaceSnippet schema invites anchor-only edits; SubAgent bare InvalidOperationException and no findable run log; `InlineVariableAsync` fallback uses `FormatWholeDocumentAsync`; `MappingEngine.InvertAssignments` leading trivia leak; `IDEStyleEngine.SimplifyMemberAccess` drops `this` trivia; `AddUsingDirective` simplifyExisting no-op; `GitImpl` generic catch blocks put `ex.Message` into `Error`; `NormalizeEolOfChangedLines` repeated identical lines; `MoveMember` ledger `TryOpen` path not exercised at the tool layer.

## `SubAgentEval` unexplained failed test - resolved (2026-10-20)

**Evidence (2026-10-20):** the original failing test (2026-10-01, 1 failed of 2658) could not be recovered from stored artifacts. A full solution `RunTest` on 2026-10-20 had 0 failures (5535 tests, 28 skipped), so it is not reproducible. `SubAgentEvalResult.FirstFailedTestName` now names the first failed test; the post-run `RunTest` call no longer sends `summary=true`, which had omitted the results array (818390f3). Doc: [issues/issue_tool_hygiene_open_items.md](issues/issue_tool_hygiene_open_items.md).

Original TODO.md text (moved 2026-10-20): `SubAgentEval` child full `RunTest` reported 1 failed test (2657 passed), unexplained - failing test name not captured.

## C5c formatting leftovers and whitespace follow-ups - resolved (2026-10-20)

**Evidence (2026-10-20):** `ExtractInterfaceAsync` keeps the new base-list colon on the class line (7517b6d1); files scaffolded by `MoveTypeToFileAsync` / `MoveAllTypesToFilesForDocumentAsync` end with one EOL in the source's dominant EOL (1aab6edf); `RemoveMethodParameterAsync` annotates only the parameter and argument lists, so the method body is no longer reformatted (7912b15e); `StripAllTriviaForNormalize` renamed `StripAllTrivia` (fb33a7bb); `AsyncBatchEngine` checks `IsError` on the uplift and propagate-CT writes (f0ca99c7). Also pinned: `IntroduceParameterObject` empty-body TODO comment (945f13ee) and the bridge body-rewrite skip flag tests (9aac88fd). Remaining pieces stay in TODO.md (7 tagged `AsyncifyTools` sites, `SessionHalted` unmaterialized-document case).

Original TODO.md text (moved 2026-10-20, shortened to the titles): C5c leftovers: unrouted EOL flatteners (`RemoveMethodParameterAsync` annotated the whole method); C5c-3: `ExtractInterfaceAsync` colon at column 0 and `MoveTypeToFileAsync` files without a trailing newline.

## DTO hook did not inspect `Member replace`, `ReplaceSnippet` or `ApplyDiff` - resolved (2026-10-20)

**Evidence (2026-10-20):** `enforce-dto-justification.ps1` now covers `Member replace`, `ReplaceSnippet` (single edit and each `batchEdits[]` entry) and `ApplyDiff` (`changes` map and `filePath` + `unifiedDiff`) through `Add-NewDeclarations`: a type is new only if its name is not on disk in that file and not in the same edit's removed text; text with no `record|class|struct` token exits early. `settings.json` matcher extended, `dto-conventions` section 7 updated, 11 cases added to `enforce-dto-justification.Tests.ps1` (54 pass, 0 fail).

## `MoveTypeToOuterScopeAsync` did not adjust accessibility - resolved (2026-10-20)

**Evidence (2026-10-20):** a nested `private`/`protected`/`private protected`/`protected internal` type is now refused with `EditOutcome.CannotMove` (`ToolErrorCode.TargetIneligible`, message names the modifier and `allowWidenToInternal`); `MoveType(destination: outerScope, allowWidenToInternal: true)` widens it to `internal` and says so. `MoveTypeTool` forwards the refusal instead of "not found". Tests: `MoveTypeToOuterScope_PrivateNestedType_IsRefusedByDefault` and `..._WidenedToInternalWhenAllowed`. The unreachable `_ => currentContainer` arm was replaced earlier.

## C6 whole-subtree whitespace enforcement and the old NormalizeWhitespace sweep list - resolved (2026-10-19)

Moved from TODO.md (items "C6 whole-subtree whitespace enforcement", opened 2026-10-15, and "Old Advanced-side NormalizeWhitespace sweep list", opened 2026-08-24, superseded by C0-C6).
Evidence: read-only review of 9402e8d3..297fb1b1 found no production caller of the deleted family; the three sites left unfixed on 2026-08-27 (`RunMicroRefactoringAsync`, `ExtractConstantSafeAsync`, `GenerateToStringSafeAsync`) now call `FormatAnnotatedTextAsync`; the last parked raw-formatter copies were merged into `RoslynFormattingHelper.RemoveNodeAndFormatContainerAsync` in 31ecb16e, and `WhitespaceRatchetTests` now matches sync `Formatter.Format` and counts raw calls per allowlisted member. Remaining `NormalizeWhitespace` calls are non-persisted hashing (`ContentHasher`, `PersistentWorkspaceManager`) and the C9-allowlisted `AttributeTextEditBuilder`. History: [proposal](proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).

## Whitespace initiative: retire the `NormalizeWholeSubtreeWhitespace` family - resolved (2026-10-19)

**Evidence (2026-10-19):** slices C1-C11 (last commits 40c6cc58 C10, a8f581f2 C11); production has 0 callers, `WhitespaceRatchetTests` baseline is empty and `CountedWhitespaceHelpers_AreObsolete` keeps the four helpers `[Obsolete]`. Also fixed on the way: Asyncify T20 (relative-path fixture + `RunBridgeBatchAsync` step D ignoring `EolChangeRefused`) and the `FlagCandidatesInProjectAsync` shared-state race in `Parallel.ForEachAsync`. Proposal: proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md.

**Evidence (2026-10-19, slice D1):** the caller-free helpers were deleted (the four `NormalizeNew*`/`NormalizeWholeSubtreeWhitespace` methods, `StripFormattingTriviaForNormalize`, `StripUsingKeepingLeadingTrivia`, private `DetachForNormalize`/`EnsureNodeIsNew`/`ValidateEol`) together with `RoslynFormattingHelperNormalizeNewTests.cs` (73 tests, including the stale `SafeSite_*` ones); `StripAllTriviaForNormalize` stays. `WhitespaceRatchetTests.DeletedWhitespaceHelpers_NeverReappear` replaces the caller baseline and the `[Obsolete]` check. This also closes the TODO entry below.

**Evidence (2026-10-19, slice D2):** the 10 duplicated private format helpers (7 x `FormatAnnotatedTextAsync`, 2 x `FormatNewSnippetAsync`, `MsToolAugmentEngine.FormatAnnotatedRootAsync`) were consolidated into `RoslynFormattingHelper.FormatAnnotatedTextAsync` (Document and SourceText overloads) and `FormatNewSnippetAsync`; all copies had identical behaviour; pins unchanged; `RoslynFormattingHelperFormatAnnotatedTextTests` covers LF/CRLF/mixed.

Original TODO.md text (moved verbatim 2026-10-19):

- [OPEN] `NormalizeNewRootWhitespaceKeepingUsingHeaders` limitations (2026-10-16) - directive spanning usings gives CS1027 and a header on a usings-less file is not carried; fix only if the C6 variant does not subsume it - [doc](proposals/closed/proposal_enforce_normalize_whole_subtree_whitespace_rule.md)

## claude-lean schema budget over its limit / no headroom - resolved (2026-10-18)

**Evidence (2026-10-18):** proposal_trim_claude_lean_core_surface.md slices 1-8 (last commits c01b1f51, 0b0869c4, 7b01ea33) cut the core surface from 24 tools / ~42.9k chars to 19 tools / 27752 chars (largest tool Git 4157); `ClaudeLeanSchemaBudgetTests` constants lowered to 28000 total / 4500 per tool.

Original TODO.md text (moved verbatim 2026-10-18):

- [OPEN] `ClaudeLeanSchemaBudgetTests.CoreSurface_TotalSchemaSize_IsWithinBudget` fails for both flavours (2026-10-18) - Advanced 42964 of 42500, Basic 40994 of 40500, same +778 chars since the 2026-10-14 measurement in both, so not from the MoveMember work; raise the constants or trim descriptions - [doc](references/reference_changing_claude_lean_tools.md)
- [OPEN] Schema budget headroom is about 2 chars (2026-10-17) - claude-lean Search/Git schemas sit at the `CoreSurface` limits (~40498/40500); the next parameter must trim wording first - [doc](proposals/proposal_reduce_tool_schema_token_cost.md)

## `Search(mode: references)` could not resolve a type by name unless `filePath` was pinned - resolved (verified 2026-10-17)

**Evidence (2026-10-17):** the blocker is filed under `blockers/closed/blocking_error_search_references_by_name_excludes_types.md`; the TODO.md entry was a stale pointer to it.

Original TODO.md text (moved verbatim 2026-10-17):

Tracked as a blocker: `blockers/blocking_error_search_references_by_name_excludes_types.md` (root cause traced to `SymbolNavigationEngine.cs:1581-1583`; workaround: pass `filePath`). Prerequisite for the DTO proposal phases.

## `AddUsingDirective` simplifyExisting whole-document format could leak the formatter NewLine into mixed-EOL files - resolved (verified 2026-10-17)

**Evidence (2026-10-17):** `RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs:1554-1555` now routes the `simplifyExisting` path through `RoslynFormattingHelper.FormatWholeDocumentAsync(doc, originalText, ct)` (`RoslynSentinel.Common/RoslynFormattingHelper.cs:362`, `eolReference` parameter), and :1563-1568 holds a single-style original to its own EOL. The sibling entry "`Simplifier.ReduceAsync` simplifies nothing" is NOT fixed (:1553 is unchanged) and stays open in TODO.md.

Original TODO.md text (moved verbatim 2026-10-17):

The `simplifyExisting` path (`BasicRefactoringEngine` ~:1555) runs a whole-document `Formatter.FormatAsync`, and its end-of-line guard (~:1571) only normalizes single-style documents, so in a mixed-EOL document the formatter's own NewLine option can leak in. Found in the C5b-1 review; route it via `RoslynFormattingHelper.FormatWholeDocumentAsync` with the pre-edit text as `eolReference` in C5c.

## `MoveMember`'s `callSiteFixups` `"new"` sentinel only supported a parameterless constructor - resolved as a documented contract (verified 2026-10-17)

**Evidence (2026-10-17):** option (a) of the "Not fixed" paragraph is implemented: `MemberRefactoringEngine.EnsureNewFixupsAreConstructibleAsync` (`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:5285`) enforces the documented "new" contract (`plan_scoped_operation_ledger.md` section 2) and refuses up front, naming the offending keys and the real constructor signatures, when the target has no accessible zero-argument constructor. Option (b) (inferring constructor arguments from scope) was rejected on purpose: a guessed argument list can compile while silently dropping configuration.

Original TODO.md text (moved verbatim 2026-10-17):

**Found:** 2026-09-27, during real end-to-end `MoveMember` testing
(`ApiIntegrationEngine.AddValidationToPocoAsync` -> `ApiAutomationEngine`).

**What:** `AdvancedStructuralEngine.cs`'s `MoveInstanceMembersAsync` rewrite handles the `"new"`
sentinel value in `callSiteFixups` by emitting
`SyntaxFactory.ObjectCreationExpression(SyntaxFactory.IdentifierName(targetClassName)).WithArgumentList(SyntaxFactory.ArgumentList())`
— always a parameterless constructor call, regardless of what the target type's actual constructor
requires. Against `ApiAutomationEngine(IWorkspaceManager workspaceManager)` (a required-parameter
constructor), every call site fixed up with `"new"` failed with `CS7036`
("no argument given that corresponds to the required parameter 'workspaceManager'"). Worked around
in that session by passing the full expression (`"new ApiAutomationEngine(_workspaceManager)"`) as
the fixup value instead of the bare `"new"` sentinel — this already works, since non-`"new"` values
are parsed via `SyntaxFactory.ParseExpression` verbatim.

**Not fixed:** the `"new"` sentinel could inspect the target type's constructor(s) and either (a)
only offer itself as valid when a parameterless constructor exists, or (b) attempt to match
in-scope fields/parameters to the constructor's parameter types the same way call-site candidate
resolution already does. Low priority — the raw-expression fixup already covers this case, so it's
a convenience gap, not a blocker.

## `SentinelRefactoringTools` / `RefactoringStructuralTools` split left a duplicate `SyncTypeAndFilename` MCP tool registration - resolved (verified 2026-10-17)

**Evidence (2026-10-17):** `Search(mode: text, query: "Name = \"SyncTypeAndFilename\"")` finds exactly one registration, `RoslynSentinel.Tools.Basic/RefactoringStructuralTools.cs:269` (`SyncTypeAndFilenameTool`); no `SentinelRefactoringTools` class exists any more.

Original TODO.md text (moved verbatim 2026-10-17):

**Found:** 2026-09-20, while investigating whether `SyncTypeAndFilename` is duplicated.

**What:** `RoslynSentinel.Server.Basic/RefactoringStructuralTools.cs` was created as step 1 of
`docs/current/plans/closed/plan_split_workspace_refactoring_tools_for_di.md` (Decision 1: split
`SentinelRefactoringTools` into `RefactoringSignatureTools`, `RefactoringStructuralTools`,
`RefactoringExtractionDocsTools`). It duplicates 6 methods verbatim from
`SentinelRefactoringTools.cs` — `Member`, `ModifyEnum`, `ModifyAttribute`, `ModifyModifier`,
`ModifyBaseType`, `SyncTypeAndFilename` — all still `[McpServerTool]`-attributed with identical
names/signatures in both classes. `RefactoringStructuralTools` is registered in DI
(`ServiceRegistrationExtensionsBasic.cs:279-282`, `AddSingleton` + `WithSentinelTools<>()`) under a
`"RefactorStructural"` mode key (`ToolClassRegistry.cs:38`) that is never enabled by any active
mode (`ServerStdio.cs`'s `ActiveToolTypes`, `AdvancedModeToToolClasses`, and every mode string
built anywhere in the repo were grepped — none reference `"RefactorStructural"` or
`RefactoringStructuralTools`). So today there is no live duplicate registration, but the two
classes are one config change away from colliding on 6 tool names simultaneously, and the class
sits as dead-in-production code with real duplicated logic behind it (bugs fixed in one copy, e.g.
`SyncTypeAndFilename`'s first-declared-type bug, silently do not apply to the other).

The plan's designed end-state (Decision 3) is NOT deleting `RefactoringStructuralTools` — it's
turning `SentinelRefactoringTools` into a thin legacy facade that internally constructs and
delegates to `RefactoringStructuralTools` + the other 2 split classes, preserving
`SentinelRefactoringTools`'s public constructor/method signatures so all 18 existing
direct-construction test call sites and the 4 `typeof(SentinelRefactoringTools)` reflection sites
keep compiling untouched. That cutover (and the matching `SentinelWorkspaceTools` -> 5-class split
in the same plan) was never carried out.

**Suggested approach:** treat as its own implementation pass, not a quick patch — follow
`plan_split_workspace_refactoring_tools_for_di.md` Decision 3's facade pattern and Decision 7's
stepwise/incremental-build-checkpoint sequencing. Minimum slice to close the actual duplication
risk: make `SentinelRefactoringTools`'s `Member`/`ModifyEnum`/`ModifyAttribute`/`ModifyModifier`/
`ModifyBaseType`/`SyncTypeAndFilename` methods delegate to an internally-held
`RefactoringStructuralTools` instance instead of containing their own copies, matching the facade
shape already spec'd in the plan doc. Full plan also covers `RefactoringSignatureTools` and
`RefactoringExtractionDocsTools`, which have not been independently verified as duplicated or not
during this pass — check those too before assuming this TODO's scope is limited to
`SyncTypeAndFilename`'s cluster.

## `SearchSolutionText`: run both modes, use `searchMode` to rank rather than to select - implemented, tool retired into `Search(mode: text)` (verified 2026-10-17)

**Evidence (2026-10-17):** the idea shipped as `design/closed/design_searchsolutiontext_dual_mode_search.md`; `Search(mode: text)` (`RoslynSentinel.Tools.Basic/WorkspaceReadNavigationTools.cs`, `SearchSolutionTool`, no `searchMode` parameter) returns both `literalResults` and `regexResults` and the old standalone tool is gone (`WorkspaceTools.cs:597-606` is a non-tool delegator). The "make searchMode required" and "auto-promotion was reverted" background below describes the pre-merge state.

Original TODO.md text (moved verbatim 2026-10-17):

## `SearchSolutionText`: run both modes, use `searchMode` to *rank* rather than to *select*

**Status:** proposal only. Deferred out of the run-398 defect sweep (A4) on 2026-09-10.

Today `searchMode` selects which single search runs. A pattern with regex metacharacters passed
under `searchMode: literal` is searched literally as requested — correct per the parameter, but
in practice it returns zero results, and run `20260910-013550-398` burned three turns (17, 18, 25)
on exactly that: the model rewrote the pattern each time rather than changing the mode.

A4 fixed the immediate footgun by making `searchMode` **required**, so an unstated mode can no
longer silently produce the wrong search (see
`feedback_prefer_mandatory_params_to_close_footgun_roundtrips`). That is a strictly smaller change
than the idea below, and does not preclude it.

**The proposal:** run *both* the literal and the regex search, return the union, and use
`searchMode` only to order the results — matches from the requested mode first. A caller who names
the wrong mode then gets the right answer ranked second instead of an empty result set.

**Why this was not bundled into the defect sweep:**

- It changes result *semantics*, not just a parameter's default — every caller's result shape and
  ordering contract shifts.
- Auto-promotion of literal→regex was already tried and deliberately reverted. `BatteryTwentyTests`
  still asserts the no-fallback behaviour explicitly ("explicit literal mode must actually search
  literally, not silently switch to regex"). This proposal is *not* that reverted design — it adds
  results rather than substituting a mode — but the history says this tool warrants its own
  evaluation run rather than a change riding along with unrelated fixes.
- Cost is unmeasured: two passes over every document instead of one, on a tool that already scans
  the whole solution in parallel.

**Open questions:** whether a regex that fails to compile should degrade to literal-only or error;
whether the union should be de-duplicated per (file, line) or per (file, line, column); whether
`maxResults` applies before or after ranking; and whether the response should label each match with
the mode that found it.

## MoveMember tool masked EolChangeRefused as UnresolvedCallSites - resolved 2026-10-17

Root cause: the early return in `MoveMemberTool` covered only `CollateralChangeRefused`, so an EOL refusal with ledger entries or skipped call sites fell into the `UnresolvedCallSites` branches. Fix: `AdvancedRefactoringTools.IsRefusalThatMustNotBeMasked` selects both refusal codes for the early return. Test: `IsRefusalThatMustNotBeMasked_SelectsCollateralAndEolRefusalsOnly` (unit test of the condition only; no end-to-end tool-level EOL refusal fixture exists).

## MoveTypeToOuterScopeAsync dropped the nested type - resolved 2026-10-17

Root cause: the namespace was looked up in the pre-removal root, so ReplaceNode silently no-op'd (no-namespace case duplicated the type). Fix: track the type and target container in the original root, re-find after removal, annotate only the re-added type and format via FormatAnnotatedSpansAsync. 25 byte-exact tests in MoveTypeToOuterScopeFormatCharacterizationTests.

## Type-by-name follow-ups: implementations+filePath, near-miss types, namespaced attributes, Git pickaxe/follow - resolved 2026-10-17

- `FindImplementationsForMemberAsync` with `filePath` + a type name now resolves the type declared in that file (type preferred over same-named ctor); a filePath miss reports the scoped failure instead of answering for another same-named type; type eligibility (sealed/static/struct/enum/delegate throw) now applies on the filePath path too. Non-sealed class targets now return transitive derived classes (`FindDerivedClassesAsync`); previously an empty list that read as "zero implementations".
- `DescribeNearMissCandidatesAsync` scans `TypeAndMember`; global-namespace types render without `()`.
- `typesWithAttribute` (`FindTypesByAttributeAsync`) resolves bare names across namespaces and referenced assemblies (hypothesis confirmed: `Search(attribute, "TestFixture")` found nothing while the qualified name found 397); `attributeUsages` matches qualified queries on the last segment; `includeTests`/`projectName` honored; XML-doc cref hits skipped; the broaden-on-empty message no longer claims "not found anywhere" when only non-test projects were searched.
- `Git(operation: log)` gained `pickaxe` (-S), `pickaxeRegex` (-G) and `follow` (--follow, one path). To fit the claude-lean schema budget, wording of other Git parameter descriptions was tightened.
- `Search(mode: attribute)` gained `hasAttribute` (usage's declaration must also carry that attribute; matched like `query`; skips the broaden-on-empty fallback; ignored-parameter warning in other modes). The `includeTests=false` attribute tests use the existing ContosoOrders.Tests csproj (TestProjectDetector only checks for the text Microsoft.NET.Test.Sdk), so no fixture change was needed.

## `contextSnippet` starting at a return-type keyword resolved to `System.Void` (closed 2026-10-17)

Fixed in bb9c477848d2f939dcfd1210ad0ed75013824709: name-directed `SnippetSymbolLocator.FindSymbolAtSnippetAsync` overload climbs to the enclosing declaration and returns null on a name mismatch (NotFound instead of a silent empty result). Tests in `SearchModeTests`/`RegressionTests`.

## DTO-justification PreToolUse hook - resolved 2026-10-16

**Resolved 2026-10-16.** Entry was: build the hook that makes a new DTO carry a `NEW-DTO:`/`DTO-EXEMPT:` justification in the call's `reason`. Built `.claude/hooks/enforce-dto-justification.ps1` (fail-open; `Member` addTopLevelType/addMember, `CreateFile` class/record/struct, `WriteFile` content minus on-disk types; production `.cs` only), a separate `settings.json` PreToolUse entry (matcher `.*__Member|.*__CreateFile|.*__WriteFile`) and `enforce-dto-justification.Tests.ps1` (plain script like `enforce-dogfood.Tests.ps1`, not Pester; 45 cases, all pass). Decisions: `*Options` types trigger, `Wire impact:` is not enforced, dry runs are not skipped, `DTO-EXEMPT` is the only bypass; service suffixes also include Builder, Visitor, Rewriter, Walker, Provider, Handler, Converter, Comparer. Remaining gap is in TODO.md.

## Instance moves left doc-comment crefs behind - resolved 2026-10-15

**Resolved 2026-10-15** (Q1 `CrefQualifier`, Q2a/Q2b `QualifyCrefs` tool in toolset `moveExtract`, Q3-1 to Q3-6c on the instance route: ab552200, 42a10387, 080fd140, d631e3cb, bd05e5d0, 8805b5e2). Entry was: after an instance move, a cref to the moved member still named the old class, while static moves retargeted it. The instance route now (1) retargets crefs to the moved member in the source, caller and other documents, counting ambiguous (candidate) crefs once and leaving them alone, and (2) qualifies bare crefs inside a moved member's own doc that name a non-moved source member (`A.Other`, `A{T}.Other` for a generic source class). Both report notes. Tests: `MoveMemberInstanceCrefTests`, `InstanceMoveCrefEditsTests`, `InstanceMoveCrefQualifyEditsTests`. Plan: `plans/closed/plan_cref_qualifier_helper_and_tool.md`. Remaining limitations are in TODO.md ("Instance-move cref limitations").

## `MoveMember` follow-ups: full text returned on success, and `MethodSignature` whole-file CRLF rewrite - resolved 2026-10-13

**Resolved 2026-10-13** (verified against master source). Two items from the "`MoveMember` follow-ups left open by the whole-file reformat fix" TODO entry, plus one related item from the resolved blocker's "Not done" list:

- **Full text still returned on success: fixed in effect by 8c987c97 (2026-10-08).** `AppliedChangeSummary.ChangedContent` is null on a real apply (`Validated && !DryRun && !InlineOnApply`); the stored text comes back as `ChangedContentResultId`. It stays inline for dry runs and `autoStage:false`. `AdvancedRefactoringTools.cs:538` still passes `ChangedContent: result.Changes`, but it is dropped on a real apply. The misleading comment at `AdvancedRefactoringTools.cs` (now ~543-547) was rewritten on 2026-10-15 to say the text is not echoed on a real apply and how to fetch it.
- **`MethodSignature` rewrote whole caller files as CRLF: fixed by d89cc95f (2026-10-09).** `RemoveMethodParameterAsync` in `MemberRefactoringEngine.cs` (about lines 1548-1647) now restores the file's dominant EOL; commit 276aface covers the neighbouring signature tools. Still tree-based, and files with mixed EOL are untested.
- **Two `ExtractMethodAsync` `NormalizeWholeSubtreeWhitespace` calls: classified safe.** Lines 4334 and 4398 of `MemberRefactoringEngine.cs` normalize newly synthesized nodes, not an existing tree root.

Source: `blockers/closed/blocking_error_movemember_reformats_entire_source_and_caller_files.md` ("Follow-up status (2026-10-13)").

## Result and status types used independent vocabularies; MCP `isError` was re-parsed from JSON - resolved 2026-10-11

**Resolved 2026-10-11** (24499e55 phase 1, b406f44a, 333f3147, 4c82fa12, 68502897, 2e428075, 10018cdd, f2642445, 770748d2, 70322b91, 1122f2c3, a4c733c7, 00abfd09, 527a7287, 18ce4ef0, 018e9436, 9c92ae51, 0d692829; ModelEval DI fix 5e36871c). Every status-carrying type now implements `IResultStatus` (`IsError`, `Status`, `Code`, `Message`, all `[JsonIgnore]`); the envelope serializes `isError` and `isSuccess` is gone from the wire; agent-side consumers read `isError`. See `plans/closed/plan_shared_result_status_interface.md` and `proposals/closed/proposal_shared_result_status_interface.md`. Deferred: JSON-text read of build `outcome` in `SubAgentEvalResult.cs` (~94-95); dead `RenameSymbolResult.ToToolResponse` (`BasicRefactoringEngine.cs:58`, anonymous `success` key, 0 callers); `ClassEdit` `AlreadyInTargetState` reports Success.

## Search / ListAll / FindReferences: exclude test projects by default - resolved 2026-10-10

**Resolved 2026-10-10** (9eb70d8e (shared detector), 52fc2f59 (text), 8b51cf94 (ListAll/kinds), b92d1e70 (symbol), f5b9b649 (references)). Tools gained `includeTests` (tool default false, impl default true); see `plans/closed/plan_search_exclude_test_projects_by_default.md`. Entry was: hits from test projects buried answers (392 of 423 text hits for `PersistentWorkspaceManager`).

## `Git` tool: tag, stash, worktree and hunk-level staging (closed 2026-10-10)

Implemented by `plans/closed/plan_git_tool_tag_stash_worktree_hunks.md` (steps 1-15): `tag`, `stash`, `worktree` (one shared `action` parameter) and
`hunks` / `stage` with `hunkIds`+`hunkFingerprint` or `lineRange`. Commits f209e84 .. 4a9c1f9 (tool), 1f71ced (live smoke + `enforce-dogfood.ps1` now denies
shell tag/stash/worktree). Full suite 3734 total / 0 failed / 26 skipped; the eight-call live smoke passed on two restarted servers. `stash drop`/`clear`,
tag push and worktree prune/lock/move stay shell-only by decision (D-16, D-17).

## `SessionHalted` message named one file and no recovery path - resolved 2026-10-09

**Found:** 2026-09-15 (a gap under the TODO.md entry "`SessionHalted` external-drift latch: false-positive from timestamp-only touch"). **Resolved 2026-10-09.** The halt message now states how many tracked files drifted, names the first few, and spells out the recovery (`ExternalFileDrift` List, then Acknowledge, then `LoadSolution(forceReload)`); the latch policy for partial acknowledge was decided and implemented. See `plans/closed/plan_external_file_drift_tool_and_halt_stamping.md` (halt wording is its step 6) and `plans/closed/plan_session_halt_recovery_and_git_gaps.md` (deferred Step 2). The other two gaps in that TODO entry stay open.

## Mutating tools don't return the resulting content, forcing a separate ReadFile - resolved by design decision 2026-10-08

**Found:** 2026-08-19/20. **Resolved 2026-10-08.** Mutating tools do not echo content on a real apply: the result carries lineChanges, a changedContentResultId for the stored content (fetch with GetLargeResult) and a Note naming the read-back tools (ReadFile, GetMethodSource, Member(view)). Dry runs and autoStage:false results keep the inline changedContent. Inline echo on a real apply is a server-wide opt-in (env var ROSLYNSENTINEL_INLINE_CHANGED_CONTENT, ChangedContentOptions.InlineOnApply), pending an impact review. See plan_mutating_tools_compact_result_default.md; commits 8c987c9, 2ac8461, e198987.

Original report (trimmed): an agent wanting to confirm what its edit produced had to make a second call to ReadFile/GetMethodSource, shifting cost from one bloated response to two calls.

## Move syntax-side target resolution out of `SymbolNavigationEngine`, then unify hint formatters - implemented 2026-10-03

This was found on 2026-10-02 while reviewing the refactoring-engine reorg (72a327e).

- **Step 1 (50378ea).** The stateless syntax-only resolution layer moved out of `SymbolNavigationEngine` into the new static `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs`.
  - Moved: `ResolveCandidates`, `ResolveBySnippetOrThrow`, the `Prefer*` and `FilterByContainingType` helpers, `NormalizeTypeName`, `GetMemberName`, the hint builders, `CandidateKind` and `SyntaxNodeCandidate`.
  - There are no forwarders. Call sites were rewritten by `MoveMember`'s make-static conversion (b773820).
- **Step 2 (b95e857).** A single `BuildHintForCandidates` replaces `BuildMemberHint`, `BuildTypeHint` and both `*ForCandidates` adapters.
  - Type-hint previews are now cut at 50 characters, where the old adapter allowed 80.
  - `AdvancedStructuralEngine`'s write-only `_symbolNavigationEngine` field was removed.
- **Results.** Full suite: 2981 tests, 0 failed, 109 skipped.
- **Docs.** `proposal_universal_symbol_resolver.md` is marked resolved for its resolver scope, with a note on its pre-reorg paths.

Left open (tracked in TODO.md under "`SyntaxTargetResolver` extraction follow-ups"):
- `Describe*` formatter placement;
- `BasicRefactoringEngine`'s write-only field.

## `SubAgentEval`/`SubAgent` MCP tools - implemented and live-smoke-verified 2026-10-01

Design `design_subagent_tool.md`, plan `plans/closed/plan_subagent_tool_implementation.md`. Commits: Step 1
`8e2a59e`, Step 2 `495d012`, `--include-tools`/`--exclude-tools` prerequisite `6d57b41`, Step 3 `cffd20b`,
Step 4 `9859277`, Steps 5+6 `8a69ff9`, Step 7 tests + stdin fix `874f80f`, Step 8 docs `4572586`,
offloaded-result fix `a4d5aea`. Live smoke runs against qwen/qwen3.6-35b-a3b on LM Studio all passed:
`SubAgentEval` (worktree created, child launched and loaded, model edited
`SubAgentRunNaming.cs`, Build + RunTest snapshot in the result, worktree and branch removed),
`SubAgent` text and json formats, and the nested check (the child exposes 65 tools vs the parent's 67,
with neither SubAgent tool; the model answered NONE). The smoke runs found and fixed two real bugs:
spawned git/dotnet children inheriting the stdio server's stdin (`874f80f`), and a full-solution
child Build/RunTest result being offloaded to a file so `SubAgentEvalResult` read it as unreadable
(`a4d5aea`). Run transcripts: `RoslynSentinel-TestRuns\SubAgent\<runId>\subagent\Logs	ranscript.json`.
Known limits kept: `SubAgentEval` runs the child's full `RunTest` (slow, ~10 min observed), and both tools
only work when the loaded solution is the RoslynSentinel repo. The unexplained 1-test failure in the
child's suite is tracked separately in TODO.md.

## RenameSymbol namespace follow-ups: residualMentions summary and Search namespace docCommentId - resolved (2026-11-05)

Owner ruling D2. A namespace rename of N:RoslynSentinel.Engines.Basic returned 1363 residual hits (~340 KB). Now the scan matches the full old qualified name for namespaces (dotted-token boundary: Basic.Sub matches, BasicX and Other.Basic do not), scans .csproj/.props/.targets/.json/.md/.ps1/.slnx/.sln too (skipping bin, obj, .git, .roslynsentinel, Worktree, generated files), buckets hits (code, comment, string, pathLike, nonSource) and the tool returns only a summary (`residualMentions` = total, byBucket, topFiles max 10, examples max 5 per bucket, details = one ready-to-run GetLargeResult hint per bucket). `Search(mode: namespace)` rows carry `docCommentId` `N:Full.Name`. Cross-segment moves (D2c) stay rejected.
Evidence: ResidualMentionScannerTests (6), ResidualMentionSummarizerTests (18), ResidualMentionDetailPagingTests (2), SearchNamespaceDocCommentIdTests (8); full RunTest 6673 tests, only new failure was the regenerated architecture_tools.md (fixed).


## MoveMember fold of engine-reorg group 6: empty engine classes removed - resolved (2026-11-05)

The four empty classes (BreakingChange, Dependency, ProjectConsistency, SolutionManagement engines) are gone with their ctor params, DI registrations, test factories and dead test fields. The logic was already in SolutionStructureEngine. BreakingChangeEngine.cs and ProjectConsistencyEngine.cs remain for records they also hold. See decisions_autonomous_run_2026_11_05.md (W18).
