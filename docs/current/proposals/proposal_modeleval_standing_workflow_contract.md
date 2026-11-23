# One standing workflow contract for model evals, and a fixed shape for scenario prompts

**Status:** PROPOSED 2026-11-23; design decisions settled 2026-11-23 (see "Decisions"). Nothing built.

## Motivation

The only instructions an eval model receives are the harness system prompt
(`AgentSystemPrompts.CodingAgent`, RoslynSentinel.AgentLoop/AgentSystemPrompts.cs:21, composed by
`ErrorPolicyWiring.ComposePrompt`) and the scenario's task prompt. No training or client prompt can be
assumed. When a scenario expects a process (stop, leave the tree unchanged, report) that neither prompt
states, models latch onto the least ambiguous instruction, usually the edit itself. Each time this has
been patched in the one scenario prompt that failed; the cause has not been addressed.

Evidence from the 2026-11-23 overnight ImplementationScenario runs:

- **WideningRejectedByGate** prompt (WideningRejectedByGateScenario.cs:166-169): "The callers are
  expected to compile unchanged. If they do not, do not work around it: report what breaks."
  - "expected to compile unchanged" reads as "already validated, safe to do".
  - "they" / "it" have no clear referent; the double negation is awkward.
  - "report what breaks" reads most directly as "make the change, then report what breaks".
  - The grader (WideningRejectedByGateScenario.cs:213, `CheckReportedOutcome`) expects no file changed.
    Ornith on 112 (run 20261123-155817-336) got the gate rejection at T4, resent the same edits with
    `validateOnApply: false` at T5 ("With validateOnApply=false, the tool will still write ... that's what
    I want"), then reported all 9 breaks accurately: graded BuildBroken.
  - Qwen3.5-4B on 85 edited the callers after the gate hint in 3 of 4 runs (20261123-091450-528,
    -102214-665, -102706-975).
- **ObsoleteNamespaceCopy**: 0/5 across both models with one long sentence; 5/5 after the prompt was
  split into one numbered step per file (commit f16f4458). The fix was local.
- **PlanStalenessReview** is a review-only task; the system prompt's role line is "You fix the specific,
  scoped problem described in the task" (AgentSystemPrompts.cs, "Your role").
- **4B finish-rule A/B** (a70b9f7e vs 6eff933d): a generic "report and stop" rule did not reduce cap
  hits (B arm 6/10 capped vs control 2/5); the cap hits were rejected-edit loops and unfinished work, not
  re-verification. A finish rule alone does not tell the model what "done" or "cannot be done" means for
  the task.

Defects in the standing prompt itself:

- **No precondition or stop criterion concept.** The BLOCKED rule fires only for "a named file or symbol
  does not exist" or "the same call has failed twice". A condition the task sets ("callers compile
  unchanged") failing is not covered, and nothing says what state to leave files in when stopping.
- **No rule on switching validation off.** The eval tool schema offers `validateOnApply`; neither the
  system prompt nor any ModelEval prompt mentions it (0 matches in RoslynSentinel.Tests.ModelEval).
- **Accumulated patches contradict each other.** Workflow step 4 (changed 2026-11-23 to "build the whole
  solution") contradicted the Verifying section ("a quick build of the affected project") and the worked
  example (`scope: project`) until both were aligned the same day.
- **Open-ended endings.** Many eval prompts end with a variant of "when you are done, report what you
  changed". That makes the model judge doneness itself, which invites looping and self-doubt.

## Inventory (2026-11-23)

36 scenario prompts in RoslynSentinel.Tests.ModelEval and the Harness StopAdherence set:

| Kind | Count | Scenarios |
| --- | --- | --- |
| Change | 30 | 13 ImplementationScenario files (NotFoundToTypedThrow has two steps), StopAdherenceControl, 4 WholeFileRewrite templates, 7 OrderPricing templates, ShippingKind steps 2-6 |
| Review / report-only | 3 | PlanStalenessReview, PlanOnly, ShippingKind step 1 |
| Conditional | 3 | WideningRejectedByGate, StopAdherence MissingFile, StopAdherence MissingMethod |

Not yet classified: SizeThreshold, LargeResultPaging, PlanThenExecute, PlanImplementVerify,
ScenarioAgentTests.

Findings that shape the design:

- **Graders check final-message content, not only disk state.** WideningRejectedByGate needs a `CS####`
  code or the caller names (WideningRejectedByGateScenario.cs:213-234); SweepConstructionSites needs the
  three kept sites or CS0266; PlanStalenessReview needs the review (CheckReview); ShippingKind step 1
  wants "one line per occurrence", step 4 "the number of literals replaced per file". These are task
  facts and need their own field (`Report`).
- **Ending lines today.** About 20 older prompts end with "Report what you changed and the verification
  result." (OrderPricing 4-10 steps, WholeFileRewrite) or "Reply with what you changed." (ShippingKind).
  ImplementationScenario prompts mostly carry no ending; WideningRejectedByGate and
  WidenToReadOnlyDictionary carry ad-hoc stop wording.
- **A positive control may already exist.** WidenToReadOnlyDictionaryScenario.cs:280-287 performs the
  same widening and ends "...stop and report that instead of working around it"; it looks like the twin
  of WideningRejectedByGate where the condition holds (grader not yet verified). StopAdherenceControl
  is the recovery control for the two StopAdherence scenarios.
- **Prompt construction.** ImplementationScenario prompts are raw-string constants passed verbatim as
  `Steps[].Prompt` (ImplementationScenarioAgentTests.cs ~94-127). ShippingKind uses
  `ShippingKindStepGoldens.PromptForStep` (480-492). Other families keep per-file templates.
- **Consumers of `CodingAgent`.** PlanStepRunner Program.cs:239 (via ComposePrompt);
  SubAgentEvalImpl.cs:56 in Tools.Advanced (raw, no ComposePrompt); every ModelEval call site via
  ComposePrompt. Tests compare against the constant itself (ErrorPolicyWiringTests.cs:13-14 and :37,
  ErrorPolicyEvalPromptTests.cs:22, :36, :51 with `Is.EqualTo` / `StartsWith(CodingAgent.TrimEnd())`);
  none asserts on its wording, so a rewrite breaks no test.
- **Version stamping.** The transcript records `SystemPrompt` (ModelAgentRunner.cs:261) and
  `ErrorPolicyHash` (:272); the per-sample CSV ends with errorPolicyMode, errorPolicyHash
  (ScenarioSample.cs:57-58). runs.csv (scripts/roslynsentinel-evalruns.ps1, Add-EvalRunIndexRow 57-75)
  has 17 columns and no prompt column, but its `pinnedCommit` already fixes the prompt text.

## Decisions (settled with the owner 2026-11-23)

| Question | Decision |
| --- | --- |
| Outcome signal | Both endings are tokens: the last message starts with `DONE:` or `BLOCKED:`. |
| File state on stop | Each scenario states it (mandatory field). Migrated existing scenarios whose expected behavior is unclear get "make no further changes; list what you wrote". Reason: a contract default could disagree with a grader nobody re-checked, failing a model that stopped correctly. |
| Prompts not yet migrated | The contract carries one temporary fallback sentence for them (no Done-when list, no stop-state line); removed when every consumer is migrated. |
| Prompt shape | A record rendered to text, with a `Report` field. |
| Contract placement | System prompt (replaces `CodingAgent`). The user turn carries only the scenario fields. The renderer can repeat the endings in the user turn for an A/B. |
| Positive controls | Every conditional scenario has a twin where the condition holds, and graders gain a false-BLOCKED outcome, before the A/B runs. |
| First slice | ImplementationScenario and the two StopAdherence scenarios. OrderPricing stays on the old prompt as the regression check until the A/B is read. |
| Offer to real clients | As an MCP prompt, after the A/B, using the winning text. |
| `validateOnApply` | Removed from the eval tool surface: hidden from the schema, `false` refused. A scenario opts back in to the full schema only when shown to need it; none does today. Evidence: Ornith on 112 (run 20261123-155817-336) sent the gate-rejected edits again with `validateOnApply: false` at T5. |
| Finish rule | `CodingAgent` workflow step 6 stays as it is until the contract replaces it. |
| A/B arms | Old vs new contract, both on the reworked prompts at the same commit; no pinned pre-migration arm. |
| WideningRejectedByGate overload outcome | Grader unchanged until the rework; the overload outcome is recorded as its own outcome class. |
| Authoring and grading | Scenarios are realistic briefs graded by behavioural tests the model can run, with no hidden criteria (section 3). An audit of the first slice comes before any migration. |
| Comparability | Not a goal. Results across revisions are noted as not comparable; the metric is whether models complete realistic refactoring tasks with the server tools. |

## Proposal

### 1. Standing workflow contract (restructure, do not append)

Rewrite `CodingAgent` as one short contract, positively phrased, one rule per idea, no incident history,
shorter than today's prompt (about 40 lines against 150). Draft:

```
You are a coding agent in a C#/.NET repository. You work only through the RoslynSentinel
MCP tools; there is no shell or file access outside them.

## The task message
- Goal: what to achieve.
- Files: the files you may change ("none": change no file).
- Conditions: facts that must hold. If one does not, the task cannot be completed as described.
- If a condition fails: what to leave on disk and what to report.
- Done when: the checks that mean the task is complete.
- Report: what your final message must contain.
If there is no Done-when list, the task is done when the Goal is met and a quick build of
the solution reports 0 errors. If the task does not say what to leave on disk when you
stop, make no further changes and list what you wrote.

## Working
1. Use only names, values and ids you have seen in a tool result this session. If you do
   not know an exact name, list with Search (mode: class, mode: method) instead of guessing.
2. Read code before editing it. Copy existing code character for character from your
   latest read; re-read if you can no longer see it.
3. Make the smallest change that meets the Goal. Change only the listed files; every other
   member keeps its logic. Removing a comment or doc comment is a content change.
4. The server rejects an edit that adds a compiler error and lists the errors.
   - Error in code you may change: correct your edit, or reorder edits so each compiles.
   - Error that shows a Condition fails: that is the answer. Take the BLOCKED ending.
5. Check each Done-when item once: run the tests Done when names with RunTest, run a quick
   build of the whole solution, then re-read the changed code and confirm nothing unrelated
   changed.

## When a tool call fails
Read the error and change something before retrying. A named file or symbol that does not
exist (and no match is offered), or the same call failing twice, means the task cannot be
completed as described.

## Tool side effects
Tools may normalize whitespace, re-indent, or add a "// Added by <Tool>" comment. Judge
code only on logic. A write rejected as a whitespace-only no-op means the content is
already correct.

## Endings
Your last message has no tool call and starts with DONE: or BLOCKED:.
- DONE: every Done-when check passed. List what you changed, the build result, and
  everything the task's Report line asks for.
- BLOCKED: some part of the task cannot be completed as described. Say what you tried,
  what fails and where (file, error). Leave files as the task's If-a-condition-fails line
  says, list every change already written (or "no changes"), and include everything the
  Report line asks for.
```

Design notes on the draft:

- **Gate rule split by ownership.** "A gate rejection is an answer about a condition" alone would also
  catch ordinary edit-order rejections and push weak models to false `BLOCKED`. Step 4 separates errors
  in code the task allows the model to change (fix the edit) from errors the Conditions protect (stop).
- **Partial completion is BLOCKED** ("some part of the task cannot be completed").
- **Review tasks** use `Files: none`; the review is the `DONE:` report, shaped by `Report`.
- **The worked example** is dropped from the draft. If the A/B shows it helps, keep one example ending
  in `DONE:`; no BLOCKED example (risk of priming blocking).
- **Survival rule.** Each section kept from today's prompt cites an incident: names-you-observed and
  listing (token-path slips; guessed-name Search loops), transcription (ReplaceSnippet transcription
  failures), comment removal (WholeFileRewrite MinimalGuidance), tool side effects (whitespace no-op
  loops). The citations go into the constant's doc comment, not the prompt.

### 2. Fixed shape for scenario prompts

Scenario prompts are built from a record and rendered to text; the endings come from the contract, not
from each prompt.

```
ScenarioTask
  Goal         required  one sentence
  Files        required  files the task may change; "none" for review tasks; a discovery
                         eval may say "find them" instead of naming paths
  Conditions   optional  hard, checkable facts; empty for plain change tasks
  IfFails      required  what to leave on disk and what to report when stopping
  DoneWhen     required  checkable list
  Report       optional  what the final message must contain beyond the contract default
```

`IfFails` is required even for plain change tasks so the stop state is always the scenario author's
decision; unclear existing scenarios get "make no further changes; list what you wrote". A new record
needs a NEW-DTO justification (dto-conventions skill).

The renderer emits one line per field (`Goal: ...`), skipping empty optional fields. A renderer option
can repeat the contract's Endings at the end of the user turn, for the placement A/B.

WideningRejectedByGate rewritten:

```
Goal: retype the parameter `changes` of Apply from Dictionary<FilePath, string> to
IReadOnlyDictionary<FilePath, string>.
Files: IChangeWriter.cs, ChangeWriter.cs (ContosoOrders.Core/Evals/WideningRejectedByGate) and
ContosoOrders.Tests/Evals/WideningRejectedByGate/FakeChangeWriter.cs.
Conditions: every caller of Apply compiles with no edit to the caller.
If a condition fails: write nothing.
Done when: the three signatures use IReadOnlyDictionary and a quick build of the solution reports 0 errors.
Report: if a condition fails, each caller that would fail and its compiler error code.
```

### 3. Scenario authoring rules

Scenarios written so far were designed outcome-first: the author fixed one expected end state, then wrote
a prompt that steers toward it, filling gaps with assumptions the author never noticed making. The eval
model reads such a prompt as a plain "do this", so the result depends on how each model (or seed) reads
the gaps, not on whether it can do the refactoring. Worked example, SweepConstructionSites
(SweepConstructionSitesScenario.cs):

- The task says "replace each `new Dictionary<FilePath, string>`"; three sites (`FromResult`, `Choose`,
  `Latest`, e.g. line 263: `var latest = new Dictionary<FilePath, string>();` later assigned a plain
  Dictionary) stop compiling (CS0266) once converted.
- Declaring the local as `Dictionary<FilePath, string> latest = new DocumentChangeSet();` is correct and
  satisfies the task, but the grader fails it: it counts exactly 14 `new DocumentChangeSet` and 4
  `new Dictionary` left (CheckSweep, line 319).
- A cast (`(DocumentChangeSet)second.Changes`) compiles and throws at run time; failing it is right, but
  the prompt never says why, only "if converting makes the build fail, leave that site".
- "In as few edit calls as you can" is an eval measurement inside the task.

Rules for every new or migrated scenario:

1. **Realistic brief first.** Write the prompt as it would be handed to a human or Haiku implementer
   (the dispatch-implementer brief is the model): goal, why, constraints, acceptance. Write the grader
   after, from the brief.
2. **Behavioural acceptance.** The fixture's test project carries tests that exercise the behaviour the
   task changes; `Done when` names them, the model can run them, and the grader runs the same tests. A
   failing test tells the model what is wrong while it can still fix it; a hidden check only tells the
   grader afterwards.
3. **No hidden criteria.** Every grader failure traces to a sentence of the prompt (`Files`, `Conditions`,
   `Done when`, `Report`). A check with no sentence behind it is removed or the sentence is added.
4. **List the acceptable outcomes.** For each scenario record the plausible solutions (for Sweep: explicit
   type, cast, leave the site, BLOCKED) with pass or fail and the prompt sentence that decides it.
5. **No text counting.** No regex or occurrence counts over source for anything a test or a compiled-symbol
   check can decide (see proposal_modeleval_test_axes_and_aligned_grading.md section 2).
6. **Measurements are metrics.** Edit-call counts, turns and failed calls are recorded, never graded and
   never written into the task.
7. **Cold-read review before a scenario lands.** A fresh agent gets only the rendered prompt (never the
   grader or fixture answers) and lists its interpretations and the questions it would ask. Each question
   is answered in the prompt or accepted as a passing variant.
8. **One declared axis per scenario** (proposal_modeleval_test_axes_and_aligned_grading.md section 1).
   The scenario definition names its primary axis; the other axes are held near zero. A tool-specific
   instruction ("Use the ReplaceSnippet tool") belongs only in a `ToolUse` scenario, where the prompt
   states it as a requirement and the grader checks it; elsewhere the model picks its tools.
9. **Every scenario runs tests.** Each `Done when` names at least one fixture test the model runs with
   RunTest (older scenarios that told the model to run tests saw models do it).

Audit of the first slice against these rules:
[finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md](../findings/finding_modeleval_first_slice_scenarios_grade_beyond_their_prompts.md).

### 4. Eval tool surface: `validateOnApply` hidden

Evals run with `validateOnApply` hidden from the tool schema; a call passing `false` is refused with the
existing facts-only error ("Nothing was executed", ToolArgumentValidator.cs:1292-1305). The machinery
exists for the lean schema profile (`SchemaOptions`, `McpToolSchemaPatcher.ApplyLeanProfile`,
`HiddenSchemaParams.IsHidden`), but lean also hides `autoStage`, `returnDiff`, `lineBefore` and
`lineAfter`, which would confound the A/B; phase 1 adds a switch that hides only `validateOnApply`. A
scenario can opt back in to the full schema; no current scenario does. To verify while planning: that
the eval harness's tool calls pass through schema patching and `ToolArgumentValidator` at all.

### 5. Graders

- Pass/fail comes from the build and the fixture's acceptance tests (the ones `Done when` names), plus
  the scope stated in `Files` (no other file changed) and, where the prompt has a `Report` field, the
  final-message content it asks for. Structural facts no test can reach are checked on compiled symbols,
  not source text.
- Parse the outcome token from the final message: `DONE:`, `BLOCKED:` or none.
- Outcome classes: correct-done, correct-blocked, false-blocked (BLOCKED on a task that was doable),
  bypass (files changed against IfFails, e.g. BuildBroken), silent-incomplete (no token or DONE with a
  failing check).
- Every conditional scenario has a positive-control twin. Verify WidenToReadOnlyDictionary as the
  WideningRejectedByGate twin (write one if its grader does not fit); StopAdherenceControl covers the
  StopAdherence pair.
- Existing graders are rewritten to these rules when their scenario migrates, using the audit's
  outcome list.

### 6. Version stamping

Hash the contract text the way `ErrorPolicyHash` is computed and record it beside it: transcript
(ModelAgentRunner.cs:272) and the per-sample CSV (ScenarioSample.cs:57-58), plus the renderer's
placement option. runs.csv needs no change while variants are separate commits (`pinnedCommit` fixes the
text); add a variant column only if the A/B uses a runtime switch.

### 7. Offer the same text to real clients (after the A/B)

If the contract exists only in the harness, evals measure a crutch real clients do not have. Expose the
winning text as an MCP prompt (not server `instructions`: the contract is policy, and server text is
facts and options, CLAUDE.md). Tools.Advanced already references `CodingAgent` (SubAgentEvalImpl.cs:56),
so the prompt can be served from there without moving the text to `Common`. The fallback sentence for
unmigrated prompts is removed before publishing.

## Phasing

1. **Contract and record behind a switch.** New contract text beside the old (`CodingAgent` kept,
   selected by a harness option, old text default); `ScenarioTask` record and renderer; contract hash
   stamped; `validateOnApply` hidden on the eval surface with a per-scenario opt-in. No scenario
   changes yet.
2. **Audit the first-slice scenarios** (ImplementationScenario and the two StopAdherence scenarios):
   cold read of each prompt, acceptable-outcome list, every grader check traced to a prompt sentence,
   and the behavioural tests each scenario lacks. Output: one findings doc.
3. **Rework the first slice from the audit.** Each scenario becomes a realistic brief in `ScenarioTask`
   form with behavioural acceptance tests in the fixture's test project; graders follow section 5.
4. **Controls.** Positive-control twins verified or written; grader outcome classes including
   false-BLOCKED.
5. **A/B.** Old vs new contract on Qwen3.5-4B and Ornith, both arms on the reworked prompts at the same
   commit, at least 5 runs per arm. Success: pass rate up, cap hits down, false-BLOCKED not up. Optional
   second A/B: endings repeated in the user turn.
6. **Switch and migrate.** New contract becomes the default; migrate WholeFileRewrite, OrderPricing,
   ShippingKind, PlanOnly and the unclassified families under the same rules, deleting their own ending
   lines; review the PlanStepRunner and SubAgentEval prompt builders; remove the fallback sentence when
   the last consumer is migrated.
7. **MCP prompt** with the winning text.

## Alternatives considered, not pursued

- **Keep patching the failing scenario prompt** (status quo): each fix is local, wording drifts between
  scenarios, and the same failure class returns in the next scenario.
- **Append a separate rules block to the existing prompt**: adds length for weak models and keeps the
  contradictions; restructure instead.
- **Keep `validateOnApply` visible and add a contract rule**: would measure whether wording alone holds
  the model to the gate, but a model already reasoned its way to switching validation off (Ornith T5
  above); the owner chose to remove the parameter instead (2026-11-23).
- **Hide `validateOnApply` via the whole lean profile**: also hides four other parameters and confounds
  the A/B.
- **A generic finish rule only** (tried a70b9f7e): no measurable effect on 4B cap hits.
- **A contract default for the stop state**: an unchecked grader could disagree with it and fail a model
  that stopped correctly; each scenario states it instead.
- **Free-text prompts checked by a lint test**: brittler than a record and gives no renderer switch for
  the placement A/B.
- **Undo own changes on stop**: costs turns, needs an undo tool in every eval surface, and a failed undo
  is a new failure mode.

## Open questions

- Exact wording of the draft, settled by the A/B rather than in review.
- Graders not yet read: RetypeSliceExactLines, ShareHelperDifferingRules, ThreadOptionalParameter,
  WidenToReadOnlyDictionary, OrderPricing 7-10 steps, PlanOnly, WholeFileRewrite `AssertFixApplied`.
- Prompt bodies and graders of SizeThreshold, LargeResultPaging, PlanThenExecute, PlanImplementVerify
  (including its "{2}" user-prompt splice) and ScenarioAgentTests.
- How PlanStepRunner builds its user prompt (Program.cs:239): if plan steps already carry a Done-when
  field (the plan-writer skill requires one), the record maps onto plan steps directly.

## Cost / risk

- Results before and after the change are not comparable, and that is accepted: the metric is whether
  models complete realistic refactoring tasks with the server tools, not a continuous history. The hash
  labels which text ran; no re-baselining effort.
- Writing behavioural tests for each reworked scenario costs more than a regex grader; it is the price
  of grading what a real reviewer would accept.
- Concurrent eval queues pin commits, so running jobs are unaffected; new submissions see the new text.
- Risk of over-blocking under the new gate rule; the controls and the false-BLOCKED outcome measure it.
- Risk of over-length for 4B-class models; the draft is shorter than today's prompt.

## Related

- [proposal_modeleval_test_axes_and_aligned_grading.md](./proposal_modeleval_test_axes_and_aligned_grading.md)
  (one axis per test, symbol-based structural checks; section 3 here adopts its grading direction)
- [proposal_parallel_change_deprecated_wrappers.md](./proposal_parallel_change_deprecated_wrappers.md)
  (migration-first shape for the code change)
- docs/current/references/reference_tool_error_text_facts_options_policy.md (facts vs. policy split)
- INBOX 2026-11-23 lines: edit gate hint, ObsoleteNamespaceCopy, WideningRejectedByGate prompt
- Memory: project_token_path_slips_eval_rules, feedback_prefer_mandatory_params_to_close_footgun_roundtrips
