---
name: search_vendor_issue_tracker_before_reverse_engineering
description: "When a closed-source dependency throws a distinctive error, search its public issue tracker/community for the exact symptom before reverse-engineering its binaries"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 39875bad-f2b5-4b08-9329-704932e3ced1
  modified: 2026-09-07T21:45:52.785Z
---

Before spending effort disassembling/reverse-engineering a closed-source vendor binary to find the
root cause of an error, search that vendor's public bug tracker, GitHub issues, forums, and
Reddit/community discussion for the literal error text or symptom first.

**Why**: in [[project_lmstudio_streaming_json_truncation_investigation]], three subagent passes were
spent exhaustively reverse-engineering LM Studio's webpack-bundled JS (main process, all preload
scripts, ~127 renderer chunks) hunting for the exact `JSON.parse` call site that threw
`"Unterminated string in JSON at position N"`. This confirmed useful supporting facts (the error is
V8-native, not LM Studio's own text; the bug isn't in any literal/greppable JS `JSON.parse` call)
but never found the actual root cause, because the real bug was a documented, already-reported
upstream issue (LM Studio's tool-call parser mis-handling `<think>` reasoning-tag boundaries on
Qwen3.5/3.6 models — lmstudio-ai/lmstudio-bug-tracker#827, #1589, #1592) that a single web search
for the symptom ("LM Studio Qwen tool call JSON think tag") would have surfaced immediately, with a
documented workaround already available (disable `enable_thinking`).

**How to apply**: when an error originates in a well-known third-party tool/app (not obscure
internal code), search for the vendor's issue tracker and community reports for the distinctive
error text or symptom pattern as a first or very early step — before or in parallel with, not after,
deep static/binary analysis. Reserve reverse-engineering effort for cases where no upstream report
exists or the vendor's tracker doesn't cover the symptom. This applies especially to model-serving
tools (LM Studio, Ollama, vLLM) where tool-calling/parsing bugs tied to specific model families
(reasoning tags, chat templates) are common and frequently already tracked.
