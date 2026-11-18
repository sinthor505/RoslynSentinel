# Reference: LM Studio token-usage reporting bugs and which ones affect us

**Status:** CURRENT 2026-11-18. Upstream issues all OPEN as of this date; harness guard checked against `ModelAgentRunner.cs`.

## Purpose

Consult this before treating a backend-reported token count (`InputTokens`, `CachedInputTokens`, the
runner's context warning or a `ContextExhausted` stop) as real. LM Studio misreports usage in known ways;
this lists them, the endpoint each one hits, and whether the RoslynSentinel harness is exposed.

## How it works

| Upstream issue | Endpoint | Symptom | Affects us? |
| --- | --- | --- | --- |
| [#2559](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2559) | `/v1/responses` | One request runs as several internal generation passes and usage sums the prompt (and `cached_tokens`) across them: input reported at 2x-30x the real prompt. Not deterministic; seen on turns with long output. The next request reports normally. | **Yes, probable cause** of our spike (see below). |
| [#2535](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2535) | `/v1/messages` (Anthropic-compatible) | `input_tokens` holds the whole prompt instead of only the uncached part, and `cache_read_input_tokens` repeats the cached prefix, so a spec-following client counts the cache twice on every cache-hit request (a steady ~2x). | No - the harness does not use `/v1/messages`. |
| [#2569](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/2569) | `/v1/messages` | Same bug as #2535 (independent report on MLX, 0.4.27-0.4.28). | No, as #2535. |

- The harness client calls `/v1/responses` and reads `usage.input_tokens` as the full prompt and
  `input_tokens_details.cached_tokens` as the cached part (`RoslynSentinel.Common/LmStudioAgentClient.cs:357`).
  That is correct OpenAI Responses semantics, so #2535/#2569 do not apply to it.
- Our observed case: 10-chain batch Y run 4 (`20261113-004446-932`), T3. One POST in the agent log, yet usage
  read input 53720 (about twice the real ~26k prompt) and output 196 for 6.4k characters of tool-call
  arguments; T4's cached count equalled the real T3 prompt. A second instance: run `113\20261112-031134-405`
  T8 (66381, then 34027). Write-up: [eval_tenchain_reasoning_replay_2026_11_10.md](../evals/eval_tenchain_reasoning_replay_2026_11_10.md).
  Matching it to #2559 is a hypothesis: confirming it needs T3's `CachedInputTokens` (inflated beyond the real
  prompt under #2559) or the LM Studio server log's `Prompt cache restore` lines for that request.
- Harness guard: `ModelAgentRunner.IsUsageJumpImplausible` (`RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs`)
  treats a reported jump larger than twice the estimated added content plus `UsageJumpSlackTokens` (4000)
  as implausible, logs it and uses the estimate for both the context warning and the context-exhausted check.

## Gotchas

- A one-turn `InputTokens` spike that falls back on the next turn is a usage misreport, not context growth
  and not a cache break; check the next turn's `CachedInputTokens` against the real previous prompt.
- `.claude/hooks/context-size.ps1` sums `input + cache_read + cache_creation` (Anthropic semantics). That is
  right for Anthropic's API but would double-count under #2535/#2569 if Claude Code were ever pointed at
  LM Studio's `/v1/messages`.
- When #2559 is fixed upstream, the usage-jump guard can be re-evaluated; until then keep it.
