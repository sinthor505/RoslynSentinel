# Plan: Eval client thinking on configurable triggers, with an observed reasoning budget

**Status:** IMPLEMENTED 2026-11-25. d1720c89d, 39dbc2700, ed219aa64, 18df7ffc8, 9184930d0, 9d52066e, 734d7dfb, a1f5ced3, 72b28140, b59fb544; smoke test at 8e02735a. Configurable thinking triggers (error, latchRefusal, firstTurn) with effort and an observed-only reasoning budget, then a 12-run smoke test; the enforced cut is parked.

## Problem

Source: [proposal_error_gated_thinking_and_client_reasoning_budget.md](../../proposals/proposal_error_gated_thinking_and_client_reasoning_budget.md).

Qwen3.5 is the primary eval model family. Today the eval client never asks for reasoning, so Qwen
never thinks; Ornith thinks on every turn and costs ~33 s per turn plus context overruns. The
probe on 85 showed `reasoning.effort` is an on/off switch, the level does not change length
(12.6k-17.4k chars when on) and no per-request budget field is honoured, so any cap below the
host's ~15k must be enforced by the client.

Goal: two independent eval-only switches, both off by default, so Qwen gets reasoning where it is
likely to help without the Ornith cost profile:
- A. Thinking only on the turn after a tool error.
- B. A reasoning budget enforced by the harness (cut the stream, continue with thinking off).

It worked when both are built, each turn records whether thinking was requested and whether the
budget cut fired (reasoning size, both latencies), and an eval on the four 113 scenarios shows
whether an onError arm beats baseline (14 of 64) within an agreed wall-time bound.

(Problem as stated at intake. The design changed during plan-design: B became an observed,
unenforced threshold; see the summary below and the Decision log.)

## Decision

**Summary.** Eval-only, off by default, configured by environment variables read once per ModelEval
process:
- `ROSLYNSENTINEL_MODELEVAL_THINKING = off | triggered | always` (default `off`).
- `ROSLYNSENTINEL_MODELEVAL_THINKING_TRIGGERS` = comma list of `error`, `latchRefusal`, `firstTurn`
  (default `error`), read in `triggered` mode: a request thinks when a listed signal holds (the
  previous turn had a tool error, the previous turn hit a loop-latch refusal, this is the first turn).
- `ROSLYNSENTINEL_MODELEVAL_THINKING_EFFORT` (default `low`), sent as `reasoning.effort` on
  thinking requests only; outside the known set it is passed through with a warning.
- `ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET` (tokens, unset = none): observed only. Reasoning
  delta events are counted on every request; a thinking turn over the budget is flagged, never cut.

The settings are always stamped in the transcript header; each turn records whether it thought,
which triggers fired, its reasoning delta count and the over-budget flag; run totals count thinking
and over-budget turns. The wire body is unchanged on every request that does not think. All code is
in `RoslynSentinel.AgentLoop` plus one line in `ModelEvalAssemblySetUp`. The enforced cut is parked
("Deferred: enforced cut").

**Log:**

- 2026-11-25 (intake): goal, non-goals and success criterion as stated in Problem, confirmed by
  the owner.
- 2026-11-25 (intake): both features are tunable without code changes, through environment
  variables.
- 2026-11-25 (intake, corrected the same day): Qwen3.5 produces 13-16k reasoning chars per thinking
  turn by default (probe on 85, 2026-11-24). The budget exists to bring that down to a smaller
  arbitrary value (e.g. 2000 tokens). Early testing checks whether Qwen3.5 can be made to reason
  shorter at all; the owner suspects not, which is acceptable: other models do honour thinking
  budgets, and some take an effort level instead of a number. So the design carries three settings
  (thinking mode, effort, budget) and an eval arm uses whichever ones apply to the model under test.
- 2026-11-25 (intake): no wire change while both features are off, but the effective value of both
  settings is always written to the transcript, including when unset.
- 2026-11-25 (intake): stay on `/v1/responses`; the endpoint choice is reassessed later, outside
  this plan.
- 2026-11-25 Q1 (split): one plan; feature A (thinking on error) steps first, with an optional eval
  hold point after A, then feature B (budget). Why: the settings record and transcript stamping are
  shared by both.
- 2026-11-25 Q13 (scope): ModelEval only. The `ModelAgentRunner` ctor default is Off; ModelEval
  construction passes the settings read from the environment. `SubAgentModelRun` and PlanStepRunner
  are unaffected. Applying the settings everywhere `ModelAgentRunner` is built is a follow-up
  (TODO/INBOX entry at finalize), not part of this plan.
- 2026-11-25 Q5 (trigger breadth): configurable, because the trigger is an axis under evaluation.
  Three triggers, any combination: tool error (the existing `isError`), refusal hint, verification
  turn. Exact definitions of the last two: Q23 (re-scoped by Q24).
- 2026-11-25 Q14 (cut location): inside `LmStudioAgentClient.CompleteAsync` (option A). The client
  cuts, sends the continuation and returns one message plus a cut record; the cut never reaches the
  retry filter. Required observability: a dedicated log line at the cut, a per-turn transcript
  record, and a run-total count. The cut code carries a brief comment that moving the continuation
  to the runner (option B, a typed exception the runner handles) will be needed for more control
  over the stream, citing this plan. Why: retry-safe by construction, testable with one fake
  handler, `RunCoreAsync` (570 lines) does not grow.
- 2026-11-25 Q23 (investigation, explore-tier1): `ModelAgentRunner` injects no refusal hint. The
  only refusal is the loop latch: a tool result with `ToolErrorCode.TargetIneligible` whose message
  starts with `<tool> is DISABLED until another tool succeeds` (`ToolLoopLatch.cs:36,194`, counted at
  `ModelAgentRunner.cs:709-715`); it is already an `isError` result. `ConcurrentEditRefused` is a
  tool error code (`SentinelCallToolResult.cs:189`), also an `isError` result. There is no
  verification turn: the loop ends when the model returns no tool calls (`ModelAgentRunner.cs:542-583`);
  final-state verification runs after `RunAsync` in scenario code (`ScenarioAgentTests.cs:174-185`).
  The per-turn error aggregate belongs inside the tool loop (`turnHadError |= isError` after
  `ModelAgentRunner.cs:600`), because the loop can `break` early.
  `ModelAgentRunner` construction: 16 ModelEval sites, 11 Harness, 3 ErrorVariants, no shared
  factory; production `SubAgentModelRun.cs:210`, PlanStepRunner `Program.cs:220`.
- 2026-11-25 Q24 (trigger set): three triggers, any combination: `error` (any `isError` tool call in
  the previous turn), `latchRefusal` (the previous turn hit a loop-latch refusal,
  `ModelAgentRunner.cs:709`), `firstTurn` (the run's first model request). An injected verification
  turn (thinking on a check turn when the model tries to finish) is a follow-up (INBOX/idea at
  finalize), not part of this plan.
- 2026-11-25 Q12 (wiring): a named options record read from the environment, set once as a static
  default in `ModelEvalAssemblySetUp.OneTimeSetUp` (the `ContractStamp` precedent,
  `ModelEvalAssemblySetUp.cs:57`). The `ModelAgentRunner` ctor takes an optional parameter and
  resolves `parameter ?? static default ?? Off`. Production callers stay Off.
- 2026-11-25 Q4 (replay): no special handling; the existing pressure replay policy treats reasoning
  from thinking turns like any other. Revisit as an arm if context exhaustion rises.
- 2026-11-25 Q15 (results CSV): transcript only (header stamp plus per-turn record); no CSV column.
  Arms are told apart by `job.env`, as `Get-EditArmTable.ps1` does.
- 2026-11-25 Q21 (measurement): a fresh baseline arm in the same batch at the plan's pinned commit.
  First a smoke test on 1-2 scenarios the owner names, before committing to a long batch; the full
  stage-1 size is decided after the smoke test. Thresholds, each scored and reported independently
  (no combined verdict; a tighter review comes later): keep-signal = pass rate at least baseline
  +15 pp; wall-time bound = median wall per run at most 3x baseline; park-signal = TurnCapExceeded
  down by at least a third; drop-signal = pass gain at most +5 pp. Outcome names: see Q30.
- 2026-11-25 probes (host 113, Qwen3.5-9B-MTP-GGUF, llama-server, 36 requests; bodies, scripts and
  CSVs in `<scratch root>\plan_design_error_gated_thinking\`):
  - Q11: Qwen3.5-9B cannot be steered shorter reliably. `/v1/responses` effort low: 8.4-9.7k
    reasoning chars (3 runs). A length instruction in the system message: 10.8k, 12.7k (the model
    quotes it and carries on). The same instruction in the user message: 4.1k, 2.4k, 8.7k, 10.7k.
    Effort `none` = off; `high` (4.4k) was no longer than `low`. `/v1/chat/completions` with
    `enable_thinking: true` and each of `thinking_budget_tokens`, `reasoning_budget`,
    `chat_template_kwargs.thinking_budget`, `max_reasoning_tokens` = 200: 9.9-10.2k chars, no
    effect. `/props`: `supports_reasoning_effort: false`, no budget field. The client cut is the
    only lever below the host cap.
  - Host caps differ: 113 stops reasoning at ~3,850-3,880 tokens (5 of 10 low runs hit it, ending
    in the injected "... I need to state my answer and act..."); 85 stops at ~15k ("... I should
    answer soon."). 112 not measured (Q25).
  - Q3 (counting): `/v1/responses` usage has no reasoning-token field (only `input_tokens`,
    `output_tokens`, `cached_tokens`). One `response.reasoning_text.delta` event is ~one token
    (0.91-0.97 deltas per estimated reasoning token); chars per token 2.1-3.3, mostly 2.5. Decision:
    the budget is in tokens, counted live as reasoning delta events; no chars estimate.
  - Q20 (cancel): cutting at ~2,000 chars (~10.5 s in) and sending the next request at once delays
    its first event by a steady ~2.0 s (control 27-101 ms; 37 ms after a 3 s wait), across 3 rounds
    plus an earlier run. `/slots` and `/metrics` return 401. Implementer default: no wait before the
    continuation; the ~2 s shows in the continuation latency the cut record carries.
  - Q2 (partial reasoning): a reasoning input item followed by a user message is not rendered
    (input tokens 123, same as dropping it); the chat template renders an assistant turn's reasoning
    only after the last user message (`loop.index0 > ns.last_query_index`). Partial text inside a
    user message renders (+~825 tokens). A reasoning item followed by an assistant message (no user
    message after it) renders (994 tokens). Tool-result items do not count as user messages. Choice
    of variant: Q2, open.
- 2026-11-25 Q21 (smoke candidates, owner table of the 113 comparison runs at c52d49f4):
  SweepConstructionSites Qwen 1 of 16 vs Ornith 12 of 16 (median 1.5-1.7 vs 5.4 min);
  ThreadOptionalParameter 2 of 16 (+2 passed after the cap) vs 11 of 15 (1.4-2.1 vs 7.4);
  WideningRejectedByGate 2 of 16 vs 7 of 18 (2.6-3.8 vs 8.2); WidenToReadOnlyDictionary 9 of 16
  (+3 after the cap) vs 14 of 16 (1.4-1.6 vs 5.0). Totals 14 of 64 vs 40 of 65; the proposal's
  "40 of 65" comes from this per-scenario table, not from `timing_v2.csv` (40 of 55), so Q19 keeps it.
- 2026-11-25 Q27 (names): `ROSLYNSENTINEL_MODELEVAL_THINKING = off | triggered | always` (default
  `off`); `ROSLYNSENTINEL_MODELEVAL_THINKING_TRIGGERS` = comma list of `error`, `latchRefusal`,
  `firstTurn` (default `error`; read only in `triggered` mode). Unknown mode or trigger values fail
  fast when the settings are read, with a message naming the variable, the bad value and the
  allowed values.
- 2026-11-25 Q9 (effort): `ROSLYNSENTINEL_MODELEVAL_THINKING_EFFORT`, default `low`, sent as
  `reasoning.effort` only on requests where thinking is requested. Validated against the known set
  `none, minimal, low, medium, high, xhigh` but passed through when outside it, with a logged warning
  once per run: `Warning: Custom thinking_effort value <x> was specified - ensure it is valid for the
  active model <model>`. Empty or whitespace fails fast.
- 2026-11-25 Q8 (budget values): `ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET` = tokens; unset means
  no budget. Smoke-test arm value 2000 (about half of 113's host cap). Retuned by env var only.
- 2026-11-25 Q10 (delivery): both a client-side cut and a request field are supported.
- 2026-11-25 Q28 (field): `ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET_DELIVERY = cut | field | both`
  (default `cut` when a budget is set) and `ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET_FIELD` = dotted
  JSON path (e.g. `reasoning.budget_tokens`, `thinking_budget_tokens`,
  `chat_template_kwargs.thinking_budget`), required when delivery includes `field` (fail fast
  otherwise). Sent only on thinking requests, value = budget tokens; a `reasoning.*` path merges into
  the object that carries `effort`. Inserted by serializing the request to a `JsonNode` and setting
  the path, only when a path is configured, so the default body stays byte-identical.
- 2026-11-25 Q21 (smoke set): SweepConstructionSites and ThreadOptionalParameter (widest Qwen vs
  Ornith gaps, cheapest Qwen runs).
- 2026-11-25 Budget rework (owner, supersedes Q10, Q14's cut half and Q28's `cut`/`field`
  delivery): no enforced budget for now. Why: Qwen3.5 cannot be steered shorter, the cut breaks the
  stream and needs unproven workarounds (template quirk, ~2 s cancel penalty, extra prefill), and
  the host already caps reasoning at ~3.9k tokens on 112/113. The budget becomes an observed
  threshold: every thinking turn counts reasoning delta events (needed anyway: `/v1/responses`
  reports no reasoning-token count); when `ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET` is set, a turn
  over it is flagged ("would have cut at N") in its per-turn record and in a run-total count, never
  cancelled. The threshold code carries a comment that it is an inactive, experimental feature of
  unknown value that can be removed if it causes problems. The request-field delivery is deferred
  with the cut. The enforced cut-and-continue design and its probe facts move to "Deferred:
  enforced cut" below.
- 2026-11-25 Cut code (owner): the enforced cut is not built, not even behind a gate. The
  observed-threshold spot in the stream loop carries a comment pointing at "Deferred: enforced cut"
  in this plan; a TODO.md line points there too. Why: an unexercised path rots silently (its key
  assumption is the host's chat template), and gating it saves no build or maintenance cost.
- 2026-11-25 Readiness review 1 fixes (design-level): `Decide` takes the `ThinkingTriggers` flags
  of the conditions that hold (no three-bool signature); the runner accumulates the next turn's
  signals in one `pendingSignals` local, read and reset at the top of each turn (no end-of-turn copy,
  correct on the `continue` path at `ModelAgentRunner.cs:575`); `firstTurn` means the first live
  request of the run, so resumed runs also get it; allowed setting names come from the enum names;
  both ctors take `thinking`; runner tests use an `IAgentToolHost` fake; the runner work is split
  into wiring (Step 6) and loop (Step 7); run totals moved before the runner.
- 2026-11-25 Simplicity (checklist item 7): the simplest version is mode on/off plus effort, with no
  triggers and no budget; it needs no enabling refactor. Triggers exist because the trigger is the
  axis under evaluation (Q5); the observed budget costs one counter and one comparison and feeds the
  "reasoning tokens per thinking turn" metric that `/v1/responses` does not report. Every setting
  names its user: an eval arm. Nothing else is added.
- 2026-11-25 Q30 (outcome names): the Q21 signals use the names the smoke scenarios actually emit in
  `step-<k>.outcome.txt`: pass = `outcome=Pass` (`PassedAfterCap` reported beside it, not in it, as in
  the owner's tables); park-signal = runs with `stopReason=TurnCapExceeded`. The earlier names
  (Passed, WrongFinalState) do not exist.
- 2026-11-25 Readiness (plan-design): independent review clean at 5ba43b82. Three sonnet reviews;
  round 3 left one blocker (`TranscriptFileOptions` is private, so the serialization test moved to
  Step 8 and Step 6 makes the field internal) and three mismatches (exact invalid inputs, one
  outcome file per run directory, list logger records the level), each fixed as the reviewer
  specified, with no re-review of those edits.
- 2026-11-25 Q29 (smoke size): 2 rounds per scenario per arm; arms baseline, `triggered` with
  `error`, `always`, all with no budget threshold enforced: 12 runs over hosts 112 and 113.
- 2026-11-25 Q25 (host caps, probe): host 112 caps reasoning at 3,851-3,881 deltas with the same
  injected "... I need to state my answer and act..." as 113 (2 runs, a scheduling puzzle, effort
  low; `/props`: unsloth-studio v0.1.923-beta, Q4_K, 1 slot, no budget field). The cap lives in the
  Unsloth wrapper. 112 and 113 are interchangeable for the 9B arms; 85 (~15k cap) is not, so
  stage-2 runs on 85 are compared only with 85.
- 2026-11-25 (implement-plan): staleness check clean (no commits since 5ba43b82 touch the plan's
  files). Owner away during the run; Batched mode: batch A = Steps 1-5, batch B = Steps 6-9, then
  Steps 10-11 from the main session.

## Execution rules

- All C# reads and writes through the RoslynSentinel MCP tools (CLAUDE.md, Dog-fooding). Each slice
  ends with `Build` 0 errors. One `Build`/`RunTest` at a time; the test baseline is 0 failures.
- Every commit carries `Plan: plan_error_gated_thinking_and_client_reasoning_budget` above the
  `Co-Authored-By` trailer.
- Wire identity: no caller passes `thinking` to `CompleteAsync` before Step 7, and Step 7 passes it
  only on turns that think; a request that does not think sends exactly today's body.
- Namespaces: `LmStudioAgentClient`, `AgentChatMessage`, `ReasoningReplayPolicy` are in
  `RoslynSentinel.Common`; `AgentTranscript`, `ModelAgentRunner`, `LoopAssistOptions` are in
  `RoslynSentinel.Common.AgentLoop`. The new `ThinkingOptions.cs` uses `RoslynSentinel.Common` so the
  client sees it without a new using.
- No server restart or live server check is needed: nothing here changes behaviour while the
  settings are unset, and `SubAgentModelRun`/PlanStepRunner stay Off (Q13).
- New DTOs carry the `NEW-DTO:` block given in their step (dto-conventions skill) in the creating
  call's `reason` and in the commit message.

### Test table

All in-process in `RoslynSentinel.Tests.Harness` (namespace `RoslynSentinel.Tests.ModelEval`, as the
neighbouring files). No out-of-process work. Asserts on typed fields, never on text, except the
warning test, which compares against the shared format constant. Fixtures (the existing ones are
private, so each new test file carries its own small copies):
- **Scripted model:** a private `HttpMessageHandler` that records request bodies and answers each
  request with an SSE stream of N separate `response.reasoning_text.delta` events, then the turn's
  content or tool calls and a `response.completed` event. Copy the shapes from `ReplayScriptedHandler`
  (`ReasoningReplayRunnerTests.cs:208`, which emits only one reasoning delta per turn) and
  `SseResponse`/`NewClient` (`LmStudioClientRetryTests.cs:121,143`).
- **Tool host (runner tests):** a private `IAgentToolHost` fake returning scripted results per call,
  as `FakeToolHost` (`Tests.ErrorVariants/RunnerToolHostTests.cs:409`) and `RecordingToolHost`
  (`FilteringToolHostTests.cs:108`) do; not `ReasoningReplayRunnerTests.NewRunner`, which runs a real
  in-process MCP server that cannot return errors. An error result has `IsError = true`. A latch
  refusal is `IsError = true` with body
  `{"errorData":{"errorCode":"TargetIneligible","message":"<tool>` + `ToolLoopLatch.RefusalMarker` + `..."}}`
  (shape read by `ToolLoopLatch.RefusalMessage`, `ToolLoopLatch.cs:194-215`; `RefusalMarker` is a
  public const at `:36`).
- **Runner:** the `IAgentToolHost` ctor (`ModelAgentRunner.cs:161`) with `LoopAssistOptions.Off`,
  a `repeatedFailureLimit` above any error streak in the script (e.g. 10), and
  `LlmOptions.Configure(["--llm-model", ...])` first, as `ReasoningReplayRunnerTests.NewRunner`
  (`:179`) does. The scripted handler answers the models endpoint with 404, as `ReplayScriptedHandler`
  does (context length unknown, so no context-exhaustion path).
- **Nudge turn:** script it as `MalformedToolCallRunnerTests.cs` does: build the tool-call markup by
  string concatenation (`:34-36`), because a literal is rejected by the server's edit guard.
- **Log capture:** a private `ILogger<ModelAgentRunner>` list logger, as `ListLogger`
  (`LoopAssistRunnerTests.cs:383`).
- Why copies and not a shared fixture: the existing ones are private to their classes and each
  runner test file in this project already carries its own (8 `NewRunner` helpers); extracting a
  shared fixture project-wide is outside this plan (checklist item 3, reuse considered and declined).

| Test (class.method) | Behaviour | Asserts | Pass |
| --- | --- | --- | --- |
| `ThinkingOptionsTests.FromEnvironment_Unset_ReturnsOff` | defaults | `Mode == Off`, `Triggers == Error`, `Effort == DefaultEffort`, `ReasoningBudget == null` | all equal |
| `ThinkingOptionsTests.FromEnvironment_AllSet_ParsesEach` | parsing | `triggered`, `error,latchRefusal,firstTurn` (mixed case, spaces), `medium`, `2000` map to the enum flags, effort, budget | all equal |
| `ThinkingOptionsTests.FromEnvironment_InvalidValue_Throws` (TestCase per variable: mode `sometimes`; trigger `bogus`; trigger `1`; trigger list `","` and `" , "`; effort `"   "`; budget `0`, `-5`, `abc`. Note `""` is treated as unset for every variable except effort, where it is also invalid) | fail fast | `InvalidThinkingSettingException.Variable` equals the variable's constant | throws, Variable matches |
| `ThinkingOptionsTests.FromEnvironment_CustomEffort_PassesThrough` | Q9 | `Effort == "turbo"`, `IsKnownEffort == false` | both |
| `ThinkingOptionsTests.Decide_Matrix` (TestCase rows) | Q5/Q24/Q27 decision | Off: `Requested == false`; Always: `Requested == true`, `FiredTriggers == None`; Triggered: `FiredTriggers == Triggers & signals`, `Requested == (FiredTriggers != None)` | every row |
| `ThinkingOptionsTests.VariableNames_ArePinned` | names are the job.env contract | each `*Variable` constant equals its documented name (pin-test precedent `LoopAssistTrackerTests.cs:495`) | all equal |
| `ThinkingRequestBodyTests.CompleteAsync_WithoutThinking_HasNoReasoningKey` | Q22 wire identity | parsed body has no `reasoning` property | absent |
| `ThinkingRequestBodyTests.CompleteAsync_WithThinking_SendsEffort` | effort on the wire | `reasoning.effort == options.Effort` | equal |
| `ThinkingRequestBodyTests.CompleteAsync_CountsReasoningDeltas` | delta counting | 3 `response.reasoning_text.delta` events give `ReasoningDeltaCount == 3`; none gives `null` | both |
| `ThinkingRunnerTests.Triggered_Error_ThinksOnlyAfterErrorTurn` | trigger `error` | request 2 (after an erroring turn) has `reasoning`, requests 1 and 3 do not; turn 2 `Thinking.Requested`, `FiredTriggers == Error` | all |
| `ThinkingRunnerTests.Triggered_LatchRefusal_ThinksAfterRefusal` | trigger `latchRefusal` | with `Triggers = LatchRefusal` only (a refusal is also an error): after a turn whose tool result is a latch refusal, the next request has `reasoning` and that turn's `FiredTriggers == LatchRefusal`; after a plain error turn, the next request has none | all |
| `ThinkingRunnerTests.Triggered_FirstTurn_ThinksOnFirstRequestOnly` | trigger `firstTurn` | only request 1 has `reasoning` | all |
| `ThinkingRunnerTests.Always_ThinksOnEveryRequest` | mode `always` | every request has `reasoning` | all |
| `ThinkingRunnerTests.Off_NoReasoning_HeaderStamped` | Off + header always present | no request has `reasoning`; `transcript.Thinking.Mode == Off`; every turn's `Thinking == null` | all |
| `ThinkingRunnerTests.Serialize_WritesEnumNames` | name-valued enums (the converter's reason) | a transcript carrying `ThinkingOptions` (mode `Triggered`, triggers `Error, FirstTurn`), serialized with `ModelAgentRunner.TranscriptFileOptions` (made internal in Step 6): the `Mode` property's JSON value is the string `Triggered`, the `Triggers` value is a string naming both flags; it deserializes to an equal record | both |
| `ThinkingRunnerTests.Triggered_HeaderEqualsOptions` | header carries the effective settings | `transcript.Thinking` equals the `ThinkingOptions` passed to the ctor | equal |
| `ThinkingRunnerTests.Triggered_Error_SkipsNudgeTurn` | signals belong to the previous turn only | `Triggers = Error`: turn 1 tool call errors; turn 2's response has no tool calls but tool-call markup in its content (the malformed-markup nudge, `:575` path); request 2 has `reasoning`, request 3 does not | both |
| `ThinkingRunnerTests.Budget_OverBudgetFlaggedNotCut` | observed budget | mode `Always`, budget 2, two turns: turn 1 streams 3 reasoning deltas, turn 2 streams 1. Turn 1 `OverBudget == true`, `ReasoningDeltas == 3`; turn 2 `OverBudget == false`, `ReasoningDeltas == 1`; exactly 2 model requests (no extra request); `RunTotals.OverBudgetTurnCount == 1`, `RunTotals.ThinkingTurnCount == 2` | all |
| `ThinkingRunnerTests.CustomEffort_WarnsOnce` | Q9 warning | (the list-logger copy also records the `LogLevel`) exactly one Warning-level entry equal to `string.Format(ThinkingOptions.CustomEffortWarningFormat, effort, model)` across a multi-turn run | count 1 |
| `ThinkingRunnerTests.CtorWithoutThinking_UsesStaticDefault` | Q12 | with `ModelAgentRunner.ThinkingDefault` set to Always and no ctor arg, request 1 has `reasoning`; static reset in `finally` | true |
| `ThinkingRunnerTests.Transcript_WithoutThinkingFields_Deserializes` | Q18 | old JSON gives `Thinking == null` on transcript and turn | both null |

`ThinkingRunnerTests` mutates a static: mark the class `[NonParallelizable]` with a comment saying why.

## Steps

### Step 1 - Contract skeleton
- Files: `RoslynSentinel.AgentLoop/ThinkingOptions.cs` (new), `RoslynSentinel.AgentLoop/AgentTranscript.cs`,
  `RoslynSentinel.AgentLoop/LmStudioAgentClient.cs`
- Depends on: none
- Effect: text-neutral (new types and nullable properties, nothing reads or writes them)
- Lane: `implementer`
- Change:
  - `ThinkingOptions.cs` (new, namespace `RoslynSentinel.Common`, one `CreateFile`):
    - `public enum ThinkingMode { Off, Triggered, Always }` and
      `[Flags] public enum ThinkingTriggers { None = 0, Error = 1, LatchRefusal = 2, FirstTurn = 4 }`,
      both with `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` so transcripts show names, and a
      comment marking it an intentional deviation (the other transcript enums, e.g. `AgentStopReason`,
      serialize as numbers): these values are read by humans and by `Get-ThinkingArmTable.ps1`.
    - `public sealed record ThinkingOptions(ThinkingMode Mode, ThinkingTriggers Triggers, string Effort, int? ReasoningBudget)`
      with constants `ModeVariable = "ROSLYNSENTINEL_MODELEVAL_THINKING"`, `TriggersVariable =
      "ROSLYNSENTINEL_MODELEVAL_THINKING_TRIGGERS"`, `EffortVariable =
      "ROSLYNSENTINEL_MODELEVAL_THINKING_EFFORT"`, `BudgetVariable =
      "ROSLYNSENTINEL_MODELEVAL_REASONING_BUDGET"`, `DefaultEffort = "low"`,
      `CustomEffortWarningFormat = "Warning: Custom thinking_effort value {0} was specified - ensure it is valid for the active model {1}"`;
      `public static IReadOnlyList<string> KnownEfforts { get; } = ["none", "minimal", "low", "medium", "high", "xhigh"];`
      `public static ThinkingOptions Off { get; } = new(ThinkingMode.Off, ThinkingTriggers.Error, DefaultEffort, null);`
      and stubs `public static ThinkingOptions FromEnvironment(Func<string, string?>? readVariable = null)`,
      `[JsonIgnore] public bool IsKnownEffort`, `public TurnThinking Decide(ThinkingTriggers signals)`
      (`signals` = the trigger conditions that hold for this request)
      with bodies `throw new NotImplementedException(); // TODO implement (Step 2)`.
    - `public sealed record TurnThinking(bool Requested, ThinkingTriggers FiredTriggers, int? ReasoningDeltas, bool? OverBudget)`.
    - `public sealed class InvalidThinkingSettingException(string variable, string value, IReadOnlyList<string> allowed) : Exception`
      with `Variable`, `Value`, `Allowed` properties and a message naming all three (DTO-EXEMPT: exception).
  - `AgentTranscript.cs` (one `batchEdits` call): `AgentTranscript.Thinking` (`ThinkingOptions?`) after
    `ReasoningReplayMode` (`:163-167`), and `AgentTranscriptTurn.Thinking` (`TurnThinking?`) after
    `Assists` (`:284-288`), both with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`
    like their neighbours; `RunTotals.ThinkingTurnCount` and `RunTotals.OverBudgetTurnCount` (`int`).
  - `LmStudioAgentClient.cs` (one `batchEdits` call): `AgentChatMessage.ReasoningDeltaCount` (`int?`,
    WhenWritingNull) beside `ReasoningTokens` (`:1268`); add the trailing optional parameter
    `ThinkingOptions? thinking = null` to `CompleteAsync` (`:193-198`; 14 call sites plus one doc
    cref, all positional with 3-5 args, 0 edits), not yet used.
  - NEW-DTO: ThinkingOptions (Options, AgentLoop.ModelEval). Evaluated: LoopAssistOptions - a
    different, independently evaluated axis (injection role); ReasoningReplayPolicy - per-run replay
    policy derived from context length, not request settings. Wire impact: none (transcript JSON only).
  - NEW-DTO: TurnThinking (Item, AgentLoop.Transcript). Evaluated: AgentAssist - harness
    interventions keyed by a kind, not a per-turn request decision; AgentChatMessage - model output,
    not harness decisions. Wire impact: none (transcript JSON only).
- Done when: `Build` 0 errors.
- Done: d1720c89d

### Step 2 - ThinkingOptions behaviour
- Files: `RoslynSentinel.AgentLoop/ThinkingOptions.cs`
- Depends on: Step 1
- Pairs with: Step 3
- Effect: text-neutral (no callers yet)
- Lane: `implementer`
- Change:
  - `FromEnvironment`: `readVariable ??= Environment.GetEnvironmentVariable`, as
    `LoopAssistOptions.FromEnvironment` (`LoopAssist.cs:45-53`). The allowed names come from the
    enums (one definition): modes = `Enum.GetNames<ThinkingMode>()`, triggers =
    `Enum.GetNames<ThinkingTriggers>()` without `None`, matched case-insensitively by name (not
    `Enum.TryParse`, which accepts numbers); the same lists go into the exception's `allowed`. Mode:
    unset/blank = `Off`. Triggers: unset/blank = `Error`; else split on `,`, trim, each a trigger name;
    an empty list after trimming is invalid. Effort: unset = `DefaultEffort`; set but blank is
    invalid; any other value is kept verbatim. Budget: unset/blank = `null`; else a positive `int`.
    Every invalid value throws `InvalidThinkingSettingException(variable, value, allowed)`. Unset
    defaults come from `Off` (one definition): all four unset returns `Off` itself; an unset
    triggers variable uses `Off.Triggers`, an unset effort `Off.Effort`. Read variables through
    `readVariable` as `LoopAssistOptions.FromEnvironment` does.
  - `IsKnownEffort`: `KnownEfforts` contains `Effort` (ordinal, case-insensitive).
  - `Decide(signals)`: Off gives `new TurnThinking(false, None, null, null)`; Always gives
    `(true, None, null, null)`; Triggered: `fired = Triggers & signals`, `Requested = fired != None`.
- Done when: `Build` 0 errors; no `NotImplementedException` left in the file.
- Done: 39dbc2700

### Step 3 - Client: reasoning field and delta count
- Files: `RoslynSentinel.AgentLoop/LmStudioAgentClient.cs`
- Depends on: Step 1
- Pairs with: Step 2
- Effect: text-neutral while no caller passes `thinking` (Execution rules, wire identity)
- Lane: `implementer`
- Change:
  - Add a `ThinkingOptions? thinking` parameter to the private `CompleteOnceAsync` (`:238`), placed
    before its `cancellationToken`, and pass it at all three calls inside `CompleteAsync` (first
    attempt and both retry paths).
  - `ResponsesRequest` (`:904-942`): `public ResponsesReasoning? Reasoning { get; init; }`; new private
    nested `private sealed class ResponsesReasoning { public required string Effort { get; init; } }`
    beside it. Set `Reasoning = thinking is null ? null : new ResponsesReasoning { Effort = thinking.Effort }`
    in the initializer at `:247-257`. `JsonOptions` (`:62-66`, camelCase, WhenWritingNull) omits it
    when null and writes `"reasoning":{"effort":"low"}` otherwise.
    NEW-DTO: ResponsesReasoning (Request element, AgentLoop.LmStudio wire). Evaluated: ReasoningItem
    (`:959-965`) - an input item (type/content), not the request-level reasoning object. Wire impact:
    new request field, sent only on thinking requests.
  - Stream loop, `case "response.reasoning_text.delta":` (`:308`): increment a local
    `reasoningDeltaCount` once per event (beside `sb.Append(evt.Delta)`, `:314`); set
    `ReasoningDeltaCount = reasoningDeltaCount > 0 ? reasoningDeltaCount : null` in the
    `new AgentChatMessage` at `:489`. Comment at the increment: `// Hook point for an enforced reasoning
    budget (cut and continue). Not built; design and probe facts:
    docs/current/plans/plan_error_gated_thinking_and_client_reasoning_budget.md, "Deferred: enforced
    cut". Moving the continuation to the runner (a typed exception) is the route to finer stream control.`
  - Q16 default: the field is sent the same way for both backends (`lmstudio`, `llama-server`).
- Done when: `Build` 0 errors.
- Done: ed219aa64

### Step 4 - Tests for Steps 2 and 3
- Files: `RoslynSentinel.Tests.Harness/ThinkingOptionsTests.cs` (new),
  `RoslynSentinel.Tests.Harness/ThinkingRequestBodyTests.cs` (new)
- Depends on: Steps 2, 3
- Pairs with: Step 5
- Effect: text-neutral (tests only)
- Lane: `test-writer`
- Change: the `ThinkingOptionsTests.*` and `ThinkingRequestBodyTests.*` rows of the test table, with
  the fixtures named in the test table intro.
  `FromEnvironment` tests pass a dictionary-backed `readVariable`, never the process environment.
- Done when: `RunTest` on both classes passes (dispatcher runs it).
- Done: 18df7ffc8

### Step 5 - Run totals
- Files: `RoslynSentinel.AgentLoop/AgentTranscript.cs`
- Depends on: Step 1
- Pairs with: Step 4
- Effect: text-neutral (new totals, zero for every run without thinking)
- Lane: `implementer`
- Change: in `RunTotals.FromTranscript` (`:459`, object initializer at `:463`) set `ThinkingTurnCount`
  = turns with `Thinking?.Requested == true`, `OverBudgetTurnCount` = turns with
  `Thinking?.OverBudget == true`. `RunTotals` says it mirrors `scripts/Backfill-TranscriptSummary.ps1`:
  that script is not changed (the new totals are not backfilled; old transcripts have no thinking).
- Done when: `Build` 0 errors.
- Done: 9184930d0

### Step 6 - Runner wiring: settings, header stamp, warning
- Files: `RoslynSentinel.AgentLoop/ModelAgentRunner.cs`, `RoslynSentinel.Tests.ModelEval/ModelEvalAssemblySetUp.cs`
- Depends on: Step 2
- Pairs with: none (Step 4 if it is still running)
- Effect: text-neutral for requests (no request thinks yet); transcripts gain the `Thinking` header
- Lane: `implementer` (about 8 edits)
- Change in `ModelAgentRunner.cs`:
  - Field `private readonly ThinkingOptions _thinking;` beside the other readonly fields (`:114-115`).
  - Enabling edit for `ThinkingRunnerTests.Serialize_WritesEnumNames`: `TranscriptFileOptions`
    (`:1092`) from `private static readonly` to `internal static readonly` (Tests.Harness already has
    InternalsVisibleTo, `RoslynSentinel.AgentLoop.csproj:16-18`), with a comment saying the test uses it.
  - `public static ThinkingOptions? ThinkingDefault { get; set; }` beside `ContractStamp` (`:103-106`),
    with the same "Intentional deviation" comment style (`:102`): a static so one assembly-setup line
    configures every ModelEval construction site (16, no shared factory) and production stays Off.
  - Both public ctors get a trailing `ThinkingOptions? thinking = null`: the `IAgentToolHost` ctor
    (`:161`) stores `_thinking = thinking ?? ThinkingDefault ?? ThinkingOptions.Off` (beside
    `_loopAssist`, `:186`); the `McpClient` ctor (`:121`) forwards `thinking` in its `: this(...)`
    chain.
  - After `transcript.ReasoningReplayMode = replayPolicy.Mode;` (`:348`): `transcript.Thinking = _thinking;`.
    If `_thinking.Mode != ThinkingMode.Off && !_thinking.IsKnownEffort`, log once per run:
    `_logger.LogWarning("{Warning}", string.Format(ThinkingOptions.CustomEffortWarningFormat, _thinking.Effort, _llm.Model));`
- Change in `ModelEvalAssemblySetUp.cs`: after `ModelAgentRunner.ContractStamp = ...` (`:57`):
  `ModelAgentRunner.ThinkingDefault = ThinkingOptions.FromEnvironment();`.
- Done when: `Build` 0 errors.
- Done: 9d52066e

### Step 7 - Runner loop: per-turn decision and turn record
- Files: `RoslynSentinel.AgentLoop/ModelAgentRunner.cs`
- Depends on: Steps 3, 6
- Pairs with: Step 9
- Effect: behaviour-changing only when the variables are set in a ModelEval process; default Off
- Lane: `implementer` (about 8 edits)
- Change in `RunCoreAsync`:
  - Before the turn loop (`while (turnNumber < _turnCap)`, `:362`): `var pendingSignals =
    ThinkingTriggers.FirstTurn;` (the first live request of this run, so a seeded/resumed run also
    gets `firstTurn` on its first live request; seeded turns contribute no error signals).
  - At the top of each loop iteration, before anything can `continue` (the only turn-level
    `continue` is the malformed-markup nudge at `:575`): `var turnSignals = pendingSignals;
    pendingSignals = ThinkingTriggers.None;`. Reading and resetting at the top means no end-of-turn
    copy is needed on any path.
  - Before the `CompleteAsync` call (`:398`): `var turnThinking = _thinking.Decide(turnSignals);` and
    pass `turnThinking.Requested ? _thinking : null` as the new last argument. When requested, log
    `Turn {Turn}: thinking requested (triggers: {Triggers}, effort: {Effort})` as its own line; the
    `Turn {Turn}: model responded in` line (`:456`) keeps its shape (Q17).
  - On `turnRecord` (`new AgentTranscriptTurn`, `:473`), only when `_thinking.Mode != ThinkingMode.Off`:
    `Thinking = turnThinking with { ReasoningDeltas = modelMessage.ReasoningDeltaCount, OverBudget =
    turnThinking.Requested && _thinking.ReasoningBudget is { } budget ? (modelMessage.ReasoningDeltaCount ?? 0) > budget : null }`.
    Comment on the budget comparison: `// Observed reasoning budget: an inactive, experimental feature
    of unknown value; it never cuts the stream and can be removed if it causes problems. See
    docs/current/plans/plan_error_gated_thinking_and_client_reasoning_budget.md, "Deferred: enforced cut".`
    When `OverBudget == true`, log `Turn {Turn}: reasoning {Deltas} deltas exceeded the observed budget {Budget} (not enforced)`.
  - In the tool loop: `if (isError) pendingSignals |= ThinkingTriggers.Error;` right after the
    destructuring at `:600` (inside the loop, because it can `break` early); `pendingSignals |=
    ThinkingTriggers.LatchRefusal;` inside `if (ToolLoopLatch.RefusalMessage(toolCall.Name, resultJson) is { } refusalMessage)` (`:709`).
- Done when: `Build` 0 errors.
- Done: 734d7dfb

### Step 8 - Runner tests
- Files: `RoslynSentinel.Tests.Harness/ThinkingRunnerTests.cs` (new)
- Depends on: Steps 5, 6, 7
- Pairs with: none (Step 9 if it is still running)
- Effect: text-neutral (tests only)
- Lane: `test-writer`
- Change: the `ThinkingRunnerTests.*` rows of the test table, using the fixtures named in the test
  table intro (a private scripted `HttpMessageHandler`, a private `IAgentToolHost` fake, the
  `IAgentToolHost` ctor at `:161` with `LoopAssistOptions.Off` and an explicit `thinking:` argument,
  except the static-default test). Class `[NonParallelizable]` with a comment.
- Done when: `RunTest` on the class passes (dispatcher runs it).
- Done: a1f5ced3

### Step 9 - Docs and arm table script
- Files: `docs/current/references/reference_model_eval_procedure.md`, `scripts/Get-ThinkingArmTable.ps1` (new)
- Depends on: Step 1 (names only)
- Pairs with: Step 7
- Effect: text-neutral
- Lane: `implementer` (non-C#: Write/Edit)
- Change:
  - Env var table (`reference_model_eval_procedure.md:64-77`): four rows for the variables in the
    Decision summary, with defaults, allowed values and "ModelEval only; read once per process".
  - `Get-ThinkingArmTable.ps1`, modelled on `scripts/Get-EditArmTable.ps1` (arm identity from each
    job's `job.env`; the arm key is the `THINKING`, `THINKING_TRIGGERS`, `THINKING_EFFORT` and
    `REASONING_BUDGET` values). Per arm:
    - Finding the files: recurse each job's `runFolders` (as `Get-EditArmTable.ps1:49-52` does, but
      take every run directory, not only the first transcript per folder). Each run directory holds
      one `transcript.json` and one `step-<k>.outcome.txt` (one test case per step, fresh directory
      per case, `ImplementationScenarioAgentTests.cs:42-46`); wall time per run is that file's
      `wallTime=hh:mm:ss`. The outcome line is `...stopReason=X. {detail} outcomeClass=Y
      variant=Z` (`ImplementationScenarioAgentTests.cs:174`, written at `:176`). Arm keys in
      `job.env` are the full variable names (`ROSLYNSENTINEL_MODELEVAL_THINKING`, ...).
    - runs, and counts of each `outcome=` value from those files: `Pass`, `PassedAfterCap`, `OutOfScopeEdit`,
      `CheckFailed`, `BuildBroken`, `Blocked`, `NotConverged` (`ImplementationScenario.cs:12-23`);
      counts of each `stopReason=` value from the same files (`AgentStopReason` names, e.g.
      `TurnCapExceeded`, `ContextExhausted`, `StreamTruncated`, `ReasoningLoopDetected`,
      `ModelFinished`); counts of each `outcomeClass=` value (`ScenarioVerdict.cs:13-20`);
    - pass rate (`Pass`; `PassedAfterCap` shown separately, as in the owner's tables), median wall per
      run, median turns;
    - from `transcript.json`: thinking turns per run, median reasoning deltas per thinking turn,
      over-budget turns, host-cap turns (Q26: reasoning text containing a marker from a
      `$HostCapMarkers` list: `I should answer soon`, `I need to state my answer and act`), and the
      longest run of consecutive erroring calls to the same tool. `Thinking` enums are strings;
      `StopReason` in the transcript is numeric (property `AgentTranscript.cs:199-203`), so take stop reasons from
      the outcome files. Tolerates transcripts without the new fields (columns show 0 or blank).
- Done when: the script runs on an existing evalqueue job folder (e.g. the c52d49f4 113 jobs) and
  prints one row per arm without errors.
- Done: 72b28140

### Step 10 - Stub sweep and full test run
- Files: none changed (fix-ups only if something fails)
- Depends on: Steps 1-9
- Effect: none
- Lane: dispatcher (plain calls)
- Change: `Search(mode: "text", query: "NotImplementedException")` over the plan's files finds none;
  `RunTest` full suite; compare with the 0-failure baseline.
- Done when: no stubs; no new test failures.
- Done: b59fb544 (fix-up: ResultOverride.CopyTurn copies Thinking); no stubs; full suite at b59fb544 0 failures.

### Step 11 - LIVE smoke test
- Files: none (eval output under the scratch root)
- Depends on: Step 10, committed and pinned (evalqueue, pinned checkout of that commit)
- Effect: none on code; produces the smoke numbers
- Lane: `modeleval-runner`, through `scripts/roslynsentinel-evalqueue.ps1`
  (`reference_model_eval_procedure.md`); standing contract only; hosts 112 and 113
  (Qwen3.5-9B-MTP-GGUF, same ~3.9k host cap); never load or unload models.
- Change: scenarios SweepConstructionSites and ThreadOptionalParameter, 2 rounds per scenario per
  arm, 12 runs:
  | Arm | Variables |
  | --- | --- |
  | baseline | none of the four set |
  | triggered-error | `THINKING=triggered`, `THINKING_TRIGGERS=error` |
  | always | `THINKING=always` |
  Report the `Get-ThinkingArmTable.ps1` table plus, per arm, the four Q21 signals scored
  independently and marked directional at n=4 (Decision Q30): keep-signal: `outcome=Pass` rate at
  least baseline +15 pp; wall bound: median wall at most 3x baseline; park-signal: runs with
  `stopReason=TurnCapExceeded` down by at least a third; drop-signal: `Pass` gain at most +5 pp.
  `PassedAfterCap` is reported beside `Pass`, not counted in it. Outcome classes are the script's
  `outcome=`, `stopReason=` and `outcomeClass=` counts.
- Done when: 12 runs finished and the table is in the run tracker; the owner then decides the
  stage-1 size and arms (latchRefusal, firstTurn, 85 comparisons only with 85).
- Done: 12 jobs pinned at 8e02735a (2026-11-25, all Finished, none resubmitted; jobs 20261125-192438..192505,
  tag `thinking-smoke-8e02735a`, per-run CSV `c:	mp	hinking-smoke-runs.csv`). Both scenarios pooled, n=4 per arm,
  directional:
  | Arm | Pass | Median wall | TurnCapExceeded | Thinking turns/run | Median deltas/thinking turn |
  | --- | --- | --- | --- | --- | --- |
  | baseline | 0/4 | 1:44 | 3 (+1 RepeatedToolFailure) | 0 | - |
  | triggered-error | 1/4 | 2:08 | 3 | 5.25 | 30 |
  | always | 0/4 | 3:28 | 4 | 25 | 66.5 |
  triggered-error: keep met (+25 pp, a single Pass), wall met (1.23x), park not met, drop not triggered.
  always: keep not met, wall met (2.0x), park not met, drop triggered. No PassedAfterCap, no host-cap turns;
  every run SilentIncomplete. Harness conformance checked on all 12 transcripts: no mismatch.
  Observation: in the agentic loop Qwen3.5-9B reasons briefly when asked (mostly 10-20 deltas per turn,
  peaks ~1k; ~4 chars per delta, so the count is sound), unlike the single-shot probes (8-10k chars).
  All arms ran the default (legacy) contract prompt.

## Out of scope

- Moving the eval client to `/v1/chat/completions` or another endpoint (reassess later).
- Changing the host-side reasoning budget.
- Any server or tool change; the work is confined to `RoslynSentinel.AgentLoop` and the eval
  scripts that parse its transcripts.
- Follow-ups (TODO/INBOX lines at finalize): applying the settings everywhere `ModelAgentRunner` is
  built (Q13); an injected verification turn as a trigger (Q24 option C); the enforced cut and the
  request-field delivery ("Deferred: enforced cut").
- The stage-1 batch and stage-2 arms: sized and planned by the owner after the Step 11 smoke test.

## Deferred: enforced cut

Not built (Decision, "Budget rework" and "Cut code"). Kept so it can be built for a model where a
short budget is worth enforcing, or to stop runaway thinking. Everything below was measured on
host 113 (Qwen3.5-9B-MTP-GGUF, llama-server/Unsloth) on 2026-11-25; scripts, bodies and CSVs are in
`<scratch root>\plan_design_error_gated_thinking\` (`p5*.ps1`, `p6*.ps1`, `results_p6.csv`).

- **Where:** inside `LmStudioAgentClient.CompleteAsync` (Q14 option A): the stream loop stops reading
  when the reasoning delta count passes the budget, the client sends the continuation and returns
  one message plus a cut record. Signal the cut with a private typed result, never an exception
  that derives from `InvalidOperationException` or `IOException` (the retry filter at
  `LmStudioAgentClient.cs:207-208` retries those). A cut stream never sees `response.completed`, so
  it has no usage and must not reach the `StreamTruncatedException` branch (`:394-404`). Moving the
  continuation to the runner (option B, a typed exception) is the route to finer stream control.
- **Counting:** one `response.reasoning_text.delta` event is ~one token (0.91-0.97); count events.
- **Continuation input (variant A3, chosen):** the conversation as sent, then a reasoning item
  holding the partial reasoning, then an assistant message `My thinking budget is spent, so I will
  act now.`; no `reasoning` field. In the agentic shape it renders (+~800 input tokens over dropping
  it), keeps earlier replayed reasoning, reuses the cached prefix (483-502 cached tokens), and the
  model answers without echoing the cue (2 rounds plus an early-reasoning round).
- **Rejected variants:** a reasoning item that ends the input, with or without a following user
  message, is not rendered (input tokens equal to dropping it): the chat template renders an
  assistant turn's reasoning only after the last user message (`loop.index0 > ns.last_query_index`).
  Partial text in a user message renders but strips every earlier replayed reasoning item from the
  prompt (19 tokens in the probe), changing the prefix.
- **Cancel cost:** the request sent right after a cancel waits a steady ~2.0 s for its first event
  (37 ms after a 3 s wait); `/slots` and `/metrics` return 401, so slot state is unconfirmed.
- **Record:** chars and deltas at the cut, both request latencies, the variant; a dedicated log line;
  a run-total count. The `Turn N: model responded in` line (`ModelAgentRunner.cs:456`, parsed by
  `scripts/Parse-AgentLog.ps1:238`) keeps its shape.
- **Request-field delivery (deferred with the cut, from Q28):** `..._REASONING_BUDGET_DELIVERY = cut
  | field | both` and `..._REASONING_BUDGET_FIELD` = dotted JSON path, inserted via `JsonNode` only
  when configured. No probed host honours any budget field (`reasoning.budget_tokens`,
  `thinking_budget`, `reasoning_budget`, `thinking_budget_tokens`,
  `chat_template_kwargs.thinking_budget`, `max_reasoning_tokens`).

## Risks and open decisions

- Cross-plan: `plan_startup_configuration_registry.md` (DRAFT 2026-11-25) declares every
  `ROSLYNSENTINEL_*` env var as a `StartupSetting` descriptor and adds a source-scan guard (its T10)
  that rejects a `ROSLYNSENTINEL_*` literal or an `Environment.GetEnvironmentVariable` call in product
  code outside its registry. Its author was asked (2026-11-25) to add `AgentLoop/ThinkingOptions.cs`
  to its Step 13a. Ordering:
  - This plan first (expected): run as written. The registry later turns the four `*Variable`
    constants into descriptors (scope `AgentLoop`, env-only, `Live`; a custom parser for the trigger
    list; effort non-blank), `FromEnvironment` builds the record from their values, and
    `InvalidThinkingSettingException` is replaced by the registry's Invalid record plus its
    `ThrowIfInvalid` in `ModelEvalAssemblySetUp`.
  - Registry first: Steps 1-2 declare the four settings as descriptors in a
    `[StartupSettingGroup]` class instead of reading the environment, drop
    `InvalidThinkingSettingException`, and keep the `ThinkingOptions` record, `Off`, `Decide` and the
    known-effort warning; Step 6 places the `ThinkingDefault` line after the registry's `ConfigureAll`
    in `ModelEvalAssemblySetUp`; Step 9's doc rows are replaced by the generated configuration
    reference. Re-check `ThinkingOptionsTests` rows that assert the exception.
  - Either way, both plans edit `ModelEvalAssemblySetUp.cs`; the second to land rebases.

Implementer defaults (stated again in the steps that use them):
- Q16 backend: the `reasoning` field is sent the same way on the `lmstudio` and `llama-server`
  backends; untested on `lmstudio` (unreachable on every host 2026-11-25).
- Q17 log lines: the `Turn N: model responded in ...` line keeps its shape; thinking and threshold
  facts get their own log lines.
- Q18 old transcripts: every new transcript field is nullable; a test pins that a transcript without
  them deserializes.
- Q19 proposal numbers: corrected in the proposal at finalize (85 median 8.1 s, 112 4.7 s; the
  error-turn tables have no script).
- Q22 wire identity: a request-body test asserts no `reasoning` key when thinking is not requested.
- Q26 host cap: whether a turn hit the host's own cap is derived by the analysis script from the
  injected marker in the reasoning text, not by a harness field.
