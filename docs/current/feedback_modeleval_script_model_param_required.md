---
name: feedback_modeleval_script_model_param_required
description: "roslynsentinel-modeleval.ps1's -Model parameter (default 'qwen3.5-9b-coder') always overwrites ROSLYNSENTINEL_LLM_MODEL — a manually-exported env var alone is silently clobbered"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-04T02:01:54.584Z
---

`roslynsentinel-modeleval.ps1` line ~178 unconditionally runs
`$env:ROSLYNSENTINEL_LLM_MODEL = $Model`, where `$Model` defaults to `'qwen3.5-9b-coder'`
(line 117). Manually setting `$env:ROSLYNSENTINEL_LLM_MODEL` before invoking the script does
nothing — the script's own default silently overwrites it.

**Why this matters**: caused a wasted run 2026-09-04 — launched a batch against `.112`
intending to hit a newly-loaded `ibm/granite-4-h-tiny`, set the env var by hand, but the
script re-pointed the request at `qwen3.5-9b-coder` (not loaded on that host), which failed
fast with `"Failed to load model... Engine protocol startup was aborted"` after ~14s. Looked
like a real failure until the request body's model string was checked in the log.

**How to apply**: whenever the target model isn't the script's `qwen3.5-9b-coder` default —
e.g. testing a quantization variant (`@q6_k`) or a different model entirely (`granite`,
`qwen3.5-9b` base, etc.) — always pass `-Model "<exact loaded key>"` explicitly on every
invocation of this script. Confirm the exact key first via
[[reference_lmstudio_loaded_models_endpoint]]'s `/api/v1/models` (its `loaded_instances[].id`,
not a guessed string) before launching, since a mismatched model string fails fast with a
misleading-looking error rather than a clear "wrong model name" message.
