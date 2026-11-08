# docs/research/testing — model-eval & run research records

**This directory is untracked by design.** `/docs/research/` is in `.gitignore`, so files
here never appear in `git status` and adding a new one requires no commit.

## What belongs here

Point-in-time records of model-eval and PlanStepRunner experiments — the writeups that
accrete one-per-run and are only ever read for historical comparison:

- overnight sweeps and n-run batch results
- per-prompt-variant outcomes (MinimalGuidance, Disambiguated, PlanThenExecute, PIV, …)
- per-model findings (granite, qwen, …)
- sampling-parameter A/B tests
- per-run defect remediation summaries

These are mirrors of entries in the project memory dir
(`~/.claude/projects/…-RoslynSentinel/memory/`), per the copy-memory-files-to-docs
convention. The memory dir remains the primary; this is the historical mirror.

## What does NOT belong here

Keep these tracked under `docs/current/`:

- **`feedback_*`** working agreements — reviewable in PRs, they govern how work is done.
- **`reference_*`** procedures — operational, needed by anyone running the suites.
- **Tool-defect records** (`blockers/`, `issue_*`, `bugreport_*`) — they describe live
  product behaviour and are cited by fixes.
- **Anything cited by source code.** Several `.cs` files carry comments with explicit
  `docs/current/<file>.md` paths; moving those silently breaks the reference. Check first:

  ```bash
  grep -rn "docs/current/<file>.md" --include=*.cs RoslynSentinel.* | grep -v /bin/ | grep -v /obj/
  ```

## Files deliberately left in docs/current

These are research writeups by nature but are cited by tracked code or `TODO.md`, so they
stayed put. Moving one means updating its citations in the same change:

| File | Cited by |
|---|---|
| `eval_modeleval_fixture_test_suite_redesign.md` | 7 files in `RoslynSentinel.Tests.ModelEval` |
| `eval_pattern_analysis_2026_09_02.md` | `WholeFileRewriteAgentTests.cs` |
| `eval_pattern_analysis_prompt.md` | the analysis doc above |
| `project_minimalguidance_reasoning_pattern_analysis.md` | `PlanOnlyAgentTests.cs`, `PlanThenExecuteAgentTests.cs`, `WholeFileRewriteAgentTests.cs` |
| `project_own_copy_helper_dominant_failure.md` | `WholeFileRewriteAgentTests.cs` |
| `project_planimplementverify_phase_transition_gap.md` | `PlanImplementVerifyAgentTests.cs` |
| `project_directive_error_messages_wiggle_room_theory.md` | `AgentToolErrorAssertions.cs` |
| `eval_sequential_edit_habit_vs_compiler_checks_theory.md` | `OrderPricingRefactorChainAgentTests.cs` |
| `eval_overnight_run_2026_08_30.md` | `ToolEnums.cs`, `ApplyDiffSizeGuardTests.cs` |
| `eval_replaceblockformatted_accessibility_cost_2026_09_05.md` | `TODO.md` |

## Cross-references

Files here use `[[wikilink]]` style, matching the memory dir. Wikilinks are name-based, so
they survived the move from `docs/current/` unchanged — several docs still under
`docs/current/` link to files here and vice versa. Prefer wikilinks over relative paths for
anything that might move.

## Backing data

Raw run artifacts live in already-ignored directories: `PlanStepRunner/`,
`ModelTestingResults/`, `lmstudio_logs/`, `TestResults/`. Reference them by path from a
writeup here rather than copying transcripts in.

---

Moved out of `docs/current/` on 2026-09-10 (19 files).
