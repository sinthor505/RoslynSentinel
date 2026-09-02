---
name: project_model_eval_baseline_corrected_2026_09_02
description: "Corrects stale model-eval pass-rate memories: AssertFixApplied was flipped at some point to REQUIRE calling the helper directly + raising its accessibility (previously scored that as failure). Re-derived current pass rates: MinimalGuidance 1/56 (2%), Disambiguated 0/20 (0%), PlanThenExecute 19/45 (42%), ScriptedPlan 5/5 (100%), PlanImplementVerify 14/30 (47%)."
metadata: 
  node_type: memory
  type: project
  originSessionId: 18d9cda6-eed8-4198-86a2-eaa21d82eb19
  modified: 2026-09-02T08:34:11.047Z
---

The ~34% (MinimalGuidance) / ~40% (Disambiguated) pass rates recorded in
[[project_overnight_50run_sweep_2026_08_31]], [[project_disambiguated_prompt_n20_result]],
and [[project_minimalguidance_reasoning_pattern_analysis]] were computed against
an **older version** of `WholeFileRewriteAgentTests.AssertFixApplied` that scored
"call the shared helper directly, raising its accessibility" as a **failure**.
The assertion was later flipped to **require** exactly that behavior (matching
the real-world incident — 52 duplicated call sites — the fixture models).

Re-derived pass/fail against the **current** assertion, from a 2026-09-02 full
excavation of `ModelTestingResults\113` (165+ runs, see
`docs/current/model_eval_pattern_analysis_2026_09_02.md` for the full report):

| Variant | Strict pass | Mechanically-correct-fix rate (ignoring tool-error budget) |
|---|---|---|
| MinimalGuidance | 1/56 (2%) | 16/56 (29%) |
| MinimalGuidanceDisambiguated | 0/20 (0%) | 0/20 (0%) |
| PlanThenExecute | 19/45 (42%) | 28/45 (62%) |
| ScriptedPlan | 5/5 (100%) | 5/5 (100%) |
| PlanImplementVerify | 14/30 (47%, 9 excluded — stuck in plan phase) | 19/30 (63%) |

**Why this matters**: MinimalGuidance/Disambiguated are near a complete floor
under the current assertion, not ~34-40%. The old "private priming" pass/fail
correlation (0/17 pass when "private" appears in reasoning) can no longer be
meaningfully recomputed — there's essentially no passing group left to
correlate against. Also: under the corrected numbers, scaffolding (PlanThenExecute,
PlanImplementVerify, ScriptedPlan) DOES substantially outperform no-scaffolding
variants (42-100% vs 0-2%) — the earlier "scaffolding hasn't reliably helped"
framing was itself an artifact of comparing against the stale baseline. See
[[project_modifymodifier_accessibility_footgun]] for the mechanism behind most
of the remaining PlanThenExecute/PlanImplementVerify shortfall.

**How to apply**: when citing MinimalGuidance/Disambiguated pass rates, use
these numbers, not the ones in the four memories above (kept for their
qualitative failure-mode findings, which mostly still hold — see
`docs/current/model_eval_pattern_analysis_2026_09_02.md` §5 for which do/don't).
If `AssertFixApplied`'s semantics change again, this table goes stale too —
check the assertion's current behavior before trusting any recorded pass rate
older than a given change.
