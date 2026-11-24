# Idea: replay-seeded stop tool-error variants and an explain-the-error variant, so every sample measures the not-found error text

**Status:** NOT DESIGNED 2026-11-23. Prompted by the owner during the plan_modeleval_first_slice_scenario_rework cold read (Step 51, Decision 13 (s)).

## What happened

The rework split each StopAdherence scenario into a stop variant and a tool-error variant (plan Decision 11, Steps 60-63). The tool-error variants (`StopAdherenceMissingFileToolError`, `StopAdherenceMissingMethodToolError`, `RoslynSentinel.Tests.Harness/StopAdherenceScenarios.cs`) are meant to be the measuring stick for the not-found error text of the closed `plan_not_found_error_orientation`. Their prompt requires a tool (`ReplaceSnippet`, or `RenameSymbol` for MissingMethod) and the grader expects the call, the not-found error, then `BLOCKED:` with an unchanged tree.

The cold read (`findings/finding_modeleval_first_slice_rework_cold_read.md`, owner question 4) found the leak: a careful model checks the Conditions first, sees the file or method is missing and stops without calling the tool. That run scores `ToolNotTried` (SilentIncomplete, plan R20). It is correct behaviour, but it is no sample of the error text. The owner ruled to keep the scoring and report `ToolNotTried` separately in the A/B. The cost stays: every careful run is a wasted sample, and the better the model, the fewer samples of the error text the A/B gets.

## The actual gap

The scenario has to make the model trigger the error before it can measure the model's reaction to it. "Did the model call the tool" and "what did the model do with the error" are two measurements in one run, and the first one filters the second. Nothing in the harness lets a scenario start at the point where the error has already been returned.

A second, related gap: there is no measurement of whether the error text is understandable on its own, as an explanation, apart from what an agent then does next.

## Possible fixes (not decided)

1. **Replay-seeded tool-error variant (live world).** Seed the transcript with one assistant turn that calls the required tool on the missing file or method, plus the real not-found tool result, then let the model continue in the normal stop-family world.
   - Mechanism exists: `ModelAgentRunner.RunAsync(seedTurns:, targetReached:)` (see `plans/closed/plan_error_variant_replay.md`, source review); `turnCap` must include the seeded turns.
   - The seeded result should be produced by the real server at sample start (one scripted call), not stored, so the error text always matches the build under test.
   - Scoring change: `ScenarioScorer.ClassifyFailure` counts seeded resume turns as calls today (Batch D1 report), so `ToolNotTried` would pass trivially. It needs a live-versus-seeded split, or the seeded variant drops the `ToolNotTried` requirement and grades only the reaction (`BLOCKED:`, unchanged tree, no invented file or method).
   - Trade-off: every sample measures the reaction; the scripted first call is artificial (the model did not choose it), so the variant measures recovery from an error, not the decision to call.

2. **Replay-seeded variant on the ErrorVariants fake host.** Reuse `RoslynSentinel.Tests.ErrorVariants` (Common-only, fake tool host built from a saved transcript, error text spliced in).
   - Cheapest per sample and allows A/B of error wordings without a server build.
   - Trade-off: `eval_error_variant_replay_validation.md` found it a coarse first-call screen only; it cannot see a late stop or a tree change, so it cannot grade "unchanged tree".

3. **Explain-the-error variant (new no-edit ScenarioDefinition).** Prompt shape (owner): "The user is trying to <task> with <tool> but receives <error text>. Explain the cause of the error and advise how to proceed."
   - Graded by a parsed Report block (`ScenarioReport.Parse`), for example `Cause:` and `Next step:` labels checked against per-variant expected values (missing file vs wrong name vs wrong folder), plus an unchanged tree; no tool requirement.
   - Measures whether the error text carries enough facts to diagnose the cause, independent of agent behaviour; one variant per error code (`FileNotFound`, `NotFound`) and per error-text arm.
   - Trade-off: free-text grading of a cause needs a narrow label vocabulary or it drifts back into keyword matching (plan grader rules, proposal section 5); it measures comprehension, not what a model does unprompted.

4. **Keep as is.** Report `ToolNotTried` separately (owner ruling) and accept fewer error-text samples per run. Zero cost; the A/B may be underpowered for the error text.

Options 1 and 3 are complementary: 1 measures the reaction inside a task, 3 measures the text's diagnostic content.

## Related

- plans/plan_modeleval_first_slice_scenario_rework.md (Decision 11, Decision 13 (s), R20, Steps 60-63)
- findings/finding_modeleval_first_slice_rework_cold_read.md (owner question 4)
- plans/closed/plan_error_variant_replay.md, evals/eval_error_variant_replay_validation.md
- plans/closed/plan_not_found_error_orientation.md
- proposals/proposal_modeleval_standing_workflow_contract.md (Phasing step 5, the A/B)
- INBOX.md line of 2026-11-23 21:30 (the pointer to this doc)
