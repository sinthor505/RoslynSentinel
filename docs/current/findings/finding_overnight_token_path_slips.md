# Finding: qwen-family models misspell ShippingKinds.Overnight through a token-path slip, not a reasoning error

**Status:** OPEN 2026-11-17. Mechanism established and sampler settings decided; the recovery design and a fine-stepped eval variant are open (TODO.md).

## Context

The stepped ShippingKind scenario (ContosoOrders fixture, `ShippingKindStep*` tests in
RoslynSentinel.Tests.ModelEval) asks the model to replace shipping-kind string literals with the
`ShippingKinds` constants. qwen3.5-9b-mtp and tiel-coder-35b-a3b repeatedly wrote
`ShippingKinds.Ownight`, `Oovernight`, `Oversight`, `Oversnight` or `Overtime` instead of
`ShippingKinds.Overnight`; qwen3.5-4b did so far less often in its own runs. Investigated 2026-11-14 to
2026-11-17 by sessions roslynsentinel-b2 and roslynsentinel-38 on LM Studio hosts 112, 113 and 85
(journal `.claude/journal/2026-11-14_fe254764.md`).

## What is broken

- **The slip is one token.** Every misspelling starts with the token `.O` (id 8123) where the correct
  spelling starts with a bare `.` (id 13). Streamed deltas in agent.log show it directly, e.g. 9b run
  `ModelTestingResults/112/Model_RunsShippingKindStepPromptFinal(5)/20261117-190646-972`:
  `[ Shipping][K][inds][.O][vertime][ ?]`.
- **The repair after `.O` is lossy and model-specific.** The correct word was essentially never spelled
  `.O` + rest in training, so the model continues with a plausible token: `overn` (copies the nearby
  literal `"overnight"`: Oovernight, tiel's usual form), `wn` (Ownight, 9b's usual form), `vers`
  (Oversight), `vertime` (Overtime).
- **It happens on the first write and then propagates.** Slips occur on a sample's first qualified write;
  0 of 401 later writes slipped after a correct first write (roslynsentinel-38's analysis), while a model
  that slipped repeats its own typo (9b: 18 of 27).
- **The model cannot recover even when it knows.** Tiel run
  `ModelTestingResults/113/Model_RunsShippingKindStepPromptFinal(4)/20261117-214637-592` (server
  538e6f1b) received "Closest member: ShippingKinds.Overnight. The line with it: case
  ShippingKinds.Overnight:" 14 times, wrote "I made a typo: Oovernight instead of Overnight" from turn 4
  and "I keep producing the typo ... a persistent issue with my text generation" later, and still sent
  `Oovernight` in all 16 later edit calls. All three tiel slips in the preset runs failed on the wall
  clock.
- **The rename moved the slip.** In the PriorityOvernight variant (566014bd) one 9b run wrote
  `ShippingKinds.PrioritOvernight`: `[inds][.P][rior][it][Over][night]` against the correct
  `[.Priority][Over][night]`; `.P` is the prefix of `.Pickup` (`[.P][ick][up]`).

## Root cause

**Tokenization creates the trap; the model's preference and the sampler decide how often it springs.**

1. Tokenization is deterministic and symmetric: input text and generated text use the same split
   (LM Studio Python SDK `model.tokenize`, identical ids on 4b, 9b and tiel):

   | Text | Tokens after `ShippingKinds` |
   | --- | --- |
   | `.Overnight` (correct) | `[.][Over][night]` |
   | `.Oovernight` / `.Ownight` / `.Oversight` / `.Overtime` | `[.O][overn][ight]` / `[.O][wn][ight]` / `[.O][vers][ight]` / `[.O][vertime]` |
   | `.Standard` / `.Express` / `.Pickup` / `.IsExpedited` | `[.Standard]` / `[.Ex][press]` / `[.P][ick][up]` / `[.Is][Ex][ped][ited]` |
   | `.PriorityOvernight` / `.Other` / `.Overseas` | `[.Priority][Over][night]` / `[.Other]` / `[.][Over][se][as]` |
   | ` Overnight` (bare, after `using static`) / `"overnight"` | `[ Overnight]` / `["][overn][ight]["]` |

   After `ShippingKinds` every sibling member starts with a fused dot+letters token; only `Overnight`
   starts with a bare `.`, and the fused `.O` sits right beside it.
2. Raw probability at the decision is small but real: 4b in the 9b slip context P(.) 97.58%,
   P(.O) 2.13%; 9b in its own slip context P(.O) 0.58%. In tiel's reasoning `.O` must sometimes hold
   >= 20% of `.` (it survives min-p 0.2, below).
3. **Penalties lift `.O`.** A bare `.` (list numbering `1.`, sentence ends) almost always sits in the
   last 64 tokens, so repeat_penalty > 1 or presence_penalty > 0 lowers `.` and raises `.O`.
4. **Self-propagation.** A written typo is read back as the same `[.O]...` tokens, and every rejected
   edit and error message that echoes it adds more copies right where the next decision is made.
5. **Forcing a non-canonical split fails the same way in reverse.** Banning all 52 `.X` single-letter
   tokens (chat/completions logit_bias -100, 4b, 40 samples per name) left Standard/Express/Overnight at
   40/40 but broke every name whose normal split starts with one: Pickup 0/40 (`.PeakUp`, `.Pinup`),
   Ocean 1/40 (39 `.Overnight`), Oversized 0/40 (`.OverSized`), SameDay 0/40 (`.SampleDay`,
   `.SomeDay`). Models cannot reliably assemble an alternative token sequence for a word.

## Evidence (measurements)

All counts are per first write of the name. "Per request" = sampler fields sent in a
`/v1/chat/completions` request (`scripts/Probe-OvernightLogprobs.ps1 -Endpoint chat`, 2b3d3cfa);
"preset" = the LM Studio per-model settings.

Probe, 4b on 85, 9b slip context (`c:\tmp\resume\step5-qwen9b-slip-112549-356.json`, `-KeepTurns 3`):

| Sampler | `.O` first tokens |
| --- | --- |
| per request: rp 1.1, temp 0.8, top-k 40, top-p 0.95, min-p 0.05 (three arms, MTP off and on) | 321 / 3000 (10.7%) |
| per request: rp 1.0, same cuts | 0 / 1000 |
| per request: rp 1.1 plus logit_bias 8123=-100 | 0 / 1000, no substitute misspelling |
| preset: rp 1.0, presence 0, top-p 0.95, min-p 0.05, temp 0.6, MTP off | 0 / 10000 |
| preset: rp 1.0, presence 1.0, top-p 0.95, min-p 0.05, temp 0.6 | 1 / 1000 |
| preset: rp 1.0, presence 0, top-p 0.90, min-p 0, temp 0.8 | 0 / 1000 |
| preset, values never read on 85 (treat as uncontrolled) | 22 / 1000 |

9b own slip context on 112, all cuts off (temp 1.0, top-p 1.0, min-p 0, penalties off): 3/1000, matching
its raw 0.58%.

Replays (`ShippingKindReplay`, early stop from 7edd9e04):

| Model, point | Sampler | Slips |
| --- | --- | --- |
| tiel step 4 Original | defaults (rp 1.1), temp 0.8 | 10 / 48 (21%) |
| tiel step 4 Original | rp 1.0, presence 0, top-p 0.95, min-p 0.05, temp 0.8 | 1 / 24 |
| tiel step 4 Original | rp 1.0, top-p 0.9, min-p 0.1, temp 0.6 | 9 / 200 (4.5%) |
| tiel step 4 Original | rp 1.0, top-p 0.8, min-p 0.2, temp 0.6 | 2 / 200 (1.0%) |
| tiel step 4 QualifiedResult | defaults | 0 / 13 |
| 9b step 5 slip point | rp 1.0, top-p 0.9, min-p 0.1, temp 0.6 | 0 / 197 |

Full runs, ShippingKindStepFinal steps 4+5, preset rp 1.0, presence 0, top-p 0.95, min-p 0.05, temp 0.6:
9b x20 1/40 slips (`Overtime`), 39/40 pass; tiel x15 3/30 slips, 27/30 pass, every slipped run failed.
Other: gemma-4-e4b 0 slips in 355 uses (same `[.][Over][night]` split); 4b full runs x30 on 85
0 slips in 431 uses, 176/180 steps pass.

## Why it matters

- These are not capability, reasoning or coherence failures; they look like them in whole-word logs
  (repeated identical rejected edits, a model "ignoring" a correct hint), so analyses blamed prompts and
  error text.
- More error text cannot fix them after the fact, and errors that repeat the bad name make them worse.
- Rates are 1-10% per first write, so small arms (20 runs) cannot tell a server or prompt change from
  sampling variance; several earlier "fixed" verdicts were within noise.
- The same trap applies to any identifier whose split after its qualifier differs from its siblings'.

## Recommendation

Decided:

1. **Eval sampler preset:** repeat_penalty 1.0, presence_penalty 0, top-p 0.9-0.95, min-p 0.05-0.1,
   temp 0.6. Penalties actively cause slips in code; min-p is the most reliable cut (relative).
2. **Evaluation rules** for model-eval analysis are in the `failure-analysis` skill (section "Token-path
   slips").
3. **Harness and tool fixes landed** during the investigation: replay early stop at the decision
   (7edd9e04, ~35x faster samples), the cross-edit verdict no longer claims a sibling "declares" a name it
   only uses (deb8c978, 6829655d), the `toolCall` echo is off by default (538e6f1b), `structuredDetail`
   no longer lists each diagnostic twice (e9c5c48f), corrected-line hint for CS0117/CS0103
   (679cb8b1, 09a6fe26).

Open (TODO.md):

4. **Recovery that needs no retyping:** errors that give position and corrected line without echoing the
   bad name, retry by reference, optional strict auto-apply.
5. **Fine-stepped eval variant** for finer replay points.
6. A targeted logit ban works (above) but only where the intended identifier is known, and
   `/v1/responses` (the eval client endpoint) ignores `logit_bias` and `repeat_penalty`; not pursued.

Tools for the next investigation: agent.log `LM Studio reasoning:` / `LM Studio streaming:` delta lines;
`scripts/Find-TypoTokenStream.ps1` (TODO: labels a typo with the channel of the turn's last delta);
`scripts/Probe-OvernightLogprobs.ps1` (logprobs at the decision, `-SampleCount`, `-Endpoint chat`,
`-LogitBias`, per-request sampler fields); tokenizing a name via the LM Studio Python SDK
(`lmstudio` package, `client.llm.model(key).tokenize(text)`); LM Studio trace logs print input tokens as
`prefill token: <id> = <text>`.

## Out of scope

- Renaming fixture members or reordering the fixture to dodge the trap: diagnostic only, not a fix that
  generalizes across code.
- A broad ban on `.X` tokens: breaks names whose canonical split starts with one (above).
- Reasoning-channel logprobs: LM Studio returns logprobs for output text only and renders a prefill after
  an empty closed think block, so the reasoning decision can only be approximated.
