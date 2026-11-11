# Loop latch: disable a tool that keeps failing the same way until another tool succeeds

**Status:** IMPLEMENTED (uncommitted) 2026-11-11. Open questions settled by the owner 2026-11-11: trip at 2 same-way failures (same tool + error code + first compile-gate diagnostic id, arguments not part of the key); per caller agent id (no id = shared `default` key); only the looping tool is refused, read-only calls included; K = 2 further calls after the first refusal stop the run (`LoopLatchIgnored`); flag `--loop-latch` (env `ROSLYNSENTINEL_LOOP_LATCH`), off by default; `McpServerStatus.loopLatch` shows latched tools per agent while on. Code: `Common/ToolLoopLatch.cs`, `Common/LoopLatchOptions.cs`, `Server.Advanced/LoopLatchFilter.cs` (filter 4, before argument validation), harness in `Common/AgentLoop/ModelAgentRunner.cs`. Tests: `Tests.Server/LoopLatchTests.cs`, `Tests.ModelEval/LoopLatchRunnerTests.cs`. Deviation from the Restrict text above: the refusal says "You MUST do the next step now: {the Next step sentence verbatim}" rather than building a call.

## Motivation

Weak models repeat a rejected call even when the rejection names the right next step.

- 10-chain batch C run 305 and batch E run R9 (`20261110-231957-659`, qwen3.5-9b-coder) each sent the identical
  rejected ReplaceSnippet 9 times; each response carried "use RenameSymbol(...) instead" and (R9) the exact-repeat
  note. R9 ended only when the harness RepeatedToolFailure breaker tripped at 10 consecutive failures (T11).
- Batch E run R2 (`20261110-220441-748`) ended with 10 failures alternating between "'oldContent' is required" and
  "'newContent' is required". The harness breaker counts only consecutive identical failures (TODO 4f0fa78a), so
  alternation is not caught early.
- In batch D the runs that escaped a redirect usually made one different call first (LocateSymbol or Search).
  A forced intervening call appears to break the repetition (observation, not traced).

The server already has the pattern: `OrientationCircuitBreaker` (`RoslynSentinel.Common/OrientationCircuitBreaker.cs`)
trips after consecutive zero-match searches, disables `Search(mode: text)` with a directive naming the tools to call
instead ("You MUST call ListAll ... now"), and resets automatically when another listed tool succeeds.

## Proposal

- **Trip.** The same tool fails the same way twice with no successful call of a different tool in between. "Same
  way" = same tool and same error code (and, for compile-gate rejections, the same first diagnostic id), not identical
  arguments, so alternating argument errors count. Per caller agent id: the server runs in MultiAgent mode and one
  agent's loop must not disable a tool for its peers.
- **Restrict.** Disable only the looping tool, not the whole surface. Its next call is refused with:
  - when the last rejection had a Next step: "{Tool} is DISABLED until another tool succeeds. You MUST call
    {the Next step's call} now.";
  - otherwise: "{Tool} is DISABLED until another tool succeeds. Read the current code first with ReadFile,
    GetFileOutline or Search, then choose a different approach." (only callable tools named).
- **Release.** Any successful call of a different tool resets the latch, as the orientation breaker does.
- **Unrecoverable.** If the model calls the disabled tool K more times (K = 2 to start), the harness stops the run
  with a distinct stop reason (e.g. `LoopLatchIgnored`), so the report says the model is past recovery instead of
  burning turns to the 10-failure breaker.
- **Switch.** Server flag `--loop-latch`, off by default; the refusal text reaches the transcript through the tool
  result, and the harness records latch refusals as an assist kind on the turn (see the transcript flag in
  proposal_first_person_turn_injection).

## Alternatives considered, not pursued

- **Restrict to McpServerStatus.** Breaks the streak but gives the model nothing about its task, so the next call
  likely returns to the loop. Read-only code tools or the named next step give new information.
- **Disable the whole surface except reads.** Blocks the next step the server itself recommends (often RenameSymbol).
- **Key on identical arguments only.** Misses R2's alternating failures; the exact-repeat note already covers the
  identical case.

## Open questions

- Trip at 2 or 3 same-way failures; K for the unrecoverable stop.
- Whether a validate-only call of the disabled tool should be allowed.
- Interaction with the harness RepeatedToolFailure breaker and with rename lifting (a lifted rename succeeds, so the
  latch never trips for that case).
- Where the per-agent state lives (beside the orientation breaker's state, keyed by caller agent id).

## Cost / risk

- Touches the request pipeline for every mutating tool; a false trip blocks a legitimate retry (for example after the
  model fixed the file through another tool, which already resets the latch).
- Deliberate repetition (a protocol that repeats a call on purpose) must not be blocked: the release on any other
  successful call limits the impact; verify against the step-11 case in proposal_first_person_turn_injection.

## Related

- proposal_first_person_turn_injection (harness collapse + injection, transcript assist flag)
- proposal_semantic_edit_impact_diagnostics (rename lifting, second slice step 1)
- evals/eval_tenchain_reasoning_replay_2026_11_10.md (batches C, D, E)
- TODO 4f0fa78a (breaker misses alternating identical failures)
