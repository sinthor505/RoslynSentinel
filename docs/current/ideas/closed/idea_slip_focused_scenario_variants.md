# Idea: Logic-simpler variants of the ImplementationScenarios that keep their slip-prone content

**Status:** IMPLEMENTED 2026-11-26. Plan [plan_slip_focused_scenario_variants.md](../../plans/closed/plan_slip_focused_scenario_variants.md) built the siblings and E1; the slip-rate baseline moves to [idea_slip_first_scenarios.md](../idea_slip_first_scenarios.md).

## What happened

The eval's primary axis is slips: token-path slips, escape-copies and transcription errors. The four
ImplementationScenarios are modelled on real plan tasks from this repo, so each also carries a logic
trap. With the 35-turn cap and `plain` tool results (Qwen3.5-9B, thinking off), the logic trap decides most
outcomes:

| Scenario | Pass, plain, cap 35 | What decided the failures |
| --- | --- | --- |
| ThreadOptionalParameter | 8/8 (batch `topordinal-3e62860c`) | none; slips are the only thing left to measure |
| WideningRejectedByGate | 0/4 (batch `cap35plain-590eb1c2`) | scope judgement: the task is impossible within Files and the model must stop and report; all 4 runs edited files outside Files |
| SweepConstructionSites | 0/4 (same batch) | bookkeeping: an unconverted site not named in the Report (3 runs hit the turn cap with that state) |
| WidenToReadOnlyDictionary | 2/4 (same batch) | edit order: Validate(Dictionary) forwards to the overload Validate(Dictionary, int), so retyping the outer one first never compiles; the 113 runs never found the leaf-first or one-batch route |

The slip-prone content is still useful, and these runs exercised it:
- WidenToReadOnlyDictionary doc comments full of quotes and `<see cref=.../>` produced a repeated `/>/`
  slip in batch `oldContent` (runs 113 `20261126-192345-882` T12-T26, 112 `20261126-192741-083` T18),
  and an escaped quote that swallowed the `newContent` key into `oldContent` (113
  `20261126-192009-439` T5, T12-T21). The slip classification is a hypothesis; the token stream has not been checked.
- ThreadOptionalParameter is where the json-vs-plain escape-copy difference showed up (json 59 lines,
  plain 3, xml 0 at cap 35).

Results and evidence: `docs/current/plans/closed/plan_plain_text_tool_results_for_eval_model.md`, section "Follow-up batches".

## The actual gap

Each scenario measures two things at once, logic and transcription, and pass/fail cannot tell them
apart. A run that fails on scope judgement can hide or end before the slip-prone edits, so the slip
count per run is noisy and depends on how far the model got. This conflicts with the rule of one primary
axis per evaluation.

## Possible fixes (not decided)

**A. A logic-simpler sibling of each scenario.** Keep the fixture text that produces slips. Remove the
trap:
- WideningRejectedByGate: the callers compile with the wider type. Remove the `changes: []` collection
  expression in ChangePublisher.cs, which is CS9174 with IReadOnlyDictionary, and the
  `ChangeJournal.Append(Dictionary)` dependency, so the retype across the 3 Files is allowed. Keep the
  long generic type text and the multi-file batch.
- WidenToReadOnlyDictionary: either make the overloads independent (no forwarding), or keep the chain
  and name the leaf-first order in the prompt. Keep the escape-heavy doc comments and crefs.
- SweepConstructionSites: name every site in the prompt and drop the "name unconverted sites in the
  Report" check. Keep the many similar call sites.
- ThreadOptionalParameter: no change; it already serves as the slip benchmark.

Trade-off: new scenario code and golden tests per sibling. The originals stay for logic evals.

**B. Prompt-only variants where they suffice.** `ImplementationScenarioStep.PromptVariants`
(`RoslynSentinel.Tests.ModelEval/ImplementationScenarios/ImplementationScenario.cs:36-71`, selected by
`ROSLYNSENTINEL_MODELEVAL_PROMPT_VARIANT`) already switches the prompt per step. That covers
leaf-first wording for WidenToReadOnlyDictionary and naming the sites for SweepConstructionSites. It
cannot change the fixture, so it does not cover WideningRejectedByGate, and SweepConstructionSites
still grades the Report check unless the grader also reads the variant. Cheapest to build.

**C. Fixture-start variants.** There is no mechanism today for a variant with a different starting
tree. `ScenarioVariant` (`ImplementationScenarios/ScenarioVariant.cs`) is a graded end state for the
model-free outcome-table tests, not a starting fixture. Options: a new scenario class per sibling,
registered in `ImplementationScenarioRegistry.cs` and reusing the original's seed templates (fits A).
Or a start-overlay selected like the prompt variant (more general, more harness work).

**Grading and measurement (all options):**
- Pass/fail on the code stays.
- The headline metric becomes slips per run:
  - escape-copy lines, from the counter in `scripts/Get-ToolResultFormatArmTable.ps1`;
  - near-miss identifiers;
  - snippet NotFound refusals whose closest window scores >= 0.9.
- Count the NotFound and ValidationFailed refusal codes as argument failures as well. The arm table
  counts only `InvalidArgument` and `ValidationFailed` today, so the escaped-quote refusal (NotFound)
  is missed.
- Size each arm for slip rates of 1-10% per first write (see the failure-analysis skill), which means
  many runs per cell or the `ShippingKindReplay` style early stop.

Open questions for plan-design:
- One sibling per scenario, or one combined "slip gauntlet" scenario that collects the slip-prone
  snippets?
- Does the sibling share the original's fixture folder (`ContosoOrders.Core/Evals/<name>`), or get its
  own so both can run in one queue batch?
- Which slip metric is the primary verdict, and what counts as a slip versus a logic error? The
  failure-analysis skill's near-miss rule is a starting point.

## Related

- `docs/current/plans/closed/plan_plain_text_tool_results_for_eval_model.md` (the A/B and follow-up batches that prompted this)
- `docs/current/findings/finding_overnight_token_path_slips.md`
- `.claude/skills/failure-analysis/SKILL.md` (token-path slip rules, sample size)
- INBOX lines 2026-11-26 20:05 and 20:30 (tool defects found in these runs; fixes in progress)
- Memories: `feedback_eval_focus_slip_axis_not_logic`, `project_token_path_slips_eval_rules`
