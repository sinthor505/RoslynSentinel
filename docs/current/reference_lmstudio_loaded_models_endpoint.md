---
name: reference_lmstudio_loaded_models_endpoint
description: "LM Studio REST endpoint that shows which model instances are actually loaded (vs just downloaded) on a host, with their live config"
metadata: 
  node_type: memory
  type: reference
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-04T00:48:38.204Z
---

`http://<host>:1234/api/v1/models` (LM Studio's own REST API, not the OpenAI-compat
`/v1/models`) lists every downloaded model with a `loaded_instances` array per entry —
empty if not loaded, populated with `id`/`config`/`remaining_ttl_seconds` if it is. This is
the way to confirm which model(s) a host is actually serving before launching a
[[reference_model_eval_procedure]] batch, instead of guessing from the OpenAI-compat
`/v1/models` list (which only shows what's downloaded, not what's loaded — see that doc's
existing warning about this).

Docs: https://lmstudio.ai/docs/developer/rest/list

**Gotcha confirmed 2026-09-03 on `.113`**: multiple quantizations of the same model can be
loaded concurrently (observed `qwen3.5-9b-coder@q4_k_m` AND `@q6_k` both showing
`loaded_instances` at once). The OpenAI-compat endpoint's `model` field in the request body
is what actually routes to the right instance — as long as `ROSLYNSENTINEL_LLM_MODEL`
matches the exact `key`/`id` string, requests reach the intended quant regardless of what
else is also resident. Also worth checking per-instance `config.context_length` here when
comparing runs across quant switches — different loaded instances of the same model can have
different context windows/parallelism configured (e.g. `q4_k_m` was 65536 ctx/parallel 4,
`q6_k` was 32768 ctx/parallel 2 on the same host) which can change failure signatures
(truncation) independent of the quantization itself.
