---
name: project_granite42_8b_findings
description: "Consolidated granite-4.2-8b findings: slowness root-caused to tool-schema size (not temperature/reasoning-budget/ambiguity); thinking-disabled and sampling-param levers both tested and closed; model stays out of the ladder rotation"
metadata: 
  node_type: memory
  type: project
  originSessionId: d5de6a52-d839-4902-bb2a-ed367d672eab
  modified: 2026-09-10T06:42:19.967Z
---

Consolidates four separate investigations (2026-09-03 → 2026-09-05) into granite-4.2-8b's
unusable latency on the model-eval harness. Merged 2026-09-09 from
`project_granite42_8b_tool_schema_size_isolated`, `..._slow_final_turn_not_temperature`,
`..._verbose_apply_turn_blows_wallclock`, `..._thinking_disabled_dryrun_loop`.

## Root cause: tool-schema size (2026-09-05, the conclusive test)

Direct-call A/B on `.112`, same trivial prompt ("Reply with just the number 4"), `turnCap: 1`:

| Condition | Wall-clock |
|---|---|
| `/v1/chat/completions`, 0 tools | 6.7s (~17 tok/s) |
| `/v1/responses`, 2 synthetic tools | 6.0s (correctly abstained) |
| `/v1/responses`, 2 tools + ~350-token real prompt | 25.6s / 500 tok (~19.5 tok/s), coherent |
| Real `AgentSystemPrompts.CodingAgent` + **48 MCP tools** (`ActiveModes` = Refactor+Workspace) | **88s** |

Turn cap of 1 rules out context accumulation. Prompt content was unchanged. The only variable
was tool-schema size (2 → 48) plus the full agent system prompt — a ~15x slowdown on an
otherwise identical trivial exchange, appearing immediately at turn 1 with no warmup. Consistent
with prompt-processing cost scaling with JSON tool-schema size (or llama.cpp grammar-constrained
decoding overhead for a many-function schema), **not** with the model "getting confused" by hard
tasks or drifting under context pressure.

## Levers tested and closed — do not retry

- **Temperature / top-p.** A `PlanOnly` smoke test at temp=0.1 (`.112`, 2026-09-03) still stalled
  24m52s on the final plan-commit turn (turns 1–5 were fast, 31s–3.5min). IBM's own Granite-4.2
  model card recommends `temperature=1.0, top_p=0.95` for *all* tasks including tool calling — so
  temp=0.1 ran off-spec, not conservative, and the stall persisting anyway strengthens the
  temperature-independence conclusion. A later combined-levers run at temp=0.0/top-p=1.0 took
  *longer* wall-clock (17m22s vs a 10m38s baseline) to fail the same way. Sampling params are
  orthogonal to this bottleneck.
- **Reasoning budget / prompt ambiguity / harness bug.** All superseded by the schema-size finding
  above — raw generation is fast and non-degenerate in every direct-call condition tested.
- **Disabling thinking** (LM Studio, 2026-09-05). Fixed the symptom it targeted — every turn then
  completed in under a minute, no more 14-minute reasoning blocks — but traded it for a worse
  failure. Implement phase: turn 3 correctly called `ChangeAccessibility`; turn 4 repeated it
  redundantly; turns 6–17 were **12 consecutive `ChangeAccessibility` dry-run no-ops** against an
  already-satisfied target state, ~35–45s apart, until `WallClockCapExceeded after 17 turn(s),
  00:10:29`. With `Reasoning: (none)` on every turn the model had no apparent mechanism to notice
  "I already fixed this, stop retrying." Total wall-clock to failure was about the same either
  way (~10 min), and the on-disk end state was **worse** with thinking off — it never reached
  `ApplyDiff` on `BlockConverter.cs`.

## The verbose-apply-turn pattern (thinking enabled)

Combined-levers run `20260905-040447-961` (`-MinimalTools -Temperature 0.0 -TopP 1.0`,
`Model_FixesWholeFileRewriteBug_PlanImplementVerify`): the plan phase converged on a **fully
correct** root-cause + fix plan (reuse `BlockEditHelpers.ReplaceBlockFormatted`, raise its
accessibility to `internal`, remove `ReformatWholeFile`). The implement phase read both files to
confirm content, then on turn 4 spent **14m28s of reasoning** second-guessing whether
`ChangeAccessibility` preserves the `static` modifier and re-deriving identical file content
repeatedly — before calling the tool, which succeeded in 3.2s. That single turn exceeded the
phase's entire 10-minute cap, leaving the fix half-applied.

Reasoning *correctness* was never in question here. The model had the right plan and was executing
it faithfully; its reasoning-to-action ratio is simply unusually high. This is the same qualitative
pattern as the final-turn stalls, observed on an intermediate apply turn.

## How to apply

Per the user's standing choice ("pause ladder, report only"), granite-4.2-8b stays **out of the
difficulty-ladder rotation** — ~30min per single PlanOnly call is impractical regardless of params.
Don't re-test temperature, top-p, reasoning budget, prompt ambiguity, or thinking-disabled for this
model's speed; that space is closed.

If granite's speed is revisited, the only live lead is **reducing the tool-schema surface** exposed
to it (fewer tools/modes than Refactor+Workspace's 48). Not yet tested: whether schema cost scales
linearly or has a knee — a sweep at 2, 10, 24, 48 tools would confirm before assuming partial
schema reduction is worth the engineering cost. The model card also documents one untried
chat-template parameter, `low_effort=True` alongside `enable_thinking=True` (distinct from a token
cap); check whether LM Studio exposes reasoning-effort or raw chat-template passthrough before
trying it. Secondary ideas: turn-level wall-clock caps, a "stop overthinking, just call the tool"
system-prompt nudge, and feeding `dryRun` no-op results back more assertively ("this change already
matches your target, move on") rather than trusting the model to infer it.

Related: [[project_oldblock_not_found_double_replace_bug]] (a distinct, genuine reasoning-depth
limit), [[project_planimplementverify_promptcontext_solution_preloaded]] (wasted orientation turn),
[[reference_lmstudio_reasoning_budget_message]], [[reference_model_eval_procedure]].
