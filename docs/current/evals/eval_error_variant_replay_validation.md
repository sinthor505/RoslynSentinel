# Eval: error-variant replay validation (Step 12) - F identity cell drifts from live

**Status:** OPEN 2026-11-13. Step 12 ran in full. E and G replays match their live runs, but the F identity cell does not: replay gives Search 18 of 30 where live gave 10 of 10. Cause not traced; Step 13 is on hold.

## Context

- **What ran.** Step 12 of `plans/plan_error_variant_replay.md`: one-step replay (`scripts/roslynsentinel-errorvariants.ps1`), 3 samples per history, continue turns 1, no settings overrides (`-Temperature`/`-TopP` not passed). The source jobs ran the same way (plan Step 11 notes).
- **Host and model.** Host 113, model `qwen3.5-9b-mtp`, recorded on all 210 rows. `SourceSettings` is `unknown` on every row, because the sources predate Step 10.
- **Sources.** MissingFile runs E `20261113-050525-257`, F `20261113-061105-032` and G `20261113-064107-618`, 10 histories each, cut after the turn-1 `ReadFile` miss.
- **Variants.** `ErrorVariants/FileNotFound/armE.md`, `armF.md` and `armG.md`, harvested in 09ebacf1. Each reproduces its own run's recorded result byte for byte (1845498c).
- **Code.** HEAD 916a2cb4, which includes the operation-aware classification (1367cda4, 379ffebe).
- **Output** in `ModelTestingResults\113\ReplayVariants_SamplesAndClassifies\`:
  - `20261113-201359-903`: E histories, 90 rows.
  - `20261113-201739-025`: F histories, 90 rows.
  - `20261113-202059-971`: G histories, 30 rows.
  - The 1-row folder `20261113-201321-500` is a sanity check, not counted.
- **Speed.** About 2.3 s per row.

## What is broken

Every row classed `Widen:*`. Every cell has 0 `Stop*`, 0 `Malformed`, 0 `NoCallOther`, 0 `Other:*` and 0 harness errors.

| Cell (text on histories) | Rows | ListSolutionItems | Search | ListAll | Live first call at turn 2 (histories' own arm) |
| --- | --- | --- | --- | --- | --- |
| E on E | 30 | 25 (0.83) | 5 | 0 | ListSolutionItems 7 / Search 3 |
| F on E | 30 | 10 | 18 (0.60) | 2 | (E text live) |
| G on E | 30 | 14 | 16 | 0 | (E text live) |
| E on F | 30 | 23 (0.77) | 7 | 0 | (F text live) |
| **F on F** | 30 | 10 | **18 (0.60)** | 2 | **Search 10 / 10** |
| G on F | 30 | 8 | 22 | 0 | (F text live) |
| G on G | 30 | 9 | 21 (0.70) | 0 | Search 7 / ListSolutionItems 3 |

Verdicts against the pass bars, which were fixed before the run (plan Step 12):

| Bar | Cells | Result | Verdict |
| --- | --- | --- | --- |
| F-text Search share >= 0.8 | F on F, F on E | 0.60 in both | **Drifted** |
| E-text ListSolutionItems share >= 0.5 | E on E, E on F | 0.83, 0.77 | Faithful |
| `Stop*` <= 2 of 30 for every text | all | 0 in every cell | Pass |
| HarnessFault (`Malformed` > 0.1 or any harness error) | all | none | not a HarnessFault |

- **G.** No bar applies, but G on G (Search 0.70) matches live G (0.70) exactly.
- **F's drift is not just noise.** If the replay's Search rate of 0.60 were the true rate, 10 of 10 live would have probability 0.006. A one-sided Fisher exact test of live 10/10 against replay 18/30 gives p ~ 0.016.
- **The drift is spread across samples.** Per F history, `ListSolutionItems` count out of 3: `1,0,1,2,1,2,0,1,2,0`. No single history drives it.
- **Direction holds.** On both history sets, the E text pushes toward `ListSolutionItems` (25 and 23) and the F text toward `Search` (18 and 18). The F effect is diluted, not reversed.
- **Odd row.** One F-on-F row (sample-08, index 2) has 2 calls, a `ListAll` first call, and 46,229 input tokens, double the usual ~23k. Not investigated.
- **Stop reasons.** `TurnCap` 109 (the first call matched a recorded call and was served), `OffScript` 101.

## Root cause

Not traced. Hypotheses, each untested:

1. **Host or model state changed between the live F job (about 06:11) and the replay (about 20:17)** on the same host: a model reload, a different context length (60160 now), or a different LM Studio version. E and G matching their live runs argues against a large shift, but not against one specific to F.
2. **Live F's 10/10 was a small-sample extreme.** The test above makes this unlikely, though not impossible.
3. **The replayed request differs from the live turn-2 request in something F's wording is sensitive to.** Candidates: the reasoning item for turn 1, the order of tool definitions, or the request parameters. All three builds share the code path (`SeedConversation`, `BuildInputItems`), which is why E and G matching makes a general replay defect less likely.

## Why it matters

The replay is meant to screen wording before a live arm. A text whose live effect is strong (F: Search 10/10) shows up weaker in replay (0.60), so a screen could under-rank exactly the texts that work. Until this is explained, replay results carry a known bias.

## Recommendation

1. **Proposed next step: a cheap discriminating check (needs owner approval).**
   - Re-run live arm F today: check out commit `5923cff9` in a worktree and run `-Scenario StopAdherenceMissingFile -Samples 10` on 113, about 6 minutes.
   - If live F now gives about 0.6 Search, the drift is host or day state (hypothesis 1), the replay is faithful, and Step 13 can proceed with a same-session live anchor.
   - If live F still gives about 10/10, the replay differs from live (hypothesis 3). Run `failure-root-cause-analyst` on F-on-F, comparing one replayed request payload with a reconstructed live turn-2 request.
2. **Harness change regardless of outcome.** Each validation session needs a live anchor on the same host and session: one arm re-run live beside the replay. A Step 12-style comparison against a morning baseline mixes in host drift.
3. **Sibling-model replicate.** The same 210 rows on `qwen3.5-4b-mtp` (host 112) are running; results go in a section below. There is no live 4b baseline for these histories.

## Out of scope

- Step 13, the cross-history repeat. It is on hold per the plan's stop rule.
