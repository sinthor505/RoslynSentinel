---
name: eval-review
description: Review a batch of model-eval runs for behaviour: digest and select, grade per arm with eval-run-grader, roll up with an A/B guard, trace chosen findings. Use when the user asks to review, grade or analyse eval runs.
---

# Review model-eval runs for behaviour

The driver for the run review pipeline. Layouts, file shapes and script one-liners are in the artifact map,
`docs/current/references/reference_log_sources_and_cross_referencing.md`; names, anchors and codes are in
`scripts/eval-review/vocabulary.json`. Do not copy lists from either.

1. **Locate the runs.** Use `runs.csv`, `pwsh -NoProfile -File scripts\roslynsentinel-evalqueue.ps1 status`, or
   a path the owner gives. The arms of an A/B are often under different timestamp folders and host folders.
2. **Read the outcome.** Run the summary that matches the run type: `scripts\Get-ScenarioSummary.ps1`, or the
   arm tables (`Get-ThinkingArmTable.ps1`, `Get-ToolResultFormatArmTable.ps1`, `Get-EditArmTable.ps1`).
3. **Digest and select.**
   `pwsh -NoProfile -File scripts\Get-RunDigest.ps1 -BatchRoot <a>,<b> [-Seed <n>] [-PassesPerArm <n>]`
   digests every run, writes `digest.json` beside each transcript, and writes `selection.json` (every failed
   sample plus seeded passes per arm, skipped runs listed with reasons) into a new review folder under the
   scratch root. The script prints that folder. A single run: `-RunPath <folder or transcript.json>`.
4. **Grade.** Dispatch `eval-run-grader` in the foreground, one instance per arm in `selection.json`, at most
   two at once. Brief each with the arm's selected run paths (and digest paths) and, for a repeat pass, a
   label so it writes `review.<label>.json`. If a grader returns before its last run, resume it with
   `SendMessage` naming the next run.
5. **Roll up.**
   `pwsh -NoProfile -File scripts\Get-ReviewRollup.ps1 -ReviewDir <review folder> -Vary <field>[,<field>]`
   with field names taken from `keyFields` in `vocabulary.json`. `-Vary` names the A/B variable and is
   required when the folder holds more than one arm. Any other key field that differs between arms of one
   scenario gives `MixedComparabilityKey` and exit 1 unless `-Force` is passed. Add
   `-ReviewFileName review.<label>.json` for a labelled pass. It writes `rollup.json` and `rollup.md` into the
   review folder and `review.md` beside each valid review. Read `rollup.md`, and list any `invalid` reviews and
   `gradingDefects`.
6. **Trace.** For the `findingsToTrace` the owner picks, dispatch `failure-root-cause-analyst` with the review
   path, the digest path and the turn numbers.
7. **Write up.** If the result should be kept, write `docs/current/evals/eval_<slug>.md`; the folder,
   filename and template rules are in `docs/current/templates/README.md`.

Report to the owner in a few lines: arms and runs graded, runs skipped, the per-arm score picture from
`rollup.md`, findings worth tracing, grading defects, and any `invalid` reviews.
