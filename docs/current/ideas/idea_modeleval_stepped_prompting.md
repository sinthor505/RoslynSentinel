# Idea: Feed multi-step ModelEval tasks one step at a time, with a per-step report

**Status:** NOT DESIGNED 2026-11-14. The owner asked whether giving the model one step at a time ("Now do <step> and reply with what you found / what you changed") would reduce skipped steps and show more of the model's working than its internal reasoning.

## What happened

- Multi-step ModelEval tasks hand the model the whole plan in one prompt. The main example is the ten-chain in `RoslynSentinel.Tests.ModelEval/OrderPricingRefactorChainAgentTests.cs`. The model then sequences the steps itself.
- Failures show up only in the final state, plus whatever we read out of transcripts. Skipped or merged steps, and a final reply that claims steps it never did, are found by hand.
- The error-variant replay (`evals/eval_error_variant_replay_validation.md`) has no clean place to cut a run inside a task. In regenerate mode, 23-33% of rows were dropped as `RegenMiss` when the first error came after turn 1.

## The actual gap

- **Steps cannot be told apart.** A whole-plan run does not separate "cannot do step k" from "skipped step k" or "planned the steps badly". Per-step outcome, turn count and tool choice are not measured.
- **Claims are not checked per step.** The model's own account of what it did is only checked against the final state.
- **Replay has no natural seed point.** A cut in the middle of a step lands inside the model's own turn sequence. A step boundary is a user message, so the model's next turn starts the step fresh, which is the shape regenerate mode wants.

## Possible fixes (not decided)

1. **Stepped mode as an A/B arm.** An env switch (pattern: `ROSLYNSENTINEL_MODELEVAL_EDIT_ARM`) splits the scenario's step list. The harness sends step 1. When the model gives a final reply with no tool call, the harness sends "Now do step k+1: <text>. Reply with what you found and what you changed."
   - **Measures:** per-step completion, skipped steps, turns and tokens per step, false claims per step, and wall time.
   - **Trade-off:** it tests execution and the tool surface, not planning or sequencing. Keep whole-plan runs as the capability test.
   - **Baselines:** stepped results are not comparable with whole-plan results.
2. **Grade the per-step report.** Compare the model's "what I changed" against the step's real diff, and "what I found" against a known answer where a step has one. This catches false done-claims at the step where they happen.
   - **Limit:** the report is a self-account, not reasoning. The internal reasoning is already in the transcript. The value is the check against the diff.
3. **Step-boundary replay.** Seed `RoslynSentinel.Tests.ErrorVariants` at the start of step k, with the recorded history through step k-1. Vary the step prompt wording as well as the error text.
   - **Upside:** this removes `RegenMiss` at the boundary.
   - **Cost:** the source runs must be stepped runs.

Design questions for whoever picks this up:
- **History between steps:** keep the full history (realistic), or summarize earlier steps (cheaper, less drift)?
- **Turn caps:** one per step (`ROSLYNSENTINEL_MODELEVAL_TURN_CAP` exists per scenario), or a total cap?
- **A failed step:** stop the chain, or inject the expected state and continue so later steps stay measurable?
- **Report prompt:** a fixed instruction, or one per step, and is the report graded or only logged?
- **Reuse:** can PlanStepRunner's per-step loop be reused instead of building a new one?
- **Single-step scenarios:** fault and locate scenarios are already one step. Is there a reason to step them at all, such as a separate "find" turn before "change"?

A first test: stepped against whole-plan on the ten-chain, 20-30 samples per arm, `qwen3.5-9b-mtp` on host 113.

## Related

- `docs/current/evals/eval_error_variant_replay_validation.md` (`RegenMiss` rates, late stops the replay cannot see).
- `docs/current/plans/closed/plan_error_variant_replay.md` (seeded and regenerate replay modes).
- `docs/current/proposals/proposal_modeleval_lean_tool_surface.md` (another surface change that also breaks ModelEval baselines; sequence the two A/Bs, do not overlap them).
