# Eval client: thinking only after a tool error, and a harness-enforced reasoning budget

**Status:** PROPOSED 2026-11-24. Awaiting design review in a fresh session; nothing built.

## Motivation

Qwen3.5 is the primary model family from 2026-11-24 on (owner decision after the Ornith-1.5-9B runs:
the reasoning model is too slow for the available hardware). Today every Qwen eval run has thinking
off, and Ornith shows what always-on thinking costs.

Measured on the contract A/B runs (`..\RoslynSentinel-ScratchDir\contract_ab_20261124\timing_v2.csv`,
written by `Get-TimingStats.ps1`):

| Host / model | Runs | Runs with any reasoning | Median s per turn |
| --- | --- | --- | --- |
| 85 Qwen3.5-4B | 118 | 0 | ~10 |
| 112 Qwen3.5-9B | 160 | 0 | not split out |
| 113 Ornith-1.5-9B | 55 | 55 (4.5M reasoning chars in total) | 33.5 |

- **Qwen thinks only when the request asks for it.** The eval client sends no `reasoning` field
  (`LmStudioAgentClient.ResponsesRequest`, `RoslynSentinel.AgentLoop/LmStudioAgentClient.cs:904-942`),
  so Qwen never reasons.
- **Ornith reasons on every turn regardless.** About 86% of its reasoning sits in 1-3 decision turns
  per run, where it re-quotes the task, re-derives tool output and drafts code. The reviewers judged it
  mostly correct, but not worth the time at that length (`contract_ab_20261124\review\ornith_a`, `ornith_b`).
- **The host-side cap is the only length limit today.** The cap is about 15k tokens, and the injected
  "... I should answer soon." marks its end. It fires only at the cap, mid-argument. In 4 of 55 Ornith
  runs a turn that started at 92-97% of the 70k context overran the window and the stream was cut
  (INBOX 2026-11-24 12:04).

Probe on 85 (Qwen3.5-4B-MTP), 2026-11-24, `/v1/responses`, one arithmetic question:

| Request field | Reasoning chars | Answer |
| --- | --- | --- |
| none | 0 | wrong |
| `reasoning.effort: none` | 0 | wrong (11:22) |
| `reasoning.effort: minimal` / `low` / `medium` | 12.6k-14.0k | right (16:29) |
| `reasoning.effort: high` | 17.4k | empty: hit the 8000 output-token cap |
| `high` + `reasoning.budget_tokens: 200` | 16.8k | right |
| `high` + `thinking_budget: 200` | 15.8k | right |
| `high` + `reasoning_budget: 200` | 14.2k | right |

The probe settles three facts:
- `reasoning.effort` is an on/off switch per request.
- The effort level does not change the reasoning length.
- No budget field is honoured, so any cap below the host's ~15k must be enforced by the client.

The memory note `reference_unsloth_per_request_sampler_params` records the same facts.

**Where tool errors happen.** Turns that follow a turn with at least one `IsError` tool call, counted
from the same transcripts:

| Host | Runs | Runs with an error | Turns | Turns after an error |
| --- | --- | --- | --- | --- |
| 85 failed | 92 | 83 | 1905 | 498 (26%) |
| 85 passed | 26 | 19 | 362 | 25 |
| 112 failed | 62 | 2 | 595 | 3 |
| 112 passed | 98 | 13 | 1261 | 13 |

- **85:** the 4B model fails through edit retry loops (ValidationFailed/NotFound), so a trigger on
  tool errors reaches exactly those turns.
- **112:** the 9B model's failures were silent. A tool reported success (`ReplaceRange` left
  unindented content, fixed in c8e46785), so a trigger on tool errors would almost never fire on 112.
  This is a hypothesis about the trigger's reach, not a measured effect.
- **113, same 9B model, harder scenarios:** Qwen3.5-9B-MTP on the four Ornith comparison scenarios
  (SweepConstructionSites, ThreadOptionalParameter, WideningRejectedByGate,
  WidenToReadOnlyDictionary; 2 arms x 8 rounds, pinned to c52d49f4, `jobs_v2q9b_113_c52d49f4.csv`,
  `timing_q9b_113.csv`) passed 14 of 64 runs. Ornith passed 40 of 65 on the same scenarios.
  - **Failure mode:** 39 of the 64 runs hit the 25-turn cap (TurnCapExceeded). In those runs,
    255 of 979 tool calls errored (26%). The top error codes were `ReplaceSnippet` ValidationFailed
    (70) and NotFound (64), then `ReplaceRange` InvalidArgument (30) and `Member` InvalidArgument (18).
  - **Not stuck repeating itself:** consecutive calls to the same tool are common (479), but exact
    repeats are rare (33). The model keeps changing the edit and failing again.
  - **Speed:** the median run took 1.7 min at 3.7 s per turn with no reasoning, against 5.0-8.2 min
    per scenario for Ornith.

  So the 9B model's failure mode depends on the scenario: silent on 112's two scenarios, error-driven
  retry loops on 113's four. On the 113 scenarios a tool-error trigger would fire often.

## Proposal

Two independent features. Both are off by default, are eval-only knobs, and are recorded in the
transcript.

### A. Thinking only after a tool error

- **Trigger:** after a turn whose tool calls include at least one error, the next model request sends
  `reasoning: {effort: "low"}`; every other request sends no `reasoning` field (today's behaviour).
  "Error" is the existing `isError` from `ModelAgentRunner.ExecuteToolCallAsync`. That flag covers the
  MCP envelope `IsError`, the body `isError` (`BodyReportsFailure`), malformed arguments and a thrown
  call (`RoslynSentinel.AgentLoop/ModelAgentRunner.cs:985-1029`). The runner already branches on it at
  `ModelAgentRunner.cs:616`.
- **Plumbing:**
  1. The runner decides the setting per turn and passes it to the client, as a new optional parameter
     on `CompleteAsync` beside `replayPolicy` (`LmStudioAgentClient.cs:193`).
  2. The client adds a nullable `Reasoning` property to `ResponsesRequest`, omitted when null so the
     default wire body is unchanged.
- **Configuration:** an environment variable in the style of `ROSLYNSENTINEL_MODELEVAL_CONTRACT`, for
  example `ROSLYNSENTINEL_MODELEVAL_THINKING = off | onError | always`. It is stamped into the
  transcript next to `ReasoningReplayMode` (`AgentTranscript.cs:164`, set at `ModelAgentRunner.cs:348`).
  Each turn also records whether thinking was requested, so a run can be analysed without
  re-deriving the trigger.

### B. Reasoning budget enforced by the harness

- **Cut:** the client counts streamed reasoning while it accumulates `ReasoningTextDeltaEvent`
  deltas (`LmStudioAgentClient.cs:310-319`). That is the same spot where `StreamPhraseLoopDetector`
  already aborts a reasoning stream, so a mid-stream cut has a precedent. When the count passes the
  budget (default 2000 tokens), the client cancels the HTTP request.
- **Counting:** the stream gives no live token count. Use characters / 3.5 as an estimate (Qwen
  tokens average ~2.2-2.5 chars in the probe: 13.1k chars for 5.7k output tokens, so calibrate in the
  design session). Or count deltas, if Unsloth sends one delta per token.
- **Continuation:** a second request for the same turn, with thinking off and the partial reasoning
  carried over, plus one fixed line that tells the model its thinking budget is spent and it should
  now act or answer. How to carry the partial reasoning is the main open question (below).
- **Accounting:** the turn records both requests: reasoning chars, the cut flag, and both latencies.
  `ModelLatency` covers the sum, so the timing stats stay comparable.
- **Configuration:** `ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET = <tokens>` (unset = no client cap).
  It applies to every turn where thinking is requested, under A or `always`.

### Experiment once built

Start with Qwen3.5-9B on the four 113 comparison scenarios: errors are common there, the baseline
is fast (1.7 min per run) and passes only 14 of 64, and Ornith's 40 of 65 gives a reasoning reference
point. Then run 85 (4B) on its retry-loop scenarios (LargeBlockRewrite, RemovePinnedSentence,
SweepConstructionSites). Use the evalqueue, pinned to one commit, standing contract only.

| Arm | Thinking | Budget |
| --- | --- | --- |
| baseline | off | - |
| onError | onError | none (host ~15k) |
| onError-2k | onError | 2000 |
| always-2k | always | 2000 |

Measure for each arm:
- pass rate
- turns
- wall time per run
- retry-loop length (consecutive errors on the same tool)
- reasoning tokens per triggered turn
- how often the cut fires

## Alternatives considered, not pursued

- **Send a budget field.** All three candidate fields are ignored (probe above).
- **Pick a reasoning length with `effort`.** minimal, low and medium gave the same length.
- **Lower the host-side reasoning budget to 2000.** That needs no client work, but it applies to every
  request on the host, including other sessions' runs and the Claude-side subagent on .111. It also
  cannot run in the same batch as a baseline arm, and it keeps the cut-mid-argument behaviour. It is
  still a cheap way to check whether a 2k budget helps at all before building B: run it on one
  dedicated host, with owner approval, since reloading resets the sampler.
- **Use `max_output_tokens` as the budget.** It caps reasoning and answer together, so the turn ends
  as `StreamTruncatedException` with no answer.
- **Move the eval client to `/v1/chat/completions` for `enable_thinking`.** That is a client rewrite,
  and it adds nothing over `reasoning.effort`, which already works.
- **Always-on thinking with no cap.** That is the Ornith cost profile (33.5 s per turn, 4 StreamTruncated
  runs in 55), which the owner rejected for this hardware.

## Open questions

1. **How to carry partial reasoning into the continuation request.** Needs a probe. Candidates:
   - (a) A `ReasoningItem` input item (`LmStudioAgentClient.cs:959`), which reasoning replay already
     uses. Whether the Qwen chat template renders a reasoning item for the current, unfinished turn
     when `effort` is off is unverified.
   - (b) A user-role message: "Your thinking so far: ... Thinking budget spent; act now." This is
     robust, but it changes the conversation shape the transcript shows.
   - (c) Drop the partial reasoning and only add the "act now" line. Cheapest, but it wastes the
     thinking that was done.
2. **Token counting.** Find the chars-per-token ratio, or check whether the delta count equals tokens
   on Unsloth.
3. **Interaction with reasoning replay.** The pressure policy (`pressure:0.7,pinned:3`) replays earlier
   reasoning into later turns. Should reasoning from a triggered turn be replayed, pinned, or dropped
   once the error is resolved? Replaying it uses context, the resource that ran out in the Ornith
   StreamTruncated runs.
4. **Trigger breadth.** Should the trigger also fire on the turn after a refusal hint, a
   `ConcurrentEditRefused`, or the verification turn? On 112's scenarios the failures were silent, so
   an error-only trigger does not reach them; on 113's it would. A trigger on "first turn" or "turn before the final message" is a
   separate arm, if any.
5. **Effort value.** Use `low`, or whichever level is cheapest per the probe; they measured the same.
   Re-check on the 9B model.
6. **The retry exclusion in `CompleteAsync`** (`LmStudioAgentClient.cs:204-208`). A budget cut must
   not count as a transient failure and get retried. Use a dedicated exception type, or a non-exception
   return path.

## Cost / risk

- **Code:**
  - `LmStudioAgentClient` (request DTO, stream loop, cut and continuation)
  - `ModelAgentRunner` (per-turn decision, transcript stamping)
  - `AgentTranscript` (new fields)
  - the eval settings plumbing

  All are in `RoslynSentinel.AgentLoop`; nothing in the server or the tools.
- **Default behaviour:** unchanged while both variables are unset; the wire body stays byte-identical.
- **Risks:**
  - A cancelled stream may leave the host generating until it notices the disconnect, so check that
    Unsloth stops on client cancel and the next request is not queued behind it.
  - The continuation doubles prompt processing for a cut turn (one extra prefill of the whole context).
  - New transcript fields need the parsers (`Parse-AgentLog.ps1`, `Get-TimingStats.ps1`) to tolerate
    their absence in older runs.
- **Eval cost:** a thinking turn without a cap is ~13-17k chars (~5-6k tokens, about 2 minutes at
  ~50 tok/s). On 85's failed runs that is up to 498 extra turns per batch at that size. The 2k cap
  bounds it to roughly 40 s per triggered turn.

## Related

- Memory `reference_unsloth_per_request_sampler_params`: the probe results.
- `docs/current/INBOX.md` 2026-11-24 12:04: Ornith StreamTruncated at a fixed ~15k budget.
- `docs/current/INBOX.md` 2026-11-24 00:50: `LmStudioAgentClient` retries a `status=incomplete` response.
- `docs/current/references/reference_lmstudio_reasoning_budget_message.md`: the host-side budget message.
- `docs/current/references/reference_model_eval_procedure.md`: hosts, env vars, evalqueue.
- Memory `project_token_path_slips_eval_rules`: the eval sampler preset.
