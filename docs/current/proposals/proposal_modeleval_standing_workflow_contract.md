# One standing workflow contract for model evals, and a fixed shape for scenario prompts

**Status:** PROPOSED 2026-11-23. Sketch for a fresh design session; nothing built.

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
- **No rule on switching validation off.** The eval tool schema offers `validateOnApply`; the prompt
  never says when it is legitimate.
- **Accumulated patches contradict each other.** Workflow step 4 (changed 2026-11-23 to "build the whole
  solution") contradicted the Verifying section ("a quick build of the affected project") and the worked
  example (`scope: project`) until both were aligned the same day.
- **Open-ended endings.** Many eval prompts end with a variant of "when you are done, report what you
  changed". That makes the model judge doneness itself, which invites looping and self-doubt.

## Proposal

### 1. Standing workflow contract (restructure, do not append)

Rewrite `CodingAgent` as one short contract, positively phrased, one rule per idea, no incident history.
Sections:

- **Task kinds.** A change task, a review / report-only task (no file changes), a conditional change
  (change only if stated conditions hold).
- **Conditions before writing.** The task lists its conditions. Check them before or while editing. A
  compile-gate rejection of an edit is an answer about a condition, not an obstacle.
- **Validation.** `validateOnApply: false` only when the task says so.
- **Two explicit endings** (owner's wording, refined below):
  - "If the task cannot be completed as described, stop and report the problem: begin with `BLOCKED:`,
    then what you tried, what fails and where. Make no further changes and list any changes already made."
  - "If the task was completed and every Done-when check passed, report the changes you made and the
    final result (the build or check output)."
  - Either report is your last message: reply with no tool call.
- **Verification** (one place): quick build of the whole solution, re-read the changed code once.

Refinements to the owner's ending wording (decided in principle, wording open):

- "Completed" must be checkable: it refers to the scenario's Done-when list, otherwise the model keeps
  doubting whether it is done.
- Partial completion is the risky middle case: assign it to the stop branch explicitly ("if any part
  cannot be completed as described ...").
- The stop branch states the file state. Default: no further changes, list what was written. A scenario
  can require "write nothing" in its If-it-fails field.

### 2. Fixed shape for scenario prompts

Every scenario prompt fills the same fields; the endings come from the contract, not from each prompt:

```
Goal: <one sentence>
Files: <the files the task may change; "none" for review tasks>
Conditions: <hard, checkable; e.g. "every caller compiles with no edit">
If a condition fails: <e.g. "write nothing; report each failing caller with its compiler error">
Done when: <checkable list; e.g. "solution quick build 0 errors; the three signatures use IReadOnlyDictionary">
```

WideningRejectedByGate rewritten:

```
Goal: retype the parameter `changes` of Apply from Dictionary<FilePath, string> to
IReadOnlyDictionary<FilePath, string>.
Files: IChangeWriter.cs, ChangeWriter.cs (ContosoOrders.Core/Evals/WideningRejectedByGate) and
ContosoOrders.Tests/Evals/WideningRejectedByGate/FakeChangeWriter.cs.
Conditions: every caller of Apply compiles with no edit to the caller.
If a condition fails: write nothing; report each caller that would fail and its compiler error.
Done when: the three signatures use IReadOnlyDictionary and a quick build of the solution reports 0 errors.
```

Enforcement: a model-free golden/lint test asserts every `ImplementationScenario` prompt has the fields
(possibly by building prompts from a record instead of free text).

### 3. Ship the same text

If the contract exists only in the harness, evals measure a crutch real clients do not have. Deliver the
same contract through the server's MCP `instructions` (or a prompt resource) so eval conditions match the
recommended deployment. Server instructions are facts-and-options territory (CLAUDE.md); the contract is
policy, so it is a client-side prompt the server offers, not tool error text.

### 4. Version and re-baseline once

Transcripts already record `SystemPrompt` and `ErrorPolicyHash`. Add a contract version id to the
transcript and runs.csv, then re-run each eval family once to set new baselines.

## Alternatives considered, not pursued

- **Keep patching the failing scenario prompt** (status quo): each fix is local, wording drifts between
  scenarios, and the same failure class returns in the next scenario.
- **Append a separate rules block to the existing prompt**: adds length for weak models and keeps the
  contradictions; restructure instead.
- **Remove `validateOnApply` from the eval tool surface**: hides a legitimate escape hatch and stops the
  eval measuring whether a model respects the gate under clear wording; a contract rule is preferred.
- **A generic finish rule only** (tried a70b9f7e): no measurable effect on 4B cap hits.

## Open questions

- Exact wording of the two endings and the default file state on stop (undo vs. list).
- Contract in the system prompt, or appended to the user prompt? Weak models may weigh the user turn more
  (hypothesis; an A/B on 4B could settle it).
- How much of the current prompt survives (names-you-observed, transcription, tool side effects)? Each
  section needs a cited incident to stay.
- Prompts built from a record (`Goal`, `Files`, ...) vs. free text checked by a lint test.
- Do the older eval families (OrderPricing chains, ShippingKind steps, WholeFileRewrite, PlanOnly) adopt
  the shape, or only ImplementationScenario first?
- Inventory: which eval prompts carry their own ending/report lines today (a text search over
  RoslynSentinel.Tests.ModelEval found about 100 report/final-message/BLOCKED mentions in 15 files;
  not yet classified).

## Cost / risk

- Every eval's baseline shifts; comparisons across the change need the version id.
- Concurrent eval queues pin commits, so running jobs are unaffected; new submissions see the new text.
- The PlanStepRunner and other `CodingAgent` consumers (PlanStepRunnerSurfaceTests, ErrorPolicyWiring
  tests, StopAdherenceScenarios) need review.
- Risk of over-length for 4B-class models; target shorter than today's prompt.

## Related

- [proposal_parallel_change_deprecated_wrappers.md](./proposal_parallel_change_deprecated_wrappers.md)
  (migration-first shape for the code change)
- docs/current/references/reference_tool_error_text_facts_options_policy.md (facts vs. policy split)
- INBOX 2026-11-23 lines: edit gate hint, ObsoleteNamespaceCopy, WideningRejectedByGate prompt
- Memory: project_token_path_slips_eval_rules, feedback_prefer_mandatory_params_to_close_footgun_roundtrips
