---
name: project_granite42_8b_slow_final_turn_not_temperature
description: "granite-4.2-8b PlanOnly smoke test — 25min single-turn reasoning stall persists at temp=0.1 (off IBM's documented temp=1.0/top_p=0.95 default), ruling out temperature/sampling as the cause; low_effort chat-template param is the untried lever"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-04T10:32:10.414Z
---

granite-4.2-8b's extreme single-turn reasoning time (previously ~27min at temp=1.0, confirmed via
LM Studio `/v1/responses` log timestamps) is **not fixed by lowering temperature to 0.1**. A
`PlanOnly` smoke test rerun at temp=0.1 (192.168.1.112, 2026-09-03) showed turns 1-5 completing
fast (31s-3.5min each) but turn 6 — the final turn where the model commits to and writes out its
plan — still took 24 minutes 52 seconds. Total run: ~30 min wall-clock for 6 turns, converged and
passed this time (vs non-convergence in the earlier temp=1.0 attempt).

**Why:** The earlier hypothesis (temp=1.0 causing excessive reasoning-token sampling) is now ruled
out by direct evidence — same model, same prompt shape, temp=0.1, same-magnitude stall on the same
*kind* of turn (final synthesis/plan-commit), not turns generally. IBM's own Granite-4.2 model card
(confirmed 2026-09-04) actually recommends `temperature=1.0, top_p=0.95` across *all* tasks
including tool calling — so the temp=0.1 test ran off-spec, not more conservative, and the stall
persisting anyway makes the temperature-independence conclusion stronger, not weaker. This looks
like an inherent trait of this model's reasoning behavior specifically on "now produce the final
answer" steps, not a sampling-randomness artifact.

**How to apply:** Don't re-attempt temperature tuning as a fix for granite-4.2-8b's slowness, and
restore temperature to 1.0/top_p 0.95 (the documented default) for any future granite runs — there
was no accuracy/quality reason to run it at 0.1. Per user's standing choice ("pause ladder, report
only"), granite-4.2-8b stays out of the difficulty-ladder rotation — ~30min per single PlanOnly
call is impractical regardless of sampling params. The model card documents a real untried lever
distinct from temperature: `low_effort=True` (alongside `enable_thinking=True`) is a chat-template
parameter for brief reasoning on simpler queries — separate from a token cap. If revisited, check
whether LM Studio's model config exposes reasoning-effort or raw chat-template-parameter
passthrough for this model before trying anything else.
