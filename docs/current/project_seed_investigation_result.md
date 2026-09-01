---
name: project_seed_investigation_result
description: "LM Studio's reproducibility seed is model-load-time only (UI, reloads model), not a per-request API field; batch 4/5 variance already happened under one fixed load-seed"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T03:15:54.164Z
---

Investigated whether pinning `seed` could isolate "is the model deterministic given identical
input" from batch-4/5 variance in [[reference_model_eval_procedure]] runs (`Model_FixesWholeFileRewriteBug_PlanThenExecute`).

**Finding:** LM Studio has two unrelated "seed" concepts.
- A per-request JSON `seed` field in `/v1/chat/completions` and `/v1/responses` bodies — sent
  three ways (temp=1.0, same seed=42 twice, seed=999 once) against `/v1/responses` and got
  three *different* outputs. Looked like seed was ignored on that endpoint.
- The actual reproducibility control: a **model-load-time seed** set in the LM Studio UI on the
  model-loading screen. Changing it reloads the model. User confirmed this is what they'd
  actually set (to `1`) — not a request-body field at all.

Retested with the load-time seed pinned to `1` and no per-request `seed` field: three identical
calls (`temperature=1.0`, same prompt, `/v1/responses`) produced **byte-identical** output —
same reasoning text verbatim, same number, same token counts. So reproducibility is real and
controllable, just not via the API body — `LmStudioAgentClient.cs`'s `ResponsesRequest` has no
seed field and doesn't need one; there's nothing to "wire in" at the request layer.

**Why this matters:** as long as the model isn't reloaded between runs, the load-time seed is
already constant for an entire `-Repeats N` batch — including the batch-4/5 runs analyzed
earlier. That means the clean/self-corrected/hopeless variance already observed across those
runs was NOT explained by seed-level RNG differences (seed was constant the whole time,
unknowingly). The variance must come from elsewhere: prompt-to-prompt differences as tool
results feed back into context each turn, and/or ordinary temperature-driven per-token sampling
variance compounding across many turns of a multi-step agent run.

**How to apply:** don't add a `Seed` field to `LmStudioAgentClient`'s request body — it would be
inert on `/v1/responses`. If a future test wants to compare "same load-seed" vs "different
load-seed" runs, that requires actually reloading the model between batches via the LM Studio
UI (or its model-management API, unexplored), not a per-call parameter. The open question of
what actually drives the clean/self-corrected/hopeless variance (context-saturation/attention
hypothesis, still unconfirmed) remains unresolved and does not have a seed-based explanation.
