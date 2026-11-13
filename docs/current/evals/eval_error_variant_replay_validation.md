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

### Trace 2026-11-13: the replayed request equals the live turn-2 request

Done by `failure-root-cause-analyst`, read-only.

**Confirmed from artifacts.**
- **Token counts equal.** In all 10 F histories, the replay's turn-2 `InputTokens` equals the live turn-2 `InputTokens` exactly: 23105, 23072, 23090, 23084, 23079, 23073, 23071, 23048, 23083 and 23070. The E-text and G-text replays differ, as they should.
- **Content equal.** Live sample-01 and its replay have the same `SystemPrompt` (8876 characters) and `UserPrompt`, and the same 55 tools in the same order with equal descriptions and schemas. Turn 1 is byte-identical, including `ReasoningContent`, `ResultJson` and `TrailingText`.
- **No hidden prompt composition.** `ErrorPolicyWiring.ComposePrompt` runs only in `PlanStepRunner/Program.cs:248`. `ModelAgentRunner.cs:219-225` sends the passed strings unchanged.
- **Same request path and parameters.** Both sides use `BuildInputItems`/`ToInputItems` (`LmStudioAgentClient.cs:493-547`), `max_output_tokens` 16384, temperature and top-p null, and loop assist off.
- **Cache state ruled out.** Search counts by sample index are 6, 5 and 7 of 10, so a cold versus warm prompt cache does not matter.

**Ruled out:** hidden system-prompt composition; tool order, descriptions and schemas; replay of turn-1 reasoning; tool-output bytes; turn and token limits; loop assist; the E job's policy-file env var (it changes only `ErrorPolicyHash`).

**Ranked candidates.**
1. **Live F's 10/10 was an extreme draw.** In replay, F text and G text are indistinguishable: F gives Search 36/60 (0.60) across both history sets, G gives 59/90 (0.66). F's message differs from G's only by G's appended absence sentence. At a true rate near 0.65, P(10/10) is about 0.013-0.03. The p ~ 0.016 above is also one of three live-versus-replay comparisons, so it should be read with that in mind.
2. **Host or LM Studio state between 06:11 and 20:17.** Effective sampling defaults are not recorded: the echoed response `Temperature`/`TopP` are parsed (`LmStudioAgentClient.cs:683-691`) but never stored. E and G matching argues against this.
3. **KV or numeric effects from prefilling the seeded turn instead of decoding it.** Weak; the per-sample-index counts argue against it.

**Unexplained.** One F-on-F row (sample-08, index 2) reports 46,229 input tokens with a first-token time of 0.144 s. This looks like an LM Studio usage-accounting artifact.

**Harness changes it suggests:**
- a `SourceTurnInputTokens` column next to the replay's input tokens: in an identity cell, a mismatch means the request differs;
- a request-body hash logged on both sides;
- storing the echoed temperature and top-p in the transcript header;
- live arms of 30 samples, or a same-session F-versus-G comparison, because 10 live samples cannot separate 0.6 from 0.8.

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
