# Render tool results as plain text for the eval model, so quoted source is not JSON-escaped

**Status:** APPROVED 2026-11-26. Plan: plans/plan_plain_text_tool_results_for_eval_model.md

## Motivation

The eval harness passes each MCP tool result to the model as the raw JSON text of the result
(the transcript's `AgentToolCallRecord.ResultJson`; that this is exactly the text sent is consistent
with run 192447 below but the code path is not yet read - see Open questions). Inside that JSON every plain `"` in a C# file reads as `\"` and
every `\` as `\\`. The Qwen3.5 chat template makes the model write tool-call arguments as raw
`<parameter=name>value</parameter>` text with no escaping, so the model has to translate the
escaped form it reads back into source on every copy. It often does not.

Evidence (all 2026-11-25, host 113 unless noted, Qwen3.5-9B-MTP-GGUF behind the Unsloth wrapper):

- **Smoke runs** (plan `plans/closed/plan_error_gated_thinking_and_client_reasoning_budget.md`,
  Step 11, jobs at 8e02735a): run 20261125-192447 sent `oldContent` ending `.\";` where the file has
  `.";` on every ReplaceSnippet retry from turn 8 to 13, while un-escaping the other quotes on the
  same line. Its reasoning at turn 9 reads "The last line has `\";` instead of `\";`" - two identical
  strings in the form it reads. The ReadFile result it copied from (turn 1) shows the line as
  `return $\"'{name}' ... {string.Join(\", \", candidates)}.\";`.
- **Tool-call format:** the template from `GET /props` (identical on 112 and 113, SHA-256 prefix
  D7075B7BA939042D) instructs the `<tool_call><function=...><parameter=...>` format; a raw
  `/v1/completions` call on the rendered prompt returned exactly that, with raw (unescaped) values.
  A local jinja2 render of that template matches the server's prompt token counts (461 tokens for
  `/v1/responses` without `reasoning`, 459 with `effort: low`), so it is the template in use.
- **Tokenizer** (`/tokenize`): the needed `candidates)}.";` is `[candidates][)}][.";]`; the form the
  model reads, `candidates)}.\";`, is `[candidates][)}.][\"][;]`. The boundaries differ, so copying
  is not token-for-token; emitting `)}.` (a token of the read form) leaves no `.";` token on the path.
- **Probe of this proposal** (`<scratch root>\template_probe\probe_plaintext.ps1`, results
  `probe_plaintext.csv`; temperature 0.7, thinking off, 15 samples per cell; the model gets one
  ReadFile result and must call ReplaceSnippet with text copied from it):

  | Tool result | Format | `oldContent` with a stray `\"` | exact copy of file lines |
  | --- | --- | --- | --- |
  | real ErrorHint.cs result (run 192447, turn 1) | JSON (today) | 15/15 | 0/15 |
  | same | plain text | 0/15 | 15/15 |
  | small synthetic file | JSON | 8/15 | 7/15 |
  | same | plain text | 0/15 | 15/15 |

  "Plain text" was a quick sketch: one `key: value` line per scalar field, nested objects indented,
  multi-line strings as unescaped blocks closed by `(end of <field>)`.
- **Upstream:** a web search found no report of this exact failure; the official Qwen3.5 template
  renders tool messages and parameter values raw, so the escaping is introduced by our result
  format. Coding agents fix the same failure class on the harness side (edit correctors that unescape
  and retry; matchers that reject escaped quotes and ask for a re-read). Report with URLs:
  `<scratch root>\template_probe\research_escape_and_think_leak.md`.

Per the failure doctrine this is an environment defect: the harness shows the model one
representation and requires another.

## Proposal

Decided so far: only the evidence above. Everything below is a starting point for plan-design.

- **Where:** in the agent loop (`RoslynSentinel.AgentLoop`), at the point where a tool result becomes
  the model's tool-output message (`function_call_output` on `/v1/responses`). The MCP tools keep
  returning DTOs as JSON (CLAUDE.md, "Tool responses"); the transcript keeps `ResultJson` unchanged
  for analysis and scoring; only the text sent to the model changes.
- **Shape:** a deterministic JSON-to-text renderer: scalars as `key: value`, objects nested by
  indentation, arrays as items, multi-line strings as raw blocks with an unambiguous terminator;
  no escaping inside blocks. Large-result envelopes (`largeResult`, `GetLargeResult` pointers) and
  error envelopes (`errorData.errorCode`, `message`) need explicit rules.
- **Switch:** an eval setting in the style of `ThinkingOptions` / `LoopAssistOptions` (env var read
  once per ModelEval process, default to be decided), so an A/B arm can measure it before it becomes
  the default.
- **Measure:** the four 113 scenarios against a same-batch baseline, with the escape-copy count per
  run as a direct metric (the probe's counter, applied to transcripts) beside pass rate.

## Alternatives considered, not pursued

- **Unescape-and-retry in the snippet matcher** (accept only when the unescaped text matches exactly
  once). Fixes the symptom for ReplaceSnippet/ApplyDiff only, not for names, `Member` sources or
  other copied text, and it is a semantic auto-correction, which the owner limits to off-by-default
  eval options. Kept as a possible complement.
- **Better error text only** (`SnippetErrorFormatter.DescribeEscapedQuotes` names the characters
  instead of showing them; `// TODO` there, INBOX 2026-11-25 13:40). Worth doing anyway: today the
  hint's "plain quotes (\")" itself arrives JSON-encoded as `(\")`. Alone it is weak: the model named
  the problem in its reasoning and still repeated the slip.
- **Grammar or sampler constraints** forbidding `\"`: breaks legitimate C# string escapes.
- **Switching endpoint or template** (e.g. a JSON-argument tool-call format): out of our control on
  the Unsloth hosts and would move the mismatch, not remove it.

## Open questions

- Scope: eval harness only, or also `SubAgentModelRun` and PlanStepRunner (they build
  `ModelAgentRunner` too)? And should the server offer a text rendering to any MCP client that
  drives a weak model (the mission covers self-hosted models behind other harnesses)?
- Does the renderer help or hurt models that do well with JSON (Ornith, larger models)? Needs an arm
  per model family.
- Which result shapes break a naive renderer: nested lists of hunks or diagnostics, strings that
  contain the block terminator, very large sources, `structuredDetail` arrays?
- Should field names be kept, shortened or dropped for prompt size? Token cost versus JSON is not
  measured.
- Does it change the `</think>` leak or reasoning replay (INBOX 2026-11-25 13:11)? Probably
  unrelated; check in the same A/B.
- The exact code point where `ResultJson` becomes the tool-output message has not been read for this
  proposal; locate it at design time.

## Cost / risk

- Touches only the agent loop and its tests if kept eval-side; transcripts and scoring stay on JSON.
- Risk: a lossy or ambiguous rendering hides a field the model needs (error codes, line numbers,
  paging pointers); the renderer needs round-trip tests on real result shapes.
- Risk: comparisons with earlier eval batches break once it becomes the default; arms must record
  the setting (transcript header, as `ThinkingOptions` does).
- Prompt tool descriptions and hints that quote JSON field paths (e.g. `successData.changedFiles`)
  may read oddly against a text rendering.

## Related

- `plans/closed/plan_error_gated_thinking_and_client_reasoning_budget.md` (Step 11 smoke results)
- `findings/finding_overnight_token_path_slips.md` (the earlier `.O` token-path slip)
- `references/reference_tool_response_pipeline.md` (read before changing response shape)
- INBOX 2026-11-25 13:11, 13:40, 14:05, 14:50; `// TODO` in `SnippetErrorFormatter.DescribeEscapedQuotes`
- Memory: feedback_assistance_balance_semantic_corrections_eval_only
