# Plan: replay all reasoning until 70% of the context, then drop the oldest half; pin the first 3 turns

**Status:** IN PROGRESS 2026-11-14. Steps 1-7 done (0f134491, f33130d4, fdb784ec, 7156bea4, 616df7e9, a8bd8beb; 16e09c44 adds the pressure override); Step 8 found a drop-thrash defect, fix awaiting an owner decision.

## Problem

`LmStudioAgentClient.BuildInputItems` drops reasoning by a fixed block window (`ReasoningReplayTurns = 10`, 10-19 turns
replayed, 564a2813). The window was sized for the 55-tool surface (~21.7k tokens of overhead). On the lean surface the
overhead is ~9k, and 5 full-reasoning ten-chain runs on host 113 (80k) peaked at 40.2k tokens. The block step still
breaks the prompt cache at T21/T31 and discards reasoning nobody needed to discard. The plan-bearing reasoning is at
T2/T3 (turn 1 is 21-98 tokens). Evidence and owner decisions:
`docs/current/proposals/proposal_reasoning_replay_context_management.md` ("Decisions", "Step 1a results",
"Step 1b results").

Correction to that proposal: the context length is NOT written to the transcript today. `ModelAgentRunner.RunAsync`
fetches it (`var contextLength = await _llm.GetContextLengthAsync(cancellationToken);`) and uses it only for the 0.8
warning (`ContextUsageWarningFraction = 0.8`, not 90%) and the tool-less-turn `ContextExhausted` check. This plan adds it.

## Decision

- A per-run `ReasoningReplayPolicy` (new, `RoslynSentinel.Common`) decides which assistant turns replay their reasoning.
  `ModelAgentRunner` creates it after fetching the context length, feeds it the projected prompt size before every
  request, and passes it to `LmStudioAgentClient.CompleteAsync`. All three harnesses (ModelEval, PlanStepRunner,
  `SubAgentModelRun`) run through `ModelAgentRunner` (`ModelAgentRunner.cs` `_llm.CompleteAsync` is the only production
  caller), so no harness code changes.
- **Pressure mode (default):** replay everything. When the projected prompt reaches 70% of the context, drop the
  reasoning of the oldest half of the replayed, unpinned turns, once per request. The dropped set only grows, so the
  prefix stays stable between drops (one cache break per drop).
- **Pinned:** the reasoning of the first 3 assistant turns is never dropped in pressure mode.
- **Projection:** `cumulativeTokens` (previous turn's reported total, or the estimate the runner already uses) plus
  `EstimateMessageTokens` of the messages added since (`messages[usageMessageCount..]`, the tool results).
- **`ROSLYNSENTINEL_REASONING_WINDOW` keeps working for A/B arms:** unset = pressure mode; `0` = replay all, never drop;
  `N > 0` = the legacy block window of N with no pins (exactly today's behaviour at 10).
- **Unknown context length** (`GetContextLengthAsync` returns <= 0): legacy block window of 10 (today's behaviour).
- **Recorded in the transcript** (all harnesses): `ContextLength`, `ReasoningReplayMode`, `ReasoningDropTurns`.
- A `null` policy (direct `CompleteAsync` callers, `SerializeInputItems` without a policy) keeps today's block window.

### Decisions recorded 2026-11-14 (plan review)

- Owner present during the run; mode Batched: Batch A = Steps 1-3, Batch B = Steps 4-5; Steps 6-8 by the main session.
- The unknown-context fallback lives in the constructor (review option A): `contextLength <= 0` with a null
  `fixedWindow` makes `FixedWindow` 10 and `Mode` "window:10 (context length unknown)". `FixedWindow` is the effective
  window (null only in pressure mode), so `Observe` can never run with a zero context. `FromEnvironment` only parses
  the variable and calls the constructor.
- Steps 2 and 3 run in sequence, not as a pair: a `RoslynSentinel.Tests` build rebuilds `Common`, and Step 3's tests
  need Step 2's behaviour.

## Execution rules

- Commit each step to master with `Plan: plan_reasoning_replay_pressure_policy` above `Co-Authored-By`; the last step's
  commit carries `Closes: plan_reasoning_replay_pressure_policy`. Never push.
- `Build` 0 errors per slice; implementers only Build; the dispatcher runs the named tests. One Build/RunTest at a time.
- Test baseline is 0 failures; any failure is new.
- `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs` was being edited by another session on 2026-11-14. Before Step 4,
  check `Git(operation: status)` and `ListAgents`; if a peer has uncommitted edits in it, send `HOLD`/ask for `COMMIT`
  (peer-coordination skill). Line numbers below are from 2026-11-14 and may shift; anchors are quoted code.
- `ReasoningReplayPolicy` is a stateful service class, not a DTO: its creating call's `reason` carries
  `DTO-EXEMPT: stateful per-run replay policy, not a data carrier`. The transcript gains properties on the existing
  `AgentTranscript` DTO (extension, no new type).

## Steps

### Step 1 - Contract skeleton
- Files: `RoslynSentinel.Common/ReasoningReplayPolicy.cs` (new), `RoslynSentinel.Common/LmStudioAgentClient.cs`,
  `RoslynSentinel.Common/AgentLoop/AgentTranscript.cs`
- Depends on: none
- Effect: text-neutral (new members; the new parameter is optional and nothing passes it)
- Change:
  - `CreateFile` `ReasoningReplayPolicy.cs`, namespace `RoslynSentinel.Common`, every body
    `throw new NotImplementedException(); // TODO implement (Step 2)`:
    ```csharp
    public sealed class ReasoningReplayPolicy
    {
        public const int PinnedTurns = 3;
        public const double PressureFraction = 0.7;
        public ReasoningReplayPolicy(int contextLength, int? fixedWindow)
        public static ReasoningReplayPolicy FromEnvironment(int contextLength)
        public int ContextLength { get; }
        public int? FixedWindow { get; }          // effective block window; null only in pressure mode
        public int DroppedTurns { get; private set; }
        public string Mode { get; }               // transcript text, see Step 2
        public bool Observe(int assistantTurns, int projectedPromptTokens)   // true when this call dropped turns
        public bool ReplaysReasoning(int assistantTurnIndex, int assistantTurns)  // 0-based among non-note turns
    }
    ```
    The properties are plain auto-properties (`{ get; }`, `DroppedTurns { get; private set; }`, `Mode` initialised to
    `""`); the constructor, `FromEnvironment`, `Observe` and `ReplaysReasoning` bodies are the stub.
  - `LmStudioAgentClient.cs`, one `ReplaceSnippet` `batchEdits` call:
    - `public async Task<AgentChatMessage> CompleteAsync(IReadOnlyList<AgentChatMessage> messages, IReadOnlyList<AgentToolDefinition> tools, int maxTokens, CancellationToken cancellationToken = default)`
      gains `ReasoningReplayPolicy? replayPolicy = null` as the last parameter and passes it to both
      `CompleteOnceAsync` calls. The two calls are the identical line
      `return await CompleteOnceAsync(messages, tools, maxTokens, cancellationToken);` (lines ~136 and ~144, the
      first after `try {`, the second after the retry `_logger.LogWarning(...)`): give each batch item its own
      `matchLine`, or include the preceding line in `oldContent`.
    - `private async Task<AgentChatMessage> CompleteOnceAsync(` gains a `ReasoningReplayPolicy? replayPolicy` parameter
      and passes it on: `Input = BuildInputItems(messages, replayPolicy),`.
    - `private static List<object> BuildInputItems(IReadOnlyList<AgentChatMessage> messages)` gains
      `ReasoningReplayPolicy? replayPolicy`; body unchanged plus `// TODO implement (Step 2): use replayPolicy when not null`.
    - `internal static string SerializeInputItems(IEnumerable<AgentChatMessage> messages)` gains
      `ReasoningReplayPolicy? replayPolicy = null` and passes it to `BuildInputItems`.
  - `AgentTranscript.cs`, one batch, three properties after `TopP`, in the existing
    `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` style with doc comments:
    `public int? ContextLength`, `public string? ReasoningReplayMode`, `public List<int>? ReasoningDropTurns`
    (turn numbers whose request first dropped a block).
- Done when: `Build(level: quickBuild)` 0 errors.
- Lane: `implementer` (depth tier 1: 3 files, ~9 edits).
- Done: 0f134491

### Step 2 - Policy behaviour and client wiring
- Files: `RoslynSentinel.Common/ReasoningReplayPolicy.cs`, `RoslynSentinel.Common/LmStudioAgentClient.cs`
- Depends on: 1
- Effect: text-neutral until Step 4 passes a policy (only `SerializeInputItems` callers with a policy see it)
- Change:
  - Constructor: `ContextLength = contextLength`. If `fixedWindow` is null and `contextLength <= 0`:
    `FixedWindow = LmStudioAgentClient.ReasoningReplayTurns`, `Mode = $"window:{FixedWindow} (context length unknown)"`.
    Else if `fixedWindow` is 0: `FixedWindow = 0`, `Mode = "all"`. Else if `fixedWindow > 0`: `FixedWindow = fixedWindow`,
    `Mode = $"window:{fixedWindow}"`. Else (null, context known): `FixedWindow = null`,
    `Mode = $"pressure:{PressureFraction},pinned:{PinnedTurns}"` (format `PressureFraction` with
    `CultureInfo.InvariantCulture` so it reads `0.7`).
  - `FromEnvironment`: `new ReasoningReplayPolicy(contextLength, LmStudioAgentClient.ParseReasoningWindowOverride(Environment.GetEnvironmentVariable(LmStudioAgentClient.ReasoningWindowVariable)))`.
  - `Observe`: pressure mode only (`FixedWindow` null; returns false otherwise). If
    `projectedPromptTokens >= ContextLength * PressureFraction`, let
    `replayable = assistantTurns - PinnedTurns - DroppedTurns`; if `replayable >= 2`, `DroppedTurns += replayable / 2`
    and return true. Otherwise false.
  - `ReplaysReasoning`: window 0: true. Window N > 0: `assistantTurnIndex >= LmStudioAgentClient.ReasoningDroppedTurns(assistantTurns, N)`.
    Pressure: `assistantTurnIndex < PinnedTurns || assistantTurnIndex >= PinnedTurns + DroppedTurns`.
  - `BuildInputItems`: when `replayPolicy` is null keep today's code. Otherwise take the non-note assistant message
    indexes as today (`m.Role == "assistant" && !m.IsLoopAssistNote`) and call `ToInputItems(m, replayReasoning: ...)`
    with `replayPolicy.ReplaysReasoning(k, count)` for the k-th of them; other messages pass `true` (they carry no
    reasoning). Remove the Step 1 TODO.
- Done when: `Build(level: quickBuild)` 0 errors.
- Lane: `implementer` (depth tier 1: 2 files, ~6 edits). Step 3 follows it (not a pair; see Decisions).
- Done: f33130d4

### Step 3 - Policy unit tests
- Files: `RoslynSentinel.Tests/ReasoningReplayPolicyTests.cs` (new)
- Depends on: 1 (compiles), 2 (passes)
- Effect: text-neutral
- Change: NUnit tests, using `LmStudioAgentClient.SerializeInputItems(messages, policy)` and the `PlanTurns` /
  `ReplayedReasoning` pattern of `LmStudioAgentClientInputItemsTests` (copy the helpers; do not edit that file):
  - `Pressure_BelowThreshold_ReplaysEveryTurn` (context 1000, projected 699, 30 turns: 30 reasoning items).
  - `Pressure_AtThreshold_DropsTheOldestHalfOfTheUnpinnedTurns` (30 turns, projected 700: `DroppedTurns` 13, plans
    1-3 and 17-30 replayed).
  - `Pressure_NeverDropsTheFirstThreeTurns` (several Observe calls at pressure until `replayable < 2`).
  - `Pressure_KeepsTheEarlierRequestAsAPrefix_BetweenDrops` (same policy, n-1 and n turns, no Observe in between; strip
    `"id":"rs_..."` as `ReasoningWindow_KeepsTheEarlierRequestAsAPrefix_BetweenBlockSteps` does).
  - `WindowZero_NeverDrops`, `WindowN_MatchesTheLegacyBlockWindow` (equals `ReasoningDroppedTurns(n, N)` for n = 9, 19,
    23, 45), `UnknownContext_FallsBackToTheTenTurnBlockWindow` (`new ReasoningReplayPolicy(0, null)` and `(-1, null)`:
    `FixedWindow` 10, mode "window:10 (context length unknown)", `Observe` returns false even at a huge projection),
    `ExplicitWindowTen_WithUnknownContext_IsNotLabelledAsFallback` (`(0, 10)`: mode "window:10").
  - `Mode_NamesThePolicy` (three modes).
  - Do not set `ROSLYNSENTINEL_REASONING_WINDOW` in tests (process-wide); construct with `fixedWindow` directly.
- Done when (dispatcher): `RunTest(scope: project, scopeName: RoslynSentinel.Tests, filter: "FullyQualifiedName~ReasoningReplayPolicyTests|FullyQualifiedName~LmStudioAgentClientInputItemsTests|FullyQualifiedName~LoopAssistTrackerTests")` all pass.
- Lane: `test-writer`.
- Done: fdb784ec (86/86 passed)

### Step 4 - Runner wiring and transcript fields
- Files: `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs`
- Depends on: 1 (compiles); 2 for the live behaviour
- Effect: behaviour-changing: every harness replays all reasoning until 70% of the context, then drops; transcripts
  gain three fields.
- Change, all in `RunAsync` (`transcript` is created at ~230, before the context fetch; the turn counter is
  `turnNumber`):
  - After `var contextLength = await _llm.GetContextLengthAsync(cancellationToken);` (line ~289):
    `var replayPolicy = ReasoningReplayPolicy.FromEnvironment(contextLength);`, set
    `transcript.ContextLength = contextLength > 0 ? contextLength : null;` and
    `transcript.ReasoningReplayMode = replayPolicy.Mode;`, and log the mode once at Information.
  - Before `modelMessage = await _llm.CompleteAsync(replayMessages, toolDefinitions, _maxTokensPerTurn, cancellationToken);`
    (line ~321): `projected = cumulativeTokens + sum of EstimateMessageTokens(messages[i]) for i in usageMessageCount..messages.Count-1`;
    `assistantTurns = replayMessages.Count(m => m.Role == "assistant" && !m.IsLoopAssistNote)`; if
    `replayPolicy.Observe(assistantTurns, projected)` returns true, log at Information (turn number, projected,
    context length, `DroppedTurns`) and add the turn number to `transcript.ReasoningDropTurns` (create the list on first
    use). Put it after `var replayMessages = assistTracker.CollapseForReplay(...)` (~317) and before the
    `try` that wraps the `CompleteAsync` call; the only retry is inside the client, so this runs once per turn.
  - Pass `replayPolicy` as the new last argument of that `CompleteAsync` call.
  - The transcript is rewritten after every turn (`WriteTranscriptAsync` calls in `RunAsync`), so the fields persist
    without a new write.
- Done when: `Build(level: quickBuild)` 0 errors.
- Lane: `implementer` (depth tier 1: 1 file, ~3 edits). Peer check first (Execution rules).
- Done: 7156bea4 (TODO left at the `ContextSizeExceededException` catch: force a drop and retry once)

### Step 5 - Runner test
- Files: `RoslynSentinel.Tests.ModelEval/ReasoningReplayRunnerTests.cs` (new)
- Depends on: 2, 4
- Effect: text-neutral
- Change: no existing fake has everything, so build one in the new file:
  - Copy `MalformedToolCallRunnerTests.ScriptedHandler` (reasoning via `response.reasoning_text.delta`, tool calls,
    `RequestBodies` capture); add the `/api/v0/models` route from `ContextExhaustedRunnerTests.RoutingHandler`
    (`loaded_context_length`, or 404) and a per-turn `total_tokens` array instead of the fixed 1000.
  - Fixture SetUp and `NewRunner` shape from `ContextExhaustedRunnerTests`; `turnCap` >= 10 (a drop needs at least
    5 assistant turns: 3 pinned plus 2 replayable); `[NonParallelizable]` (`LlmOptions` is static); clear
    `ROSLYNSENTINEL_REASONING_WINDOW` in SetUp and restore it in TearDown (the runner reads it).
  - Give every scripted tool call a distinct non-`reason` argument (for example `McpServerStatus` with a different
    `toolNameFilter` per turn): 3 identical successful calls draw a nudge and the 4th stops the run
    (`IdenticalSuccessRunnerTests`).
  - `PressureMode_DropsOnceTheProjectedPromptReachesSeventyPercent`: context 1000, each turn has reasoning and one tool
    call, reported `total_tokens` rising past 700. Assert the request after the crossing carries fewer reasoning items
    than the one before, the first 3 plans are still present, and the transcript has `ContextLength` 1000,
    `ReasoningReplayMode` "pressure:0.7,pinned:3" and one entry in `ReasoningDropTurns`.
  - `UnknownContextLength_UsesTheTenTurnBlockWindow_AndOmitsContextLength`: 404 on the models endpoint; transcript
    `ContextLength` null, mode text ends with "(context length unknown)".
  - `Transcript_WithoutReasoningReplayFields_Deserializes` (pattern: `Transcript_WithoutStampProperties_Deserializes`).
- Done when (dispatcher): `RunTest(scope: project, scopeName: RoslynSentinel.Tests.ModelEval, filter: "FullyQualifiedName~ReasoningReplayRunnerTests|FullyQualifiedName~ContextExhaustedRunnerTests")` all pass.
- Lane: `test-writer`.
- Done: 616df7e9 (12/12 filtered; the first draft's scripted `McpServerStatus` calls omitted the mandatory `reason`
  and ended as `RepeatedToolFailure`)

### Step 6 - Script help and docs
- Files: `scripts/roslynsentinel-modeleval.ps1`, `docs/current/proposals/proposal_reasoning_replay_context_management.md`
- Depends on: 4
- Effect: text-neutral
- Change: `.PARAMETER ReasoningWindow`: unset (-1) = the client's pressure policy (70%, first 3 turns pinned); 0 =
  replay all; N > 0 = legacy block window of N, no pins. Proposal: correct "90% warning" to the 0.8 warning, note the
  context length was not in the transcript before this plan, and set Status to "options 2 and 3 built (<hashes>);
  option 4 follow-up".
- Done when: script parses (`pwsh -NoProfile -Command "[System.Management.Automation.Language.Parser]::ParseFile('scripts/roslynsentinel-modeleval.ps1',[ref]$null,[ref]$null) | Out-Null"`).
- Lane: main session (non-C# files).
- Done: a8bd8beb (proposal Status line updated at close)

### Step 7 - Stub check and test runs
- Files: none
- Depends on: 1-6
- Effect: none
- Change: `Search(mode: "text", query: "NotImplementedException", fileGlob: "RoslynSentinel.Common/ReasoningReplayPolicy.cs")`
  returns 0 matches; `Search(mode: "text", query: "TODO implement \\(Step")` over `RoslynSentinel.Common` returns 0.
  `Build(level: fullBuild)` 0 errors. `RunTest(scope: project)` for `RoslynSentinel.Tests` and
  `RoslynSentinel.Tests.ModelEval` (no filter needed: the live tests are `[Explicit]` and sit under
  `ModelEvalAssemblySetUp` in the `.LiveModel` namespace, which ignores them without a host): 0 failures.
- Done when: all of the above hold.
- Lane: dispatcher.
- Done: 616df7e9 worktree run (Tests 978/978, ModelEval 488 + 40 live skipped, ErrorVariants 185 + 1 skipped, 0 failed)

### Step 8 - LIVE verification
- Files: none (results into the proposal's eval section or `docs/current/evals/`)
- Depends on: 7
- Effect: none
- Change, through `scripts/roslynsentinel-evalqueue.ps1` (no `-ReasoningWindow`, so pressure mode):
  - Host 113, `qwen3.5-9b-mtp`, 80k: 5 x `OrderPricingRefactorChain10`. Outcome classes per run:
    `PassNoDrop` (ideal: peak below 56k, no `ReasoningDropTurns`, no cold turn after T1 except new-content turns),
    `PassWithDrop` (record the drop turn and that the only cold turns are drop turns), `Fail` / `ContextExhausted`.
  - Host 112, `qwen3.5-4b-mtp` (context ~60k, 70% ~42k; 4b runs reached 38-45k): 5 x `OrderPricingRefactorChain10`, to
    exercise a live drop. Classes: `PassWithDrop` (ideal: cold turns only at drop turns), `PassNoDrop` (drop path not
    exercised, inconclusive), `Fail` / `ContextExhausted` (limit; trace per the failure-analysis skill).
  - Check `ContextLength`, `ReasoningReplayMode` and `ReasoningDropTurns` in each transcript.
- Done when: both batches reported with the classes above; commit the write-up with
  `Closes: plan_reasoning_replay_pressure_policy`.
- Lane: `modeleval-runner` or the dispatcher via the queue.

## Out of scope

- Auto-compaction (proposal option 4): its own follow-up plan.
- Recovering from `ContextSizeExceededException` by forcing a drop and retrying (today it ends the run as
  `ContextExhausted`); leave a `// TODO` at the catch in Step 4 if the implementer sees a clean spot.
- CSV columns for the new fields (`ScenarioSampleCsv`): transcript-only to avoid the column-count migration.
- Making the threshold or pin count configurable.

## Risks and open decisions

- **OPEN (blocks closing): drop thrash.** Step 8b at pressure 0.5 (112 `20261114-085254-907`) dropped on every turn
  T24-T32 because dropping ~80-token reasoning items never brought the projection back under the threshold; nine cold
  turns. Fix options and recommendation (hysteresis of 10% of the context plus a 10-turn minimum drop block): proposal
  "Open questions". `// TODO` in `ReasoningReplayPolicy.Observe`. Needs an owner decision, then one implementer slice
  (`ReasoningReplayPolicy.cs` + `ReasoningReplayPolicyTests.cs`) and a re-run of Step 8b.

- **Projection error.** `EstimateMessageTokens` is an estimate; a single very large tool result could push a request
  past the context before a drop. The 70% margin covers typical results (largest observed ~4.6k characters); the
  existing 500 catch still ends such a run as `ContextExhausted`.
- **First turn after a resume.** `ResumeTranscript` seeds messages with `cumulativeTokens` 0, so the first projection
  is the estimate of all seeded messages without tool schemas (~9k low on the lean surface). A resumed run near 70%
  could drop one turn late. Accept (resume runs are short); the next turn uses the reported total.
- **Behaviour change for every harness at once.** PlanStepRunner and `SubAgentModelRun` get pressure mode with no A/B.
  Owner decided all harnesses are in scope; `ROSLYNSENTINEL_REASONING_WINDOW=10` restores today's behaviour.
- **Peer edits in `ModelAgentRunner.cs`.** Clean in git at review time (last change e002cdd6); re-check before Step 4.
- **Projection in the no-usage fallback.** When a response has no `total_tokens`, `cumulativeTokens` adds only the
  model message, never tool results, so the projection undercounts there (existing behaviour, LM Studio normally
  reports usage).
- **`FromEnvironment` throws earlier.** A bad `ROSLYNSENTINEL_REASONING_WINDOW` now fails in `RunAsync` before turn 1
  instead of on the first request; same `FormatException` text.
- **Legacy block window stays** for the null-policy path and `N > 0`. Its removal is not planned; it is the A/B control.
