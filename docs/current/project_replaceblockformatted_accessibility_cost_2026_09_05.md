---
name: project_replaceblockformatted_accessibility_cost_2026_09_05
description: 67.5% of all ApplyDiff failures across 995 historical runs trace to one sequencing gap - calling ReplaceBlockFormatted before fixing its accessibility
metadata:
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T11:15:25.305Z
---

Full writeup: `docs/current/model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md`.

Using [[reference_parse_agent_log_script]] to aggregate `ToolErrorCountsByName` and per-call
`ResultOrError` text across all 995 archived runs under `ModelTestingResults\113`: 566 total
`ApplyDiff` failures, of which **382 (67.5%)** mention `ReplaceBlockFormatted` — 201 as `CS0103`
(name not in scope), 81 as `CS0122` (inaccessible), the rest split across the pre-apply/post-apply
validation guards. 206/995 runs hit this at least once; `PlanImplementVerify` is hit hardest
(126/655 of its runs) — likely because its "implement" phase re-derives execution from the "plan"
phase's prose without the same in-context self-correction opportunity a single continuous run has.

**Why**: this is the quantified cost of [[project_reason_param_reveals_toolchoice_and_selfcorrection]]'s
Finding 2 (models pick `ApplyDiff` over the purpose-built `ChangeAccessibility` tool ~8:1 for the
identical operation) — not a new gap, but proof it's the single largest concrete failure/retry
driver in the whole dataset, not just a stylistic tool-choice quirk. Both compile guards
(pre-apply diagnostic, post-apply compile check) are working as designed; this is a model
sequencing gap the guardrails surface as a visible retry rather than a tool defect.

**How to apply**: when picking the next model-eval lever to pull, this is the highest-leverage
confirmed target — bigger than any single prompt-wording tweak tested so far
([[project_directive_error_messages_wiggle_room_theory]]'s theory remains unconfirmed by
comparison). Three candidate fixes are listed in the full doc (prompt-level sequencing hint,
`ChangeAccessibility` description nudge, compile-guard error-message enrichment pointing at the
specific tool) — none implemented/tested yet. Also demonstrates
[[reference_parse_agent_log_script]]'s value: this required one aggregation pass across the whole
historical dataset instead of another hand-reviewed N-run sample.

See also [[project_modifymodifier_accessibility_footgun]] for the precedent of closing a similar
tool-choice gap at the schema layer (opposite direction — steering *away* from a tool there,
*toward* one here, so the fix mechanism may not transfer directly).
