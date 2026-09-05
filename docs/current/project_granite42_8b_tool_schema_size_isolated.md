---
name: project_granite42_8b_tool_schema_size_isolated
description: "granite-4.2-8b's slow/looping behavior isolated to tool-schema size itself (2 tools ~6s vs 48 tools ~88s on the SAME trivial prompt, turnCap:1) — not context growth, ambiguity, temperature, or reasoning budget"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T02:48:53.132Z
---

Direct-call A/B testing 2026-09-05 isolated the actual trigger behind
[[project_granite42_8b_slow_final_turn_not_temperature]]'s slow-turn finding, which had only ruled
things out (temperature, reasoning budget) without identifying a cause.

**Method**: same trivial prompt ("Reply with just the number 4[. Do not call any tools]"), sent
four ways directly to granite-4.2-8b on `.112`:
- `/v1/chat/completions`, 0 tools: 6.7s, clean single-sentence answer, ~17 tok/s.
- `/v1/responses`, 2 synthetic tools (ReadFile/ApplyDiff stubs): 6.0s, correctly abstained from
  calling either.
- `/v1/responses`, 2 tools, ~350-token real code-reasoning prompt: 25.6s for 500 tokens
  (~19.5 tok/s), coherent linear reasoning, no looping.
- **Harness's real `AgentSystemPrompts.CodingAgent` system prompt + 48 real MCP tools
  (`WholeFileRewriteAgentTests.ActiveModes` = Refactor+Workspace), turnCap:1, SAME trivial
  prompt**: **88 seconds**, 1 turn, correct answer ("4"), `Converged: True`.

Turn cap of 1 rules out multi-turn context accumulation as the cause (there was only one
request). The prompt content was unchanged (still trivial). The only variable that changed
was tool-schema size (2 → 48 tools) and the accompanying full agent system prompt. That alone
produced a ~15x slowdown on an otherwise identical, trivial exchange.

**Why this matters**: earlier hypotheses (task ambiguity/planning load, temperature, reasoning
budget, harness bug) are all now superseded for THIS specific symptom — the model's raw
generation is fast and non-degenerate in every direct-call condition tested; the slowness only
appears once the full realistic tool schema is attached, and appears immediately (turn 1, no
warmup needed). This is consistent with prompt-processing cost scaling with the JSON tool-schema
size (or llama.cpp grammar-constrained decoding overhead for a many-function schema on a
tool-use-trained architecture), not with the model "getting confused" by hard tasks or drifting
under context pressure. The 24-30 minute full-harness stalls seen in
[[project_granite42_8b_slow_final_turn_not_temperature]] and the `LiteralSteps` 30-minute-cap
failure (2026-09-05, 6 turns in 30m41s) are very likely this same fixed per-turn tool-schema
overhead compounding across turns, not a separate degenerate-loop phenomenon distinct from tool
schema size.

**How to apply**: don't re-test temperature/reasoning-budget/prompt-ambiguity levers for
granite's speed problem again — that space is closed. If granite's speed needs to improve, the
lead to pursue is reducing the tool-schema surface exposed to it (fewer tools/modes than
Refactor+Workspace's 48), or accepting per-turn latency as a fixed architecture-level cost for
tool-use-schema-attached calls to this model and keeping it out of any latency-sensitive
rotation. Not yet tested: whether schema size scales roughly linearly (e.g. does a 10-tool
schema cost ~20s, a 24-tool schema ~45s) or has a nonlinear knee — a follow-up sweep at 2, 10,
24, 48 tools would confirm before assuming any partial-schema-reduction mitigation is worth the
engineering cost.
