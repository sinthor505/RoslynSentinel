# Plan: fix the environment defects behind 10-chain batch F's two failures

**Status:** DRAFT 2026-11-11. Six fixes from the batch F root-cause trace; none built. Line numbers are from source at
d5bc2c94 and approximate where marked `~`; re-locate with `Search` before editing.

## Problem

Batch F (qwen3.5-9b-coder on 69606748, reasoning budget off) passed 8/10. Both failures trace to the environment, not
the model. Evidence and run folders: `evals/eval_tenchain_reasoning_replay_2026_11_10.md`, "Batch F" and "Root
causes". In short:

- **R1** (`20261111-042157-571`): ReplaceSnippet `action: 'replace'` six times, each paired with an `apply` retry; the
  error budget (5 per tool) tripped. The enum rejection offered no suggestion. T1 already uses 26.6k of 60,160 tokens.
- **R8** (`20261111-052200-824`): a self-made duplicate file the model could not delete. `DeleteFile` is not in the
  eval's modes, and every other route returned advice that dead-ended. T31 exhausted the context and the runner
  logged it as "finished".
- **Git**: the fixture is a temp copy (never a repo), yet `Workspace` mode exposes Git; three dead-end calls in R8.

## Decision

Fix the surface and the messages, not the prompt. Items 1-6 below are independent; order is by expected effect on
the eval. Items 1 and 6 are test-harness changes (ModelEval, Common); items 2-5 are server changes that need a
full build and server restart before live use.

## Execution rules

- Per CLAUDE.md: owner approval before each slice; MCP tools for all C#; one Build/RunTest at a time.
- Each slice: `Build` plus `RunTest` filtered to the touched test classes; commit with `Plan: plan_tenchain_batch_f_fixes`.
- Hint text naming a tool must use `ToolCallHint.Call` (or a `// hint-ok:` comment), or `Scan_MatchesBaseline` /
  `Scan_HintCallLiterals_AreValid` fail.
- Validation: after items 1-3, a 10-run batch on host 113 (same model, budget off), compared with batch F.

## Steps

### Step 1 - Eval tool surface: add DeleteFile, hide Git and project tools

- Files: `RoslynSentinel.Tests.ModelEval/OrderPricingRefactorChainAgentTests.cs` (`ActiveModes` ~line 70,
  `hideTools` ~line 132).
- Change: add `"WholeFileWrite"` to `ActiveModes` so `DeleteFile` is listed; add `WriteFile`, `ApplyDiff`,
  `ApplyUnifiedDiff` to `hideTools` (keep the edit surface unchanged otherwise). Also hide `Git`, `GitRemote`,
  `ListWorkspaceSolutions`, `LoadSolution`, `CreateProject`, `SplitProjectByFolder` (useless on a temp fixture; frees
  schema tokens).
- Check first: whether `hideTools` and modes are shared with other ModelEval tests (one fixture vs per-test), and
  whether any assertion counts tools.
- Done when: Build 0 errors; a ModelEval unit test (or the transcript `Tools` list of one live run) shows `DeleteFile`
  present and the hidden tools absent; T1 prompt tokens drop below 26.6k.

### Step 2 - Enum aliases for ReplaceSnippet `action`, and member meanings in the rejection

- Files: `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs` (`NormalizeEnumCase` ~line 727; enum rejection
  ~line 1367), its test class.
- Change: (a) an alias table beside `NormalizeEnumCase`, keyed by tool + parameter: `ReplaceSnippet.action`:
  `replace`, `edit`, `write` -> `apply`. Accept the call and append the existing normalisation note ("... treated as
  'apply'. Use 'apply' in future calls."). (b) When an enum value is rejected and no "Did you mean" applies, list each
  valid member with a short meaning (e.g. "apply: writes the change; validate: dry run, writes nothing"). Source the
  meanings from the enum's `[Description]` if present; otherwise add them.
- Done when: tests show `action: replace` succeeds with the note, and an unknown value lists meanings.
- Open: whether an alias table is wanted in general or only for this parameter (keep it data-driven, start with one
  entry).

### Step 3 - No RenameSymbol advice toward a name that already exists

- Files: `RoslynSentinel.Engines.Basic/EditImpactAnalyzer.cs` (`NextStepFor`, Renamed case ~line 321);
  `RoslynSentinel.Engines.Basic/CompilerErrorLookupHelper.cs` (~line 541, CS0101 text).
- Change: in `NextStepFor`, when the edit's diagnostics include CS0101 or CS0111 naming `NewName` (or the name
  already resolves in the namespace), do not suggest RenameSymbol; say the name is already taken and name the
  declaring file. In the CS0101 helper text add "or delete the superseded file (DeleteFile)".
- Done when: a test reproducing R8 (class renamed inside a file while another file declares the new name) gets no
  RenameSymbol hint and names the other file.

### Step 4 - Member remove: real routes instead of "Delete manually" and `skipPrecheck`

- Files: `RoslynSentinel.Engines.Basic/StructuralRefinementEngine.cs` (~line 245, reflection-risk refusal);
  `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs` (~line 729, "Pass skipPrecheck: true to remove anyway").
- Change: replace "Delete manually after verifying" with an actionable route: the string-literal locations found, plus
  either an explicit override parameter that actually bypasses the reflection check, or `DeleteFile` when the type is
  the file's only declaration. Stop advertising `skipPrecheck` for type removal (R8 T11: with it, still "has 1 usages
  and cannot be safely deleted"), or make it work for types.
- Open: override parameter vs DeleteFile pointer; decide with the owner before the slice.
- Done when: tests cover a type referenced only from a string literal: the error names the literal's location and a
  route that succeeds when followed.

### Step 5 - DeleteUnusedSymbol names the missing parameter

- Files: `RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs` (~line 903).
- Change: when `docCommentId` is supplied without `projectName`, say "projectName is required with docCommentId"
  (and which project declares the symbol, if resolvable) instead of "requires one of: (projectName, docCommentId)...".
- Done when: a test with `docCommentId` alone gets the specific message.

### Step 6 - Harness: ContextExhausted stop reason and split token logging

- Files: `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs` (~line 258, "model made no tool calls, treating as
  finished"; ~line 244 context warning); `RoslynSentinel.Common/AgentLoop/LmStudioAgentClient.cs` (~line 298, usage
  parsing); the stop-reason enum and transcript DTO.
- Change: (a) a turn with no tool call whose token total reaches the context window (or whose finish reason is
  length) ends with a new stop reason `ContextExhausted`, not `ModelFinished`. (b) log and record prompt and
  completion tokens separately per turn, so anomalies like R1's T2/T4 readings can be explained.
- Done when: a unit test with a fake client returning a length-capped, tool-less turn yields `ContextExhausted`;
  transcript turns carry prompt/completion token counts.

## Out of scope

- The prompt (`TenStepUserPromptTemplate`); batch F changed it already and the failures are not prompt-wording issues.
- Rename lifting (`--lift-renames`, second slice step 1 of `proposals/proposal_semantic_edit_impact_diagnostics.md`).
- Other open items carried from the previous session: undo note text in ReplaceSnippet success, disk-backed ChangeId
  test (TODO in ReplaceSnippetCrossFileRejectionTests), moving `proposals/proposal_loop_latch.md` to `closed/`.

## Risks and open decisions

- Source of `replace` in R1 is a hypothesis (priming from other enums); step 2 fixes the symptom either way.
- The T2/T4 token spike as an LM Studio anomaly is a hypothesis; step 6(b) is how to check it.
- Step 1 widens the eval's mode set; confirm it does not change pass criteria that count tools or modes, and that
  batch comparisons note the surface change.
- Step 4 needs an owner decision (override parameter vs DeleteFile pointer).
