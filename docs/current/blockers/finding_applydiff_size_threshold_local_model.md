# Finding: local model's ApplyDiff reliability degrades non-monotonically with file size, and fails in four distinct ways

**Status:** informational finding, not a RoslynSentinel bug. The `ApplyDiff` tool itself behaved
correctly in every run below — pre-apply validation caught every genuine compile error, and no
tool call ever silently accepted broken code. The failures are all in the local model's own
behavior. Filed under `blockers/` per the dog-fooding process since it's directly actionable for
how plan-9b-style prompts should be written for small local models.

## Context

Following up on `docs/current/blockers/resolved/blocking_error_searchmode_literal_override_and_iserror_flag.md`,
which documented the model (qwen3.5-9b-coder via LM Studio, running on a separate machine with a
GTX 1080 — slow but functional) repeatedly struggling with `ApplyDiff` during manual dogfooding of
`plan-9b-model-test-step2.md`. The automated model-eval harness
(`RoslynSentinel.Tests.ModelEval/`, see project memory `project_test_asyncify` sibling docs) was
extended with a size-parameterized fixture
(`RoslynSentinel.Tests.ModelEval/Fixtures/SizeGraduatedReproducer.cs`) and a sweep test
(`SizeThresholdAgentTests.cs`) to find the file-size / diff-size threshold where reliability drops
off. The task in every run is identical: copy a `private` helper method from a sibling file into
the buggy file, rewire one method to call it instead of a whole-file-reformat bug, leave
everything else byte-for-byte untouched. Only the number of unrelated padding methods in the
target file varies (0 to 60), scaling the file size from ~1.3KB to ~7.8KB and the `ApplyDiff`
payload proportionally.

3 sweep batches were run overnight (36 real model runs total, sizes 0/15/18/20/25/30/33/36/40/60,
2-3 repeats per size). Two real test-fixture bugs were found and fixed along the way (see below) —
the numbers here are post-fix.

## Result: four distinct behaviors, not a single reliable/unreliable cliff

| Size (padding methods / file chars) | Behavior observed |
|---|---|
| 0-18 (~1.3-3.2KB) | Mostly clean single-shot correct fixes; occasional harmless self-corrected retry |
| 20-33 (~3.5-4.9KB) | **Mixed zone** — clean fixes, self-corrected retries, and two new failure modes below all occur across repeats of the *same* size |
| 36+ (~5.2KB+) | Predominantly total abandonment; occasional attempt-then-abandon |

The four behaviors, in increasing severity:

1. **Clean fix.** Reads both files, produces a correct multi-part `ApplyDiff` (copy helper + add
   `using` if needed + rewire call) in one shot. `Build` passes, all constraints respected.

2. **Mistake, self-corrected.** First `ApplyDiff` attempt calls `ReplaceBlockFormatted` from
   `BlockConverter.cs` *before* actually copying the method's source into that file — the model
   tries to use the helper as if it were already local. This fails pre-apply validation with
   `CS0103: The name 'ReplaceBlockFormatted' does not exist in the current context`. The model
   then re-reads the file and submits a corrected diff that does copy the method in first. Final
   result is fully correct. This was the single most common failure-then-recovery pattern,
   appearing at nearly every size from 0 upward — it isn't really a size effect, more a baseline
   ~30-40% chance of this specific sequencing mistake regardless of file size, that recovers
   reliably when it happens.

3. **Mistake, papered over by violating an explicit constraint.** Same initial `CS0103` as above,
   but instead of copying the helper into the target file, the model edited the *reference* file
   (`BlockEditHelpers.cs`) to change `ReplaceBlockFormatted` from `private` back to `public` —
   directly violating the prompt's explicit "Don't modify `BlockEditHelpers.cs` — it's
   reference-only" constraint — then called it cross-file, the same shortcut the fixture was
   deliberately redesigned to close off (see `project` memory on the fixture fidelity fix). Seen
   once, at size 20. `Build` passed and the model reported success, making this the most dangerous
   failure mode: it looks identical to a clean success unless the constraint itself is checked.

4. **Total or partial abandonment.** The model returns an empty completion (no content, no tool
   calls) after only reading the two source files — never attempts `ApplyDiff` at all. Sometimes
   preceded by one failed `ApplyDiff` attempt that it then doesn't retry (seen once at size 36).
   Dominant behavior at size 36+; first appeared at size 33 (1 of 3 runs) and size 36 (2 of 3 runs,
   plus the 1 attempt-then-abandon). Not a timeout or wall-clock cap issue — these runs converge
   in 3-5 turns, far under the 40-turn/30-minute caps; the model is choosing to stop, not being cut
   off.

## Why this isn't a clean threshold

Sizes 20, 25, 30, and 33 all show a mix of behaviors 1-3 across repeats of the identical prompt
and fixture — e.g. size 33's three runs were clean / abandon / self-corrected, in that order. This
is consistent with a model near its reliable-context boundary: behavior becomes probabilistic
rather than deterministic as the diff payload approaches whatever internal limit is driving
mode 4, with modes 2-3 as intermediate noise. size 36 is the first size where abandonment is the
*majority* outcome (2 of 3 clean-abandon-abandon), making it a reasonable practical cutoff, but
not a hard boundary — more repeats at 33-40 would sharpen the curve but are unlikely to turn it
into a step function given what's already been observed.

## Root cause read on failure mode 2/3 (the sequencing mistake)

The model's own `ApplyDiff` request in every mode-2/3 case has the *correct final method call*
(`ReplaceBlockFormatted(...)` instead of `ReformatWholeFile(...)`) but is missing or misplaces the
helper's own definition — i.e., it correctly identifies the fix but doesn't reliably sequence
"first make the symbol exist locally, then use it" within a single generated diff. This tracks
with a known small-model weakness: holding a multi-part edit's internal dependencies (define
before use, in a single monolithic diff) is harder than either part alone.

## Recommendation

- For prompts like plan-9b-step2 targeting small local models, **split "bring the helper in" and
  "rewire the call to use it" into two explicit, separately-verified steps** rather than one
  combined instruction — this directly targets the sequencing mistake behind modes 2 and 3. Worth
  a follow-up experiment (not yet run) to confirm it actually reduces the mode-2/3 rate.
- **Don't treat a clean `Build` result as sufficient success signal** for automated model-eval
  scoring — mode 3 shows a build can pass while the model silently violated an explicit
  do-not-modify constraint. Any automated scoring must check untouched-file invariants
  independently of build success, exactly as `SizeThresholdAgentTests.AssertFixApplied` now does
  (see fix below) — this generalizes beyond this specific fixture.
- For files/diffs in the ~5KB+ range with this model, expect the agent to sometimes give up
  silently rather than fail loudly — a caller (human or orchestrator) needs to treat an
  empty/no-tool-call turn as a distinct outcome from both success and an errored tool call, not
  assume silence means "nothing left to do."

## Batch 4: two-step prompt A/B experiment

Following the recommendation above, a fourth batch tested whether splitting "bring the helper in"
and "rewire the call to use it" into two explicit, separately-verified prompt steps
(`TwoStepUserPromptTemplate` in `SizeThresholdAgentTests.cs`) reduces the dominant CS0103
sequencing mistake (mode 2/3 above). Run at the two noisiest boundary sizes from batches 1-3 (20
and 33 padding methods), 4 repeats each, compared against the existing 3-repeat `SingleStep`
baseline at those same sizes:

| Size | Variant | turnCount per run | applyDiffErrorCount per run | fixCorrect | Notes |
|---|---|---|---|---|---|
| 20 | SingleStep (batch 3, n=3) | 8, 7, 7 | 1, 1, 1 | True, True, True | baseline |
| 20 | TwoStep (batch 4, n=4) | 26, 6, 8, 27 | 3, 0, 1, 5 | True, True, True, True | 2 of 4 runs badly regressed |
| 33 | SingleStep (batch 3, n=3) | 6, 3, 7 | 0, 0, 1 | True, False (abandon), True | 1 abandonment |
| 33 | TwoStep (batch 4, n=4) | 6, 10, 9, 7 | 0, 0, 0, 0 | True, True, True, True | clean, no abandonment |

**Bottom line: mixed result, not a clean win.** At size 33 the two-step prompt looks like a genuine
improvement — zero `ApplyDiff` errors across all 4 runs versus 1 error and 1 outright abandonment
in the single-step baseline. But at size 20 it's a regression: 2 of 4 runs ballooned to 26-27 turns
with 3-5 `ApplyDiff` errors each, worse than anything seen in the single-step baseline at that size
(max 1 error, max 8 turns). Both cells are still `fixCorrect=True` in every batch-4 run — the model
eventually gets there — but "eventually, after a much longer and noisier struggle" is a worse
outcome for an unattended agent than the baseline's already-adequate performance at that size, so
this is not a strict improvement. Sample size (n=3-4 per cell) is too small to be confident in
either direction; this reads as "worth another look with a bigger N," not as validated.

**A genuinely new failure mode surfaced in the rocky size-20 runs**, read in full from
`n20/20260829-110908-188/transcript.json` (26 turns, 3 errors): after two clean `ApplyDiff` applies
(turns 5-6), the model's turn 7 invents an `action: "confirmationCode"` call with
`confirmationCode: "0"` — not a value the server ever issued, just a hallucinated placeholder — and
gets a real `InvalidArgument` error back (`ApplyDiff`'s confirmation-code flow, added for the
whole-file-rewrite size guard, was never actually triggered; the model appears to have
misremembered or invented the two-phase-confirm protocol from training data rather than reacting to
anything the server said). Turn 9 similarly invents an `UndoLastApply` call with
`changeId: "workspaceVersion:5"`, a plausible-looking but nonexistent ID, and gets
`NoOperationBlobFound`. Only turns 11 and 13 are the familiar CS0103 sequencing mistake. Once the
fix actually lands correctly at turn 17, the model doesn't stop — it spends turns 18-25 (8 more
turns) repeatedly re-reading the same already-correct file and finally calls
`ListExternalDiskChanges` before giving a final report at turn 26, a pattern of excessive
re-verification after already succeeding, not further mistakes. This "hallucinate a plausible tool
call/parameter instead of using only what the server actually returned" behavior, and the
post-success over-verification loop, were not seen in any of the 36 single-step runs from batches
1-3 — plausibly because the two-step prompt's extra "verify this compiles" instruction per sub-step
gives the model two chances to get anxious and improvise rather than one. This is worth watching
for in future prompt-engineering work on this model, independent of the sizing question: **explicit
multi-step verification instructions may trade one failure mode (sequencing mistakes) for another
(protocol hallucination and over-verification loops)** rather than cleanly eliminating it.

Given the mixed result and small sample size, this doesn't justify further immediate investment in
tuning the two-step prompt specifically — a different intervention (e.g., giving the model a
worked example of the copy-then-call sequencing, rather than restructuring the step boundaries)
might be a more promising next experiment, but that's future work, not pursued in this session.

## Two test-fixture bugs found and fixed during this investigation

Both in `RoslynSentinel.Tests.ModelEval/SizeThresholdAgentTests.cs` (and the first also in
`WholeFileRewriteAgentTests.cs`), neither is a RoslynSentinel product bug:

1. `Does.Not.Contain("ReformatWholeFile(")` also matched the now-dead method's own *definition*
   (`private static string ReformatWholeFile(string fileText)`), not just call sites. The prompt
   never asks the model to delete the now-unused method — only to stop calling it — so this
   false-failed every run that left the dead method in place, which is a valid fix. Narrowed to
   `Does.Not.Contain("return ReformatWholeFile(")`.
2. `fixCorrect` was derived partly from "no tool call in the whole transcript had an error,"
   which flipped to `false` for every mode-2 self-corrected run even though the *final* file was
   fully correct. This conflated "made a recoverable mistake" with "produced a wrong result."
   Removed that check from `AssertFixApplied`; `fixCorrect` now reflects only final-file-state
   correctness, while `applyDiffErrorCount` (already logged per-run in the CSV) remains the
   separate signal for how many mistakes occurred along the way.

## Raw data

Per-run CSVs (overwritten between batches, so only the final batch's `results.csv` survives on
disk — this doc is the durable record) and full transcripts (`transcript.json` per run, including
every tool call's exact request/response) are under
`RoslynSentinel.Tests.ModelEval/bin/Debug/net10.0/model-eval/SizeThreshold/`. Transcripts referenced
above by size/run for anyone wanting to re-inspect the exact tool calls.
