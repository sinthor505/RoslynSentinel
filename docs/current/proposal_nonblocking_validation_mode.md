# Non-blocking validation mode (report, don't reject)

## Motivation

`ValidateAndApplyHelper.ValidateAndApplyAsync` (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:49-57`)
rejects any change whose post-edit solution introduces new compiler errors, and writes nothing. This
is the guarantee that keeps a model from wholesale-destroying a codebase, and it works — but it also
straight-jackets legitimate refactoring, because a coordinated change spanning several files cannot
be green after each individual call, only after the last one.

PlanStepRunner run `20260911-205633-213`, step `02-phase1-types-and-engine-fix`, is the worked
example. The step asked for `BuildResult`'s `bool BuildSucceeded` to become a 3-state
`BuildOutcome Outcome`, with every call site fixed "in the same pass" so the solution is clean at
the *end* of the step. The model had no way to express that. It:

1. Tried to land `BuildResult.cs` alone — rejected (CS1739/CS1061 from `BuildEngine.cs` and
   `BatteryTwentyTests.cs`, files it had not yet touched).
2. Landed the purely-additive pieces (new enum value, new static helper) — succeeded, since
   additive changes break nothing.
3. Tried `BuildEngine.cs`, expecting `BuildOutcome`/`Outcome` to exist — rejected twice (CS0103),
   because step 1 had never actually landed.
4. Re-read `BuildResult.cs`, confirmed it was still the old shape, reasoned "I need to do all
   changes together," and re-submitted via `ReplaceSnippet`'s `action: apply` — a path that skips
   the gate for that one call.
5. Only then could `BuildEngine.cs` land.

5 failed tool calls and ~8 turns spent discovering, by trial and error, an escape hatch that already
exists — while the compile errors it needed most (the ones naming exactly which files still
referenced the old shape) were delivered as rejection text attached to changes that were thrown
away.

The observation that motivates this proposal: **in every one of those rejections, the validation
result was the useful part and the rejection was the obstacle.** A model that can see "this landed;
there are now 15 errors, here are the top groups" has strictly more information than one that sees
"rejected, here are the errors, nothing was written" — it can act on the former by fixing the next
file, where the latter forces it to reconstruct what state the solution is even in (which is what
turn 13's re-read of `BuildResult.cs` was for).

## Proposal

An opt-in write mode in which `ValidateAndApplyAsync`:

- **always applies** the change to disk,
- **always runs** the same `ValidationEngine.ValidateChangesAsync` it runs today,
- **always reports** the resulting error count and a capped sample of the actual diagnostics,
  whether validation passed or failed,
- **never rejects** the call on validation grounds.

No scoring, no better/worse classification, no convergence heuristics. Report the raw count and a
bounded sample of real diagnostics; the model reads the direction the same way it reads compile
output everywhere else. Deliberately avoids applying arbitrary "improving"/"regressing" judgments
the server is not in a position to make correctly — 15 CS1739s that all trace to one root cause and
3 unrelated new errors are not comparable by count alone, and only the caller knows which it is
looking at.

### Output shape

Reuse `DiagnosticReportExtensions.GroupBySeverity(topN)`
(`RoslynSentinel.Common/DiagnosticReport.cs:33-41`) — already the shared capped-summary component
behind `GetDiagnostics`' `summarize=true` path and `Build`'s `ErrorSummary`/`WarningSummary`. Its
existing contract (group by diagnostic id, sort by group size descending, take top N, up to 10
distinct locations per group) is exactly the shape wanted here, and its XML doc already specifies
calling it on the full uncapped list so counts reflect the whole run rather than the sample.

Per call, alongside the normal apply result: total error count, and `GroupBySeverity(topN)` over the
full diagnostic list. The cap exists for output economy, not to withhold information — the total
count is always exact even when the sample is truncated.

### Audit trail

A write that lands despite failing validation **must** be distinguishable after the fact from a
normally-validated apply — in the operation blob, and in whatever `UndoLastApply` reports. Without
this, a run that ends green looks identical to one that never had a broken intermediate state, and
the distinction is exactly what post-run review (the review this proposal came out of) depends on.
`OperationBlobWriter.WriteApplyBlobAsync` is the natural place to carry the flag, since
`ValidateAndApplyAsync` already calls it with the apply result in hand
(`ValidateAndApplyHelper.cs:76-77`).

### Opt-in, not default

This weakens the never-write-broken-code guarantee that exists because of real divergence-era pain,
so it is scoped to an explicit mode a caller enters deliberately — for a task known to span several
files and known not to be green until the last one. Default behavior for every write stays strict.

Open: whether the mode is entered per-call (a parameter on the mutating tools), per-plan-step, or
per-session. Per-plan-step is appealing given `PlanStepRunner` already isolates steps, and a plan
step is exactly the unit that knows "this one spans four files."

### Stall backstop

Non-blocking validation tells a model it is stuck ("15 errors", "15 errors", "15 errors") but does
not *stop* it. A model that ignores its own stall signal burns the same turns it does today, with
richer output each time. So this mode needs a companion circuit-breaker keyed on **no new
information across N consecutive calls** — not a fixed call budget (see Alternatives below for why
the budget framing was dropped). On trip, revert to strict validation rather than halting outright:
the model can still make progress, it just has to be green again to do it.

This is close in spirit to the existing `IManualCircuitBreaker` (batch-failure-rate, manual reset
only — `RoslynSentinel.Common/ICircuitBreaker.cs:19-45`) and may be better implemented as a tighter
threshold on that breaker than as a fifth breaker type. Worth checking before building anything new:
the 60-turn case noted in `IUnrecoverableBreaker`'s remarks suggests the existing thresholds are
already looser than intended.

## Alternatives considered, not pursued

Kept here deliberately — both were live proposals during the design discussion and may be worth
revisiting if this proposal proves insufficient.

### Bypass budget (N unvalidated calls, then relock)

Enter a bypass mode granting a small budget (2-3) of validation-free mutating calls, after which the
breaker relocks and strict validation resumes. A refinement added: run validation on every call
anyway without failing, and relock early — on the first call that validates clean *or* on budget
exhaustion, whichever comes first — so a model that fixes things in one call does not get two more
unvalidated writes it has no reason to make.

Dropped because the budget is the wrong dimension to meter. A coordinated change legitimately
spanning five files hits a wall one call short of a working state; a model thrashing on a single file
burns its budget on three useless attempts. Call count does not distinguish these. Non-blocking
validation plus a stall detector keyed on *information gained* meters the thing that actually
matters, and needs no budget sizing.

Worth revisiting if non-blocking mode turns out to need a hard ceiling on how far a run can drift
from green — a budget is a cruder but simpler bound than a stall detector, and simpler to reason
about when reviewing a run after the fact.

### Trend reporting / convergence classification

Have the server classify each validation result against the previous one — "less broken", "more
broken", "still broken", "green" — possibly tracking the distinct diagnostic-id set as well as raw
count, so "you were seeing CS1739+CS1061, now only CS1739" could be reported.

Dropped in favor of reporting the raw count and a capped sample and letting the model judge
direction itself. The server cannot reliably tell whether a count change means progress: 15
CS1739/CS1061s from one root cause collapsing to 0 in one call, and a bad edit adding 3 new distinct
errors while fixing none, are not distinguishable by count, and a wrong "improving" label is worse
than no label. The model reads compile diagnostics natively everywhere else; it needs data, not a
verdict.

Worth revisiting only if observed runs show models consistently misreading the raw numbers — which
would be evidence for a classifier, but should be evidence-driven rather than assumed up front.

## Relationship to other in-flight proposals

- **Staged/uncommitted writes** (prerequisite doc: `docs/current/design_read_chokepoint.md`) solves
  a *different* failure mode from this one, and the two are complementary rather than alternatives.
  Staging addresses the coordinated-multi-file case — the model knows what it wants and is blocked
  only by per-call sequencing; stage N files, commit once atomically, never expose a broken
  intermediate state at all. It does **not** address thrashing: a model unsure what to write will
  stage twenty broken attempts as readily as it commits them, and staging removes the per-call
  compile feedback that currently forces a checkpoint, so a confused model could drift further
  before anything stops it. This proposal is the one that keeps feedback flowing every call.
  If staging ships, this mode remains useful for exactly the case staging does not cover.
- **`PreviewSymbolTypeChangeImpact`/`ChangeSymbolType`**
  (`docs/current/proposal_changesymboltype_tool.md`) attacks the same motivating run from the other
  end: make the coordinated multi-file change expressible as a single atomic tool call, so no
  intermediate broken state is needed. Independent of this proposal; either could land first.

## Open items

- Where the mode is entered (per-call parameter / per-plan-step / per-session) — see above.
- `topN` for the diagnostic sample. `GetDiagnostics`/`Build` already pick values for their own
  paths; match whichever is closest in intent rather than introducing a third constant.
- Whether the stall backstop is a new breaker or a tighter threshold on `IManualCircuitBreaker`.
  Check the existing thresholds first.
- Interaction with `_sessionHalted` / `IUnrecoverableBreaker`: both are checked at the top of
  `ApplyProposedChangesAsync` (`PersistentWorkspaceManager.cs:1235-1255`) and must keep winning
  unconditionally. This mode relaxes *validation*, never drift-refusal or integrity halts.
- Whether `dryRun` remains meaningful in this mode (it currently short-circuits before apply —
  `ValidateAndApplyHelper.cs:59-63`). Probably unchanged, but worth stating explicitly.

## Status

Design proposal only — not yet implemented. Motivated by PlanStepRunner run
`20260911-205633-213`, step `02-phase1-types-and-engine-fix`.
