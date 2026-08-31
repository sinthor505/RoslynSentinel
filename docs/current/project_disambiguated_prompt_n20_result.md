---
name: project_disambiguated_prompt_n20_result
description: "n=20 result for the disambiguated MinimalGuidance prompt against .113: 8/20 (40%) pass vs 34% baseline — not a strong signal at this N, but the ChangeAccessibility-on-helper failure mode collapsed (48%→17% of fails) while re-invents-own-helper became dominant (24%→75% of fails). Supports 'constraint adherence is secondary to make-it-work' over pure prompt-ambiguity."
metadata:
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-31T20:48:19.194Z
---

Follow-up to [[project_minimalguidance_reasoning_pattern_analysis]] — ran
`Model_FixesWholeFileRewriteBug_MinimalGuidanceDisambiguated` (the tightened prompt from
commit `5a7ee29`) against `.113` at n=20, same sampling config as the 50-run baseline (RP
disabled, Top P 0.7).

**Result: 8/20 (40%) pass**, vs. 17/50 (34%) on the plain MinimalGuidance prompt. Directionally
positive but the confidence interval at n=20 easily overlaps 34% — this alone does **not**
confirm the prompt fix meaningfully raised the pass rate. Needs a larger N (ideally 50, matching
the baseline) before treating 40% as real.

**The more interesting result is the failure-mode shift**, not the raw pass rate:

| Failure mode | Plain prompt (n=33 fails) | Disambiguated (n=12 fails) |
|---|---|---|
| ChangeAccessibility/ModifyModifier on helper | 16/33 (48%) | 2/12 (17%) |
| Re-invents own helper (no real call) | 8/33 (24%) | 9/12 (75%) |
| Excessive thrashing | 7/33 (21%) | 3/12 (25%) — roughly flat |

The disambiguating sentence ("treat it as a pattern to copy... not a method to call directly")
clearly worked at killing the *specific* interpretation it targeted — `ChangeAccessibility`-on-
helper dropped from the dominant mode to a minor one. But total failures barely moved, because
**re-inventing the helper under a new name took over as the dominant failure** instead. The
model stopped trying to call the private method directly, but a comparable fraction still didn't
transcribe `ReplaceBlockFormatted` verbatim — it wrote its own similarly-purposed method instead.

**Why this matters:** this is the pattern the user's stated hypothesis predicted before this
batch ran — that "make it work" is the model's primary instinct and constraint adherence
("reuse this exact thing, don't touch that file, copy it verbatim") is secondary, so closing one
specific loophole in the wording just redirects the same underlying tendency into a different
violation rather than eliminating it. This result is more consistent with that theory than with
the original "pure prompt ambiguity" read — though it's not fully dispositive at n=20, since a
prompt that more forcefully demanded verbatim transcription (rather than just closing the
"call directly" reading) hasn't been tried yet.

**How to apply:** two live open questions, neither resolved yet:
1. Re-run at n=50 to confirm whether 40% is real or noise (per [[project_repeat_penalty_ab_test]]'s
   caution that 3-4 run samples were too unreliable to trust — n=20 sits between "too small" and
   the 50 used for the baseline).
2. If the re-invents-own-helper mode holds up as dominant, the next prompt iteration should
   target THAT — e.g. "transcribe the method character-for-character, do not write your own
   version under a different name" — rather than further tweaking the "reuse"/"call" wording,
   since that specific ambiguity is now demonstrably closed.

Also ran a small n=5 batch against `.112` at Top P 0.3 concurrently (not yet analyzed in
depth) — first observed run also re-invented the helper (as `ReformatBlock`) despite the
disambiguated prompt, consistent with the re-invention mode appearing across hosts/sampling
configs, not specific to `.113`/Top P 0.7.

**Methodology note:** analysis script `C:\tmp\analyze_disambiguated_113_n20.ps1` (scratch, not
committed) reuses the transcript-reconstruction approach from
[[project_minimalguidance_reasoning_pattern_analysis]] (last ApplyDiff vs. last ReadFile by
tool-call index, real-call regex for `ReplaceBlockFormatted`). One run
(`20260831-200122-588`) was initially lost to a `roslynsentinel-modeleval.ps1` bug (see
below) and manually recovered from `_scratchbuild_113` before analysis.

**Unrelated bug found and fixed during this batch** (commit `374ce75`):
`roslynsentinel-modeleval.ps1`'s `-Repeats` loop used `$ErrorActionPreference = 'Stop'` around
the `dotnet test` call, which PowerShell 7 promotes a non-zero exit code into a terminating
`NativeCommandError` for — this silently truncated the `-Repeats` loop (skipping archiving and
all remaining repeats) on the **first real test failure**, not just a crash. This means any
prior multi-repeat batch that included a failure may have run short of its requested count.
Fixed by scoping `$ErrorActionPreference = 'Continue'` around just the `dotnet test` call.
