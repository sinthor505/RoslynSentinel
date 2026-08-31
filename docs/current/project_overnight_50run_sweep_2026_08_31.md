---
name: project_overnight_50run_sweep_2026_08_31
description: "Overnight 50-run sweeps (RP disabled, Top P 0.7, Temp 0.1): .113 MinimalGuidance 17/50 (34%) pass vs .112 SizeThreshold n=60 47/50 (94%) pass — same config, same night; gap tracks prompt/fixture variant, not host, since .112 never once hit the breaker or ChangeAccessibility-on-helper failure mode that dominates .113's failures"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-31T17:01:53.500Z
---

Two large overnight batches run 2026-08-31 under identical sampling config (Repeat Penalty
disabled, Top P 0.7, Temperature 0.1 on both hosts) to get a real sample size after
[[project_repeat_penalty_ab_test]]'s small-N (3-4 run) results proved too noisy to trust.
See [[reference_model_eval_procedure]] for the harness and [[project_modeleval_testhost_crash_gotcha]]
for the crash risk this run's batching design was built to contain.

**`.113` — `Model_FixesWholeFileRewriteBug_MinimalGuidance` × 50:**

| Metric | Result |
|---|---|
| Pass rate | 17/50 (34%) |
| Converged (`ModelFinished`) | 48/50 |
| `ChangeAccessibility`-on-helper failure signature | 14/50 |
| Orientation breaker tripped ≥1× | 37/50 (74%) |
| Turns | min 6, max 40 (cap), avg 17.8 |

**`.112` — `Model_SizeThresholdSweep` n=60 × 50 (batched 10×5 via `ROSLYNSENTINEL_MODELEVAL_REPEATS=5` + script `-Repeats 10`):**

| Metric | Result |
|---|---|
| Pass rate (`fixCorrect=True`) | 47/50 (94%) |
| Converged | 48/50 |
| Orientation breaker tripped | 0/50 |
| `ChangeAccessibility`-on-helper | 0/50 |
| Turns | min 0, max 13, avg 6.2 |
| Failures | 1 `WallClockCapExceeded`/13 turns/5 ApplyDiff errors, 1 `HarnessException` (0 turns, no transcript — transient, not investigated further), 1 `ModelFinished` but `fixCorrect=False` (11 turns) |
| results.csv row count | 50/50 — exact match, no silent testhost-crash truncation this run |

**Why the gap is probably prompt/fixture, not host:** same sampling config, same night, wildly
different pass rate (34% vs 94%) and turn count (avg 17.8 vs 6.2). Critically, `.112`'s batch
NEVER triggered the orientation breaker or the `ChangeAccessibility`-on-helper mistake even
once across 50 runs — the exact two things that dominate `.113`'s failures. If the host/GPU
were the deciding factor you'd expect at least some bleed-through of these failure modes on
`.112` too. Instead the two hosts ran genuinely different test scenarios:
`MinimalGuidance` gives a deliberately sparse, symptom-only prompt (by design — see
[[project_system_prompt_v1_result_and_modifymodifier_gap]]), while `SizeThreshold`'s "SingleStep"
prompt variant is more structured. The sparse prompt appears to be what drives the model into
search-thrashing → breaker-trip → grab-the-nearest-tool (`ChangeAccessibility`) spiral;
the structured prompt heads it off almost entirely.

**Caveat:** this is not a clean host-vs-host or prompt-vs-prompt controlled comparison — two
different test methods, different fixture sizes, running concurrently on different nights'
tails. Don't cite this as "`.112` beats `.113`" or "SizeThreshold's prompt is definitively
better" without running MinimalGuidance's exact prompt against `.112`'s fixture (or vice
versa) to isolate the variable. Worth doing as a follow-up if prompt-hardening work continues.

**How to apply:** the 34% MinimalGuidance pass rate at the best sampling config found so far
means roughly 2 in 3 unassisted runs on that prompt still fail — this is the task the
[[reference_model_eval_procedure]] harness and any future orientation-breaker/prompt work
should keep targeting. The `.112` SizeThreshold config's near-0% breaker/ChangeAccessibility
rate is worth understanding structurally (what specifically about "SingleStep" phrasing
avoids the spiral) rather than attributing to host speed or luck.

**Harness note:** the `-Repeats 10` × `ROSLYNSENTINEL_MODELEVAL_REPEATS=5` batching (see
`roslynsentinel-modeleval.ps1`) worked as designed — the one `HarnessException` row cost
only itself, not the whole 50-run batch, unlike the earlier single-process-50-repeat crash
risk this design was built to avoid.
