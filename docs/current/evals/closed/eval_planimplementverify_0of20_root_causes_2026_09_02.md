---
name: project_planimplementverify_0of20_root_causes_2026_09_02
description: "Root-caused why .113 PlanImplementVerify's fresh 20-run batch (post 8-error-budget/10-min-cap raise, commit 5f23641) came back 0/20 despite the report's ~47% baseline prediction: two dominant, distinct, fixable causes, not one."
metadata:
  type: project
---

Investigated after a 20-run batch of `Model_FixesWholeFileRewriteBug_PlanImplementVerify`
against `.113` (post commit `5f23641`'s error-budget/timing raise) came back 0/20, sharply
below [[project_model_eval_pattern_analysis_2026_09_02]]'s ~47% baseline for this variant.
Broke down all 20 `Error Message:` blocks in the batch log — two dominant causes, plus two
minor ones:

**1. Verify-phase `WallClockCapExceeded` at 2 turns — 9/20 runs (45%), THE dominant cause.**
The verify phase's prompt (`VerifyUserPromptTemplate` in `PlanImplementVerifyAgentTests.cs`)
asks the model to confirm the project builds, but — unlike the implement/plan prompts — never
tells it to use the cheapest build level. Several runs called `Build` with `level: "fullBuild"`
instead of `quickBuild`; one observed call took **00:15:03** for a single tool call (vs.
`quickBuild`'s ~0.04s in the implement phase), blowing straight through the (already-doubled)
10-minute wall-clock cap in just 2 turns even though the turn cap (15) was nowhere near hit.
This is a prompt-wording gap, not a budget-sizing problem — doubling the wall-clock cap again
would only delay the same failure. **Fixed**: added the same "use the cheapest build level...
an expensive build can eat your whole time budget" guidance already present in the coding-agent
system prompt to `VerifyUserPromptTemplate` directly (the verify phase uses a separate
`CodeReviewer` system prompt that doesn't inherit that instruction).

**2. `FunctionalFixVerifier: ConvertAbstractClassToInterface threw at runtime: oldBlock not
found in fileText` — 5/20 runs (25%), a real model logic bug, not a harness defect.** The
model's fix pattern: `var rewritten = fileText.Replace(oldHeader, newHeader, ...); return
BlockEditHelpers.ReplaceBlockFormatted(rewritten, oldHeader, newHeader);` — it replaces
`oldHeader` with `newHeader` first, *then* calls `ReplaceBlockFormatted` asking it to find
`oldHeader` inside text where that string no longer exists (already replaced on the line
before). `ReplaceBlockFormatted` throws `InvalidOperationException` at runtime as designed.
This mirrors the exact reference solution's own mistake pattern (see the `implement/transcript`
excerpt for run `20260902-123534-346` in this same investigation — even a byte-perfect-looking
fix used this exact double-replace ordering, but that one happened to still find the string;
here it doesn't). Not fixed — this is a genuine model reasoning gap (conflating "replace the
header text" with "replace the header text via the formatting helper" as two independent
steps instead of one), worth a future prompt experiment (e.g. explicitly warning against
double-processing the same block) but out of scope for tonight's harness-layer fixes.

**3. `TearDown: UnauthorizedAccessException: Access to path 'ContosoOrders.Core.dll' is
denied` — 4/20 runs, always masking a #2 failure underneath (`FunctionalFixVerifier` is
NUnit's "failure 1)", TearDown is "failure 2)" in the same run).** `TestSolutionFixture.Dispose()`
tries to delete the temp solution directory while a build output DLL is still locked (likely a
lingering MSBuild/analyzer process from the verify phase's `fullBuild`, see #1). Not
independently counted — these 4 runs are a subset of #2's 5, not additional distinct failures.
Worth revisiting only if it starts occurring on runs that don't also fail #2/#1, since a
locked-file TearDown failure masking a real assertion result makes triage harder.

**4. `Plan phase did not converge (TurnCapExceeded) within 15 turns` — 1/20 runs.** One-off;
not enough signal yet to act on.

**5. Mechanical `AssertFixApplied` mismatch — 1/20 runs.** `convertedOutput` was literally
`public interface I{className}` (unsubstituted template placeholder) — the model's fix
introduced a bug of its own (mishandled string interpolation/escaping), caught correctly by
the mechanical check exactly as designed. Not a harness or prompt issue.

**Why this matters**: the 0/20 headline number looked like a regression from the report's 47%
baseline, but it was never really about model correctness at that rate — 9 of the 20 (45%)
never got a real correctness signal at all because the verify phase's own tooling choice ate
the time budget. The true "model produced a fix and verify judged it" sample size this batch
was closer to 11/20, of which more than half (6/11) still passed the mechanical+verify bar.
**How to apply**: re-run the 20-run `.113` `PlanImplementVerify` batch now that the verify
prompt's build-level guidance is fixed (commit pending); expect the `WallClockCapExceeded`
bucket to collapse toward zero and the effective pass rate to move back toward the ~47%
baseline, modulo #2's genuine 25%-ish model defect rate on this specific double-replace
pattern. If `WallClockCapExceeded` still occurs post-fix, check whether the model is issuing
`fullBuild` at **solution** scope rather than project scope — that's a second, larger version
of the same mistake this fix targets.
