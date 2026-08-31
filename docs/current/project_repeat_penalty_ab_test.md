---
name: project_repeat_penalty_ab_test
description: "A/B/C test results: Repeat Penalty 1.1 is 0/6 pass across two hosts (.112, .113) on Model_FixesWholeFileRewriteBug_MinimalGuidance, vs 3/4 with Repeat Penalty disabled; same ChangeAccessibility-on-helper failure signature in all 6 failures"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-31T08:15:49.027Z
---

Results of the `Model_FixesWholeFileRewriteBug_MinimalGuidance` A/B/C test run 2026-08-31
across LM Studio hosts `.112` (GTX 1080) and `.113` (RTX 4060), triggered by a hypothesis
that Repeat Penalty explains the model's occasional sudden success on this task. See
[[project_lmstudio_sampling_params_for_code]] for the sampling-param background and
[[reference_model_eval_procedure]] for the harness.

**Results table** (Temperature 0.1 fixed throughout):

| Host | Repeat Penalty | Top P | Pass rate | Notes |
|---|---|---|---|---|
| .113 | disabled | 0.7 | 3/4 | best config tested; failures/passes: 28-turn fail, 10-turn pass, 36-turn pass, +1 earlier 10-turn pass |
| .113 | 1.1 | 0.7 | 0/3 | 17, 13, 20 turns — all 3 via ChangeAccessibility-on-helper |
| .112 | 1.1 | 0.7 | 0/3 | 9 (WallClockCapExceeded), 19, 22 turns — all 3 via ChangeAccessibility-on-helper |
| .113 | disabled | 0.5 | 1/3 | 40-turn fail (TurnCapExceeded), 20-turn fail (ChangeAccessibility-on-helper), 20-turn pass |

**Combined Repeat Penalty 1.1 tally: 0/6 across two different hosts/GPUs**, all six hitting
the identical failure signature (see below). This is the headline result — reproducible
across independent hardware, not a fluke of one host's queue or thermal state.

**The `ChangeAccessibility`-on-helper failure signature:** the model, needing to reuse
`BlockEditHelpers.ReplaceBlockFormatted` (a `private static` method in a reference-only
sibling file it must NOT modify), calls the `ChangeAccessibility` tool to make it `public`
and then calls it cross-class (`BlockEditHelpers.ReplaceBlockFormatted(...)`) instead of
transcribing the method body directly into `BlockConverter.cs`. This fails
`AssertFixApplied`'s check that `BlockEditHelpers.cs` remains byte-for-byte untouched.
Present in all 3 `.113`@1.1 failures, all 3 `.112`@1.1 failures, AND one of the
disabled-RP/Top-P-0.5 failures — so this specific mistake is not *exclusively* gated by
Repeat Penalty; it looks like the model's dominant default failure mode for this task,
which Repeat Penalty (and possibly Top P) modulate the frequency of rather than strictly
prevent.

**Why:** disabling Repeat Penalty removes pressure to substitute/drift tokens during
large verbatim reproduction (see [[project_lmstudio_sampling_params_for_code]] for the
mechanism). The 0/6 vs 3/4 split is consistent with that mechanism but the one
disabled-RP failure sharing the exact same signature means Repeat Penalty is not a strict
on/off gate for this failure mode — treat it as the strongest lever found so far, not a
guarantee.

**How to apply:** default to Repeat Penalty disabled + Top P 0.7 + Temperature 0.1 for
any future model-eval run on this task unless specifically testing sampling params again.
If a future transcript shows `ChangeAccessibility` called on a file the prompt says must
stay untouched, recognize it immediately as this known pattern rather than re-diagnosing
from scratch — worth considering a harness-level or prompt-level guard against
`ChangeAccessibility` targeting reference-only files, since prose guidance alone
(system prompt already warns against modifying `BlockEditHelpers.cs`) is evidently not
sufficient to prevent it in ~40% of runs even under the best sampling config tested.

**Caveat:** `.112` was dropped from active use mid-session for being too slow for rapid
iteration ("we can just use .113") — its 3 Repeat-Penalty-1.1 runs were already in flight
and allowed to finish rather than being actively re-launched. Its data is a useful
independent-hardware confirmation point, not evidence `.112` should be reintroduced to the
regular test rotation.
