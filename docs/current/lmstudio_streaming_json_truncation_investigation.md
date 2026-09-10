---
name: lmstudio_streaming_json_truncation_investigation
description: "RESOLVED: LM Studio's tool-call parser mis-handles <think> reasoning-tag content on Qwen3.5/3.6 models; confirmed still broken as of 0.4.23 (tracker #1592 open); try Preserve Thinking ON first, else disable enable_thinking; not a RoslynSentinel code fix"
metadata: 
  node_type: memory
  type: project
  originSessionId: 39875bad-f2b5-4b08-9329-704932e3ced1
  modified: 2026-09-07T21:52:04.788Z
---

Secondary finding from a separate qwen3.6-35b ladder investigation: 3/17 overnight .113 ladder
failures were a hard LM Studio streaming abort mid-response — the stream cuts off mid-JSON and
`LmStudioAgentClient` shuts the whole run down immediately with no recovery attempt.

## Root cause (RESOLVED 2026-09-07): known upstream LM Studio bug, not RoslynSentinel-side

The user found a Reddit PSA
(`r/LocalLLaMA "LM Studio's parser silently breaks Qwen3.5 tool..."`) and matching LM Studio bug
tracker issues describing this exact failure class:

- **lmstudio-ai/lmstudio-bug-tracker#827** — "Qwen3 thinking tags break tool call parsing." When a
  Qwen3 model responds with `<think>...</think>` reasoning enabled, LM Studio's tool-call parser
  fails to correctly bound where the thinking block ends and the actual `<tool_call>`/JSON content
  begins — tool calls embedded in/after thinking tags aren't extracted into the structured
  `tool_calls` array the way they are when thinking mode is off. Confirmed on LM Studio
  0.3.21-beta, affects Qwen3 1.7B/0.6B/30B-A3B and GLM-4.5-Air.
- **lmstudio-ai/lmstudio-bug-tracker#1589** — "Qwen3.5 think tags break JSON output" (same class,
  later model generation).
- **lmstudio-ai/lmstudio-bug-tracker#1592** — "Parser scans inside thinking blocks" — possibly
  fixed in 0.4.7b3, unverified per the tracker.
- Reddit PSA (LM Studio 0.4.6-1-x64, Qwen3.5-35B-A3B): reasoning text interleaves with tool-call
  JSON in the raw output stream; Ollama strips reasoning tags before tool-call parsing but LM
  Studio does not, exposing the raw mixed content to the parser. **Documented workaround: disable
  reasoning via `enable_thinking = false`** — this alone resolves the corruption.

**Why this fits our data better than every earlier theory in this investigation**: our 3 known
repros and the 8 total occurrences found were exclusively on `qwen/qwen3.6-35b-a3b`,
`qwen3.5-9b-coder`, and `lmstudio-community/qwen3.5-9b@q4_k_m` — all Qwen3.5/3.6-family reasoning
models that use `<think>` tags. The failure signature (`n_tokens` at release is very high, 32k-35k+,
`truncated=0` — generation completed normally — yet the assembled tool-call JSON buffer is missing
its closing quote/brace, causing V8's native `JSON.parse` to throw
`"Unterminated string in JSON at position N"`) is exactly consistent with the parser incorrectly
tracking the thinking-block boundary and handing a corrupted/misaligned buffer to the JSON parser,
not a truncated generation or a genuine LM Studio buffer-overflow bug.

## What earlier passes in this investigation got wrong or spent effort on unnecessarily

This session initially chased two dead ends before finding the actual explanation above — worth
recording so a future investigation doesn't repeat them:
1. An early subagent pass wrongly concluded the LM Studio server log trail was silent/crashed
   before all 3 failures — it only checked one late log rotation. Directly grepping the whole log
   directory for the literal error string found it immediately in-place — a reminder to verify a
   suspected root cause against the raw evidence before theorizing further.
2. Two further subagent passes exhaustively reverse-engineered LM Studio's shipped JS bundles
   (`index.js`, all preload scripts, all ~127 renderer chunks — every `JSON.parse` call site
   checked) hunting for the exact throw site, on the theory that a Unicode U+2028/U+2029 line
   separator (per anthropics/claude-code#15349) or a generic buffer-overflow bug was responsible.
   This confirmed the error is V8's native JSON.parse error (the string is compiled into
   `node.exe`/`LM Studio.exe`) and narrowed the throw site to LM Studio's native
   `liblmstudio_bindings.node`/`lmstudiocore.dll` inference binding (invisible to JS-level
   grepping) — but this was ultimately a wrong avenue: the actual bug is a **logic error in
   thinking-tag boundary detection**, a known and already-reported issue, not something that
   needed binary reverse engineering to find. A basic web/issue-tracker search for the exact
   symptom would have found this in one step. **Lesson: search the vendor's own public issue
   tracker for the exact error text/symptom before reverse-engineering closed-source binaries.**

**Version status confirmed (2026-09-07)**: user is running LM Studio 0.4.23 (Build 1) — well past
the speculative "possibly fixed with 0.4.7b3" note on #1592. Checked the tracker directly:
**#1592 is still Open**, no fix/version noted. #1589 is closed but with no comments indicating what
fixed it or which version — GitHub closure often just means "workaround accepted," not "root cause
fixed upstream." **Conclusion: the bug is very likely still present in 0.4.23** — there is no
evidence LM Studio has shipped an actual fix as of this version. Upgrading further is not a
reliable mitigation right now; only the documented workaround is confirmed effective.

**Possible alternate/additional fix found by user (2026-09-07)**: a second Reddit PSA
(`r/LocalLLaMA "PSA: Qwen3.6 ships with preserve_thinking..."`) documents a related but distinct
LM Studio/Qwen3.6 bug: with LM Studio's "Preserve Thinking" setting OFF (the default), prior-turn
`<think>` content is stripped from conversation history before the next turn, causing the model to
lose track of what it actually generated earlier — e.g. asked "give me the second number you came
up with" after generating two numbers in one `<think>` block but only surfacing one, the model with
`preserve_thinking: off` falsely claims it never generated a second number, while with it ON the
model correctly recalls and returns the real second value. User confirmed this live with side-by-side
screenshots (Qwen3.6-35B-A3B-8bit via omlx). **This is a different mechanism** than the
tool-call-JSON-corruption bug above — this one is a cross-turn context-continuity failure caused by
stripping reasoning from history, not a same-turn parser boundary error — but both stem from how LM
Studio/this model family handle `<think>` content, and could plausibly interact. **Not yet tested
against our specific "Unterminated string in JSON" failures** — worth trying `Preserve Thinking: ON`
as an alternative or additional mitigation to disabling `enable_thinking` entirely, since it may
avoid the timing/buffering conditions that trigger the parser bug without sacrificing reasoning
quality the way fully disabling thinking would.

## How to apply

1. **This is not a RoslynSentinel bug and does not need a code fix in `LmStudioAgentClient`** for
   root-causing purposes — it's a documented, still-open LM Studio limitation specific to
   Qwen3.5/3.6-family reasoning models during tool-calling, present as of LM Studio 0.4.23.
2. **Two candidate mitigations, not yet compared head-to-head against our specific failure**:
   (a) disable `enable_thinking` entirely (documented fix for #1589/#1592, but sacrifices reasoning
   quality), or (b) enable LM Studio's **"Preserve Thinking"** setting (keeps `<think>` content in
   conversation history across turns — addresses a related but distinct cross-turn continuity bug,
   unconfirmed against the same-turn JSON-corruption failure but worth testing first since it
   doesn't require giving up reasoning). Recommend trying (b) first on a small repro batch before
   falling back to (a), since #1592 remains open with no shipped parser fix either way.
3. Separately, `LmStudioAgentClient` still has no recovery/retry when this specific error event
   arrives — it just shuts the whole run down. Independent of the root cause, adding a
   retry-the-same-turn or reconnect-and-resume strategy for this specific error class is still a
   reasonable resilience improvement so one corrupted turn doesn't fail an entire ladder run.
4. Periodically re-check lmstudio-ai/lmstudio-bug-tracker#1592 and #827 for a real fix landing in a
   future LM Studio release, since disabling thinking is a workaround, not a resolution.
