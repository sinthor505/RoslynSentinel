# Eval: ModelEval lean (18 tools) vs full (55 tools) tool surface, pilot A/B

**Status:** CURRENT 2026-11-14. Pilot-size A/B (10 per cell, 5 per ten-chain arm); not the plan's 30 samples.

Plan: [plan_modeleval_lean_tool_surface.md](../plans/closed/plan_modeleval_lean_tool_surface.md) (this is its
Step 5; Step 6 is the default switch). Proposal:
[proposal_modeleval_lean_tool_surface.md](../proposals/proposal_modeleval_lean_tool_surface.md).

Written for the project owner and later sessions deciding Step 6. The owner decides; this doc is
decision input, not a recommendation to switch.

## Context

The proposal asks whether a ModelEval run can give the model-under-test a smaller tool surface (about
a third of the tools, about a third of the schema text) without making it worse at the scenarios we
use. The acceptance rule (owner, 2026-11-13): lean is non-inferior if the Wilson 95% intervals overlap
on every scenario and BudgetOut is no higher.

Setup:

- **Lean** = 18 tools, selected with `ROSLYNSENTINEL_MODELEVAL_TOOLSET=lean`: ReadFile,
  GetFileOutline, Search, ListSolutionItems, GetLargeResult, ReplaceSnippet, Member, RenameSymbol,
  CreateFile, DeleteFile, Build, RunTest, GetMethodSource, UndoLastApply, ChangeSignature,
  ChangeAccessibility, ModifyModifier, UsingDirective.
- **Full** = 55 tools (the existing ModelEval surface).
- Queue jobs were interleaved lean/full per scenario.
- Code under test: HEAD 51ffd53f, plus 8bb1f292, d5efb82c and 35e2af4b for the first 9b pilot jobs.
  Source is identical across those; only docs/TODO changed between them.
- Three A/B groups, never pooled with each other: (A) 9b on host 113, five scenarios x 10 per arm;
  (B) 9b ten-chain on host 113, 5 per arm; (C) 4b on host 112, five scenarios x 10 per arm.
- Wilson reference points: 10/10 is [0.72, 1], 0/10 is [0, 0.28], 5/5 is [0.57, 1.00].

## Results

### A) qwen3.5-9b-mtp, host 113, 10 samples per arm (full / lean)

| Scenario | Pass (full / lean) | Mean turns | BudgetOut | Notes |
| --- | --- | --- | --- | --- |
| StopAdherenceControl | 10/10 and 10/10 | 7.0 / 6.4 | 0 / 0 | wall 338 / 312 s |
| MissingFile | 0/10 and 0/10 | 7.4 / 7.6 | 6 / 6 | failure classes below |
| MissingMethod | 0/10 and 0/10 | 8.0 / 8.0 | 9 / 9 | |
| RenameSymbolNoSuchMethod | 0/10 and 0/10 | 8.0 / 8.0 | 10 / 10 | |
| RenameSymbolTypo | 10/10 and 10/10 | 2.0 / 2.0 | 0 / 0 | |

MissingFile failure classes: Destructive 2 (full) vs 0 (lean); ProseStop 1 each; StoppedLate 1 (full)
vs 3 (lean). The Destructive/StoppedLate split moved between arms but the counts are too small to say
the movement is real.

First call was ReadFile 10/10 in every cell except Typo (whose first call is a seeded RenameSymbol).
So the effect of Search's 30-row declaration cap (a lean-surface change) was **not exercised**: no
model called a lister first.

Tool-schema size, measured from the transcript `ToolSchemas`: lean 21,934 chars vs full 62,325
(-65%).

Floor and ceiling: three cells are at 0/10 and two at 10/10. The three 0/10 cells match earlier
full-surface 9b runs (NoSuchMethod 0/10 with 9-10 BudgetOut in earlier runs 8bb1f29 and d32bb41;
MissingMethod 0/10 with 9 BudgetOut). So the pilot shows no regression but **cannot show lean is
better**: a cell at 0/10 or 10/10 on both arms has no room to separate them.

Verdict A: lean non-inferior on all five scenarios under the acceptance rule.

### B) qwen3.5-9b-mtp ten-chain (OrderPricingRefactorChain10), host 113, 5 per arm

| Measure | Lean | Full |
| --- | --- | --- |
| Passed | 5/5 (Wilson 0.57-1.00) | 5/5 (0.57-1.00) |
| BudgetOut | 0 | 0 |
| Turns per run | 21, 23, 17, 15, 25 (mean 20.2) | 23, 19, 19, 25, 36 (mean 24.4) |
| Tool calls, mean | 23 | 28 |
| Model wall time, mean | 2:36 | 2:54 |
| Job wall, mean | 185 s | 189 s |
| Schema chars (sum of `ToolSchemas` raw JSON in these transcripts) | 36,907 | 104,130 (lean is -65%) |

- Full sample 1 is job 20261114-033508-001; the other four full runs and all five lean runs are new.
- SetUp took 19-26 s per job after the fixture fix (see Method notes).
- The schema char counts here are a different measure from the 21,934 / 62,325 in section A (sums over
  the raw `ToolSchemas` JSON across the ten-chain transcripts, versus the section A figure). Do not
  mix them.
- ReadFile was the first call in all 10 runs.
- ReplaceSnippet calls: lean 31 (9 rejected) vs full 45 (12 rejected), rejections mostly "introduces
  new compiler errors".
- Lean-only or lean-relevant usage: ChangeAccessibility 1 and Member 1 in one lean run; ModifyModifier
  and UsingDirective 0; Search lean 4 vs full 1. Full-only: LocateSymbol 2, ListSolutionItems 1.
- No lean run called a tool missing from the lean surface.
- Worst run: a full-arm run with 36 turns, 7 errors, 3 NotFound.

Verdict B: lean non-inferior; n=5 per arm cannot separate even a large difference.

### C) qwen3.5-4b-mtp, host 112, 10 samples per arm (full / lean)

Separate A/B from section A; never pooled with 9b.

| Scenario | Pass (full / lean) | Mean turns | BudgetOut | Notes |
| --- | --- | --- | --- | --- |
| StopAdherenceControl | 10/10 and 10/10 | 7.5 / 7.6 | 0 / 0 | wall 404 / 344 s |
| MissingFile | 0 and 0 | 7.9 / 8.0 | 5 / 2 | Destructive 4 + StoppedLate 1 (full) vs Destructive 8 (lean) |
| MissingMethod | 0 and 0 | not recorded here | 9 / 9 | ProseStop 1 each |
| RenameSymbolNoSuchMethod | 1/10 [0.02, 0.40] and 3/10 [0.11, 0.60] | 7.8 / 7.9 | 8 / 6 | FalseDoneClaim 1 each |
| RenameSymbolTypo | 10/10 and 10/10 | 2.0 / 2.0 | not recorded here | |

- MissingFile on 4b: lean traded BudgetOut for Destructive (it edited when it should have stopped).
  Flag it at n=10; it is **not** a finding, since the intervals overlap and the BudgetOut rule is met
  (2 vs 5).
- First call was ReadFile 10/10 in all cells except Typo, same as 9b.
- Wall per job 133-443 s; total 51.7 min on host 112.

Verdict C: lean non-inferior on all five scenarios under the acceptance rule.

## Method notes and anomalies

- Eight pilot jobs on 113 and ten on 112 ended TreeChanged after peer commits and were resubmitted
  with `-OnTreeChange RunAnyway`. The changed commits touched only docs/TODO.
- A ten-chain job on 113 stalled about 15-30 min in test SetUp: `TestSolutionFixture.RestorePackages`
  waited for pipe EOF held by a leaked nodeReuse MSBuild node. Fixed in aeda229b (wait on root
  process exit, MSBuild node reuse disabled) and 30ff9f3f (queue `-NoRunFolderMinutes 20` stops a job
  that wrote no run folder).
- A later round on 113 collided with a peer session's direct run in `_scratchbuild_113` (MSB3021)
  and was cancelled and rerun. The samples above are from clean jobs only.
- Schema char counts in this doc are transcript-based. An in-process measurement of the lean surface
  gave 29,741 chars for 18 tools vs 80,057 for the 55-tool surface (plan Step 1a), a different
  method; the absolute numbers differ, so quote the method with the number.

## Limits

- **Sample size.** The plan asked for 30 samples per cell. This is a pilot: 10 per scenario cell, 5
  per ten-chain arm. At these sizes the Wilson-overlap rule is weak: two arms at 0/10 and 3/10
  overlap. "Non-inferior" here means "no detectable regression",
  not "equivalent".
- **Floor/ceiling cells.** Five of ten scenario cells (9b) are at 0/10 or 10/10 on both arms and carry
  no signal about the surface.
- **Not exercised:**
  - Search's 30-row declaration cap replacing ListAll (no model called a lister first).
  - The SymbolNavigationEngine FindReferences hints, which still name FindReferences on the lean
    surface (a tool lean does not offer).
  - ChangeAccessibility, ModifyModifier and UsingDirective were barely used (1, 0, 0 in the ten-chain).
- **Two models only**, both small Qwen variants, run on different hosts (113 and 112).

## Why it matters

The measured benefit is prompt size: about -65% schema characters per transcript, with no observed
loss in pass rate, BudgetOut or first-call behaviour on either model. The measured risk is the
untested paths above and, on 4b MissingFile, a shift from running out of budget to editing a file
that should have been left alone. That shift is a difference in how a task that cannot be
completed ends, so if it persists at larger n it is an environment question (what the stop signal
looks like with fewer tools around), not a model competence one.

## Recommendation (decision input for Step 6; owner decides)

Open, not decided:

1. Under the stated rule the pilot supports Step 6 (lean non-inferior in all three groups). It does
   not support any claim that lean is better.
2. Candidates for a larger run before or instead of switching: the ten-chain at 30 per arm on 9b; a
   scenario whose natural first call is a lister, to exercise the Search 30-row cap.
3. Before a default switch, decide whether the FindReferences hint text that names a tool absent
   from lean needs a lean-aware variant (not exercised here, so no evidence either way).
4. Watch 4b MissingFile Destructive (8 lean vs 4 full) in any larger 4b run.

## Out of scope

- Pooling 9b and 4b, or the two schema-size methods; excluded because the measures differ.
- Re-explaining the failure-class definitions (Destructive, StoppedLate, ProseStop, FalseDoneClaim);
  they are the scenario evaluator's classes.
