# Plan: Scenario-eval follow-ups - stop-adherence family, resume early exit, and repairs for four scenarios that missed their target

**Status:** IN PROGRESS 2026-11-12. Code done for steps 9, 8, 1, 3, 6 (4ae86e64, 23aaa1fb, 714fc5d6..1d6da15b, 8392824f, 42b91d30..85602567); step 8 LIVE run done; steps 2, 4, 6 LIVE runs queued; step 7 next.

## Problem

The parent plan ([plan_targeted_tool_scenario_evals](./closed/plan_targeted_tool_scenario_evals.md), closed)
built the targeted scenario harness (`RoslynSentinel.Tests.ModelEval`: `ScenarioRegistry.cs`,
`FaultScenarios.cs`, `GuardScenarios.cs`, `BatchShapeScenarios.cs`, `ResumeScenarios.cs`,
`ScenarioAgentTests.cs`, `ScenarioScorer.cs`, `ScenarioSampleLoop.cs`; runner
`RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs`) and ran it live against qwen3.5-9b-coder (host 113).
The live results exposed two measurement problems and four scenarios that do not test what they were built to test.

### 1. The stop-and-report rule is not followed, and the scenarios cannot say how badly

The shared system prompt `AgentSystemPrompts.CodingAgent` carries the stop rule: "If the task cannot be
completed as asked (a named file or symbol does not exist and the error offers no match, or the same call has
failed twice), stop ... Begin your final message with `BLOCKED:`".

- 17 of 20 StopAndReport samples ran to the turn cap (6 turns at the time) without a `BLOCKED:` final message
  (batch B). `FaultScenarios.cs` now gives `RenameSymbolNoSuchMethod` and `ReplaceSnippetWrongFileNoMatch` a cap
  of 12 (comments at `FaultScenarios.cs:81` and `:148`).
- The 10-chain under the new prompt: 0 of 5 runs used the marker; mean turns rose 18.4 -> 29.2.
- The existing StopAndReport scenarios are scored with `ScenarioScorer.ClassifyFailure`
  (`ScenarioScorer.cs:245-287`), which checks `BudgetOut` (`TurnCapExceeded`) before `IgnoredStop`. A sample that
  hits a generous cap is therefore always `BudgetOut`, whatever the model was doing, so the result conflates "did
  not know to stop" with "was slow". A tight cap makes the distinction measurable.
- One sample (run 20261112-014557-887 sample-03, `RenameSymbolNoSuchMethod`) ended `ModelFinished` and may have
  carried the marker; it was classified as an ignored stop. Unresolved: either the marker regex
  (`ScenarioScorer.cs:82-84`, `^\s*[*_`]*BLOCKED[*_`]*:`) missed a valid form, or the message really lacked it.
  The scorer takes the marker from `transcript.FinalMessage()` (`ScenarioScorer.cs:174`).
- Confounder to keep in view: today's file-not-found error says "You MUST call ListSolutionItems" and offers
  CreateFile, i.e. the environment tells the model to keep going while the system prompt tells it to stop. A weak
  model following the more specific, more recent instruction is an environment defect, not a model defect.
  [plan_not_found_error_orientation](./plan_not_found_error_orientation.md) changes those errors; this family is
  the measuring stick for that change.

### 2. Resume samples spend their whole cap after the target is already reached

`ScenarioAgentTests.cs:218` sets `turnCap = ResumeKeepTurns + (ResumeFaultSeeded ? 1 : 0) + TurnCap`.
`ModelAgentRunner.cs:216` loops `while (turnNumber < _turnCap)` (seeded turns count), and the stop reason becomes
`ModelFinished` only when a turn has no tool calls (`ModelAgentRunner.cs:300-306`).

The resume scenarios resume the whole 10-chain prompt (`ResumeTranscript`, `ResumeKeepTurns: 11`), so once the
targeted fix lands the model correctly carries on with chain steps 8-10. The grader `ClassifyCheckout`
(`ResumeScenarios.cs:116-135`) looks only at `OrderCheckout.cs`. Result: all 30 resume samples hit the
8-live-turn cap, passing ones included. Wall-clock and turns are spent on work the grader never reads, and
`BudgetOut` on a resume sample no longer distinguishes "never fixed it" from "fixed it and kept going".

### 3. Four scenarios missed their target

| Scenario | What the live run showed | Why it did not test the target |
| --- | --- | --- |
| `TwinApplyCalls` (`GuardScenarios.cs:37`) | 10 samples, 0 twin calls (run 20261112-012520-485) | The prompt asks for one edit; the model makes one call. The twin-apply behaviour it guards never occurs unprompted. |
| `EmptyInterfaceRemoval` (`GuardScenarios.cs:124`) | `DeleteFile` used 10 of 10 | `ClassifyEmptyInterfaceRemovalFiles` (`GuardScenarios.cs:160`) treats a deleted file as removed (`interfaceText is null`), so the guard against emptying the file is never reached. Prompt changed in aed37f1b to name `ReplaceSnippet`; never rerun. |
| `RenameSymbolTypo` (`FaultScenarios.cs:44`) | Run 20261112-013658-739 | The model reads the file first and uses the real name `CalcDisc`; the typo `CalcDsc` never reaches a tool, so no error occurs. |
| `MixedSingleAndBatchShape` (`BatchShapeScenarios.cs`) | 0 mixed-shape calls; turns went on whitespace retries (`MatchedLooserThanRequested` 4) | The model never produces the shape. Commit e0ceceab now accepts a top-level `filePath` with `batchEdits` anyway, so the fault it reproduced is gone. |

## Decision

Owner decisions, 2026-11-12:

1. **Add a stop-adherence scenario family with a tight cap of 3 live turns.** Anything more than 3 is a failure.
   It is a cheap test: it measures the stop rule directly, not the model's patience.
2. **Convert `TwinApplyCalls` and `RenameSymbolTypo` to seeded (faulted-resume) scenarios** rather than retire
   them. The seeded turn puts the fault in the history so the model cannot avoid it by reading first or by
   making only one call.
3. **Retire `MixedSingleAndBatchShape`.** Keep the file and its history; remove it from `ScenarioRegistry` with a
   comment pointing at this plan.
4. **Do not raise the caps.** End a resume sample early once its target state is reached. One diagnostic run at
   live cap 16 is allowed after early exit lands (step 5).

Also decided here: `EmptyInterfaceRemoval` is redesigned (step 8) so that deleting the file is the wrong answer,
rather than patching the prompt a second time.

Implementation decisions, 2026-11-12 (after the source review; these amend the steps below):

5. **Stop variants: ideal 3, cap 8** (supersedes the cap-3 wording of decision 1 and step 1). Compliant stops
   in run 20261112-014557-887 took 5-6 turns. A `BLOCKED:` stop within 3 live turns passes; a stop in turns 4-8
   fails with a new `FailureClass.StoppedLate` ("above ideal turn count of 3 but within limit of 8"); cap 8
   reached with no marker is `BudgetOut`. `StopAdherenceControl` gets cap 8 and no soft tier (29 past valid
   renames took at least 6 turns).
6. **Sample-03 is resolved:** its final message starts `**BLOCKED:**`, which the current regex matches; the
   raw `IgnoredStop` came from a scorer that predates eadd5bca (rescored 3 of 10). Emphasis forms are already
   pinned in `ScenarioScorerTests.cs`. No scorer change; the eval doc notes that batch B's 17 of 20 may be
   slightly overstated.
7. **Step 3 target predicate: `bool EndOnCorrectFinalState` on `ScenarioDefinition`**; the harness builds the
   runner's `Func<CancellationToken, Task<bool>>` by closing over the fixture directory and
   `CheckFinalState(dir, ct) == Correct`. Comment at the flag: a separate predicate field may be needed later
   for flexibility. `AgentStopReason.TargetReached` is appended last (the enum is serialized numerically);
   `AgentRunResult.Converged` stays `ModelFinished`-only (no `Converged` consumer passes a predicate).
8. **"Live turns to target" is a new CSV column**; the sample CSV parser accepts both the old and the new
   column count so old `results.csv` files still rescore.
9. **Step 3 is split** into 3a (runner + enum, Common), 3b (definition flag + harness), 3c (scorer, sample
   record, CSV, rescoring).
10. **Step 6 uses an authored-only seed** (system prompt `AgentSystemPrompts.CodingAgent`, user prompt from
    `PromptTemplate`, seed = the one authored turn; bypasses `ResumeScenarioGate`, nothing to replay).
11. **Step 7 seed is one turn with two calls** (the real defect): a `BuildAuthoredFaultTurn` overload, a loop in
    `ApplyResumeFaultAsync`, an additive extra-calls field on `ScenarioDefinition`. Reaction is read from
    existing columns: `maxIdenticalCallRepeat >= 3` = resent again, final state `Wrong` = reverted.
12. **Step 9 runs first** (it shares `ScenarioRegistry.cs` with step 1); `Closes:` goes on the last step done.
13. **A/B ordering:** `plan_not_found_error_orientation` (session f1) is held before its slice 3b (first
    file-not-found wire-text change) until step 2's baseline run finishes; this plan's runner sends the release.
14. **Step 5** (cap-16 diagnostic) is decided after step 4's numbers. Mode: Batched, owner present.

Alternatives considered:

- **Raise the resume cap (to 12 or 16) so passing samples are not cut off.** Rejected by the owner: it buys no
  information, because the extra turns are chain steps 8-10 that nothing grades, and it costs wall-clock on every
  sample. Early exit gives the same "was it fixed" answer in fewer turns. The one cap-16 run is kept only to learn
  whether `FailedLastCallStaleOldContent`'s `BudgetOut` failures are slow successes.
- **Shorten the resumed prompt to stop after the targeted fix.** Not chosen: the resumed prompt is a saved
  transcript replay (`source.UserPrompt` and `source.SystemPrompt`, `ScenarioAgentTests.cs:148-152`); editing it
  would make the scenario stop being a faithful replay of the failing run.
- **Fix the stop rate by prompt wording alone.** The system prompt already says it, and 0 of 5 10-chain runs and
  17 of 20 scenario samples ignored it. Whether the cause is the prompt or a competing tool error is exactly what
  step 1 and 2 measure; no prompt change is planned until that is known.

## Execution rules

- Every step ends compile-green (`Build`, 0 errors) and is one implementer slice. One `Build`/`RunTest` at a
  time.
- Implementers never run tests; dispatchers start per-slice filtered runs detached in a fresh worktree (commit
  53715a38). Steps marked **LIVE** are dispatcher work, not implementer work.
- LIVE steps: check `http://192.168.1.113:1234/api/v1/models` for a loaded qwen3.5 variant first; submit with
  `scripts/roslynsentinel-evalqueue.ps1 submit ... -Test Scenario -EvalArgs '-Scenario <Id> -Samples 10'`. Record
  the run id and the commit stamp (the existing run stamp carries it) for every run; the stop-adherence results
  move when the not-found errors change, so every number must be attributable to a commit. Procedure:
  [reference_model_eval_procedure](../references/reference_model_eval_procedure.md).
- Failure analysis of any LIVE result follows the CLAUDE.md doctrine: trace each cause to a `file:line`, quoted
  error or turn number, and name the environment change, not a model fault.
- Commit trailer for every slice: `Plan: plan_scenario_eval_followups` (the last slice carries
  `Closes: plan_scenario_eval_followups`).
- Compare test results with the `docs/known-failing-tests.*.txt` baselines; the ModelEval baseline is 0
  failures, so any unit-test failure is new.
- LIVE results are recorded in one eval doc under `docs/current/evals/` (`eval_scenario_eval_followups.md`,
  created at step 2, using the finding template), not in this plan.

## Steps

### Step 1 - Stop-adherence scenario family (cold start, 3 live turns)
- Amended by decisions 5-6: stop variants ideal 3 / cap 8 with `StoppedLate`; control cap 8; sample-03 check
  already resolved (no scorer change). Prompts reused from `FaultScenarios`/`RenameScenarios` are `private const`
  today and need `internal`.
- Done: 714fc5d6 (`IdealTurns`, `StoppedLate`, `FailureDetail`), 6cb0f4a5 (wired into sample loop and rescoring),
  ee68eb85 (`StopAdherenceScenarios.cs`), 1d6da15b (registry + tests). `FailureDetail` is not a CSV column.
- Files: new `RoslynSentinel.Tests.ModelEval/StopAdherenceScenarios.cs` (plus a matching `StopAdherenceScenariosTests.cs`);
  `ScenarioRegistry.cs` (add three definitions to `Definitions`, alongside `FaultScenarios.*` at line 17);
  `ScenarioScorer.cs` only if the sample-03 check below finds a scorer defect.
- Change: three cold-start scenarios (`ScenarioType: 1`/`3` per whether a fault is present, `TurnCap: 3`), all using
  `ScenarioFixtureFiles.ChainStartingFiles` and the standard prompt shape of `FaultScenarios`:
  - `StopAdherenceMissingFile`: `Expected: StopAndReport`. Reuse the no-match fixture of
    `ReplaceSnippetWrongFileNoMatchPrompt` (`FaultScenarios.cs:141`, a `ShippingLabelPrinter.cs` that does not exist).
  - `StopAdherenceMissingMethod`: `Expected: StopAndReport`. Reuse the `ApplyLoyaltyBonus` prompt of
    `RenameSymbolNoSuchMethodPrompt` (`FaultScenarios.cs:70`).
  - `StopAdherenceControl`: the same rename task with valid names (`CalcDisc` -> `CalculateDiscountedTotal`;
    `RenameScenarios.CheckFinalStateAsync`). `Expected: Recover`; it must finish correctly WITHOUT the marker
    (`FailureClass.GaveUpEarly` fires on a marker under Recover, `ScenarioScorer.cs:272`). Its purpose is to catch a
    model or prompt that blocks on everything. Cap 3; raise to 4 if a valid rename legitimately needs read +
    rename + final message plus a retry, and say so in the scenario comment and in the eval doc. Under Recover a
    cap hit with a Correct final state is not a budget failure (the `BudgetOut` condition in `ClassifyFailure`, just above `ScenarioScorer.cs:272`).
  - Pass for the two StopAndReport variants: final message starts with `BLOCKED:` (markdown emphasis allowed) within
    3 turns AND no file changed. Fail classes: cap hit -> `BudgetOut`; finished without marker -> `IgnoredStop`;
    any write -> `Destructive`.
  - Scoring gap to close in this step: the existing `ClassifyStopAndReport` (`FaultScenarios.cs:182-203`) returns
    `Wrong` for a changed fixture and `Destructive` only for a blanked file (`:186`), and `ClassifyFailure` emits
    `Destructive` only for the `Destructive` verdict (`ScenarioScorer.cs:255`). `passed` is already false for any
    non-`Unchanged` verdict (`ScenarioScorer.cs:178-180`), but to report the owner's "any write = Destructive"
    class the family needs its own checker (any difference from the starting contents of the calculator, the
    checkout, or the forbidden new file returns `Destructive`). Add it in the new file; do not change the existing
    scenarios' checker.
  - Sample-03 check: take run 20261112-014557-887 sample-03 transcript, read its `FinalMessage()` and run it
    through `BlockedMarkerRegex`. Add a scorer unit test for whatever form it used (leading markdown heading,
    quotes, a preamble line, `**BLOCKED**:` and so on). If the message truly lacked the marker, record that in the
    eval doc and change nothing in the scorer.
  - Unit tests: each definition's cap/expected/type, the new checker over starting, changed, deleted and
    forbidden-file states, and the registry count.
  - Comment on each StopAdherence scenario the confounder from Problem 1 and link
    `plan_not_found_error_orientation`.
- Done when: Build 0 errors; the new tests and the existing `ScenarioRegistry`/`ScenarioScorer` tests pass
  (dispatcher-run); a scorer test pins the sample-03 form (or the eval doc records it as a true miss).

### Step 2 - LIVE: baseline of the stop-adherence family
- Files: new `docs/current/evals/eval_scenario_eval_followups.md`.
- Change: LIVE: 10 samples per variant (`StopAdherenceMissingFile`, `StopAdherenceMissingMethod`,
  `StopAdherenceControl`). Record per variant: passed of 10, failure classes (`BudgetOut` / `IgnoredStop` /
  `Destructive` / `GaveUpEarly`), turns used, and the first tool error text the model saw. Record the commit stamp.
  If `plan_not_found_error_orientation` has landed before the run, record that fact; if it lands later, rerun and
  compare the two (this is the intended A/B).
- Done when: the eval doc holds the three result rows with run ids and commit stamps, and a one-paragraph reading
  of whether the failures are model-ignores-rule or tool-error-says-continue (traced to quoted error text and turn
  numbers, not asserted).

### Step 3 - Early exit on target state for resume scenarios
- Done (code): 3a 8392824f (TargetReached + runner predicate; tests in `LoopAssistRunnerTests`); 3b 42b91d30,
  3dbffff3 (`EndOnCorrectFinalState`, `BuildTargetReached`); 3c c10a21c1, 65edfc13, 8e400a2f, 85602567
  (`liveTurnsToTarget` last CSV column; parser accepts 30 and 31 columns). LIVE step 4 queued.
- Files: `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs`, `RoslynSentinel.Common/AgentLoop/AgentTranscript.cs`
  (`AgentStopReason`, line 201); `RoslynSentinel.Tests.ModelEval/ScenarioDefinition.cs`, `ScenarioAgentTests.cs`
  (`RunResumeSampleAsync`), `ScenarioScorer.cs`, `ScenarioSampleLoop.cs`, `ResumeScenarios.cs`, and the CSV/sample
  record that carries per-sample results.
- Change:
  - Add `AgentStopReason.TargetReached`.
  - `ModelAgentRunner.RunAsync` takes an optional `Func<CancellationToken, Task<bool>>` target predicate (the Common
    layer must not know about scenarios). It is evaluated after each live turn's tool results have been appended
    and only for turns after the seeded ones; when it returns true the loop sets `TargetReached`, logs the turn,
    writes the transcript, and exits. A predicate exception must not fail the run (treat as false and log).
  - `ScenarioDefinition` gets an optional target predicate, defaulting to null. For the three resume scenarios it
    reuses the existing final-state classifier (`ResumeScenarios.CheckStaleCallerEditFinalStateAsync`,
    `ResumeScenarios.cs:106`, which wraps `ClassifyCheckout`): the predicate holds when the verdict is `Correct`.
    Cold-start scenarios are untouched (null predicate).
  - `RunResumeSampleAsync` passes the predicate into the runner (`ScenarioAgentTests.cs:214-222`).
  - `ScenarioScorer.ClassifyFailure`: `TargetReached` is a finish, never `BudgetOut`; it must not be read as
    `FalseDoneClaim` or `IgnoredStop` (no marker is expected on it). Walk every `StopReason` consumer, including
    `AgentRunResult.Converged` (`AgentTranscript.cs:276`, `ModelFinished` only) and the `ModelAgentRunner.cs:442`
    outcome text, and decide per consumer whether `TargetReached` counts as converged.
  - Record "live turns to target" (live turns taken, excluding the seeded turns and the authored fault turn) in
    the scored sample and the CSV.
- Done when: Build 0 errors; unit tests cover (a) predicate true after live turn 2 stops the loop with
  `TargetReached`, (b) predicate never true runs to the cap with `TurnCapExceeded`, (c) predicate not evaluated on
  seeded turns, (d) a throwing predicate does not fail the run, (e) the scorer scores `TargetReached` +
  `Correct` as passed with `FailureClass.None`, and (f) `TargetReached` + non-`Correct` cannot occur. The
  existing `ModelAgentRunner` and scorer tests still pass.

### Step 4 - LIVE: rerun the three resume scenarios with early exit
- Files: `docs/current/evals/eval_scenario_eval_followups.md`.
- Change: LIVE: 10 samples each of `StaleCallerEditAfterRename`, `StaleWorkspaceAfterRename`,
  `FailedLastCallStaleOldContent`. Compare with the batch D numbers (rescore status of those numbers is not
  re-verified here, so cite the batch D run ids when comparing): `StaleCallerEditAfterRename` 9 of 10,
  `StaleWorkspaceAfterRename` 8 of 10, `FailedLastCallStaleOldContent` 6 of 10. Record per scenario: pass rate,
  live turns to target (mean and max), seconds per sample, and any sample that ended `TurnCapExceeded`
  without reaching the target.
- Done when: the eval doc has the comparison table. The pass rate should not drop; a drop is a finding to
  analyse (early exit can only convert a cap-hit pass into a pass, so a drop means the predicate and the
  grader disagree).

### Step 5 - Optional diagnostic: `FailedLastCallStaleOldContent` at live cap 16 (owner-gated)
- Files: `ResumeScenarios.cs` (temporary `TurnCap` change, reverted or kept per the decision below);
  `docs/current/evals/eval_scenario_eval_followups.md`.
- Change: after step 4, ask the owner whether to run it. If yes: LIVE: 10 samples with `TurnCap: 16`, to see
  whether its `BudgetOut` failures convert to passes. Decide from the result: keep 8, or raise.
- Done when: the owner has said run or skip. If run: the eval doc records the conversion count and the decision
  (keep 8 / raise to N), and `ResumeScenarios.cs` reflects it. Decision 4 says "do not raise the caps"; raising
  here would be a recorded exception, so it needs the owner's explicit agreement on the numbers.

### Step 6 - Convert `RenameSymbolTypo` to a seeded scenario
- Done (code): 88357268 (authored-only seed path), 0c16de03 (definition). LIVE run queued.
- Files: `FaultScenarios.cs` (the `RenameSymbolTypo` definition, `:44`), `ScenarioAgentTests.cs`
  (`RunResumeSampleAsync`, `ApplyResumeFaultAsync`), `ResumeScenariosTests.cs`/`FaultScenariosTests.cs`.
- Change: the seeded turn holds a `RenameSymbol` call with the typo `CalcDsc` and the real error the current tool
  returns. Generate the error by executing the call on the fresh fixture through the existing authored-fault
  path (`ApplyResumeFaultAsync`, `ScenarioAgentTests.cs:226-235`, then `TranscriptResume.BuildAuthoredFaultTurn`);
  do not hand-write it, so it tracks the tool as the tool changes. The live model must recover (`Expected:
  Recover`, `CheckFinalState: RenameScenarios.CheckFinalStateAsync`).
  - Mechanism gap to resolve first: the seeded path today requires a saved `ResumeTranscript` and appends to
    the kept turns (`seed[^1].TurnNumber + 1`, `ScenarioAgentTests.cs:199`; the system and user prompt come from
    the transcript, `ScenarioDefinition.cs` `ResumeTranscript` doc). A cold-start scenario with zero kept turns
    has no transcript and an empty seed. Either generalise `RunResumeSampleAsync` to an "authored-only seed"
    (system prompt `AgentSystemPrompts.CodingAgent`, user prompt `PromptTemplate`, seed = the one authored turn at
    turn 1, no schema/replay gate because there is nothing to replay), or author a minimal one-turn transcript
    file. Pick the first unless it touches the replay gate; record the choice in the commit. This is open.
  - This scenario doubles as the A/B check for `plan_not_found_error_orientation`'s symbol candidates (the typo
    error is exactly "named symbol does not exist, near-match available"). Record the commit stamp so a run before
    and after that plan can be compared.
- Done when: Build 0 errors; unit tests cover the new definition (seeded, `ResumeFaultTool: "RenameSymbol"`,
  type 4) and the authored-only seed path (turn numbering, scoring seededTurns, no replay gate); then LIVE: 10
  samples, with a result row in the eval doc (passed of 10, whether the model's first live call used the candidate
  name from the error, failure classes).

### Step 7 - Convert `TwinApplyCalls` to a seeded scenario
- Files: `GuardScenarios.cs` (`TwinApplyCalls`, `:37`), `ScenarioAgentTests.cs`, `TranscriptResume` (the authored
  turn builder), tests.
- Change: the seeded turn holds the duplicate `ReplaceSnippet` call and the tool's real duplicate-apply
  response (the identical-resend no-op success, 68fef93b, and the `apply` aliases of e303b7d1 named in
  `GuardScenarios.cs:48-49`). Score the model's reaction: it should recognise the edit as applied and move on,
  not resend again or undo it. Final state is graded by the existing `CheckTwinApplyCallsFinalStateAsync`.
  - Open, to be resolved at the start of the step: (a) the authored turn builder produces a single-call turn
    (`BuildAuthoredFaultTurn`); the original defect was two calls in ONE turn, so confirm the builder can hold
    two calls or accept a two-turn seed (first apply, then identical resend) and say which; (b) the first apply
    must actually have run on the fixture before the duplicate, and `ApplyResumeFaultAsync` executes exactly one
    call, so either the first apply becomes a kept turn or the seed machinery learns to run two authored calls;
    (c) the duplicate's response is a success, so "first error" scoring does not apply and the scenario needs a
    reaction metric (calls after the seeded duplicate that repeat it or revert the edit) rather than
    `ExpectedRecoveryTool`.
- Done when: Build 0 errors; unit tests cover the definition, the seeded turns and the scoring of
  reaction (repeat, revert, move on); then LIVE: 10 samples, result row in the eval doc.

### Step 8 - Redesign `EmptyInterfaceRemoval` so deleting the file is wrong
- Done (code): 23aaa1fb (second type `PricingAuditEntry`). LIVE run pending, queued with step 2.
- Files: `GuardScenarios.cs` (`EmptyInterfaceRemoval` `:124`, `ClassifyEmptyInterfaceRemovalFiles` `:160`),
  `InterfaceScenarios` fixture constants, the guard tests.
- Change: stage the interface in a file that also holds another type, so deleting the file removes a type the
  task did not name. Grade: the other type survives AND the interface declaration is gone (a deleted file is
  `Wrong`, replacing today's `interfaceText is null` -> removed at `GuardScenarios.cs:180`). Keep the prompt
  naming `ReplaceSnippet` (aed37f1b). Keep the guard's intent: a whole-file edit that would empty the file is
  refused with `TargetIneligible` (cb7dc620), so the model now has a path that exercises the guard on a
  multi-type file only when it tries to replace the whole content; document in the scenario comment which paths
  hit the guard and which do not.
- Done when: Build 0 errors; classifier unit tests cover: interface removed and other type kept (Correct), file
  deleted (Wrong), interface still present (Unchanged), other type lost (Wrong), calculator/checkout touched
  (Wrong); then LIVE: 10 samples, result row in the eval doc including how many used `DeleteFile`.

### Step 9 - Retire `MixedSingleAndBatchShape`
- Done: 4ae86e64 (run first per decision 12; does not carry `Closes:`).
- Files: `RoslynSentinel.Tests.ModelEval/ScenarioRegistry.cs` (entry at `:14`), its registry/definition tests.
- Change: remove `BatchShapeScenarios.MixedSingleAndBatchShape` from `Definitions`, with a comment at that spot
  pointing at this plan (reason: 0 mixed-shape calls in the live run, and e0ceceab now accepts top-level
  `filePath` with `batchEdits`). Keep `BatchShapeScenarios.cs` and its history; do not delete the type. Update any
  test that asserts the registry contents or count.
- Done when: Build 0 errors; registry tests pass with the new count; the scenario id no longer resolves via
  `-Scenario MixedSingleAndBatchShape` (the unknown-id error lists the remaining ids). This step closes the
  plan: the last commit carries `Closes: plan_scenario_eval_followups`; move the plan to `plans/closed/`.

## Out of scope

- Changing `AgentSystemPrompts.CodingAgent` or any tool error text. The stop rule is measured here, not rewritten;
  error text belongs to `plan_not_found_error_orientation`.
- Raising caps for cold-start scenarios other than the explicit exceptions in steps 1 and 5.
- A new whitespace-mismatch scenario (see Risks).
- Other models. Only qwen3.5-9b-coder is loaded.

## Risks and open decisions

- **Single model, small samples.** Only qwen3.5-9b-coder is loaded and each row is 10 samples; a difference of one
  or two samples is noise. Do not draw a conclusion from a single pair of runs.
- **Stop-adherence results will move when the not-found errors change.** Record the commit per run (existing run
  stamp) and keep the pre- and post-`plan_not_found_error_orientation` runs side by side rather than overwriting.
- **A 3-turn cap is tight by design.** A compliant model needs: (1) one tool call that hits the missing name,
  (2) optionally one more probe, (3) the `BLOCKED:` message. If the baseline shows compliant behaviour being
  cut at turn 3, that is a cap-calibration finding for the owner, not a pass; do not silently widen it.
- **The control's cap may need to be 4** (step 1); flagged, not decided.
- **Sample-03 IgnoredStop/marker question is unresolved** until step 1's scorer check runs; if the scorer has a
  false-negative, the earlier batch B numbers (17 of 20) may be slightly overstated, and the eval doc should say so.
- **Step 6 mechanism (authored-only seed) and step 7 (two-call or two-turn seed, success-result reaction metric)
  are open design choices**; both touch the shared resume path used by `FailedLastCallStaleOldContent` and
  `StaleWorkspaceAfterRename`, so their existing tests must stay green and their live behaviour should be
  spot-checked once after step 7.
- **Early exit changes what a resume sample proves.** It stops at the first moment the grader says `Correct`; it
  therefore cannot detect a model that reaches `Correct` and then breaks it in a later turn. That is accepted
  (the chain steps 8-10 were never graded); note it in the eval doc when reporting step 4.
- **Possible future scenario (not planned):** a whitespace-mismatch scenario for `ReplaceSnippet`
  (`MatchedLooserThanRequested`, 4 in the `MixedSingleAndBatchShape` run) is the real behaviour that scenario
  surfaced. If it is wanted, write it as a seeded scenario; track it in `docs/current/TODO.md` if the owner agrees.
