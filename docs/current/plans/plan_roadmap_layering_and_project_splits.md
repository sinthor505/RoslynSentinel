# Plan: Roadmap for the layering fix and the project splits (Common, engines, tool layer)

**Status:** DRAFT 2026-11-27. Master plan: it orders sub-plans and holds no implementation steps of its own. Phase 0.1 done (Git namespace rename,
c2b3acb9..1a7f3b1f); Phase 2 grounding started; the phase order below awaits owner review.

## Problem

Three connected problems, found while designing the Build/RunTest project split
([plan_isolate_build_test_area_into_own_project.md](plan_isolate_build_test_area_into_own_project.md), on hold):

1. **Common is overgrown.** `RoslynSentinel.Common` is 308 files / 39,583 lines doing 21 different jobs (Appendix B). About 20,000 lines are
   engine-like logic, about 6,600 are tool-layer code (tool surface, result delivery, hint rendering), 1,505 are hosting. `AgentLoop`,
   `Utilities.PlanStepRunner` and `Git` together use about 35 of its types.
2. **The layering is inverted.** Because tool-layer concepts sit in Common, every layer can reach them, and engines use them: engines call
   `ToolCallHint` (152 text matches in `Engines.*`), take the caller's tool surface as a callable check (about 64 matches in 10 engine files, 62 code
   lines in 11 Common files) and throw `ToolException` subclasses whose messages already name tools (Engines.Basic 323 throws, Engines.Advanced 234,
   Common 26; tool classes 9). Most of the text comes from a few shared builders: `DocumentLookup.RequireDocument` and its not-found messages (about 260
   engine call sites), snippet lookup (165), `DeclarationNotFoundDetail` (71, of which 58 from Engines.Advanced). Tool-layer helpers also sit in
   Engines.Basic (`RefactoringToolHelpers`, `RejectedContentClassifier`, `ViewResults`) because the tool projects cannot see each other.
3. **Slow tests run whether or not their area changed.** The Git split moved the slow Git fixtures into `Tests.Git`, but the selection rule that would
   skip them is an unstarted draft ([plan_full_suite_nightly_and_tail_shard.md](plan_full_suite_nightly_and_tail_shard.md)).

About 25 open plans, proposals and findings touch these themes (Appendix C); several would entrench the inversion if run as written. The counts above are
text-search counts from three surveys of 2026-11-27 (condensed copies under the scratch root, `plan_design_roadmap_layering_and_splits\`).

## Decision

Dated entries; later entries do not override earlier ones unless they say so. Q-numbers continue the Build/RunTest design session.

- **2026-11-26, Common rule (owner):** no new functionality goes into Common for lack of a home; it is a standing objective, not a mandate to empty
  Common at once. Lightweight DTOs may stay; a DTO moves with its area only when the move is low friction (all users at or above the target, mechanical
  fixes only).
- **2026-11-26, splits are mechanical and focused (owner):** a split is file moves, wiring and a namespace rename, and it produces a project with one
  job. Coupling that a split exposes is a finding with its own step or proposal, never hidden by leaving things in Common. Exposing it is a benefit of
  the splits: the masked coupling has added complexity and amplified the blast radius of changes.
- **2026-11-26, target layering (owner):** engines report what went wrong and which kind of next step resolves it, as typed facts; only the tool layer
  knows the caller's tool surface, names tools and writes the text. Engines never take a callable check and never call `ToolCallHint`. Origin of the
  inversion (owner): "the engine knows what went wrong and how to resolve it, so let it write the hint".
- **2026-11-26, Q35 (owner):** the `ToolException` fix (an engine-layer exception family carrying facts, the rename, the rollout) is its own proposal
  (Phase 3), not part of the Build/RunTest plan.
- **2026-11-27, roadmap first (owner):** review the existing plans, survey Common and the engines by area, tie the sub-plans together in this roadmap,
  then tackle areas in order of complexity so easier splits and fixes make later ones easier. Owner lean: fix existing issues first, so the Build/RunTest
  move comes last in its group and is clean and mechanical. This reverses the earlier Q6 order (extraction now precedes the move).
- **2026-11-27, Q40 Core projects (owner):** a shared tool-layer project (`Tools.Core` or similar) is likely needed, and possibly an `Engines.Core` so
  things can move out of Common. Owner context: `*.Advanced` used to depend on `*.Basic` and has been kept separate on purpose, so the flavors do not
  depend on each other and shared code needs a home of its own. Design belongs to Phase 3. (Fact to reconcile there: `Engines.Advanced` still references
  `Engines.Basic`.)
- **2026-11-27, Q42 SnippetMatching goes into `Engines.Core` (owner):** Phase 2 creates `RoslynSentinel.Engines.Core` with SnippetMatching as its
  first resident, so the engine-side Core project of Q40 exists from Phase 2 on. (The planner had recommended a focused project of its own; the owner
  chose `Engines.Core`.) Planner follow-up for the Phase 2 design: one folder and namespace per area inside it, and a written admission rule
  (engine-like logic used by both engine flavors or by engines and tools; no tool-surface text), so it does not become a second Common.
- **2026-11-27, Q41 start now (owner):** Phases 0 to 2 need no design and run while Phase 3 is designed. The Git namespace rename is dispatched first.
- **2026-11-27, order boundary (planner, proposed, not yet confirmed):** the Build/RunTest move is gated only by work that touches its files (its own
  extraction, Phase 1's `FullTestMarkerStore` edit, the plans that edit `TestInstanceLauncher`); the repo-wide rollout (Phase 5) does not gate it.

## Execution rules

- This doc is the index. Each item below is designed as its own plan or proposal (`plan-design`), run with `/implement-plan`, and closed on its own.
  Only the next two or three items carry detailed plans; a later item is planned when its gate is met, from the facts as they are then.
- When a sub-plan closes, its row in "Steps" gets the commit hashes and this doc's Status line is updated. A finding that belongs to another phase is
  added to Appendix D with its owner, not fixed in passing.
- Split plans follow [reference_splitting_functionality_out_of_common.md](../references/reference_splitting_functionality_out_of_common.md). Layering
  plans follow the Phase 3 proposal once it is approved; until then no plan adds engine-side wording helpers or passes a callable check into an engine.
- A parked or draft doc listed in Appendix C under "reshape" is not run as written.

## Steps

### Phase 0.1 - Git namespace rename
- Doc: [plan_rename_git_project_namespaces.md](closed/plan_rename_git_project_namespaces.md) (IMPLEMENTED 2026-11-27, commits c2b3acb9, fdd7a862, cc147f9e, 1a7f3b1f).
- Depends on: none
- Effect: text-neutral.
- Change: `ChangeNamespace` to `RoslynSentinel.Git` and `RoslynSentinel.Git.Worktrees`; folder `GitWorktree/` -> `Worktrees/`.
- Done when: that plan is in `plans/closed/`.
- Done: one `ChangeNamespace` call rewrote 144 files / 1,866 references exactly as its dry run said (first real use of the tool); parent folder
  first, sub-namespace folder second worked (8 files / 19 references); full run `cc147f9e-20261127-004211-40584`: 12,633 tests, 0 failures. Tool
  findings: the dry-run note names a `returnDiff` parameter the tool rejects; a folder `MoveFile` leaves the emptied source folder on disk (both in
  INBOX).
- Effort: M

### Phase 0.2 - Agent-loop support files leave Common
- Doc: to be written (small plan).
- Depends on: none
- Effect: text-neutral.
- Change: move `AgentLoop/ErrorPolicyDocument.cs` and `PromptTokenEstimator.cs` (180 lines) from Common to `RoslynSentinel.AgentLoop`. No Common file
  uses them; Server.Advanced also uses `ErrorPolicyDocument` (confirm it references AgentLoop). A warm-up for the cross-project `MoveFile` route.
- Done when: `Build` 0 errors, full run clean, plan closed.
- Effort: S

### Phase 0.3 - Doc hygiene
- Doc: none (a `todo-review` session).
- Depends on: none
- Effect: text-neutral.
- Change: close `plan_prebuild_and_idle_stale_server_restart.md` (built; one observation left); fold `plan_error_codes_follow_cause.md` into
  `proposal_throw_typed_exceptions_not_failure_results.md`; give the untracked docs TODO.md lines (Appendix C, "untracked"); triage the unowned INBOX
  lines of Appendix D; correct the stale lines named there.
- Done when: every doc in Appendix C has a TODO.md line or is closed.
- Effort: S

### Phase 1 - Test selection
- Doc: [plan_full_suite_nightly_and_tail_shard.md](plan_full_suite_nightly_and_tail_shard.md) (DRAFT; owner decisions D1-D6 open),
  [plan_shorten_verification_loop.md](plan_shorten_verification_loop.md) (Step 13 gate, data from 2026-11-29),
  [plan_in_memory_test_tier_for_battery.md](plan_in_memory_test_tier_for_battery.md) (dormant).
- Depends on: none. Gate it sets: its Step 1 changes `FullTestMarkerStore.Describe`, so it lands before Phase 4.2 pins that text.
- Effect: behaviour-changing (the marker records the head commit and counts a red run as stale; task-end runs select by changed area).
- Change: settle D1-D6, run Steps 1-6 (marker, watch list, nightly run, debt check); decide whether the in-memory tier's leftover
  `RunTestTests`/`BuildEngineTests` items are finished or dropped before Phase 4.3 moves those fixtures.
- Done when: a change outside `RoslynSentinel.Git/` and `RoslynSentinel.Tests.Git/` skips `Tests.Git` at task end and the nightly run covers it.
- Effort: M

### Phase 2 - First mechanical split out of Common: SnippetMatching into a new Engines.Core
- Doc: [plan_move_snippet_matching_into_engines_core.md](plan_move_snippet_matching_into_engines_core.md) (DRAFT 2026-11-27; grounded; three owner
  questions open).
- Depends on: Phase 0.1 (so the first real `ChangeNamespace` result is known).
- Effect: text-neutral.
- Change: create `RoslynSentinel.Engines.Core` (Q42; above Common, below both engine flavors) and move 18 files / 3,945 lines (`SnippetMatching/` plus
  `LineBlockAligner`, `SnippetMatchOutcome`, `SnippetSpan`) into it, with a test project for its fixtures. Two uses to cut first: `ToolException.cs` -> `SnippetFailureDetail`, and one edit-application file ->
  `LineBlockAligner`. First real use of the multi-file `MoveFile` on production code. It carries its own inversion with it (10 exception
  constructions and 2 `ToolCallHint` uses in `SnippetErrorFormatter`), recorded for Phase 5.
- Done when: `Build` 0 errors, test counts preserved, full run clean, plan closed.
- Effort: M

### Phase 3 - Layering design
- Doc: to be written: one proposal (working name `proposal_engines_report_facts_tool_layer_writes_text.md`).
- Depends on: none (runs in parallel with Phases 0 to 2).
- Effect: design only.
- Change: settle (a) how facts travel: the engine-layer exception family, fact records on exceptions and results (Q37), the rename of the
  `ToolException` family; (b) the Core projects of Q40: what a shared tool-layer project holds, whether an `Engines.Core` exists and what goes in it,
  and whether `Engines.Advanced` keeps its reference to `Engines.Basic`; (c) the one render point (Q38) and its relation to
  `plan_shared_tool_execution_pipeline.md`'s `FromException`; (d) the vocabulary for "kind of next step" (candidate: the roles of
  `proposal_tool_role_attribute.md`); (e) the namespace convention (project-named, versus the feature sub-namespaces of
  `proposal_dto_ownership_role_lineage.md`). It then reshapes the docs marked "reshape" in Appendix C.
- Done when: the proposal is APPROVED and each reshaped doc's Status line points at it.
- Effort: DS

### Phase 4.1 - Pilot: the docs engine
- Doc: to be written after Phase 3.
- Depends on: Phase 3
- Effect: text-neutral (wire text unchanged; it is written one layer up).
- Change: `DocTrailerEngine` (3 hint sites at `:311`, `:405`, `:811`, no throws, one consumer) reports facts; the tool layer writes the text. Proves the
  pattern and creates the shared tool-layer home on the smallest area.
- Done when: golden pins through the tool unchanged; no `ToolCallHint` in the area.
- Effort: S

### Phase 4.2 - Build/RunTest extraction
- Doc: to be written from the on-hold draft (its S1-S24 inventory, Q33-Q38).
- Depends on: Phase 4.1, Phase 1 Step 1; ordered against `plan_stateless_http_toolset_enablement.md` Step 5 and the JSON toolsets plan (both edit
  `TestInstanceLauncher`) and the tool usage review's Step 1b (`BuildEngine.ListingCall`).
- Effect: text-neutral.
- Change: 24 sites in four channels (exception message 15, result field 5, returned string 3, finding 1) report facts; the two `isCallable`
  parameters leave `BuildEngine`; the three defects of Appendix D are fixed in the slices that touch them.
- Done when: pins unchanged; `Search` for `ToolCallHint` and `isCallable` in the ten Build/RunTest files returns nothing.
- Effort: L

### Phase 4.3 - Build/RunTest move
- Doc: to be rewritten from the on-hold draft (its decisions Q3, Q5, Q7, Q8, Q9, Q29, Q31, Q32 stand).
- Depends on: Phase 4.2, Phase 0.1
- Effect: text-neutral.
- Change: `RoslynSentinel.Engines.BuildTest` and `RoslynSentinel.Tests.BuildTest`, mechanical route of the splitting reference.
- Done when: counts rule satisfied, full run clean, the selection rule of Phase 1 names the new directories.
- Effort: M

### Phase 5 - Shared text builders, then engine areas by tier
- Doc: several plans, written one at a time after Phase 4.2.
- Depends on: Phase 3; each plan on the previous one's result.
- Effect: text-neutral per plan.
- Change: first the builders that feed most areas (not-found family, snippet lookup, `DeclarationNotFoundDetail`, the callable-check channel), then the
  areas of Appendix A, tier 1 to tier 4; tier 4 files are broken up before their text moves.
- Done when: no `ToolCallHint` use and no callable check below the tool layer; the `ToolException` family is renamed per Phase 3.
- Effort: L (several plans)

### Phase 6 - Rest of Common
- Doc: several plans and one proposal (the workspace manager), written after Phase 5 shows which inbound uses are gone.
- Depends on: Phase 3 for the homes; Phase 5 for `ToolCallHint`.
- Effect: text-neutral per plan.
- Change: tool-layer code moves up (result delivery 17 files, tool surface 18 files: Common no longer references the MCP SDK; hint rendering last);
  hosting pieces, migration support, line-ending policy, single-consumer DTOs and the telemetry slice move to their users; `PersistentWorkspaceManager`
  (3,957 lines, 18 interfaces) is taken apart last, under its own proposal.
- Done when: Common holds the shared kernel only (configuration, result contract, error types, paths, DTOs).
- Effort: L (several plans)

## Out of scope

- Splitting the Advanced engines and tools into vertical clusters (`proposal_usage_based_core_split.md`); it is tracked on its own and only ordered
  against Phase 4.2 where it edits `BuildEngine`.
- Any change to wire text, DTO properties or tool schemas. Every item is text-neutral except Phase 1.
- Numeric error codes (`proposal_tool_error_code_taxonomy.md` recommends against them).

## Risks and open decisions

1. **`Engines.Core` must not become a second Common** (follows Q42). Phase 2 writes its admission rule and keeps one folder and namespace per area;
   Phase 3 confirms or adjusts the rule when it settles what else goes there.
2. **Q43 (owner, Phase 3):** does `Engines.Advanced` stop referencing `Engines.Basic` once a shared engine project exists? It uses
   `DeclarationNotFoundDetail` at 58 sites, constructs `SymbolNavigationEngine` and is injected with `BasicRefactoringEngine`.
3. **Q44 (owner, recommendation given 2026-11-27):** the nightly plan's D1-D6 are settled in a fresh session running `plan-design` on that draft, in
   parallel with this roadmap's other items; each decision already carries a recommendation there. Constraints that session takes from this roadmap:
   the area watch list names project directories (today `RoslynSentinel.Git/`, `RoslynSentinel.Tests.Git/` and the tool-side Git files; later
   `RoslynSentinel.Engines.BuildTest/` and its test project), and its Step 1 lands before Phase 4.2.
4. **Q37 and Q38** (fact shape, render point) are carried into Phase 3; the owner leans to typed facts plus a tool-layer builder.
5. **The order boundary** in Decision is proposed, not confirmed.
6. **Survey quality.** Counts are text searches; the engine survey was a light pass and the Common consumer counts can include name collisions. Each
   sub-plan re-measures its own area (rule 2.1 of the splitting reference) before it is approved.
7. **Tool-side text needs a home both tool projects can reach** (reviewer inference): `Tools.Basic` and `Tools.Advanced` cannot see each other, and the
   central call filter sits in `Server.Advanced`. Phase 3 settles it.
8. **Size.** This is months of work across many sessions. The rule that only the next items carry detailed plans is what keeps the roadmap from
   drifting; a phase whose facts have changed is re-surveyed, not run from this doc.

## Appendix A - Engine areas, easiest first (survey 2026-11-27)

| Tier | Areas (throws / hint sites) |
| --- | --- |
| Clean | Git; edit-effect facts |
| 1 | Docs (0 / 3), discovery (4 / 0), move helpers (0 / 0), move-set (0 / 0), rename (0 / 0), test-category tagging (0 / 0), analysis detectors (8 / 3) |
| 2 | Build/RunTest (24 / 14), namespace and copy-and-obsolete (0 / 0, `ResultError` strings), project structure (9 / 9), semantic replace (0 / 5), MS-tool augmentation (1 / 0), code generation (28 / 4), commenting and instrumentation (11 / 1) |
| 3 | Signature change (4 / 2), edit-impact (0 / 7, takes `ActiveToolSurface`), diagnostics (0 / 3, `isToolActive`), navigation (31 / 16, stores `_isCallable`; hub) |
| 4 | Member edit (137 / 32, one 6,119-line file), `BasicRefactoringEngine` (51 / 16), syntax transforms (59 / 16), async migration (41 / 1), structural refactoring (77 / 3), modernization (78 / 5) |

Only Build/RunTest can become its own project by mechanical moves; the other areas hang off hubs (rename -> edit-impact -> diagnostics -> navigation;
the Advanced engines -> shared not-found helpers and `BasicRefactoringEngine`).

## Appendix B - Common by role (survey 2026-11-27; 308 files, 39,583 lines)

| Role | Files / lines | Belongs to | Split difficulty |
| --- | --- | --- | --- |
| Workspace manager and contracts | 21 / 5,678 | engine-like | hardest: `PersistentWorkspaceManager` implements 18 interfaces |
| Snippet matching | 18 / 3,945 | engine-like | easy: 2 inbound uses (Phase 2) |
| Edit application | 17 / 3,768 | engine-like | hard |
| Result envelope | 24 / 3,727 | contract part: kernel; delivery part (17 / 2,888): tool layer | delivery part medium: 5 inbound files |
| Operation ledger, edit attribution | 21 / 2,958 | engine-like | hard: used by 7 roles |
| Tool surface, toolsets, schema patching | 18 / 2,900 | tool layer | medium: 3 inbound uses, `ToolCallHint` -> `ToolSignatureCatalog` the hard one |
| Telemetry, call context, breakers | 16 / 2,235 | mixed | medium to hard: `ToolCallContext` used by 8 roles |
| Not-found diagnostics | 11 / 2,170 | mostly text writing | hard: 30 of Common's 62 callable-check lines; feeds about 260 engine sites |
| Configuration | 22 / 2,109 | kernel | stays |
| Server lifecycle | 13 / 1,505 | hosting | medium |
| Migration support | 12 / 1,384 | Advanced engines and tools | medium: split `BatchTypes.cs` first |
| Lookup | 12 / 1,365 | resolvers engine-like; path identity kernel | hard: 61 Common files use it |
| Pure data | 58 / 1,111 | kernel (46 of 56 types have one consumer project) | easy per type |
| Error vocabulary | 5 / 1,043 | kernel until Phase 3 | Phase 3 |
| Tool metadata, hint rendering | 14 / 799 | tool layer | last: `ToolCallHint` used by 25 Common and 27 engine files |
| Line endings | 8 / 764 | engine-like | medium |
| Tool-parameter vocabulary | 2 / 698 | tool layer (enums used by engines) | later |
| Child process, scratch, paths | 6 / 499 | kernel | stays |
| LLM client, agent-loop support | 6 / 493 | AgentLoop | easy: 2 files have no Common users (Phase 0.2) |
| Build and diagnostics result model | 3 / 372 | kernel; `BuildResultProjector` -> Build/RunTest | with Phase 4.3 |
| Repo-query seam | 1 / 60 | kernel | stays |

The MCP SDK is used by 13 Common files: tool surface 9, result delivery 2, `ResponseGatedProcessExit`, `UnrecoverableBreakerAttribute`.

## Appendix C - Existing docs and where they land

| Doc | State 2026-11-27 | Lands in | Action |
| --- | --- | --- | --- |
| plans/closed/plan_rename_git_project_namespaces.md | IMPLEMENTED 2026-11-27 | 0.1 | done |
| plans/plan_isolate_build_test_area_into_own_project.md | DRAFT, on hold | 4.2, 4.3 | split into two plans |
| proposals/proposal_isolate_heavy_test_areas_by_project.md | ACCEPTED, Area 1 done | 4.3; Area 3 unplaced | correct Area 2 text (pins helper and `TestProjectDetector` do not go to Common) |
| plans/plan_full_suite_nightly_and_tail_shard.md | DRAFT, untracked | 1 | settle D1-D6, run |
| plans/plan_shorten_verification_loop.md | IN PROGRESS | 1 | finish Step 13; sharding goes to the nightly plan |
| plans/plan_in_memory_test_tier_for_battery.md | dormant, untracked | 1 | finish or drop the Build/RunTest items |
| plans/plan_prebuild_and_idle_stale_server_restart.md | built, untracked | 0.3 | close |
| plans/plan_error_codes_follow_cause.md | IN-PROGRESS, untracked | 0.3 | fold into the typed-exceptions proposal |
| proposals/proposal_throw_typed_exceptions_not_failure_results.md | IN-PROGRESS, untracked | 3 | remaining S4, S5, Q4b, Q6b, Q6d, Q9; its `SuggestedTool` payload is reshaped |
| plans/plan_shared_tool_execution_pipeline.md | DRAFT, parked | 3, then 5 | reshape: `FromException` needs a render hook; its Step 4 passes a callable check into an engine helper; its Step 16 deletes the catches |
| plans/plan_central_engine_throw_and_lookup_helpers.md | DRAFT, parked | 3, then 5 | reshape: helpers throw facts, wording moves to the tool layer |
| proposals/proposal_tool_role_attribute.md | PROPOSED | 3 | reshape: roles as the "kind of next step" vocabulary; attribute home not Common |
| plans/plan_json_toolset_definitions.md (+ proposal) | DRAFT | 3 for the home of `ToolsetService`; ordered against 4.2 | reshape (already flagged in TODO.md) |
| plans/plan_stateless_http_toolset_enablement.md | APPROVED, not started | ordered against 4.2 | run before or after, not during |
| references/reference_tool_error_text_facts_options_policy.md | CURRENT | 3 | rule 2 states which layer holds the callable check |
| findings/finding_engine_layer_anonymous_and_untyped_returns.md | OPEN | 3, then 5 | `RefactoringToolHelpers` returns the tool envelope from Engines.Basic |
| proposals/proposal_dto_ownership_role_lineage.md | APPROVED, no plan, untracked | 3 (namespace convention), 6 (DTO moves) | reconcile |
| proposals/proposal_usage_based_core_split.md, findings/finding_tool_surface_usage_review.md | PARTIAL / DECISION | out of scope; Step 1b ordered against 4.2 | none |
| proposals/proposal_runtest_tests_for_members.md | PROPOSED, gated | ordered against 4.3 (`TestRunEngine`) | none |
| proposals/proposal_tool_error_code_taxonomy.md | no status | 0.3 | close |
| findings/finding_core_change_cost_causes_and_non_split_remedies.md | OPEN | 3 | its "one owner per idiom" remedies need a layer |

## Appendix D - Coverage ledger (findings of the design session and their owners)

| Finding | Evidence | Owner |
| --- | --- | --- |
| 24 hint sites S1-S24 in the Build/RunTest files, four channels | on-hold draft, Q34 | 4.2 |
| Q37 fact shape, Q38 render point | on-hold draft | 3 |
| Build/RunTest move decisions (names, one project, tests that move, `MsBuildPins`, DI extension, exceptions and enums that move) | on-hold draft Q3, Q5, Q7-Q9, Q29, Q31, Q32 | 4.3 |
| Message does not match its cause | `BuildEngine.cs:299` (INBOX 2026-11-27) | 4.2 |
| `TestInstanceException` not-found carries no missing-target kind | `TestInstanceLauncher.cs:351, 437, 606` (INBOX) | 4.2 |
| Gate-busy re-wraps drop typed facts | `BuildEngine.cs:333-336`, `TestRunEngine.cs:223-226` (INBOX) | 4.2 |
| Same advice or hint on the wire twice | `WorkspaceBuildTestImpl.cs:338-343`, `:546-549` (INBOX) | 4.2 |
| `GetWorkspaceHealth` passes no callable check | `WorkspaceHealthMiscImpl.cs:154-156` (INBOX) | 4.2 |
| `SuggestedTool` set, never read | `TestInstanceLauncher.cs:532` (INBOX) | 3 |
| `BuildToolResultShapeTests` may build without the OutOfProcess tag (hypothesis) | test inventory, on-hold draft Q16 | 4.3 |
| `EngineError`/`EngineErrorCode` are a leftover; `EngineErrorCode.BuildNotRun` is kept alive by one test | `Common/EngineError.cs` | 3 |
| `ErrorPolicyClassifier` hand-mirrors two Git error codes | `ErrorPolicyClassifier.cs:20-21` | 3 |
| Two engines name tools unconditionally (own TODOs) | `NamedArgumentsEngine.cs:162`, `MsToolAugmentEngine.cs:1885` | 5 |
| Tool-layer helpers in Engines.Basic | `RefactoringToolHelpers`, `RejectedContentClassifier`, `ViewResults` | 3, then 5 |
| `SentinelCallToolResult.cs`, `BatchTypes.cs`, `ToolEnums.cs`, `ToolException.cs`, `ItemFailure.cs` each hold several concepts | Common survey | 6 (split before moving) |
| Stale reference lines (architecture map "no MCP types"; splitting reference anchors) | INBOX 2026-11-27 | 0.3 |
| `ChangeNamespace` limits (cref case, single pass), `MoveType` cannot move into an existing file, `MoveFile` namespace update unbuilt | INBOX and TODO.md lines | tool follow-ups; 0.1 reports what it meets |
| Unowned INBOX lines in these themes (Git blob reads in Tools.Basic, rejected-filter hint and test, `RestoreState` text, runtime-toolset callability, hint goldens pinning git version, `suite: fast` hiding an `[Explicit]` test, closest-window score as text) | INBOX | 0.3 triage |
| Lesson for the splitting reference: expect masked coupling; record each instance as a finding with its own step or proposal | Decision, 2026-11-26 | 4.3 closing step |
