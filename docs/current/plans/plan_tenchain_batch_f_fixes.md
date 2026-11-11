# Plan: fix the environment defects behind 10-chain batch F's two failures

**Status:** APPROVED 2026-11-11. Six fixes, batched in order A=4, B=6, C=2+5, D=1, E=3 (E after the peer's edit-impact
work commits). Line numbers were re-checked against HEAD 25482ef4 on 2026-11-11 (review notes per step); still
re-locate with `Search` before editing.

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

- 2026-11-11 (owner): Batched mode, order A = step 4, B = step 6 (6a then 6b), C = steps 2 + 5, D = step 1, E = step 3.
  Owner present briefly, then away: later decisions are deferred to the tracker.
- 2026-11-11 (owner): step 4 - warn, do not block. No override parameter. A string-literal match becomes a
  non-blocking warning on the success result listing every literal location (file:line); the delete proceeds
  (undoable). References inside the deleted declaration's own span no longer count as usages. Stop advertising
  `skipPrecheck` for type removal. Reason: the literal check is a heuristic, and the compile gate cannot catch real
  reflection use anyway, so an override only adds a footgun parameter.
- 2026-11-11 (owner): step 3 - `CompilerErrorLookupHelper.DescribeAsync` gets an optional `isToolActive` predicate;
  the CS0101 hint names DeleteFile only when it is callable.
- 2026-11-11 (owner): step 1 - modes and hidden tools move to one shared static (both `hideTools` arms, `:128` and
  `:132`) and a non-live test asserts the offered tool list (pattern: `LoopAssistRunnerTests.TranscriptHeader_ListsTheToolsOfferedToTheModel...`).
- 2026-11-11 (owner): step 2 - data-driven enum-value alias table (tool -> parameter -> alias -> member) with a guard
  test (alias is not a member; target is a schema enum member), seeded with `ReplaceSnippet.action`: replace, edit,
  write -> apply. Enum members carry no `[Description]`; the rejection appends the parameter's schema `description`
  (which already explains apply/validate) instead.
- 2026-11-11 (owner): step 5 - also cover the mirror cases (projectName without docCommentId, line without column).
  The declaring project can be derived from `filePath`.
- 2026-11-11 (owner): step 6 - the client speaks the Responses API and has no `finish_reason` (a length-capped turn
  already throws), so `ContextExhausted` is token-based only: a tool-less turn whose `TotalTokens >= contextLength`.
  Prompt/completion counts go on `AgentChatMessage` beside `TotalTokens` (verify the usage field names, probably
  `input_tokens`/`output_tokens`, before naming). Split into 6a (stop reason) and 6b (token split).

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
- Review 2026-11-11: `ActiveModes` `:70-73`; `hideTools` at TWO sites, `:128` (EditSnippets arm) and `:132`.
  `TranscriptResumeAgentTests.cs:40` has its own modes copy (point it at the shared static). No assertion counts
  tools. WholeFileWrite adds WriteFile, DeleteFile, ApplyDiff, ApplyUnifiedDiff; hiding three leaves DeleteFile.
  `MemberRefactoringEngine.cs:2633` names ApplyDiff/DeleteUnusedSymbol ungated - leave a TODO, do not fix here.

### Step 2 - Enum aliases for ReplaceSnippet `action`, and member meanings in the rejection

- Files: `RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs` (`NormalizeEnumCase` ~line 727; enum rejection
  ~line 1367), its test class.
- Change: (a) an alias table beside `NormalizeEnumCase`, keyed by tool + parameter: `ReplaceSnippet.action`:
  `replace`, `edit`, `write` -> `apply`. Accept the call and append the existing normalisation note ("... treated as
  'apply'. Use 'apply' in future calls."). (b) When an enum value is rejected and no "Did you mean" applies, list each
  valid member with a short meaning (e.g. "apply: writes the change; validate: dry run, writes nothing"). Source the
  meanings from the enum's `[Description]` if present; otherwise add them.
- Done when: tests show `action: replace` succeeds with the note, and an unknown value lists meanings.
- Review 2026-11-11: `NormalizeEnumCase` `:707` (called from `ServiceRegistrationExtensionsBasic.cs:1227`, before
  `ApplyParameterAliases` `:1232`); rejection block `:1342-1368`, schema property in hand at `:1345`. Enum is
  `ProposedChangeAction { apply, validate }` (`Common/ToolEnums.cs:116`). Existing aliases are parameter-name only
  (`ParameterAliases` `:577`). Tests: `Tests.Server/ToolArgumentCaseNormalizationTests.cs`,
  `Tests.Server/ToolParameterAliasTests.cs`. Settled (see Decision).

### Step 3 - No RenameSymbol advice toward a name that already exists

- Files: `RoslynSentinel.Engines.Basic/EditImpactAnalyzer.cs` (`NextStepFor`, Renamed case ~line 321);
  `RoslynSentinel.Engines.Basic/CompilerErrorLookupHelper.cs` (~line 541, CS0101 text).
- Already landed (3d4120b0, another session): the CS0101 hint now names the file that already declares the type and
  says to remove the declaration from the edit; CS0111 inside a CS0101-duplicated type points at the duplicate. That
  commit left a TODO in `NextStepFor` ("rename headline buries a co-occurring CS0101") - this step resolves it; re-read
  both files and extend the new wording rather than replacing it.
- Change: in `NextStepFor`, when the edit's diagnostics include CS0101 or CS0111 naming `NewName` (or the name
  already resolves in the namespace), do not suggest RenameSymbol; say the name is already taken and name the
  declaring file. In the CS0101 helper text add "or delete the superseded file (DeleteFile)".
- Done when: a test reproducing R8 (class renamed inside a file while another file declares the new name) gets no
  RenameSymbol hint and names the other file.
- Review 2026-11-11: blocked until peer session work on `EditImpactAnalyzer.cs`/`EditImpactResult.cs`/new
  `EditImpactLocalDiff.cs` commits (anchors there are moving). `NextStepFor` receives no error list and CS0101/CS0111
  are not in `MissingNameIds` (`:35`), so the error set must be threaded in. CS0101 text is in
  `DescribeDuplicateDeclarationAsync` (`CompilerErrorLookupHelper.cs:486-570`, text ~`:556-563`). Tests:
  `Tests/CompilerErrorLookupHelperTests.cs` (`:322`, `:339`), `Tests.Server/EditImpactToolNamesTests.cs`.

### Step 4 - Member remove: real routes instead of "Delete manually" and `skipPrecheck`

- Files: `RoslynSentinel.Engines.Basic/StructuralRefinementEngine.cs` (~line 245, reflection-risk refusal);
  `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs` (~line 729, "Pass skipPrecheck: true to remove anyway").
- Already landed (3d4120b0): ReplaceSnippet's no-type-left refusal now includes `memberName` in its Member remove
  hint (R8 T8's "memberName is required"). Still open here: the reflection-risk refusal and the `skipPrecheck` claim.
  The same commit left TODOs above `DescribeNoTypeLeft` (namespace-only strip unflagged; no cross-file duplicate-type
  precheck) - out of scope for this step.
- Change: replace "Delete manually after verifying" with an actionable route: the string-literal locations found, plus
  either an explicit override parameter that actually bypasses the reflection check, or `DeleteFile` when the type is
  the file's only declaration. Stop advertising `skipPrecheck` for type removal (R8 T11: with it, still "has 1 usages
  and cannot be safely deleted"), or make it work for types.
- Settled 2026-11-11: warn, do not block (see Decision); this supersedes the "Change" paragraph above.
- Done when: tests cover (a) a type referenced only from a string literal: Member remove succeeds and the result
  carries a warning listing each literal's file:line; (b) a type whose only references are inside its own declaration
  (constructor, self-typed member) is removed; (c) the type-removal refusal text no longer mentions `skipPrecheck`.
- Review 2026-11-11: `CheckReflectionRiskAsync` `StructuralRefinementEngine.cs:234-250` throws (never returns), so the
  `if (reflectionRisk != null)` checks at `:133`/`:284` are dead. `SafeDeleteSymbolAsync` `:256`, usage refusal
  `:281`, reached from `MemberRefactoringEngine.cs:2623`. `skipPrecheck` text `RefactoringStructuralImpl.cs:730`
  (`MemberRemoveAsync` `:703`); it bypasses only the Impl-level caller check. Tool description overclaims
  (`RefactoringStructuralTools.cs:135`). Existing pin: `MemberRemoveTests.cs:229-254`. `SafeDeleteSymbolAsync` is
  also DeleteUnusedSymbol's engine: the warn-not-block change applies there too.

### Step 5 - DeleteUnusedSymbol names the missing parameter

- Files: `RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs` (~line 903).
- Change: when `docCommentId` is supplied without `projectName`, say "projectName is required with docCommentId"
  (and which project declares the symbol, if resolvable) instead of "requires one of: (projectName, docCommentId)...".
- Done when: a test with `docCommentId` alone gets the specific message.
- Review 2026-11-11: message `:905`; pair check `:874`, symbolName `:890`, line/column `:896`; params default `""`
  (`:839`). Direct-Impl test pattern: `Tests.Tools.Basic/StaleReadEngineToolsTests.cs:399`.

### Step 6 - Harness: ContextExhausted stop reason and split token logging

- Files: `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs` (~line 258, "model made no tool calls, treating as
  finished"; ~line 244 context warning); `RoslynSentinel.Common/AgentLoop/LmStudioAgentClient.cs` (~line 298, usage
  parsing); the stop-reason enum and transcript DTO.
- Change: (a) a turn with no tool call whose token total reaches the context window (or whose finish reason is
  length) ends with a new stop reason `ContextExhausted`, not `ModelFinished`. (b) log and record prompt and
  completion tokens separately per turn, so anomalies like R1's T2/T4 readings can be explained.
- Done when: a unit test with a fake client returning a length-capped, tool-less turn yields `ContextExhausted`;
  transcript turns carry prompt/completion token counts.
- Review 2026-11-11 (supersedes the finish-reason half, see Decision): `LmStudioAgentClient.cs` is at
  `RoslynSentinel.Common/` (not AgentLoop); usage parsing `:300`, `UsageObject` `:679`, `ModelMessage.TotalTokens`
  `:747`; length-capped turns already throw (`status=incomplete`, ~`:237-245`). `ModelAgentRunner.cs`: warning
  `:247-253`, "treating as finished" `:258-261`, `contextLength` `:188`. `AgentStopReason`
  (`AgentLoop/AgentTranscript.cs:154`); `Converged` = ModelFinished (`:223`); `AgentTranscriptTurn` `:74` has no
  token fields. New transcript fields must be nullable/ignore-when-null (`TranscriptResume.Load`). Stop reason flows
  as text into `SubAgentResult`/`SubAgentEvalResult`. Test: a scripted `HttpMessageHandler` that routes by URL (models
  endpoint returns `loaded_context_length`); existing handlers (`LoopLatchRunnerTests.cs:~194`) answer every request
  with SSE. 6a: stop reason + test; 6b: token split.

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
- Step 4 decided 2026-11-11 (warn, do not block). Risk: a real reflection use of a deleted type now fails only at
  runtime; the warning lists the literal locations so the agent can check.
- Step 3 overlaps steps 2-3 of the second slice of `proposals/proposal_semantic_edit_impact_diagnostics.md` (edited-file
  attribution, local diffing, verdict list on the wire), which were in progress in another session on 2026-11-11 in
  `EditImpactAnalyzer.cs`, `EditImpactGate` and the ReplaceSnippet paths of `WorkspaceFileEditImpl.cs`. Before step 3,
  check `Git log` for those files; if that work is still uncommitted, coordinate (peer-coordination skill) first.
