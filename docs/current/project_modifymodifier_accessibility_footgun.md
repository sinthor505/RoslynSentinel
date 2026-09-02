---
name: project_modifymodifier_accessibility_footgun
description: "ModifyModifier silently accepts an accessibility keyword (e.g. 'public') and only rejects it at call-time with a redirect to ChangeAccessibility. The model reliably self-corrects on the next turn, but because the two calls hit different tools, AssertWithinBudget's per-tool cap never trips while its total cap does — turning ~27 mechanically-correct fixes into scored failures."
metadata: 
  node_type: memory
  type: project
  originSessionId: 18d9cda6-eed8-4198-86a2-eaa21d82eb19
  modified: 2026-09-02T08:34:25.206Z
---

Found during the 2026-09-02 model-eval excavation
(`docs/current/model_eval_pattern_analysis_2026_09_02.md` §2.3). Mechanism:
model calls `ApplyDiff` to switch a call site to the shared helper before
raising its accessibility → real `CS0122` (inaccessible due to protection
level) — a reasonable ordering mistake. Model then calls `ModifyModifier` with
`modifier: "public"`, which is rejected by design:
`"ModifyModifier does not handle accessibility keywords (got 'public'). Use
ChangeAccessibility instead..."`. The model reliably follows this redirect on
the very next turn and succeeds via `ChangeAccessibility`.

**Why this matters**: this is exactly the self-correction retry
`AssertWithinBudget`'s own doc comment says should be tolerated. But the
CS0122 error and the ModifyModifier rejection land on two *different* tools,
so the **per-tool** error cap (2) never trips — while the **total** cap (2)
does, the moment any third, unrelated benign hiccup occurs (almost always a
zero-match `SearchSolutionText`, see [[project_per_tool_error_budget_added]]).
Result: **27 runs across the corpus (~16%, mostly MinimalGuidance — 94% of its
16 mechanically-correct fixes) produce a byte-perfect, functionally correct
fix and still fail the test** purely on this budget interaction, not on any
model or code defect. See [[project_model_eval_baseline_corrected_2026_09_02]]
for how this affects the corrected pass-rate table.

This error is hit by ~50% of MinimalGuidance runs, ~22% of PlanThenExecute,
and widely in PlanImplementVerify implement-phase logs — but 0% in
MinimalGuidanceDisambiguated and ScriptedPlan, only because those variants
either never attempt to raise accessibility at all (Disambiguated mostly
duplicates the helper's body or ignores it instead) or are never asked to
(ScriptedPlan is handed the exact tool to use).

**How to apply**: this is the highest-leverage, lowest-risk fix identified by
the excavation. Two options, either of which would flip ~27 runs from FAIL to
PASS with zero model/prompt change:
1. Loosen/restructure `AssertWithinBudget`'s total cap, or special-case the
   `ModifyModifier`→`ChangeAccessibility` redirect sequence as one logical
   retry rather than two independent tool errors.
2. Fix it at the tool layer: have `ModifyModifier` route accessibility
   keywords to `ChangeAccessibility` internally (or reject earlier/more
   clearly) instead of requiring the model to discover the redirect via a
   failed call every time. Given how load-bearing this exact error message
   is, this option likely raises real pass rates more than any prompt change
   tried so far.

**Update 2026-09-02**: option 2 is already done, and predates this excavation —
commit `a817869` ("Make ModifyModifier's modifier param an enum, excluding
accessibility keywords") changed the `modifier` parameter from a free string to
a `NonAccessibilityModifier` enum. Accessibility keywords like `"public"` are
no longer a constructible argument value at all, so the runtime rejection
message quoted above (`"ModifyModifier does not handle accessibility
keywords..."`) no longer exists in the codebase — the model gets a schema/type
validation failure immediately instead of a call-then-redirect round trip, and
never burns an error-budget slot on it. Stronger than either option above: no
wasted turn, no error-budget interaction, no possibility of a model missing
the redirect. **All 165 runs behind the 27-run figure predate this fix**; a
fresh batch should show this specific bucket at or near zero. No further
action needed here — treat as closed, verify via the next batch's results
rather than more source changes.
