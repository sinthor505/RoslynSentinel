# Plan: short targeted model-eval scenarios (cold-start and resume, clean and faulted)

**Status:** IN PROGRESS 2026-11-11. Steps 1, 3 and 4 done (3f87a527, 1506ed14, 56ab9526, bf403f75, 030c70d6, f4a69c16, 2e15938d); step 2 waits on another session's uncommitted edits in `ModelAgentRunner.cs`; steps 5 to 11 not started.

## Problem

`ReplaceSnippet` tuning is measured with the 10-chain eval (`Model_AppliesTenChainedRefactors`,
`RoslynSentinel.Tests.ModelEval/OrderPricingRefactorChainAgentTests.cs`). It is slow and indirect:

- A passing run is 18-29 turns at about 17 s per turn (362-546 s); a failing run reached 1040 s. Model
  time is 95-98% of the turn loop; in-process setup is 9-11 s. Measured on the ten batch-F runs under
  `ModelTestingResults/113/Model_AppliesTenChainedRefactors/`.
- Only one run fits on host 113 at a time, so a batch of 10 is about 70 minutes.
- The result is pass/fail plus turn count for ten steps. First-call success and recovery for one tool
  are derived afterwards by log analysis; nothing records them.
- No existing eval targets `ReplaceSnippet` directly.

The failure modes 10-chain surfaced are listed in
[eval_tenchain_reasoning_replay_2026_11_10.md](../evals/eval_tenchain_reasoning_replay_2026_11_10.md) and
[eval_tenchain_frozen_batch_2026_11_10.md](../evals/eval_tenchain_frozen_batch_2026_11_10.md). Most can be
reproduced with one staged file and a one or two step prompt; the rest need the state left by earlier
chain steps.

## Decision

Add a scenario harness to `RoslynSentinel.Tests.ModelEval` that runs a few turns against one targeted
condition and scores each tool call. One scenario shape covers four types:

| Type | Start | Condition | Answers |
| --- | --- | --- | --- |
| 1 | Cold start | Clean | Does the model pick the right tool, pass correct parameters, read the error, and does the error name the right recovery step? |
| 2 | Resume | Clean | What does the model do at chain step N, without paying for steps 1 to N-1? |
| 3 | Cold start | Faulted | Given wrong information (typo, stale or wrong symbol name, wrong file name), do the model and the tools recover? |
| 4 | Resume | Faulted | Given a failed call or stale state mid-run, do they recover? |

A scenario is: staged files, an optional seeded conversation (resume), an optional fault, a prompt, a
turn cap, a declared expected outcome, an optional expected recovery tool and a final-state check.

Fixed design points:

- **Same model-facing environment as 10-chain.** Scenarios use `ChainEvalToolSurface`
  (`RoslynSentinel.Tests.ModelEval/ChainEvalToolSurface.cs:19`) and `AgentSystemPrompts.CodingAgent`
  (`RoslynSentinel.Common/AgentLoop/AgentSystemPrompts.cs:21`), so results stay comparable.
- **Score per call.** First-call success, first error code, calls to recover, identical resends, whether
  the recovery used the expected tool, final state, failure class. Written to a CSV per run.
- **Faulted scenarios have their own pass rule.** The first error is expected, so the 10-chain error
  budget (`AgentToolErrorAssertions.AssertWithinBudget`, `AgentToolErrorAssertions.cs:55`) is not applied.
  Each declares its expected outcome: `Recover` (a near match exists) or `StopAndReport` (nothing close
  exists; a guess is a failure).
- **Faults are real.** A failed call in a seeded history is produced by making that call against the
  fixture, never by hand-writing a result.
- **Resume scenarios are gated against drift.** A structural schema change in any tool called in the
  seeded history, or a replay failure, marks the sample Inconclusive (`Assume.That`). Description-only
  changes and changed result text run and are flagged in the results for review.
- **Provenance.** Fixed facts are recorded per scenario (source run and turn, reproduces-at commit and
  rate, fixed-by commit). The capture commit of a resume transcript lives in the transcript. Every
  ModelEval run stamps the commit and dirty flag of the working tree under test. No hand-maintained
  "last validated" field.
- **10-chain stays** as the confirmation run after a change looks good in scenarios.

### Owner decisions 2026-11-11

- **Reproduces-at:** measured only where the fix is behind a flag (`ROSLYNSENTINEL_LIFT_RENAMES`) or the
  mode is still open. Other scenarios are regression guards and record "not measured".
- **Resumed history:** the model sees the saved result text; differences from the current server are
  counted in `resultDrift`.
- **Stop marker in the system prompt, for all evals.** `AgentSystemPrompts.CodingAgent` gains a stop
  instruction: when the task cannot be completed, stop and begin the final message with `BLOCKED:`.
  Owner's reasoning: real plans carry error-handling and blocker instructions; their absence from the
  eval prompts likely reinforces "keep trying", and a system-prompt instruction shows whether a model
  complies with a top-level stop rule. Consequence: 10-chain results after this change are not
  comparable with batches A to H4 until a new baseline batch runs (step 11).
- **Grading `StopAndReport`:** marker present in the final message, final state not changed or damaged,
  run ended inside the turn cap. A `Recover` scenario that ends with the marker is graded "gave up early".
- **Recovery telemetry:** counts that are not pass/fail on their own (calls and output after the first
  error, repeats, retry phrases) are recorded by the scorer, and the scorer can be run over any saved
  transcript so no manual read is needed each time (step 3).
- **Run:** Batched mode, four batches (A: steps 1-4, B: steps 5-7, C: steps 8-9, D: steps 10-11), owner
  present.

Implementation choices made in review (not owner decisions):

- The run stamp is a process-wide value set once by the ModelEval assembly setup; the runner copies it
  into each new transcript. Runs outside ModelEval (PlanStepRunner, sub-agents) leave it null.
- Scenarios are selected by an environment variable and one `[Test]`, not one test method per scenario,
  because the script filters on the method name (`scripts/roslynsentinel-modeleval.ps1:487-488`).

## Execution rules

- Dog-fooding rules in CLAUDE.md apply: C# through the MCP tools, `Build`/`RunTest`, one at a time.
- Only one live-model run on host 113 at a time, and other sessions run eval batches there: check the
  host before each live run. Live scenario tests are `[Explicit]`, like the existing agent tests.
- Scoring, schema comparison and replay gating are pure logic: each gets non-live unit tests in
  `RoslynSentinel.Tests.ModelEval` (the pattern `TranscriptResumeTests.cs` uses), so the test baseline of
  0 failures guards them.
- Reuse before adding types (`dto-conventions` skill). Extend `AgentTranscript` and reuse
  `AgentToolCallRecord`. A new record needs its `NEW-DTO:` block after the section 1 search; evaluate
  `ResumeSampleRow`, `AgentToolErrorAssertions.ToolErrorSummary` and `AgentRunResult` first. New names
  take a plain noun or a table suffix; no `*Row`, `*Outcome`, `*Summary`, `*Info` or `*Report`.
- Do not edit `OrderPricingRefactorChainAgentTests.cs`; scenarios copy what they need from it.
- Changing an existing member that tests depend on: add the new member beside the old one.
- Each slice is compile-green; commit per slice with a `Plan: plan_targeted_tool_scenario_evals` trailer.

## Steps

### Step 1 - Stamp each ModelEval run with the working-tree commit

Today nothing in a run folder records the commit (no match for sha, commit or dirty in the ModelEval
C#; none in `agent.log` or `test-result.txt`).

- **1a - transcript properties and runner hook**
  - Files: `RoslynSentinel.Common/AgentLoop/AgentTranscript.cs`, `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs`, one test file.
  - Change: add a commit hash and a dirty-tree flag to `AgentTranscript`, omitted from JSON when null
    (`LoopAssistTrackerTests.cs:475-508` pins the JSON shape). The runner creates the transcript at
    `ModelAgentRunner.cs:155` and writes it at `:687-692`; copy the process-wide stamp in at creation.
  - Done when: Build 0 errors; a unit test shows a stamped transcript carries both values, an unstamped
    one omits them, and a transcript without them deserializes.
  - Done: 3f87a527840ebc47f6b51176aea669e234e6fb6f (`AgentTranscript.CommitHash`, `WorkingTreeDirty`,
    `ModelAgentRunner.ProcessStamp`).
- **1b - ModelEval sets the stamp**
  - Files: `RoslynSentinel.Common/RoslynSentinel.Common.csproj` (`InternalsVisibleTo`, `:26-35`), the
    ModelEval assembly setup, one test file.
  - Change: read HEAD and status with `GitReaders.Default.GetHeadSha` and `GetStatus` (internal,
    `FallbackGitReader.cs:221`) from the repo root (the walk `ModelTestingResultsArchiver.FindRepoRoot`
    does), once, in `ModelEvalAssemblySetUp`.
  - Done when: Build 0 errors; a unit test shows the helper returns the repo's HEAD.
  - Done: 1506ed149e066802de28a085c7a3728059c91147. The dirty flag counts tracked changes plus untracked
    `.cs` and `.csproj` files. The stamp is null when the test binaries run from a folder with no
    `RoslynSentinel.slnx` above them.

### Step 2 - Record tool descriptions and parameter schemas in the transcript

- Files: `RoslynSentinel.Common/AgentLoop/AgentTranscript.cs`, `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs`, one test file.
- Change: `AgentTranscript.Tools` is `List<string>` (`AgentTranscript.cs:29`), filled with names at
  `ModelAgentRunner.cs:156`. Beside it record, per tool, the description (set at `:133`) and
  `AgentToolDefinition.ParametersSchema` (a `JsonElement`, `LmStudioAgentClient.cs:882`, set at `:134`).
  Keep `Tools` as it is. Dictionaries need no new type.
- Also record the run's stop reason on the transcript: a saved transcript has none today, so `BudgetOut`
  cannot be scored from a file (found in step 3d).
- Done when: Build 0 errors; a unit test with a fake client shows all three recorded; a transcript
  without them deserializes (`Turns` and `Tools` are get-only: read back with
  `PreferredObjectCreationHandling = Populate`, as step 3d does).
- Waiting 2026-11-11: `ModelAgentRunner.cs` carries another session's uncommitted edits. Needed before
  step 8d only.

### Step 3 - Per-call scorer, recovery telemetry and results CSV

- **3a - error classification helper**
  - Files: `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs` or a new helper beside it; one test file.
  - Change: a reusable read of a failed call's error code. `ResultJson` is the raw MCP envelope with the
    code nested: `{"isError":true,"errorData":{"errorCode":"ValidationFailed","message":"..."}}`.
    `BuildFailureSignature` reads it with `TryReadStringProperty(resultJson, "errorCode")`
    (`ModelAgentRunner.cs:517`); confirm that read finds the nested value. A guardrail redirect and a
    compile-gate rejection both carry `ValidationFailed`, so the redirect regex
    (`AgentToolErrorAssertions.cs:32`) refines the class within the code; it is not a fallback.
  - Done when: unit tests classify a redirect, a compile-gate rejection, a `NotFound` and a result with
    no code, using result JSON copied from archived transcripts.
  - Done: 56ab9526c3185d077cca912f794aa582c97d45fb (`ErrorCodeOf`, `ClassifyError` in
    `AgentToolErrorAssertions.cs`; a copy of the runner's read, with a `// TODO` to merge the two once
    `ModelAgentRunner.cs` is free).
- **3b - scorer**
  - Files: new scorer in `RoslynSentinel.Tests.ModelEval`, one test file.
  - Change: a pure function over an `AgentTranscript`, a tool under test, the expected outcome, the
    optional expected recovery tool and a final-state verdict (`Correct`, `Unchanged`, `Wrong`,
    `Destructive`; supplied by the caller, absent when scoring a bare transcript). It yields:
    - graded fields: `firstCallSucceeded`, `firstErrorCode`, `firstErrorClass`, `callsToRecover`,
      `recoveryUsedExpectedTool` (the first call after the first error is the expected tool),
      `markerPresent` (final message begins with `BLOCKED:`), `turnCount`, `stopReason`;
    - `failureClass`: `None`; `IdenticalResend` (a call repeats the tool and arguments of an earlier
      failed call); `GaveUpEarly` (`Recover` expected, marker present); `IgnoredStop` (`StopAndReport`
      expected, no marker); `Destructive` (verdict `Destructive`); `FalseDoneClaim` (no marker, verdict
      not `Correct`, run ended by itself); `BudgetOut` (ended by the turn cap);
    - telemetry, not graded: calls, turns and model output characters after the first error; the
      highest repeat count of one identical call; identical reasoning blocks if the transcript records
      reasoning; count of retry phrases ("let me try again", "a different approach" and similar, from
      one list in the scorer).
  - Done when: unit tests score hand-built transcripts for every failure class and each telemetry count.
  - Done: bf403f754b1b8c9d42dc762442f8fddad826532f (`ScenarioScorer`, `ScoreResult`, which also has a
    `Passed` field). Transcripts do record reasoning, so identical reasoning blocks are counted.
- **3c - results CSV**
  - Files: new type and writer in `RoslynSentinel.Tests.ModelEval`, one test file.
  - Change: one line per sample: the 3b fields plus scenario id, type, fault kind, commit, dirty flag,
    `ROSLYNSENTINEL_LIFT_RENAMES` state, `schemaDrift`, `resultDrift`, `inconclusiveReason` and the
    transcript path. `ResumeSampleRow` (`TranscriptResume.cs`, header constant at `:95`) has
    rename-specific columns and stays as it is.
  - Done when: a unit test round-trips a line, including one Inconclusive sample.
  - Done: 030c70d6a0950beed784c8b1903c6a64a437bebe (`ScenarioSample`, `ScenarioSampleCsv`).
- **3d - score a saved transcript**
  - Files: a no-model `[Explicit]` test in `RoslynSentinel.Tests.ModelEval`, `scripts/roslynsentinel-modeleval.ps1`.
  - Change: score a transcript file or run folder named by an environment variable and append to a CSV;
    a script switch sets the variable. Works on archived 10-chain runs (final-state verdict absent).
  - Done when: run over one archived run folder under
    `ModelTestingResults/113/Model_AppliesTenChainedRefactors/` it writes one line with the
    `ReplaceSnippet` counts.
  - Done: f4a69c16b427574bb36eef202ab5c29a1d98a0c0 (`-Test ScoreTranscript` with `-ScoreTranscript`,
    `-ScoreCsv`, `-ScoreTool`). On run `20261110-194407-573`: first `ReplaceSnippet` call failed with
    `ValidationFailed` (guardrail redirect), 6 calls to recover.

### Step 4 - Stop marker in the system prompt

- Files: `RoslynSentinel.Common/AgentLoop/AgentSystemPrompts.cs`; any test that pins the prompt text.
- Change: add to `AgentSystemPrompts.CodingAgent` (`:21`), verbatim:
  "When a tool returns an error, follow the recovery step the error names. If the task cannot be
  completed as asked (a named file or symbol does not exist and the error offers no match, or the same
  call has failed twice), stop: do not guess and do not work around it with a different edit. Begin
  your final message with `BLOCKED:` followed by what you tried and the error you received."
  List every other user of the constant in the commit message.
- Done when: Build 0 errors; the ModelEval and `RoslynSentinel.Tests` classes that reference the prompt pass.
- Done: 2e15938de0a6af591b99387c946a7b1b80f67cd5. The commit message lists the 10 users of the constant;
  no test pins the prompt text.

### Step 5 - Scenario base fixture and the first type 1 scenario

- **5a - base fixture, scenario type, selection**
  - Files: new base fixture and scenario type in `RoslynSentinel.Tests.ModelEval`; `scripts/roslynsentinel-modeleval.ps1`.
  - Change:
    - Setup any scenario needs, copied from `OrderPricingRefactorChainAgentTests.SetUp`: `LlmOptions`
      and the `Assert.Ignore` check, `TestSolutionFixture`, the in-process MCP server on
      `ChainEvalToolSurface`, the host and the solution load. Leave out `RefactorAcceptanceTests.cs`
      (gated to 10-chain at `:198-206`) and the baseline `dotnet test` (`:217`).
    - One `[Explicit]` `[Test]` that runs the scenario named by an environment variable for N samples,
      a fresh `TestSolutionFixture` per sample (as `TranscriptResumeAgentTests` builds one environment
      per sample, `:191-262`), scoring each with step 3 and writing the CSV.
    - Scenario definition type: the fields in "Decision" plus provenance (source run and turn,
      reproduces-at, fixed-by).
    - Script: a `-Scenario <id>` parameter and one `-Test` alias (`ValidateSet` at `:229`, alias table
      at `:296-310`); `-Samples` (`:259`, today wired only to the resume variable at `:404`) also sets
      the scenario sample count. Keep the run-folder leaf in the timestamp form the archiver expects
      (`:437-446`).
  - Done when: Build 0 errors; a non-live unit test runs the sample loop with a fake client and gets a CSV line.
- **5b - first scenario: method rename written inside a snippet**
  - Files: scenario definition.
  - Change: stage `OrderPricingRefactorReproducer.StartingCalculatorFileContent` (`:26`) and
    `CheckoutCallerFileContent` (`:78`). Prompt: the rename step of `TenStepUserPromptTemplate`
    verbatim (`OrderPricingRefactorChainAgentTests.cs:1434-1436`, `CalcDisc` to
    `CalculateDiscountedTotal`, caller in `OrderCheckout.cs`). Expected: `Recover`; final state has the
    new name in both files and the solution builds. Turn cap 8. Reproduces-at: measure with the lift
    flag off and on.
  - Done when: 10 samples run on host 113 in one invocation and write the CSV; the measured time per
    sample is recorded here (estimate: 1 to 1.5 minutes).
  - Measured 2026-11-11 (`qwen3.5-9b-coder`, host 113, commit f661adb4): 83 s per sample with the lift flag
    off (job 20261111-233623-001, 15m35s for 10), 66 s with it on (20261111-235219-001, 12m52s). Other
    scenarios ran 18 to 88 s per sample (the one-line edit scenarios fastest). Each job also pays about 15 s
    of build and start. One sample in each job ended Inconclusive (LM Studio HTTP 500, "Context size has been
    exceeded", on the first request).

### Step 6 - Remaining type 1 scenarios

- Files: scenario definitions only. Two slices.
- Change: one scenario per cold-start failure mode from the eval docs:
  - slice 1: whole-file edit that renames two symbols; re-declaring a member already in the file
    (CS0111) and the resend that followed; mixed single and batch shape (`filePath` or `oldContent` with
    `batchEdits`);
  - slice 2 (regression guards, reproduces-at "not measured"): twin `replace`/`apply` calls in one turn;
    whole-file `oldContent` with empty `newContent`.
- Done when: each runs 10 samples; provenance fields are filled.

### Step 7 - Type 3: faults in the prompt

- Files: scenario definitions.
- Change: fault variants of the type 1 scenarios:
  - symbol name with a typo (near match exists): `Recover`;
  - symbol name that is stale after an edit or simply wrong (no near match): `StopAndReport`;
  - wrong file name, with a near match (`Recover`) and without (`StopAndReport`).
- Done when: each runs 10 samples; the CSV shows the error code the model received, the call it made
  next and the marker result.

### Step 8 - Type 2: resume scenarios with drift gates

- **8a - schema comparer**
  - Files: new comparer in `RoslynSentinel.Tests.ModelEval`, `TranscriptResume.cs` (`ResumeSource`, `:19`,
    gains the recorded descriptions and schemas), one test file.
  - Change: compare recorded and live schema for every tool called in the kept history. Structural
    difference (parameter names, types, required, enum values): Inconclusive, naming the tool, the
    difference and the capture commit. Description-only difference (tool or parameter): `schemaDrift`.
    A transcript with no recorded schemas is Inconclusive.
  - Done when: unit tests cover structural, description-only, identical and missing.
- **8b - replay gate**
  - Files: `TranscriptResume.cs`, `TranscriptReplayHelper.cs`, one test file.
  - Change: a replayed call whose `IsError` differs from the recording is only counted and logged today
    (`TranscriptResume.cs:245`, `TranscriptResumeAgentTests.cs:126`). `ReplayKeptCallsAsync` returns an
    `int` that `TranscriptResumeTests.cs:169-195,315` depend on, so add a new member that returns the
    mismatches and the replayed result text; a scenario with any mismatch is Inconclusive.
  - Done when: a unit test with a doctored transcript yields the mismatch; existing resume tests pass.
- **8c - result drift**
  - Files: `TranscriptResume.cs`, one test file.
  - Change: the model is shown the saved result text (`RewriteTurns`, `TranscriptResume.cs:189`).
    Compare it with the replayed text from 8b after normalising the fixture directory and volatile
    fields (call ids, timings); count differences into `resultDrift`.
  - Done when: unit tests show a wording change counted and a call-id change ignored.
- **8d - capture, tracked folder, first scenario**
  - Files: a tracked transcript folder under `RoslynSentinel.Tests.ModelEval/` read through the repo-root
    walk (the project has no content items; none needed); scenario definition.
  - Change: capture after steps 1, 2 and 4 so the transcript carries commit, schemas and the new system
    prompt. Trim the file to the kept turns (archived transcripts average 178 KB). Today transcripts are
    read from `ModelTestingResults/`, which is ignored (`.gitignore:42`). First scenario: the caller
    edit right after a rename (stale `oldContent`). Then, as captures allow: an `oldContent` not-found
    turn; chain step 7 with CS0246 or CS0144.
  - Done when: one resume scenario runs 10 samples and writes the CSV.

### Step 9 - Type 4: faulted resume scenarios

- Files: resume harness, scenario definitions. Two slices, one per fault kind.
- Change:
  - Stale workspace: after the replay, apply a real edit the seeded conversation does not show.
  - Failed last call: append one assistant tool call to the history (for example a rename with a wrong
    symbol name), execute it on the fixture and seed the result the server returned. The call is
    authored; only its result is real. The CSV fault kind says so.
- Done when: one scenario of each kind runs 10 samples with a declared expected outcome.

### Step 10 - Batch summary and reference doc

- Files: `scripts/roslynsentinel-modeleval.ps1`, `docs/current/references/reference_model_eval_procedure.md`.
- Change: after a run, print per scenario: samples, Inconclusive count with each reason, first-call
  success rate, mean calls to recover, failure-class counts, drift flags. Inconclusive samples are never
  in the denominator. Document how to add a scenario, re-capture a transcript and score a saved one.
- Done when: a canned CSV with one Inconclusive sample shows it by name in the summary.

### Step 11 - Re-baseline 10-chain under the new system prompt

- Files: [eval_tenchain_reasoning_replay_2026_11_10.md](../evals/eval_tenchain_reasoning_replay_2026_11_10.md).
- Change: one batch of 5 runs of `Model_AppliesTenChainedRefactors` with the step 4 prompt; record pass
  rate, mean turns and how many runs ended with the marker; score each with step 3d.
- Done when: the batch is in the eval doc, marked as the first batch under the stop-marker prompt.

## Out of scope

- Replacing 10-chain, or changing it beyond the shared system prompt (step 4).
- Trimming the tool surface for speed.
- Running more than one model run at a time on host 113.
- Tracing the root cause of the `oldContent`-not-found transcription drift.
- Scenarios for tools other than `ReplaceSnippet` (the harness is tool-agnostic; they can follow).
- An ad hoc probe a working session can call with a prompt for quick feedback on a tool or error-hint
  change: [idea_adhoc_tool_probe_harness.md](../ideas/idea_adhoc_tool_probe_harness.md).

## Risks and open decisions

- **The stop marker breaks the 10-chain baseline.** Until step 11 runs, a 10-chain result cannot be
  compared with batches A to H4. The marker wording in step 4 is itself under test: it may make a model
  stop on errors it used to recover from. The `GaveUpEarly` class measures that.
- **Scenarios may show nothing on the current build.** Most modes are fixed or mitigated: the rename
  loop was 0 of 10 in batches F and G, stale `oldContent` 0 of 7 in H2 and H4. A scenario with a
  near-zero failure rate is a regression guard, not an iteration tool. Types 3 and 4 are where new
  signal is most likely.
- **Low base rates need many samples.** At 4 of 22, 10 samples cannot separate a fix from noise. Resume
  scenarios start at the failing moment and should need fewer. Sample counts per scenario are not set.
- **Error codes are coarse.** Of 413 failing `ReplaceSnippet` calls in the 89 archived transcripts, 203
  carried `Exception`, 82 `NotFound`, 74 `ValidationFailed`, 40 `InvalidArgument`, 6 `TargetIneligible`,
  1 `MatchedLooserThanRequested` and 7 no code. The scorer therefore records a class as well as the code.
- **Result-text comparison needs normalising.** The volatile fields beyond call ids, timings and
  absolute paths are not identified yet (step 8c).
- **Failing transcripts cannot be re-captured on demand.** A structural schema change invalidates a
  resume scenario until a run fails the same way again. Keep the resume set small; prefer a cold-start
  scenario wherever a mode reproduces that way.
- **Per-sample time is an estimate.** 17 s per turn was measured on long conversations; LM Studio
  prompt processing on a 2-5 turn conversation was not measured. `dotnet test` build and start overhead
  per invocation is not logged.
- **Not verified:** that `TryReadStringProperty` finds a nested `errorCode` (step 3a checks it); whether
  the transcript records reasoning blocks; whether the `Tools` list order is stable across runs; every
  user of `AgentSystemPrompts.CodingAgent` (step 4 lists them).
