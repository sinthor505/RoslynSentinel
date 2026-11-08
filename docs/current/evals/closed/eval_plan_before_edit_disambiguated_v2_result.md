---
name: project_plan_before_edit_disambiguated_v2_result
description: "Result of the 2026-09-02 plan-before-edit prompt fix (commit 7bfba2d, porting PlanThenExecute's disambiguating text + plan-before-editing instruction into MinimalGuidanceDisambiguated): 0/20 on .113, but the target failure mode (own-copy-of-helper) actually collapsed from dominant to 1/20 — it was replaced by ApplyDiff thrashing and non-convergence, not by the same bug persisting."
metadata:
  type: project
---

Direct test of [[project_own_copy_helper_dominant_failure]]'s hypothesis: does porting
`PlanThenExecute`'s "state a complete plan before any edit tool call" instruction into
`MinimalGuidanceDisambiguated` (making its prompt textually identical to `PlanThenExecute`'s,
per commit `7bfba2d`) replicate `PlanThenExecute`'s validated 80% mechanical-correctness rate?
20-run batch on `.113`, same night as the prompt change, full clean rebuild beforehand.

**Result: 0/20 pass** — the hypothesis's predicted improvement did not show up in the pass
rate. But the failure breakdown shows this is not simply "the fix didn't work":

| Failure mode | Count | vs. baseline expectation |
|---|---|---|
| [[project_oldblock_not_found_double_replace_bug]] (double-replace runtime throw) | 3/20 | new bug, first seen tonight, also hit PlanImplementVerify |
| `SearchSolutionText` per-tool thrashing (`maxPerTool` exceeded) | 4/20 | **new, root-caused below — model ignores its own system prompt's "don't guess names one at a time" rule** |
| `TurnCapExceeded` at 40 turns, no convergence | 4/20 | new/underexplored |
| `ApplyDiff` per-tool thrashing (`ApplyDiff=5`, genuine repeated failures) | 1/20 | small, not the dominant thrashing bucket (see below) |
| `UnauthorizedAccessException` TearDown (masks an oldBlock-not-found failure, already counted above) | included in the 3 above | file-lock artifact, same as PlanImplementVerify tonight |
| Total error budget exceeded (9 > 8: SearchSolutionText=6, ApplyDiff=2, ReadFile=1) | 1/20 | same SearchSolutionText pattern, just under the total cap instead of the per-tool cap |
| **Own-copy-of-helper** (the ORIGINAL target failure mode this fix aimed at) | **1/20 (5%)** | **down sharply from ~75% of fails pre-fix** |
| Unrelated-method formatting changed | 1/20 | pre-existing failure class |
| Mechanical output mismatch (bad interpolation) | 1/20 | pre-existing failure class |

**Root cause of the `SearchSolutionText` thrashing bucket (4-5/20, the single largest
non-model-defect bucket)**: directly inspected run `20260902-125516-990`'s transcript. The
model searches for a plausible-but-nonexistent symbol name (`ReformatBlock` — a guess, the
real name is `ReformatWholeFile`) five times in a row, alternating `literal`/`regex` search
modes, getting `NoMatches` every single time, before the orientation-breaker (`SearchSolutionText
is DISABLED after 3 consecutive calls returned no matches... You MUST call ListAll...`) finally
fires and redirects it. By then it's already past the per-tool cap. This is not an ambiguous or
borderline case: the system prompt's own rule #3 ("If you don't know the exact name... do NOT
guess plausible names and search for them one at a time... call ListAll first") describes this
exact behavior and the model does it anyway. The orientation-breaker catches it eventually but
only after 3 consecutive failures — by design it can't fire any earlier without false-positiving
on a single legitimate typo/retry.

**Why this matters**: the plan-before-edit instruction appears to have actually worked at
its stated job — own-copy-of-helper dropped from the dominant failure mode (~75% of fails per
[[project_disambiguated_prompt_n20_result]]) to a single occurrence. What it didn't do is
raise the overall pass rate, because two other failure modes expanded to fill the gap:
`ApplyDiff` thrashing (30%) and outright non-convergence (20%). Read together with
[[project_planimplementverify_0of20_root_causes_2026_09_02]] (same night, same host,
`PlanImplementVerify` also came back 0/20 for reasons unrelated to model correctness), the
model's raw correctness rate at the actual bug-fixing task may not have moved much either way
tonight — most of the signal is currently drowned out by harness-adjacent and mechanical
issues (`ApplyDiff` reliability, turn/time budgets, and the oldBlock double-replace bug) that
have nothing to do with the disambiguation/planning wording being tested.

**How to apply**: don't conclude the plan-before-edit lever is ineffective — the one piece of
direct evidence it targeted (own-copy-of-helper) moved exactly as predicted, and it wasn't
displaced by a repeat of the same bug wearing a different name; the displacing failures are
genuinely distinct (a symbol-name-guessing habit, an unrelated runtime-throw bug, and outright
non-convergence). The `SearchSolutionText` thrashing bucket is now the clearest, most
actionable next lever: unlike `AssertWithinBudget`'s cap (a test-side tolerance), this is a
model behavior directly contradicted by an explicit instruction already in its own system
prompt (`CodingAgentSystemPrompt` rule #3) — worth testing whether restating that rule more
forcefully, or triggering the orientation-breaker after 2 consecutive no-match calls instead of
3, meaningfully reduces this bucket, similar in spirit to
[[project_directive_error_messages_wiggle_room_theory]]'s unhedged-language approach. The
`TurnCapExceeded`-at-40 bucket (4/20) still needs its own root-cause pass before acting on it —
unlike the thrashing bucket, its cause isn't yet identified.
