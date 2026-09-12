# First-person turn injection: carrying tool outcomes forward as the model's own statements

## Motivation

PlanStepRunner run `20260911-205633-213` completed all 11 steps, with intervention needed at only
three of them — and on review, none of those interventions corrected the model's *reasoning*. They
closed tooling gaps (incomplete wiring, a wrong test-fixture pattern, a repeated-failure breaker
with no exception for deliberate repetition). Compared against runs from ~10 days earlier, where
`MinimalGuidance`/`MinimalGuidanceDisambiguated` fixtures sat near a 0% pass rate and models
routinely produced malformed tool calls, >30% `ApplyDiff` failure rates, and lost track of their own
actions from one or two turns prior, this is a large step change in agent competence.

That change reframes what harness assistance is *for*. The old problem was a model that couldn't
hold a task together; assistance meant supplying missing competence. The current problem is a model
that is competent and directionally correct but occasionally loses a specific fact it already had.
That is a much narrower target, and it can be hit with a much smaller mechanism.

### The observation that motivates the mechanism

LM Studio's "reasoning budget message" injects text into the reasoning stream when a token budget is
hit. That specific mechanic isn't reusable here — it's a model-load-time sampler configuration, not
something the harness can reach into per-message. But the property that makes it effective is not
the *stream* it writes to; it's the **position** the text occupies relative to the model's own
output.

Text arriving as a tool response reads as *this one tool's opinion* — evidence to be weighed, and
easily de-prioritized two turns later. Text arriving as part of the conversation's prior-turn record,
phrased in first person, reads as *a commitment the model already made*. Models are strongly
self-consistent with their own prior assertions (the same property that makes them double down on
mistakes). This proposal borrows that property in the useful direction.

Critically, the positional difference is available at PlanStepRunner's message-assembly layer with
no LM Studio involvement at all.

## Proposal

After each tool call, the runner composes a short first-person statement of the outcome and injects
it into the loop so the model's next turn is conditioned on it. Shape:

```
My call to {toolName} {succeeded|failed} because {error message from tool}.
I need to {guidance message for that error code}.
```

Worked example — `ModifyEnum` failing to resolve its container:

> My call to ModifyEnum failed because the container was not found. I need to confirm the container
> exists and that its name matches before calling ModifyEnum again.

### Placement

Injected as its **own message between turns**, not concatenated into the previous assistant turn's
content. This keeps the positional benefit (it precedes the model's next generation as established
context) while:

- leaving the real model output byte-intact for eval review — no fabricated history,
- keeping every injection greppable in the transcript as a distinct event.

Rewriting or annotating the model's actual prior assistant turns was considered and rejected; see
Alternatives.

### Success injections, not just failures

The template's `succeeded` branch matters at least as much as the failure branch, because the
failure it targets is different. Failure injections target *wrong next action*; success injections
target *lost state*:

> My call to Member(add) succeeded. ValidateOrder now exists in OrderService.cs; I have not yet
> updated its callers.

This is the direct countermeasure to the "lost track of what it did two turns ago" class of failure.
That class is substantially reduced at current model competence, but it is also the one most likely
to resurface as steps get longer — a success injection is cheap insurance that degrades gracefully
(worst case it restates something the model already knows).

### Escalation on repeat, never verbatim repetition

If an identical call fails identically twice, re-injecting the same sentence teaches the model the
line is noise. The second occurrence must say something new — specifically, that repetition itself
is now the evidence:

> I have now called ModifyEnum twice with the same container name and received the same error. Either
> the name is wrong or the type isn't loaded — I need to determine which before a third attempt.

The repeat detector here is the same signal
[`proposal_nonblocking_validation_mode.md`](proposal_nonblocking_validation_mode.md) needs for its
stall backstop ("no new information across N consecutive calls"), and the same signal the harness's
existing `RepeatedToolFailure` breaker keys on. This is shared infrastructure, not a third
implementation — and worth unifying deliberately, because step `11-final-verification` of run
`20260911-205633-213` proved that breaker can false-positive on *intentional* repetition (that
step's own Phase 3 protocol required three consecutive identical zero-match searches as its
verification method). Any repeat detector feeding injection must distinguish "stuck" from
"deliberately repeating," or it will inject a correction against a model that is doing exactly what
it was asked.

## The correctness constraint (this is the load-bearing part)

A first-person injection is **not discounted the way a wrong tool message would be**. The model has
no mechanism to doubt its own prior turn. A wrong *diagnosis* injected first-person is close to
unrecoverable within a step: the model will pursue it past contradicting evidence, because
abandoning it means contradicting itself.

So the rule is:

> **Guidance strings are derived from the error code alone. They are never inferred from context,
> and never guess among multiple possible causes for one code.**

The `ModifyEnum` example is safe precisely because it is near-tautological: container not found →
check the container name. Contrast the unsafe version — "I need to load the solution first" when the
real problem was a wrong path, or "I need to use a different tool" when the call was fine and the
target genuinely didn't exist.

Where one code covers several real causes, the guidance must stay at the level the code actually
supports ("I need to confirm the container exists and the name matches") rather than picking one.

### Why this blocks on the error-code audit

This constraint makes the proposal dependent on
[`proposal_tool_error_code_taxonomy.md`](proposal_tool_error_code_taxonomy.md). A code that means
exactly one real failure mode can carry one honest guidance string. A catch-all cannot carry any —
and `ToolErrorMapper.ToCodeAndMessage` (`RoslynSentinel.Common/ToolException.cs:167-180`) currently
collapses every non-`ToolException` failure into `ToolErrorCode.Exception`, which is exactly the
bucket that would force guessing. `ToolErrorCode.Exception` must therefore map to **no guidance
string at all** (inject the outcome sentence, omit the "I need to" clause) until the audit tightens
its call sites.

## Error-code grouping: how to get smaller units without inheritance

A per-tool split of the code taxonomy was raised, as either per-tool types extending
`ToolErrorCode` or an `IToolErrorCode` interface implemented by per-tool error types — motivated by
not wanting one massive class holding every code.

**Neither is mechanically possible against the current shape**, and the reason is worth recording:

- `ToolErrorCode` (`RoslynSentinel.Common/ToolResult.cs:26-56`) is a `static class` of
  `const string` fields. A static class cannot be inherited from, and `const` members cannot be
  virtual or interface-implementing.
- `ResultError.ErrorCode` (`RoslynSentinel.Common/ToolResult.cs:183-184`) is a bare `string`. There
  is no declared type at the consumption point for an interface to constrain, so an `IToolErrorCode`
  would be erased to `.ToString()` at every use anyway.
- The values are serialized across MCP as strings and asserted as string literals in tests
  (`ToolErrorCode.` appears 282 times across 28 files, including several `Tests.Battery` suites).
  Any scheme that changes the wire value breaks those assertions; any scheme that preserves it makes
  the type hierarchy decorative.

There is also a substantive argument against splitting codes per-tool even if it were possible:
codes are the axis you want to **aggregate across** tools. "How many `NotFound` failures did this run
produce, across all tools" is the question the taxonomy exists to answer, and per-tool code types
fragment exactly that. Tool identity is already free at the filter chokepoint
(`context.Params?.Name`), so the `(toolName, errorCode)` tuple gives per-tool grouping without
duplicating the code space per tool.

### Recommended alternative: split the guidance registry, not the codes

Keep `ToolErrorCode` as one shared, flat set of codes — it stays small precisely *because* it is
shared, and the audit's job is to tighten meanings rather than multiply entries. Split the thing
that genuinely is per-tool: the guidance strings.

- Codes stay shared and flat, one code per real failure mode.
- Guidance lives in a per-tool registry keyed on `(toolName, errorCode)`, with a fallback to a
  generic per-code string where a tool has nothing more specific to say. This is where per-tool
  files/partials keep the unit small, and it is also the only layer with anything genuinely
  tool-specific to express — `NotFound` from `ModifyEnum` wants different remediation wording than
  `NotFound` from `ReadFile`.
- A tool contributes guidance only for codes it can actually return, so each registry piece stays
  reviewable in isolation.

If a per-tool *enum* of codes is still wanted for authoring ergonomics (compile-time checking of
which codes a tool may return), the safe version is an enum whose members map **onto** the existing
shared string constants rather than defining new values — the enum constrains the author, the string
stays the wire format and the aggregation key. Worth deciding during the audit, not before.

## Eval integrity (do not skip this)

Injected guidance is a capability assist, and if it is invisible it silently invalidates the eval —
the run no longer measures the model, and the harness cannot tell you which steps needed help. The
conclusion from reviewing run `20260911-205633-213` ("the model was directionally correct on its
own") becomes unsupportable the moment injection is on and unlogged.

Requirements:

- Every injection is recorded in the run's transcript as a first-class event (tool, code, which
  guidance string, whether it was an escalation), not just as conversation text.
- Run reporting distinguishes **passed clean** from **passed with N injections**, per step.
- Injection is toggleable per run, so a clean-baseline run remains obtainable for comparison.

### Scope caveat: not for the MinimalGuidance fixtures

The `MinimalGuidance`/`MinimalGuidanceDisambiguated` fixtures deliberately withhold guidance. Turning
injection on there would *re-add what the fixture exists to remove* — those tests would start passing
while measuring nothing. Injection is aimed at fuller-guidance runs, where the model demonstrably has
the information and loses track of it. Verify before building whether those fixtures' failures are a
state-tracking problem or a guidance-content problem; only the former is in scope here.

## Alternatives considered, not pursued

### Rewriting or annotating prior assistant turns

Editing what the model "said" can make a drifting transcript coherent retroactively, and is the most
powerful version of this mechanism. Rejected as a starting point: it fabricates history, so a wrong
injected correction poisons the transcript with something the model has no basis to doubt, and it
destroys the raw model output that post-run review depends on. Revisit only if
between-turn injection proves too weak to hold, and only with the original turns preserved
alongside.

### Guidance in the tool response only (status quo)

Already how remediation advice reaches the model. Retained — this proposal adds a channel rather than
replacing one. The gap it addresses is durability across turns, not first-turn visibility.

### Unconditional per-turn state re-injection

Injecting a full state summary every turn regardless of signal. Rejected: unconditional injection
becomes wallpaper the model learns to skip, and it costs context on every turn. Outcome-triggered
injection (every call produces exactly one short sentence) is bounded and stays legible.

## Open items

- Whether the injected message is assigned a user role, a system role, or a synthetic assistant role.
  First-person phrasing suggests assistant; attribution honesty and eval-transcript clarity suggest
  otherwise. Needs a decision against how LM Studio's chat-template handles each.
- Repeat/escalation threshold, and how the detector distinguishes deliberate repetition from a stall
  (see the step-11 false-positive above).
- Whether success injections are emitted for every successful call or only state-mutating ones
  (read-only calls arguably need no state assertion, and would be the bulk of the volume).
- Guidance string authorship: written per code during the taxonomy audit, or added lazily per tool as
  each is touched — mirrors the same open question in
  [`proposal_tool_error_code_taxonomy.md`](proposal_tool_error_code_taxonomy.md).
- Interaction with the existing `RepeatedToolFailure` breaker: does an escalation injection reset it,
  extend it, or leave it untouched?

## Status

Design proposal only — not yet implemented. Depends on
[`proposal_tool_error_code_taxonomy.md`](proposal_tool_error_code_taxonomy.md)'s audit for
per-code guidance to be honest. Motivated by the competence step-change observed between
early-September model-eval fixtures and PlanStepRunner run `20260911-205633-213`.
