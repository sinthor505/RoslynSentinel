---
name: disambiguated_v3_result_solution_preloaded_line
description: 5-run .113 batch after porting PIV's "solution already loaded" line + consolidated constraints to Disambiguated — orientation line confirmed live-working; 2/4 pass excluding one infra crash
metadata:
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T08:25:23.638Z
---

5-run `.113` `MinimalGuidanceDisambiguated` batch after the changes in commit `331c1e8` (re-added
PIV's "solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution" line, plus
consolidated the two "don't touch unrelated code" constraints into PIV's single affirmative
sentence). See [[feedback_verify_per_fixture_workspace_lifecycle_before_porting_prompt_lines]] for
the back-and-forth that preceded this — this batch is the live verification that thread called for.

**Orientation line confirmed working**: turn 1 of the first run (archived at
`ModelTestingResults/113/.../20260905-065222-325/agent.log`) goes straight to `ReadFile` on
`BlockConverter.cs` with zero `ListWorkspaceSolutions`/`LoadSolution` calls — the wasted
orientation turn PIV had is eliminated here too, same mechanism, now proven for this fixture
specifically rather than assumed from code.

**Results (5 runs total, 1 smoke test + 4 batch)**:
- 2 passed (`ModelFinished`, correct fix, `AssertFixApplied` green)
- 1 failed but completed (`ModelFinished` after 16 turns) — reasoning, tool choice (used
  `ChangeAccessibility` correctly), diff content, and build (0 errors) all looked identical in
  shape to the passing runs; root cause of the assertion failure not pinned down (tool-error
  budget was 5/8 total, 2/4 max-per-tool — under both caps, so not `AssertWithinBudget`; textual
  checks for `UnrelatedMethodBefore`/`After` formatting and `ReplaceBlockFormatted` presence all
  matched in the raw `ApplyDiff` payload). Most likely candidate by elimination is
  `FunctionalFixVerifier`'s reflection-invoke check, but the fixture's temp directory was already
  cleaned up by the time this was investigated, so it couldn't be confirmed directly.
- 1 `TestRunAborted` — testhost process crashed outright, an infra failure unrelated to the model
  (see [[project_modeleval_testhost_crash_gotcha]]).
- 1 `TurnCapExceeded` after 40 turns/15m36s — got stuck in a loop re-reading `BlockConverter.cs`
  repeatedly near the end ("Let me read the file again to see the actual content"), never
  reaching a tool call that would resolve it. Different failure shape from the pre-fix baseline's
  own-copy-of-helper or wrong-workspace-path failures — this one is a stall, not a wrong action.

**Pass rate**: 2/5 raw (40%, same as the pre-change baseline in
[[project_disambiguated_prompt_n20_result]]), but 2/4 (50%) excluding the infra crash as noise.
Small n (this is 5 runs, not 20) — not strong enough to claim a real uplift over the 40% baseline,
but directionally consistent with removing a wasted turn and tightening the prompt without making
anything worse.

**Why this matters**: this closes out the live-verification loop from
[[feedback_verify_per_fixture_workspace_lifecycle_before_porting_prompt_lines]] — the port was
correct, the earlier revert was the mistake, and this is now confirmed via transcript rather than
code-reading alone.

**How to apply**: if pursuing this fixture further, a larger n (10-20 runs) is needed before
treating 40%→50% as a real signal rather than noise. The turn-40 stall pattern (repeatedly
re-reading the same file with no new action) is worth watching for recurrence — if it shows up
again on a future batch, it may be a distinct failure mode worth its own memory, similar to
[[project_granite42_8b_thinking_disabled_dryrun_loop]]'s no-op loop but on a different model
(qwen3.5-9b-coder) and a read rather than a dry-run write.
