# Harness discards a turn whose tool call fails on a near-miss member name, and resamples it

**Status:** PROPOSED 2026-11-18. Awaiting review; depends on the server signal in
[proposal_server_slip_recovery.md](closed/proposal_server_slip_recovery.md).

Scope (owner decision 2026-11-18): an evaluation feature. In production, the sampler preset makes slips
rare, and a stuck run is cheaper to revert and re-run than to add more crutches.

## Motivation

A token-path slip (`ShippingKinds.Oovernight` for `ShippingKinds.Overnight`, see
[finding_overnight_token_path_slips.md](../findings/finding_overnight_token_path_slips.md)) does its damage
by propagation, not by the first write:

- Slips happen on a sample's first qualified write; 0 of 401 later writes slipped after a correct first
  write, while a model that slipped repeats its own typo (9b: 18 of 27).
- A correct hint does not undo it: tiel received "Closest member: ShippingKinds.Overnight" 14 times, said
  it had made a typo, and still sent `Oovernight` in 16 later edit calls
  (`ModelTestingResults/113/Model_RunsShippingKindStepPromptFinal(4)/20261117-214637-592`).
- The first-write slip rate is a few percent (tiel 6.0% at temp 0.6, 1.25% at 0.4), and a tool result
  that shows the qualified names took tiel from 24/400 to 0/400.

The typo stays in the model's own tool-call arguments, which no server change can remove. Only the
harness, which owns the message list, can take the slipped turn out of context. A resample without the
typo in context is an independent draw: about 6% x 6% = 0.4% for tiel after one retry, before any hint.
Add the qualified names and the measured rate is 0/400.

This works only for clients we own (the ModelEval tests and PlanStepRunner); other MCP clients get the
server-layer recovery only.

## Proposal

### Where it lives

All of it is in `RoslynSentinel.Common`, so the ModelEval tests and PlanStepRunner share it:

- `ModelAgentRunner.RunCoreAsync` (`RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs:223`) appends the
  assistant turn with `messages.Add(modelMessage)` at `:434` and each tool result at `:576`.
- Requests are stateless, so the full history is sent every turn (`LmStudioAgentClient.ToInputItems`,
  `RoslynSentinel.Common/LmStudioAgentClient.cs:716-762`, comment at `:720`). Dropping messages from the list is
  enough to remove them from the model's view.
- Precedent for keeping the request and the transcript apart: `LoopAssistTracker.CollapseForReplay`
  (about `:357`) rewrites only the request copy, and the transcript keeps every turn.

### Trigger (decided: structured signal, no text parsing)

The rollback fires only when a tool result carries the server's near-miss signal: an
near-miss item in `structuredDetail[0]` (receiver type, replacement text, ranked candidates, and whether the
wrong name is declared as a member anywhere in the solution). The harness never matches error text.
Without the item, or when the wrong name is declared elsewhere (more likely a wrong receiver than a slip),
the normal error path runs unchanged. A name the model means to declare in a later call cannot be told
apart from a slip; the per-name budget below bounds that cost to one resample.

### Action

After the turn's tool calls run, if one result carries the signal and rollback budget remains:

1. Make sure nothing from the turn is applied. If the server refused the write (the expected case), nothing
   needs undoing. If any other call in the same turn wrote successfully, see Open questions.
2. Remove `modelMessage` and its tool messages from `messages`, including the reasoning.
3. Insert the injection (below) and request the turn again with the same tools and settings.
4. Record the rollback in the transcript (below).

### Injection forms (option; default A)

| Form | What the model sees | Why |
| --- | --- | --- |
| A. Synthetic lookup pair (default) | An assistant turn that calls a member-listing tool on the receiver type, then a tool result listing `ShippingKinds.Overnight`, `ShippingKinds.Airmail`, ... | Closest to the measured 0/400 arm (qualified names in a tool result). It keeps the assistant, tool, assistant role order and gives the model nothing to reply to. |
| B. Amend the previous tool result | The last tool result before the slip gains a member list | Reads as if the tool had supplied it. Invalidates the prompt cache from that message on. |
| C. User turn | "The members of ShippingKinds are ShippingKinds.Overnight, ..." | Simplest. Weak models may answer it conversationally or treat it as a new instruction. |
| D. None | Plain resample | Baseline. Tells us how much of the gain is the discard alone. |

The member list text is built from the server's candidates (symbol display strings), never from a
template, so the hint cannot introduce a slip of its own.

### Budget

- One rollback per (receiver type, bad name) per run. A second slip on the same name after the injection
  goes through the normal error path, so a systematic failure cannot loop.
- A per-run cap (proposed 3) on all rollbacks.

### Transcript and reporting (decided: the transcript keeps everything)

- The discarded turn stays in `AgentTranscript.Turns` (`RoslynSentinel.Common/AgentLoop/AgentTranscript.cs:174`),
  marked as discarded with the reason, the signal fields and the injection form. Today nothing records a
  discarded turn, so this needs a new field on `AgentTranscriptTurn` (`:216-251`); the per-turn `Assists`
  list (`AgentAssist`, `LoopAssist.cs:16`) records only injected text and does not fit.
- The transcript summary and `results.csv` gain a rollback count, so pass rates with rollback are never
  read as pass rates without it.

### Server substitution records

When the server's eval-only auto-correction option is on
([proposal_server_slip_recovery.md](closed/proposal_server_slip_recovery.md), parts 3 and 5), each substitution
comes back as a record in the tool result's `_meta` (tool, file, line, from, to, diagnostic id, candidate
count, display mode). The harness:

- copies the records onto the call's `AgentToolCallRecord` (`AgentTranscript.cs:253`), as a new field;
- keeps them out of the model-facing content (`ModelFacingToolContent`, `ModelAgentRunner.cs:978`);
- adds a per-run substitution count to the transcript summary and `results.csv`;
- reports a passing run with any substitution or rollback as an assisted pass, never as a plain pass.

If `_meta` does not reach the harness (server plan Step 8), the server puts the records in a result field
that the harness strips before the model sees it.

### Measurement before turning it on

Extend `ShippingKindReplayTests` (`RoslynSentinel.Tests.ModelEval/ShippingKindReplayTests.cs:31`): seed the
transcript up to the slip turn, drop that turn, seed the injection for each form A-D as an
`AgentTranscriptTurn`, then sample the next write. Today the replay seeds only a `KeepTurns` prefix and
stops at the `.O` decision before the call runs (`SeedConversation`, `ModelAgentRunner.cs:767-809`;
`EarlyStopLoggerProvider`, `ShippingKindReplayTests.cs:335`), so seeding a synthetic turn is new work.
Report the second-attempt slip rate per arm, 200+ samples each. If arm D is already close to 0, ship D
and skip the injection.

## Alternatives considered, not pursued

- **Rewind to the dot with a prefill.** Cut the output before `.O`, force the `.` token, continue. It is
  the most precise fix but needs raw completions with the chat template, prefill inside tool-call
  arguments, and an endpoint that supports it. `/v1/responses` does not.
- **Cancel the stream mid-write.** `EarlyStopLoggerProvider` can already cancel on a regex, but a generic
  version would need the list of valid names before the write, which is the "show the values first"
  requirement this proposal avoids.
- **Logit ban.** Works only when the intended name is known, and `/v1/responses` ignores `logit_bias`
  (finding open item 6).
- **Leave the turn in and only add the hint.** That is the current behaviour and fails (tiel: 14 hints,
  16 repeated typos).

## Open questions

- **Multi-call turns.** If an earlier call in the same turn wrote successfully, the options are: undo it
  through the server's undo before discarding (complete, more moving parts), or skip the rollback and use
  the normal error path (simple, misses some cases). Recommendation: skip for the first version and
  count the skipped cases in the transcript.
- **Synthetic tool name.** Form A must name a tool the model can call (the eval tool list is filtered by
  `FilteringToolHost`), or the model may try to call a tool it does not have. Which tool and argument
  shape best matches the eval tool lists is open.
- **Counters.** How a discarded turn counts for the turn cap, `ReasoningReplayPolicy.Observe`
  (`assistantTurnCount`) and the identical-success and repeated-failure counters. Proposed: it counts for
  the wall clock and turn cap only.
- **Is form A honest enough for evals?** It fabricates a turn the model did not take. The transcript
  records it, but reviewers reading `agent.log` need the same marker.

## Cost / risk

- Touches `ModelAgentRunner.RunCoreAsync`, `AgentTranscript`, the summary and `results.csv` writers,
  and the replay test. No server change beyond the signal.
- Eval validity: the rollback can hide a real capability gap. The recorded count and the per-name budget
  limit this.
- Costs one extra generation per rollback (the full turn, reasoning included) and one prompt-cache miss
  for form B.

## Related

- [proposal_server_slip_recovery.md](closed/proposal_server_slip_recovery.md) - the signal this consumes, and
  recovery for clients we do not own.
- [finding_overnight_token_path_slips.md](../findings/finding_overnight_token_path_slips.md) - evidence,
  open items 4 and 6.
- TODO.md "Token-path slip recovery design".
- Memory: project_token_path_slips_eval_rules.

## From TODO.md (2026-11-25)

Open follow-ups from the TODO.md item "Slip recovery follow-ups (2026-11-19)", NOT DESIGNED:
1. Near-miss items (`structuredDetail[0].nearMisses`, auto-correct) for `CreateFile`, `Member`, `MethodSignature` and the other helper-based tools (tiel fell back to `CreateFile` and got only the hint text).
2. A `ReplaceSnippet` snippet-miss result echoes the model's `oldContent` including the wrong name, which hide mode cannot remove.
3. The hide-only arm records nothing for ambiguous items (decided no).
4. The eval arms ran on host presets; a defaults-sampler arm (fresh model load) was not run.
5. Harness-side rollback and resample (this proposal).

Finding: hints and hiding the name do not break a retype loop, auto-correct does ([finding_overnight_token_path_slips.md](../findings/finding_overnight_token_path_slips.md)).
