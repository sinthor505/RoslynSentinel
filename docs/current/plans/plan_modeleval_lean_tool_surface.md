# Plan: give ModelEval an 18-tool lean surface behind an env switch, A/B it against the 55-tool surface, then make it the default

**Status:** IN PROGRESS 2026-11-13. Steps 1a-1b done (be340c7e, 0a0b0ecc), 2a-2e done (e68197f8..a8dee25a). 18-tool lean ModelEval surface behind an env switch, A/B against the 55-tool surface (hold point), then default switch; PlanStepRunner deferred to a TODO.md line.

## Problem

ModelEval offers the model 55 tools (modes `Refactor`, `Workspace`, `WholeFileWrite` minus an 11-name
hide list). Their schemas and descriptions cost about 114k chars, roughly 28.5k tokens, which is most of
the ~23k-token turn-2 prompt measured in `eval_error_variant_replay_validation.md`. 25 of the 55 tools were
never called in 1,702 samples (2026-11-10 to 11-13), and near-duplicate tools split the first call after a
file miss between `ListAll`, `ListSolutionItems` and `Search`. Full evidence, the tool-by-tool table and the
alternatives that lost are in
[proposal_modeleval_lean_tool_surface.md](../proposals/proposal_modeleval_lean_tool_surface.md); this plan
sequences that proposal and does not restate it.

**Anchor status.** `file:line` anchors were checked by a reviewer against HEAD e5b0661c (2026-11-13). The
proposal had three wrong numbers, corrected here: `SymbolWireResolver.cs` signature :153, TODO :174, the
`isCallable: null` call :176 (proposal said :147/:174); the hide-list block in
`ServiceRegistrationExtensionsBasic.cs` is :203-211 (proposal :205-210); the `ChainEvalToolSurface.cs` hide list is
:22-33 (proposal :22-32). The reviewer also found hint sites the proposal missed (Step 2d/2e). Line numbers drift:
the implementer re-confirms each name with `Search`/`GetFileOutline` before editing (plan-writer skill, section 1).
Items marked `(new)` are created by this plan. A few files are named without a project because the reviewer gave
only the file name; the implementer locates them with `Search(mode: class|method)`.

## Decision

- Add `ROSLYNSENTINEL_MODELEVAL_TOOLSET=lean` to `ChainEvalToolSurface.NewHiddenTools()`: when set, the hide
  list is every discovered tool minus an 18-tool allow-list (12 core + 3 hedges: `GetMethodSource`,
  `UndoLastApply`, `ChangeSignature` + `ChangeAccessibility`, `ModifyModifier` and `UsingDirective` as plain
  members, not hedges). Default stays the full 55-tool surface until the A/B has run.
- Make every model-facing hint that names a tool outside the lean list surface-aware through
  `ActiveToolSurface.IsCallable`, so the full-surface text stays byte-identical.
- Reword prompts so they name `ListSolutionItems`/`Search` instead of `ListAll`.
- Run lean against full on the not-found/stop families and one ten-chain, then switch the default.
- PlanStepRunner gets its own allow-list later (deferrable).

Lean allow-list (18): ReadFile, GetFileOutline, Search, ListSolutionItems, GetLargeResult, ReplaceSnippet,
Member, RenameSymbol, CreateFile, DeleteFile, Build, RunTest, GetMethodSource, UndoLastApply, ChangeSignature,
ChangeAccessibility, ModifyModifier, UsingDirective. Schema cost is about 8.2k plus three schemas (8.2k is the
proposal's figure for 15 tools); re-measure in Step 1a. The resume family no longer needs extras; the LargeResult family keeps
its own two-tool allow-list, the legacy whole-file fixtures stay on the full surface, and the
`EditSnippetsExperiment` arm swaps in its own edit tool (Step 1).

### Owner decisions (2026-11-13)

- **2026-11-13 - Hedges: settled.** Keep all three (`GetMethodSource`, `UndoLastApply`, `ChangeSignature`) in the
  first A/B.
- **2026-11-13 - Resume family: settled, a different option from the draft's.** `ChangeAccessibility` and
  `ModifyModifier` join the BASE lean allow-list instead of being per-family extras. The resume capture then
  keeps its tools and is not re-recorded. This supersedes the draft's recommendation (per-family extras).
- **2026-11-13 - UsingDirective: settled.** `UsingDirective` is added to the base lean allow-list as a plain
  member (not a hedge). With the entry above the lean surface is 18 tools. `RenameSymbol`'s description reference
  to `UsingDirective` is therefore valid on lean and is not edited (Step 4). The proposal's "Overlaps resolved"
  modifier row is now only `ModifyBaseType` replaced by `ReplaceSnippet`/`Member`.
- **2026-11-13 - PlanStepRunner: settled.** Later, as Step 7. The plan closes after Step 6; Step 7 moves to a
  TODO.md line.
- **2026-11-13 - A/B rule: settled, decided before the runs.** Lean is non-inferior if its pass rate has
  overlapping confidence intervals with full on every scenario and `BudgetOut` is no higher. Interval method:
  Wilson 95% (the drafter's recommendation; the plan had no method before).
- **2026-11-13 - Step 4 description cross-references: settled, option (a).** Drop the references to tools off the
  lean list (no surface-conditional description text).
- **2026-11-13 - Prompt rewording: settled, option (a).** `AgentSystemPrompts.cs` is reworded unconditionally;
  PlanStepRunner and the full-surface legacy fixtures get the new text too.
- **2026-11-13 - A/B run mode: settled.** `modeleval-runner`. The owner will be present during the run, so
  mid-run decisions are asked of the owner with a recommendation.
- **2026-11-13 - Plan approved as written, including the pre-Step-3 gate:** ask the owner and peers (`ListAgents`)
  before Step 3 starts (Step 3, "Gate before starting").
- **2026-11-13 - Remaining items accepted at their recommendation** by the owner's overall approval of the plan:
  decision 4 (row cap: accept and read the A/B first-call split and turns), decision 9 (replay needs lean source
  runs: do nothing now), decision 10 (Step 1 signatures: settle at the Step 1a review) and decision 14 (unplaced
  anchors: the implementer locates them). No decision is listed open.

## Execution rules

- `Build` 0 errors before each commit. One `Build`/`RunTest` at a time (shared `bin/obj`); tests are run by the
  dispatcher, not the implementer.
- Compare test results with the baselines (`docs/known-failing-tests.*.txt`, `docs/known-build-warnings.*.txt`);
  report only new failures.
- Slices marked text-neutral change no model-visible text on the full surface and run first, so a hold at Step 5
  does not idle the run. Steps 3 and 4 are behaviour-changing; do not start them until the owner has seen
  Steps 1-2 land.
- Commits: `Plan: plan_modeleval_lean_tool_surface` trailer on each slice; `Closes:` only on the commit that
  finishes the plan, which is Step 6 (Step 7 is moved to a TODO.md line, owner decision 2026-11-13).
- Tool error and hint text carries facts and options only, never policy (CLAUDE.md); the lean-surface wording in
  Step 2 names callable alternatives and nothing more.
- Every Step 2 slice carries two kinds of test: (1) a golden-string test, full surface, that the text equals a
  copy captured from HEAD before the edit (byte-identical); (2) a lean-surface test that the text names no tool
  outside the list. Each test must exercise the changed site itself, not a shared helper (reviewer confirms).
- Contract skeleton: the new members in Step 1 are small enough to carry real bodies, so no
  `throw new NotImplementedException()` stubs are expected. Step 6 still searches for leftover stubs because
  stubs compile clean.
- Lane tiers (`.claude/implementer-tiers.json`, 2026-11-13): depth tier 1 = 3 files / 15 edits / 1 acceptance /
  35 tool calls; sweep tier 0 = 1 operation / 8 files / 25 call sites / 2 projects / 30 tool calls. Slices below
  are sized to those; a slice that grows past them is split, or goes to `implementer-senior`.
- Peers: before committing Step 3, call `ListAgents`; the prompt strings are the files peer eval jobs build from.

## Mode

Owner present during the run. Recommended mode: **Batched** (about 15 slices over 2 code areas, the ModelEval test
project and the hint/prompt sites in `Common` and the engines). Owner to confirm. Batches:

- **A** = Steps 1a + 1b (env switch, 18-tool allow-list, arm and legacy opt-out).
- **B** = Steps 2a-2e (surface-aware hints; pairs 2a/2b/2c/2d/2e as listed under Steps).
- **C** = Steps 3-4 (prompt rewording after the Step 3 gate, then the two description edits).
- **D** = Steps 5-6 (live A/B, then default switch and re-baseline; the main session supervises via
  `modeleval-runner`, and the hold point applies between 5 and 6).

## Steps

Schedule: 1a, 1b and 2a-2e are text-neutral. Parallel pairs (two implementing agents at most, disjoint files):
1a with 2a; 2b with 2c; 2d with 2e. 1b waits for 1a. Steps 3 and 4 are behaviour-changing.
Steps 5 and 6 are hold-gated. Step 7 is independent and deferrable.

### Step 1a - Env switch, allow-list and lean surface test (contract first)
- Files: `RoslynSentinel.Tests.ModelEval/ChainEvalToolSurface.cs` (hide list :22-33; `NewHiddenTools()`); the
  existing surface test class `ChainEvalToolSurfaceTests`; a new test file beside it for the lean tests
  (`ChainEvalToolSurfaceLeanTests.cs`, new).
- Depends on: none.
- Effect: text-neutral. Default is the full surface; nothing changes unless the env var is set.
- Change: in one `batchEdits` call on `ChainEvalToolSurface.cs`, add
  - (a) `LeanAllowList` (new), the 18 names above; re-measure the lean schema size (about 8.2k plus three schemas
    is the expected figure, not a measurement) and record it in the commit;
  - (b) an env reader for `ROSLYNSENTINEL_MODELEVAL_TOOLSET`, copying the pattern at
    `OrderPricingRefactorChainAgentTests.cs:113-116` (SetUp reads the variable, `OrdinalIgnoreCase` comparison,
    a `TestContext.Out` line naming the arm); unknown values fall back to full;
  - (c) the lean branch in `NewHiddenTools()`: every tool from `McpToolSchemaPatcher.DiscoverToolMethods`
    (`Common/McpToolSchemaPatcher.cs:222`) minus the allow-list;
  - (d) a way for the `EditSnippetsExperiment` arm to swap its edit tool into the allow-list (an optional
    parameter or a sibling overload (new)) and a way for the legacy whole-file fixtures to stay on the full
    surface. No resume-family extras are needed (owner decision 2026-11-13). Signatures are the implementer's
    choice, recorded in the commit (Risks, decision 10).
  - **Assembly timing hazard.** `DiscoverToolMethods` sees only already-loaded `RoslynSentinel*` assemblies, and
    `NewHiddenTools()` is evaluated before tool registration. A Tools assembly not yet loaded would be missing
    from the discovered set, hence missing from the hide list, and then registered as allowed. The lean test must
    assert that every tool outside the allow-list is hidden with all Tools assemblies loaded, and the change must
    make that true (for example force-loading the Tools assemblies in the lean branch) rather than rely on test
    ordering.
  - **`EditSnippetsExperiment` arm** (`OrderPricingRefactorChainAgentTests.cs:119-126`, arm at :121-122): it builds
    `NewHiddenTools()` plus "ReplaceSnippet" (:122). Under lean that leaves no edit tool, because the swapped-in
    edit tool is not on the allow-list either. Under lean the arm must add the swapped-in edit tool (the reviewer
    calls it `EditSnippets`) to the allow-list as well as hiding `ReplaceSnippet`. The `EditSnippets` tool name
    was not placed in source for this plan; confirm it first.
- Done when: `Build` 0 errors; `ChainEvalToolSurfaceTests.OfferedTools_HaveDeleteFile_AndNoWriteFileGitOrProjectTools`
  still passes unchanged (reviewer: it requires DeleteFile, ReplaceSnippet, Member, GetFileOutline and RenameSymbol
  present and ten others absent, and holds under lean as well); the new lean test class passes for: lean
  hide list complement equals exactly the 18 names (`ChangeAccessibility`, `ModifyModifier` and `UsingDirective`
  callable; the EditSnippets arm is the only case that adds a tool beyond the 18, and it also hides
  `ReplaceSnippet`); first re-confirm that the ten names the existing test requires absent do not include any of
  those three (the reviewer's "passes unchanged under lean" predates the 18-tool list); every
  discovered non-allow-listed tool is hidden with all Tools assemblies loaded; unset returns the pre-change list;
  an unknown value returns the default.
- Lane/tier: `implementer` depth tier 1 for `ChainEvalToolSurface.cs` (and the arm edit if it lands here); the
  test file goes to `test-writer`. The dispatcher runs the tests.
- Done: be340c7e (2026-11-13). Lean schema 29,741 chars for 18 tools vs 80,057 chars for the 55-tool surface,
  measured in-process (a different method from the 114k estimate; compare like with like). Signatures:
  `NewHiddenTools(IEnumerable<string>? extraAllowed = null)` reads the env var; `NewHiddenTools(string? toolsetValue,
  IEnumerable<string>? extraAllowed)`; `NewFullSurfaceHiddenTools()`; `LeanAllowList`; `IsLeanValue`; `ToolsetEnvVar`;
  private `LoadToolAssemblies()` handles the assembly-timing hazard.

### Step 1b - Wire the EditSnippets arm and the legacy-fixture opt-out
Shrunk by the owner's resume-family decision (2026-11-13): there are no per-family extras for the resume family,
so `TranscriptResumeAgentTests.cs` (:207-209) takes the 18-tool list from `NewHiddenTools()` as it is and needs no
edit. If the EditSnippets arm is already done in 1a, this slice is only the legacy opt-out and may be merged into
1a (it is then one more call site, not a new file set).
- Files: `OrderPricingRefactorChainAgentTests.cs` (:119-126 incl. the EditSnippets arm :121-122, if not done in
  1a); the legacy whole-file fixtures that must stay on the full surface (identify the call sites with
  `Search(mode: references)` on `NewHiddenTools`; the proposal lists WholeFileRewrite, SizeThreshold, PlanOnly,
  PlanThenExecute, PlanImplementVerify); the other callers (`ScenarioAgentTests.cs:425-427` and the surface test)
  take the list already and are expected to need no edit. A test in the 1a lean file or a new per-slice file.
- Depends on: 1a.
- Effect: text-neutral on the default surface. With lean set, the EditSnippets arm has an explicit surface and the
  legacy fixtures keep the full one.
- Change: apply the 1a mechanism at the arm and the legacy fixtures; comment the EditSnippets interaction
  (CLAUDE.md, comment intentional deviations). The LargeResult family keeps its own two-tool allow-list
  (`LargeResultPagingAgentTests.cs:79`) and is not edited.
- Done when: `Build` 0 errors; a test asserts that under lean a legacy-fixture surface is the full surface and the
  EditSnippets arm has an edit tool; the default surface test still passes. Dispatcher check: run the resume
  scenario (`stale-caller-edit-after-rename.json`) once with `ROSLYNSENTINEL_MODELEVAL_TOOLSET=lean`; it must not
  end Inconclusive on "SchemaChanged" or an `IsError` mismatch (owner expectation: it still replays, because
  `ChangeAccessibility` and `ModifyModifier` are on the list).
- Lane/tier: `implementer` depth tier 1 (up to 3 source files plus a test file; split if the reference search
  finds more legacy call sites).
- Done: 0a0b0ecc (2026-11-13). The legacy whole-file fixtures never call `NewHiddenTools`, so they needed no edit
  (a test pins it). Resume check done with `ResumeDriftProbeTests.Probe_ReplaysTheCaptureAndPrintsDrift`: all 7
  tools in the kept turns are still offered under lean; the output is identical on the full surface, and both
  report `SchemaChanged: ReadFile: properties.preferredTarget differs` (pre-existing, not caused by lean).

### Step 2a - Surface-aware hints: SymbolWireResolver, SymbolNotFoundDetail and the workspace manager
- Files: `RoslynSentinel.Common/SymbolWireResolver.cs` (signature :153, TODO :174, `isCallable: null` call :176,
  so `SymbolNotFoundDetail.cs:163-176` names `ListAll` regardless of surface); `PersistentWorkspaceManager.cs:435`
  (path to confirm); `SymbolNotFoundDetail.cs` only if its parameter needs a change (it already honours
  `isCallable`); new test file `SurfaceAwareHintsSymbolResolverTests.cs` (new).
- Depends on: none (disjoint from 1a, 2b, 2c, 2d, 2e).
- Effect: text-neutral on the full surface; the lean text names `ListSolutionItems`/`Search` instead of `ListAll`.
- Change: pass the real `isCallable` at `SymbolWireResolver.cs:176` (resolving the TODO at :174). The workspace
  manager has no `ActiveToolSurface` (the TODO at `SymbolWireResolver.cs:174` says so): inject it, and use it at
  :435. Measure the blast radius of the constructor change with `Search(mode: references)` before editing; if
  injection needs a DI registration edit or touches more than 3 files, split it into a prep slice (inject, no
  behaviour) and a consumer slice (mark `implementer-senior` if it still exceeds depth tier 1).
- Done when: `Build` 0 errors; the golden-string and lean-surface tests for both call sites pass. The existing
  `SymbolNotFoundDetail` tests cover only the path that already honoured `IsCallable` and are not evidence here.
- Lane/tier: `implementer` depth tier 1 (3 files plus a test file may exceed it; see Change); `test-writer`.

### Step 2b - Surface-aware hints: ToolArgumentValidator
- Files: `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs` (static dictionary hints :467-473, naming
  `ListWorkspaceSolutions` and `ListAll` in two filePath hints); new test file
  `SurfaceAwareHintsArgumentValidatorTests.cs` (new).
- Depends on: none (disjoint; pairs with 2c).
- Effect: text-neutral on the full surface; lean text drops the non-lean names.
- Change: the hints are a static dictionary, so they cannot call `IsCallable` at definition time. Make the lookup
  surface-aware (resolve the text per call, or hold callable-and-fallback pairs), keeping full-surface strings
  byte-identical.
- Done when: `Build` 0 errors; golden-string test for each of the three dictionary hints on the full surface, and
  a lean-surface test that none names `ListWorkspaceSolutions` or `ListAll`.
- Lane/tier: `implementer` depth tier 1 (1 source file plus a test file); `test-writer`.

### Step 2c - Surface-aware hints: WorkspaceReadNavigationImpl and ProjectStructureEngine
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` (:649 truncation warning, which also names
  `QuerySymbolRelationships`, not on the lean list; :1268); `RoslynSentinel.Engines.Basic/ProjectStructureEngine.cs`
  (:80); new test file `SurfaceAwareHintsNavigationEngineTests.cs` (new).
- Depends on: none (disjoint from 2b and 2d). Layering: `ActiveToolSurface` must be reachable from `Engines.Basic`
  (sibling rule: shared helpers go one layer down in `Common`); if it is not, report instead of working around.
- Effect: text-neutral on the full surface; lean text changes as in 2a.
- Change: same pattern at the three sites; :649 also needs the `QuerySymbolRelationships` mention made
  surface-aware.
- Done when: `Build` 0 errors; golden-string and lean-surface tests per site.
- Lane/tier: `implementer` depth tier 1 (2 source files plus a test file); `test-writer`.

### Step 2d - Surface-aware hints missed by the proposal: BuildEngine and TestingEngine
- Files: `RoslynSentinel.Engines.Basic/BuildEngine.cs` (:106, :219, :252, zero-project messages naming `ListAll`);
  `RoslynSentinel.Engines.Advanced/TestingEngine.cs` (:331); new test file
  `SurfaceAwareHintsBuildTestingTests.cs` (new). Found by the reviewer, not in the proposal.
- Depends on: none (disjoint from 2e and 2c; Engines.Basic and Engines.Advanced are sibling projects, so
  `ActiveToolSurface` must come from `Common`).
- Effect: text-neutral on the full surface.
- Change: same pattern at four sites.
- Done when: `Build` 0 errors; golden-string and lean-surface tests per site.
- Lane/tier: `implementer` depth tier 1 (2 source files plus a test file).

### Step 2e - Surface-aware hints missed by the proposal: orientation-breaker fallback and SymbolNavigationEngine
- Files: `ServiceRegistrationExtensionsBasic.cs` (:879-880, orientation-breaker fallback, which has its own TODO;
  project to confirm); `SymbolNavigationEngine.cs` (:2793-2794, :2881, names `FindReferences`; project to
  confirm); new test file `SurfaceAwareHintsOrientationNavigationTests.cs` (new). Found by the reviewer.
- Depends on: none (disjoint from 2d). Note `OrientationCircuitBreaker.cs:76-94` already drops a hint when the tool
  is not callable; this site is the fallback path.
- Effect: text-neutral on the full surface.
- Change: same pattern; resolve or update the TODO at :879-880 as part of the edit (CLAUDE.md: the code is the
  source of truth).
- Done when: `Build` 0 errors; golden-string and lean-surface tests per site.
- Lane/tier: `implementer` depth tier 1 (2 source files plus a test file); `test-writer`.

### Batch B outcome (Steps 2a-2e, 2026-11-13)
- Done: 2a e68197f8 + b839a447 (isCallable through `SymbolWireResolver.ResolveAsync`; `PersistentWorkspaceManager` takes
  an optional `ActiveToolSurface`; no-candidate text names `Search` when `ListAll`/`GetFileOutline` are not callable);
  2b 0c045363 (`Validate` takes `isCallable`; `BuildParameterHints`); 2c 8c39e6e1 (`TruncationRecoveryAdvice`,
  `NoMatchBrowseAdvice`); 2d bae0f0e6 + dc1a68d7 (`BuildEngine` and `WorkspaceBuildTestImpl` get the surface);
  2e dd9b206e (`OrientationBreakerFallbackText(isCallable)`, public because Server.Advanced has no
  InternalsVisibleTo; its TODO removed).
- Deliberately unchanged, comment-only TODOs in a8dee25a: `ProjectStructureEngine` :80 (its only caller is a tool not
  on the lean list), `TestingEngine` :331 (`AddBenchmarkStub`, not on the lean list), `SymbolNavigationEngine`
  :2793-2794,:2881 (names `FindReferences`; needs a designed `Search(mode: references)` text, the one real remaining
  gap), `PersistentWorkspaceManager` :435 (a TODO to add `ListWorkspaceSolutions`, not a lean-unavailable name).
- Golden tests are copies of the HEAD source text with `ToolCallHint.Call(...)` segments, not strings captured by
  running HEAD (`ToolCallHint` renders from the live tool catalog). No manager-level test for
  `PersistentWorkspaceManager.ResolveFromWireAsync` (no helper loads a solution into a manager).
- Open follow-up (owner question, recommendation: only if the A/B shows models failing there): design the lean
  `Search(mode: references)` text for the `SymbolNavigationEngine` sites.

### Step 3 - Prompt rewording: ListAll -> ListSolutionItems/Search (behaviour-changing)
Slices 3a-3c. 3b and 3c are behaviour-changing (3a is read-only). Schedule after Step 2.

**Gate before starting 3b or 3c (owner-approved 2026-11-13).** Ask the owner, and ask peers via `ListAgents`,
whether any live eval job or A/B baseline is mid-run, and in particular whether `plan_error_variant_replay`
(Steps 13-14 on hold) or a peer is building from `AgentSystemPrompts.cs` or the scenario files. Start only after
the owner confirms; report who was asked and the answer in the slice report. 3a (inventory) needs no gate.

#### Step 3a - Inventory (read-only)
- Files: none edited.
- Depends on: none.
- Effect: no change.
- Change: `Search(mode: text, query: "ListAll")` over the ModelEval project and `Common/AgentLoop`, classify every
  hit as prompt string (reword), comment/test name (leave), or legacy full-surface fixture (leave). The reviewer's
  count is about 13 ModelEval files plus `AgentSystemPrompts.cs`. Known so far: the proposal's list
  (`FaultScenarios.cs:30,76,113,146`, `GuardScenarios.cs:36,189`, `InterfaceScenarios.cs:38`,
  `StopAdherenceScenarios.cs`, `RenameScenarios.cs`, `TwoRenameScenarios.cs`,
  `OrderPricingRefactorChainAgentTests.cs:258,360,496,678,869,1083,1447`), plus `BatchShapeScenarios.cs:17`
  (missed by the proposal). **Not reworded** (legacy fixtures that stay full-surface):
  `OrderPricingRefactorAgentTests.cs:51`, `PlanImplementVerifyAgentTests.cs:68,109,144`,
  `WholeFileRewriteAgentTests.cs:182`. `ErrorPolicyIntentTable.cs:104` is an intent table and probably not model
  text: check, and record the answer.
- Done when: the inventory (file, line, classification) is in the dispatcher report and cut into slices of at most
  8 files and 25 call sites each.
- Lane/tier: `explore-tier1`.

#### Step 3b - AgentSystemPrompts.cs
- Files: `RoslynSentinel.Common/AgentLoop/AgentSystemPrompts.cs` (:46, :51, :140; :60 names `GetMethodSource`, which
  stays on the lean list).
- Depends on: 3a (confirm no other `ListAll` in the file); 1b not needed.
- Effect: **behaviour-changing beyond ModelEval.** This file is also used by PlanStepRunner and the full-surface
  legacy fixtures, so their prompts change too, even though they do not run lean. Not text-neutral on the full
  surface. Handled as its own slice so it can be held separately; reworded unconditionally (owner decision
  2026-11-13, decision 12) after the Step 3 gate.
- Change: replace the `ListAll` mentions with `ListSolutionItems`/`Search`.
- Done when: `Build` 0 errors; a text search of the file for `ListAll` is empty; any test pinning the prompt text
  is updated in this slice only.
- Lane/tier: `implementer` depth tier 1 (1 file, 3 edits).

#### Step 3c - Scenario prompt files
- Files: the ModelEval scenario files from the 3a inventory (at least those listed in 3a). Sliced as 3c-1, 3c-2
  (and more if the inventory exceeds 8 files or 25 sites per slice); `OrderPricingRefactorChainAgentTests.cs`
  (7 sites) must come after Step 1b because both edit it.
- Depends on: 3a; 1b for the OrderPricing slice.
- Effect: **behaviour-changing for the full surface.** The prompt string is the same on every surface, so
  full-surface evals stop being told to "go straight to ReadFile/Search/ListAll" and results are no longer
  comparable with earlier full-surface runs at the prompt level. Peer eval jobs build from these files, so a peer
  run straddling the commit mixes prompt versions: `ListAgents` first.
- Change: one operation, repeated `ReplaceSnippet` `batchEdits` per file (the wording from 3b).
- Done when: `Build` 0 errors; the 3a-classified prompt strings no longer contain `ListAll`; prompt-pinning tests
  updated in one slice; dispatcher reports the leftover matches (comments, names, legacy fixtures) by file.
- Lane/tier: `implementer-sweep` tier 0, one operation per slice, at most 8 files and 25 sites each.

### Step 4 - Tool-description cross-references (behaviour-changing for every caller)
- Files: the `Member` tool description (names `TypedMember`) and the `ListSolutionItems` parameter schema text
  (names `ProjectDoc`); locate each with `Search(mode: attribute, query: McpServerTool)` or `mode: method`. The
  `RenameSymbol` description (names `UsingDirective`) is NOT edited: `UsingDirective` is on the lean list. Check
  `ClaudeLeanSchemaBudgetTests`.
- Depends on: none technically; scheduled after 3 so all behaviour-changing text lands together before the A/B.
- Effect: behaviour-changing. These descriptions are schemas served to every caller, not only ModelEval.
- Change: drop the two cross-references (owner decision 2026-11-13, decision 7, option a; no conditional text).
  `TypedMember` (in `Member`) and `ProjectDoc` (in the `ListSolutionItems` schema) are off the 18-tool list, so
  those references go. `ModifyModifier`, `ChangeAccessibility` and `UsingDirective` are on the list, so a
  description naming them (including `RenameSymbol`'s) needs no change. The proposal's "Overlaps resolved" row for
  modifiers is now only `ModifyBaseType` replaced by `ReplaceSnippet`/`Member`.
- Done when: `Build` 0 errors; `ClaudeLeanSchemaBudgetTests` passes; any generated tool table under
  `docs/generated/` is regenerated if the build does not do it.
- Lane/tier: `implementer` depth tier 1 (2 files, 2 edits).

### Step 5 - A/B lean against full (LIVE, hold point)
- Files: none edited; outputs go to an eval doc `docs/current/evals/eval_modeleval_lean_vs_full.md` (new, finding
  template).
- Depends on: Steps 1a, 1b, 2a-2e, 3, 4 landed and a qwen3.5 host with a loaded instance (the proposal names 113
  with `qwen3.5-9b-mtp`).
- Effect: no code change; produces the decision input for Step 6.
- Change: run Control, MissingFile, MissingMethod, NoSuchMethod, Typo and one ten-chain, 30 samples each, with
  `ROSLYNSENTINEL_MODELEVAL_TOOLSET=lean` and unset (full), on the same host and instance, interleaved if the
  harness allows. Follow `reference_model_eval_procedure.md`; the run goes through `modeleval-runner`.
- Done when: the eval doc reports, per scenario and per arm: pass rate with sample count, turns, `BudgetOut`
  count, first-call split (`ListSolutionItems` / `Search` / `ListAll` (full only) / other) and wall time, plus
  tool-schema tokens per arm. Outcome classes are named, not just pass/fail: ideal = lean non-inferior (Wilson
  95% intervals overlap on every scenario, `BudgetOut` no higher) with fewer tokens; limit = intervals do not
  overlap on any scenario, or `BudgetOut` higher (Step 6 held); infra = host crash or silent testhost exit
  (check `results.csv` rows), excluded and re-run, not counted. The acceptance rule is settled (owner decision
  2026-11-13, decision 8); the interval method is the drafter's recommendation. The first-call split and turns also show the
  effect of Search's 30-row cap replacing `ListAll` (decision 4).
- Lane/tier: not an implementer slice; `modeleval-runner` (owner decision 2026-11-13). The owner is present during
  the run; ask mid-run decisions (host crash, infra re-run, a scenario failing for an environment reason) with a
  recommendation. About 30 x 6 x 2 = 360 samples; flag as high-token work before starting.

### Step 6 - Switch the default to lean and re-baseline (behaviour-changing, hold-gated)
- Files: `ChainEvalToolSurface.cs`; baseline/eval docs that cite 55-tool numbers (list produced during the step).
- Depends on: Step 5 with a lean result the owner accepts.
- Effect: behaviour-changing. Every ModelEval family in use runs on 18 tools; the legacy whole-file fixtures stay
  on the full surface.
- Change: invert the default (lean unless `ROSLYNSENTINEL_MODELEVAL_TOOLSET=full`); re-run the families in use to
  produce new baselines; mark earlier results as 55-tool in the docs that quote them. Stub check:
  `Search(mode: text, query: "NotImplementedException")` over this plan's files. Add a TODO.md line for Step 7
  (PlanStepRunner allow-list) so the plan can close here.
- Done when: `Build` 0 errors; surface tests updated for the new default and passing; stub search empty;
  re-baseline numbers recorded; the Step 7 TODO.md line exists; this plan's Status updated and the closing commit
  carries `Closes: plan_modeleval_lean_tool_surface`.
- Lane/tier: `implementer` depth tier 1 for the code; re-baseline via `modeleval-runner`.

### Step 7 - PlanStepRunner allow-list (separate, deferrable)
- Files: `PlanStepRunner/Program.cs` (:212, :266, :277 runner calls `LoadSolution`, `Build`, `RunTest`; :242 names
  `LoadSolution`, `ListWorkspaceSolutions`), `RunnerOptions.cs` (:98-99 stale class names),
  `ServerStartupHelpers.cs:113` (`--hide-tools`).
- Depends on: none technically; scheduled last and tracked as a TODO.md line after Step 6 (owner decision
  2026-11-13, decision 3). `Utilities.PlanStepRunner` references only `Common`.
- Effect: behaviour-changing for PlanStepRunner runs only.
- Change: the reviewer found that `--hide-tools` already builds "all discovered minus hidden" outside claude-lean
  (the hide-list block at `ServiceRegistrationExtensionsBasic.cs:203-211`), so this step may need only a computed
  `--hide-tools` list and no new `--allow-tools` argument. Settle that first. Keep `LoadSolution` registered but
  hidden from the model; add `GetDiagnostics` (`UsingDirective` is already in the base lean list); fix the stale text at `Program.cs:242` and
  `RunnerOptions.cs:98-99`.
- Done when: `Build` 0 errors; a PlanStepRunner dry run lists only the intended tools to the model. Sizing when
  scheduled.
- Lane/tier: probably `implementer` or `implementer-senior` depending on the allow-list answer; re-size then.

## Hold point

**The A/B (Step 5) must run before the default switch (Step 6).** Every ModelEval result to date is a 55-tool
result and is not comparable with lean results, so no number measured before Step 5 supports flipping the
default. Steps 1-2 are text-neutral and may land earlier. Steps 3 and 4 change what full-surface evals see and
must be announced to peers (Step 3 especially, because peer eval jobs build from the same prompt files).

## Out of scope

- The legacy whole-file fixtures (WholeFileRewrite, SizeThreshold, PlanOnly, PlanThenExecute,
  PlanImplementVerify): they test `ApplyDiff`/`WriteFile` and stay on the full surface; their `ListAll` prompt
  mentions (listed in Step 3a) are not reworded.
- The LargeResult family's own two-tool allow-list.
- Changing claude-lean or `ToolClassRegistry.cs`; gating through `McpToolsetControl` (both rejected in the
  proposal).
- Re-recording the resume capture (not needed: owner decision 2026-11-13, decision 2).
- Step 7 (PlanStepRunner allow-list) is out of this plan's closing scope; it is a TODO.md line.

## Risks and open decisions

Every item below is settled or accepted at its stated recommendation by the owner (2026-11-13); none is open.
Items 6, 11 and 13 are findings and notes rather than decisions. Answers marked "review" come from the reviewer's
source check of 2026-11-13.

1. **Hedges (GetMethodSource, UndoLastApply, ChangeSignature). SETTLED 2026-11-13: keep all three** in the first
   A/B. `GetMethodSource` is named by the system prompt (`AgentSystemPrompts.cs:60`), `UndoLastApply` by
   `DeleteFile`'s description, and `ChangeSignature` serves ten-chain step 5. Revisit after the A/B using call
   counts.
2. **Resume family. SETTLED 2026-11-13, owner chose a third option:** `ChangeAccessibility` and `ModifyModifier`
   go into the BASE lean allow-list, not per-family extras (the draft's option a) and not a re-record (option b);
   `UsingDirective` was added the same way later (18 tools in all). Cost: about three more schemas on every lean
   prompt (re-measure in Step 1a), and near-duplicates of `ReplaceSnippet`/`Member` stay visible to the model (choice noise, the thing the proposal set out to reduce;
   the A/B first-call and tool-call counts will show it). Review, for the record: the capture calls `ModifyModifier`
   (line 1046) and `ChangeAccessibility` (line 1095); `KeepTurns` (`ResumeScenarioGate.cs:24-36`) decides whether
   those turns are kept. A recorded tool absent from the live list is Structural ("is no longer offered",
   `ToolSchemaComparer.cs:70-73`) -> `CheckSchemas` "SchemaChanged" -> Inconclusive (also
   `ScenarioAgentTests.cs:181`, `ResumeDriftProbeTests.cs:64`), and a hidden tool in a replayed call errors
   (`TranscriptReplayHelper.cs:32`), an `IsError` mismatch, also Inconclusive. With both tools on the list neither
   path is expected to trigger; Step 1b's dispatcher check confirms it with one resume run.
3. **PlanStepRunner. SETTLED 2026-11-13: later, as Step 7.** The plan closes after Step 6 and Step 7 becomes a
   TODO.md line (Step 6 creates it). Review suggests Step 7 may only need a computed `--hide-tools` list.
4. **ListAll vs Search row cap (answered by review, effect unmeasured). ACCEPTED 2026-11-13 at the
   recommendation: accept and read the A/B first-call split and turns.** `ListAll`
   (`WorkspaceReadNavigationTools.cs:51-61`, impl :547) is uncapped when `maxItems` is null, and large results go
   to `GetLargeResult`. Search declaration modes cap at 30 rows (`WorkspaceReadNavigationTools.cs:153`
   `KindListingMaxItems`, used at :193), ignore `maxResults` there, have no offset, and return `hasMoreData` plus a
   `listSummary` by project/file. So Search replacing `ListAll` truncates harder rather than paging more.
   Fixtures are small, so impact is probably low (hypothesis). Options: (a) accept and read the A/B first-call
   split and turns; (b) raise the cap or add an offset first (a product change, out of this plan). Recommendation:
   (a).
5. **Cross-plan baseline and the shared prompt file. SETTLED 2026-11-13, not a separate open decision.** The owner
   approved the plan as written, which includes the pre-Step-3 gate: ask the owner and peers (`ListAgents`) before
   3b/3c. `plan_error_variant_replay` (Steps 13-14 on hold) and live eval jobs use these prompts; Step 3 changes
   the prompt for every arm, and `AgentSystemPrompts.cs` also feeds PlanStepRunner and the full-surface legacy
   fixtures. The gate lives in Step 3.
6. **Scoring (answered by review).** Scoring is name-based on calls only (`ScenarioScorer.cs:121,177`) and the
   expected recovery tools are all on the lean list, so no scoring change is needed. `ToolSchemaComparer`: see
   decision 2. No open action beyond decision 2.
7. **Description cross-references (Step 4). SETTLED 2026-11-13: option (a), drop the references.** The
   alternative (b), conditional text, was rejected: no precedent for surface-conditional schema text and it adds
   code. `RenameSymbol`'s `UsingDirective` reference stays (the tool is on the lean list). Cost: `Member` and
   `ListSolutionItems` lose a pointer on the full surface too; if the
   full-surface evals later show the pointers mattered, restore them then.
8. **A/B acceptance rule. SETTLED 2026-11-13, decided before the runs:** lean is non-inferior if its pass rate has
   overlapping confidence intervals with full on every scenario and `BudgetOut` is no higher. At 30 samples per
   cell, noise is large (prior live arms needed p-values to separate F from G), so overlap is a weak bar: a real
   regression can hide inside the intervals. The interval method (Wilson 95%) is the drafter's recommendation,
   not an owner statement.
9. **Replay needs lean source runs. ACCEPTED 2026-11-13 at the recommendation: do nothing now.**
   `RoslynSentinel.Tests.ErrorVariants` replay sends each source transcript's own tool list, so lean screening
   needs lean-surface source runs. Harvest after Step 6; replay results to date remain 55-tool.
10. **Step 1 contract. ACCEPTED 2026-11-13 at the recommendation: settle it at the Step 1a review.** The proposal
    gives no signature for the EditSnippets-arm swap or the legacy-fixture full-surface opt-out (the
    per-family-extras part is gone with decision 2). Slices that depend on it: 1b, 6.
11. **Ten-chain accessibility is no longer a harder case.** With `ChangeAccessibility` and `ModifyModifier` on
    the list (decision 2), ten-chain step 3 has both routes available: `ReplaceSnippet`/`Member` and the two
    modifier tools (live ten-chain used `ChangeAccessibility` in 22% of samples). A lean ten-chain regression is
    therefore less likely to come from accessibility; if one appears, look elsewhere first (for example the
    `ListAll` route lost by the stop family, which the proposal expects to help stop adherence; hypothesis). The
    proposal's "Cost / risk" line saying step 3 "falls back to `ReplaceSnippet`/`Member`" is superseded.
12. **Prompt rewording is not surface-conditional. SETTLED 2026-11-13: option (a), reword unconditionally.**
    Step 3b changes `AgentSystemPrompts.cs` for PlanStepRunner and the full-surface fixtures too (`Search` and
    `ListSolutionItems` exist on every surface). Option (b), a surface-conditional prompt, was rejected. Effect: the
    A/B arms differ only in the tool list, and the old 55-tool numbers are not comparable anyway.
13. **Tests are new, not inherited.** No existing test is known to cover the Step 2 sites on either surface; the
    golden-string and lean tests in 2a-2e and the lean-list tests in 1a are what verify this plan. The planner
    (this document) defines that coverage; the reviewer confirms each test exercises its specific site, not a
    shared helper. An all-green run without them says nothing about these paths. The existing
    `ChainEvalToolSurfaceTests` case proves only that DeleteFile, ReplaceSnippet, Member, GetFileOutline and
    RenameSymbol are offered and ten others are absent, not that the lean list is exact.
14. **Unplaced anchors. ACCEPTED 2026-11-13 at the recommendation: the implementer locates them.**
    `PersistentWorkspaceManager.cs:435`, `ServiceRegistrationExtensionsBasic.cs:879-880` and
    `SymbolNavigationEngine.cs:2793-2794,2881` came from the review without a project, and the tool name
    `EditSnippets` (Step 1a) was not located in source for this plan. The implementer locates them before editing.
    The reviewer gave the PlanStepRunner hide-list path once as `ServerStartupHelpers` 203-211 and once as
    `ServiceRegistrationExtensionsBasic.cs:203-211`; this plan uses the latter and keeps `ServerStartupHelpers.cs:113`
    for `--hide-tools`.
15. **Run mode of the A/B. SETTLED 2026-11-13: `modeleval-runner`**, on a qwen3.5 host with a loaded instance (the
    proposal names 113). The owner will be present during the run; mid-run decisions (for example a host crash,
    an infra re-run, or a scenario that looks to be failing for an environment reason) are asked of the owner with
    a recommendation rather than decided by the runner.
