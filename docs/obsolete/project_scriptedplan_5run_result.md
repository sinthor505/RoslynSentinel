---
name: project_scriptedplan_5run_result
description: "ScriptedPlan test (execute a known-correct plan verbatim) went 5/5 on .113, vs ~20-34% on MinimalGuidanceDisambiguated/PlanThenExecute — execution is not the bottleneck, planning/bug-location is"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T03:31:19.387Z
---

Added `Model_FixesWholeFileRewriteBug_ScriptedPlan` (`WholeFileRewriteAgentTests.cs`, commit
`ce1f981`) to test the user's hypothesis directly: take a concrete 3-step plan a real model
already produced correctly in a passing `PlanThenExecute` run (transcript `20260901-005448-159`
— raise `ReplaceBlockFormatted` to `internal`, redirect the call site, delete
`ReformatWholeFile`), hand it to the model verbatim instead of asking it to find the bug and
derive the plan itself, and see if execution alone is reliable.

**Result: 5/5 pass (100%) on `.113`**, run via `roslynsentinel-modeleval.ps1 -HostAddress 113
-Test ScriptedPlan -Repeats 5` (archived under
`ModelTestingResults\113\Model_FixesWholeFileRewriteBug_ScriptedPlan\`). Every run: 0 failed
tool calls, 4-6 turns (vs 6-40 turns for the reasoning-required prompts), `ReplaceBlockFormatted`
correctly raised to `internal`, call site correctly redirected, `ReformatWholeFile` correctly
deleted, unrelated methods byte-for-byte untouched. One run had a harmless one-turn confusion
(model briefly thought it needed to delete something from `BlockEditHelpers.cs`, self-corrected
without making any bad edit) but still converged clean.

Compare to [[reference_model_eval_procedure]]'s baseline numbers for the same fixture/model with
the model doing its own bug-finding + planning: `MinimalGuidanceDisambiguated`/`PlanThenExecute`
land around 20-34% pass rate on `.113` across multiple batches.

**Why this matters:** this is the cleanest evidence yet for the "weak 9B model juggling
bug-finding + planning + implementing degrades" hypothesis the user raised (see
[[project_minimalguidance_reasoning_pattern_analysis]] and
[[project_disambiguated_prompt_n20_result]]) — mechanical execution of a known-correct multi-file
plan is essentially perfect at this model size. The ~70-80% of failures on the harder prompts are
not coming from an inability to reliably apply edits/raise accessibility/call a shared helper;
they're coming from the planning/bug-location phase (misidentifying the fix, re-inventing instead
of reusing, or the ChangeAccessibility-on-helper failure signature noted in
[[project_repeat_penalty_ab_test]]). Also rules out seed/RNG as a factor per
[[project_seed_investigation_result]] — this was a genuinely separate variable.

**How to apply:** when investigating why a run failed, first check which phase it was in
(locating/reasoning about the bug vs. executing an already-decided plan) before assuming it's a
tool-use or ApplyDiff reliability problem — execution-phase reliability is now measured at ~100%
in isolation. If pursuing the plan-then-subagent-implementation redesign the user proposed
earlier (parked, not built), this result is direct supporting evidence that splitting planning
from execution across two calls/contexts should help, since execution alone is not the weak link.
n=5 is still a small sample; a larger ScriptedPlan batch (10-20 runs) would firm up "essentially
100%" into a real number if that precision becomes load-bearing for a future decision.
