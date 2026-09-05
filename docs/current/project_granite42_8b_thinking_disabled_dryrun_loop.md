---
name: granite42_8b_thinking_disabled_dryrun_loop
description: "Disabling thinking on granite-4.2-8b sped up each turn but caused an unrecoverable 12-turn dry-run ChangeAccessibility loop, burning the same 10-min wall-clock cap with a worse outcome"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T06:03:20.880Z
---

Same smoke test as [[project_granite42_8b_verbose_apply_turn_blows_wallclock]]
(`-MinimalTools -Temperature 0.0 -TopP 1.0`, `Model_FixesWholeFileRewriteBug_PlanImplementVerify`
against .112), re-run with the user disabling "thinking" in LM Studio for granite-4.2-8b. Plan
phase converged cleanly and fast (6 turns, ~26s-3min each, same correct plan as before: reuse
`ReplaceBlockFormatted`, change to `internal`, remove `ReformatWholeFile`).

Implement phase: turn 3 correctly called `ChangeAccessibility` (private→internal, no `dryRun`).
Turn 4 called the **identical change again**, redundantly (every turn now shows
`Reasoning: (none)` — no reasoning trace at all, unlike the thinking-enabled run). Turn 5 re-read
the file to check state. From turn 6 through turn 17 — **12 consecutive turns** — the model
called `ChangeAccessibility` with `dryRun: true` and the exact same already-satisfied target
state ("undo the duplicate change; revert to internal static (already correct)"), each call a
no-op, roughly 35-45s apart, until `WallClockCapExceeded after 17 turn(s), 00:10:29`. It never
reached `ApplyDiff` on `BlockConverter.cs` — the actual bug fix was left more incomplete than the
thinking-enabled run's turn-4 cutoff, despite running 17 turns instead of 5.

**Why this matters**: disabling thinking did fix the specific problem it was meant to fix (no
more single 14-minute reasoning blocks — every turn now completes in under a minute), but traded
it for a different, arguably worse failure: with no visible reasoning trace, the model has no
apparent mechanism to notice "I already fixed this, the dry-run confirms it, stop retrying" and
just loops. This is a new, distinct failure mode from both
[[project_granite42_8b_verbose_apply_turn_blows_wallclock]] (verbose-but-correct single turn) and
[[project_oldblock_not_found_double_replace_bug]] (a different double-replace bug, already
confirmed a genuine reasoning-depth limit) — here the model isn't confused about the *edit*, it's
stuck re-verifying an edit it already confirmed succeeded.

**How to apply**: turning off thinking is not a clean win for this model on this fixture — total
wall-clock to failure was about the same (~10 min either way) and the on-disk end state was worse
with thinking off. If pursuing this lever further, look at whether a `dryRun: true` no-op result
should be fed back into the prompt more assertively ("this change already matches your target,
stop calling ChangeAccessibility and move to editing BlockConverter.cs") rather than trusting the
model to infer that from a repeated identical tool result. Don't treat "thinking disabled" as
strictly better just because individual turns are faster — check turn count AND whether the fix
actually progressed, not just latency.
