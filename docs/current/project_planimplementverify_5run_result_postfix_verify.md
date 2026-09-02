---
name: project_planimplementverify_5run_result_postfix_verify
description: "Post-message-strengthening validation batch: 3/5 pass, same rate as pre-fix — but the 2 failures are two NEW, distinct signatures (reasoning-loop-with-no-tool-call, and a false-successful whole-file-comment-out ApplyDiff) neither targeted by nor catchable by the CS0103/orientation-breaker/ReadFile message fixes or the new per-tool error budget"
metadata:
  type: project
---

## What happened

Ran a fresh 5-run `PlanImplementVerify` batch against `.113` (clean rebuild, same MTP-disabled
config) specifically to validate [[project_directive_error_messages_wiggle_room_theory]]'s three
message-strengthening fixes. Result: **3/5 pass — identical rate to the batch that surfaced the
original CS0103/wrong-path failures**, before those fixes existed.

This is not evidence against the wiggle-room theory — it's evidence the theory was never tested by
this batch, because neither failure in this run hit any of the three rewritten messages:

### Run 2 (20260902-061735-790): reasoning loop, zero tool calls, never edited the file

5 turns total. Turns 1-4: read both files, tried `ModifyModifier(action: add, modifier: "internal")`
(correctly errored — modifier tool doesn't handle accessibility), correctly self-corrected to
`ChangeAccessibility` on the next turn (1 error, 1 clean retry — exactly the intended
self-correction shape, no thrashing). Turn 5: the model's own reasoning text cycles the same 4
sentences verbatim ("But this doesn't make sense... Actually, I think the simplest fix is... But
this doesn't make sense...") for the entire turn, producing no tool call and empty `Content`. The
model never attempted to edit `BlockConverter.cs` at all — the mechanical `AssertFixApplied` check
failed because the original `ReformatWholeFile` call was simply still there, untouched.

No error tool call ever fired on this turn — the model got stuck deliberating with itself, not
retrying a failed action. None of the three strengthened messages (all triggered by tool-call
*errors*) had any chance to intervene, because nothing errored.

### Run 3 (20260902-062730-159): ApplyDiff "succeeded" but commented out the entire file

5 turns, 1 error (same `ModifyModifier`→`ChangeAccessibility` self-correction as run 2, not
thrashing). At turn 2, the model called `ApplyDiff(changesetFormat: files, action: apply)` with a
full replacement body for `BlockConverter.cs` where **every single line is prefixed with `//`** —
the entire original file, including the class declaration and both unrelated methods, commented
out wholesale. `ApplyDiff` returned `isError: false` (a fully-commented file is syntactically valid
C#), `Build` at turn 4 returned `errorCount: 0` (dead code doesn't fail a build), and the model
declared success at turn 5. Its own verify-phase review, given the same on-disk state, correctly
caught the problem and answered `VERIFIED: FAIL`.

This is a **false-successful edit**: neither the tool call nor the build ever produced an error, so
again none of the three rewritten error messages had a chance to fire. The failure exists entirely
outside the space those fixes touch.

## How to apply

Both signatures are catalogued as open gaps, not yet designed against:

1. **Reasoning-loop-with-no-output** — a model can burn its full turn re-deriving the same
   conclusion without emitting a tool call or content, then simply run out of turns/never act. No
   existing mechanism (turn cap, wall-clock cap, error-tool budget) catches "converged with
   `StopReason.ModelFinished` but never touched the target file" as distinct from "converged after
   correctly finishing." `AssertFixApplied`'s mechanical checks catch the *result* (file unchanged)
   but nothing surfaces that the *cause* was an internal loop rather than a deliberate no-op.
2. **False-successful ApplyDiff** — `ApplyDiff` and `Build` both validate syntactic/semantic
   correctness, not fidelity to the original file's *content* outside the intended edit region.
   Commenting out an entire file passes both. [[project_planimplementverify_5run_result_2]]
   already flagged "member vs container accessibility, losing track of own edits" as model
   reasoning bugs rather than RoslynSentinel defects — this is the same category, a new instance.

Neither is caused by, nor fixed by, [[project_directive_error_messages_wiggle_room_theory]]'s
changes or the new [[per-tool error budget]] (`AgentToolErrorAssertions`) added this session — both
of those target *retry* behavior after an error, and these two runs show a 9B model failing in ways
that never produce an error to retry against. If pursuing this further, the more relevant next
question is probably: should `AssertFixApplied` (or a new check) flag "unrelated content changed
even though the build passed" as its own failure category, separate from "the specific required
change wasn't made" — since run 3's diff would fail exactly that check today only via the
`Does.Contain("public string UnrelatedMethodBefore...")` assertion, which happened to also catch
it, but for the wrong stated reason (it reports as "should no longer call ReformatWholeFile" first,
before reaching the unrelated-content checks).
