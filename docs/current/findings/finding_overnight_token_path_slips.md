# Finding: qwen-family models misspell ShippingKinds.Overnight through a token-path slip, not a reasoning error

**Status:** OPEN 2026-11-17, updated 2026-11-18 (overnight batch: temperature, qualified-name and presence arms). Mechanism
established and sampler settings decided; server-side recovery built and measured 2026-11-19; the harness-side
recovery and a fine-stepped eval variant are open (TODO.md).

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
- **The repair after `.O` is lossy and model-specific.** The likely reason (an inference, not measured) is
  that the word was rarely spelled `.O` + rest in training, so the model continues with a plausible token: `overn` (copies the nearby
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
  clock. The first recovery seen came on 2026-11-18: tiel step 5 run
  `ModelTestingResults/113/Model_RunsShippingKindStepPromptFinal(5)/20261118-140429-519` slipped and still passed
  (8 turns); the other slip that night (step 4, `20261118-141631-323`) failed at the 30-minute cap.
- **The rename moved the slip.** In the PriorityOvernight variant (566014bd) one 9b run wrote
  `ShippingKinds.PrioritOvernight`: `[inds][.P][rior][it][Over][night]` against the correct
  `[.Priority][Over][night]`; `.P` is the prefix of `.Pickup` (`[.P][ick][up]`).

## Root cause

**Tokenization creates the trap; the model's preference and the sampler decide how often it springs.**

1. Splitting text into tokens is deterministic (LM Studio Python SDK `model.tokenize`, identical ids on
   4b, 9b and tiel), but generation is not bound to that split: the model can emit any token sequence,
   and the slip is exactly that. The table shows the standard split of each text:

   | Text | Tokens after `ShippingKinds` |
   | --- | --- |
   | `.Overnight` (correct) | `[.][Over][night]` |
   | `.Oovernight` / `.Ownight` / `.Oversight` / `.Overtime` | `[.O][overn][ight]` / `[.O][wn][ight]` / `[.O][vers][ight]` / `[.O][vertime]` |
   | `.Standard` / `.Express` / `.Pickup` / `.IsExpedited` | `[.Standard]` / `[.Ex][press]` / `[.P][ick][up]` / `[.Is][Ex][ped][ited]` |
   | `.PriorityOvernight` / `.Other` / `.Overseas` | `[.Priority][Over][night]` / `[.Other]` / `[.][Over][se][as]` |
   | ` Overnight` (bare, after `using static`) / `"overnight"` | `[ Overnight]` / `["][overn][ight]["]` |

   After `ShippingKinds` every sibling member starts with a fused dot+letters token; only `Overnight`
   starts with a bare `.`, and the fused `.O` sits right beside it. That split is neither necessary nor
   sufficient for a slip: PriorityOvernight slipped from a fused standard token (`.Priority` to `.P`),
   and gemma-4-e4b has the same `[.][Over][night]` split with 0 slips in 355 uses. The general condition
   is that a shorter dot+letter token than the standard one exists, which holds for nearly every name;
   how often it is picked depends on the model's probability at that position. A `.O` start is also not
   always a misspelling: in the rp 1.1 arms below about 15 of 103 `.O` starts were completed correctly
   as `.O` + `vernight`, so `.O` first-token counts are an upper bound on misspellings.
2. Raw probability at the decision is small but real: 4b in the 9b slip context P(.) 97.58%,
   P(.O) 2.13%; 9b in its own slip context P(.O) 0.58%. In tiel's reasoning `.O` must sometimes hold
   >= 20% of `.` (it survives min-p 0.2, below).
3. **Penalties lift `.O`.** A bare `.` (list numbering `1.`, sentence ends) often sits in the last 64
   tokens, and then repeat_penalty > 1 lowers `.` and raises `.O`. presence_penalty acts the same way
   (measured 2026-11-18 per request on the 4b, below: presence 0 / 1 / 2 gave 0.2% / 0.9% / 4.7% `.O`
   with no top-p/min-p cut, matching a logit estimate from the raw probabilities; min-p 0.1 removed it
   in that context). Where no bare `.` is in the window
   the penalty has nothing to act on: the 4b in its own step-4 context (dash lists) gave 0/1000 at a raw
   P(.O) of 3.07%, against 22/1000 in the 9b slip context at 2.13% (both on the preset whose values were
   never read on 85, so uncontrolled).
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

### Overnight batch 2026-11-18 (session roslynsentinel-13, commit 2c2bf105)

All three hosts on preset temp 0.6, top-k 20, top-p 0.9, min-p 0.1, repeat 1.0, presence 0, ctx 60k; replays with
early stop; `-Temperature` sent per request where stated (LM Studio echo confirmed it in transcript.json).

Replays (first write of the name, per sample; folders under `ModelTestingResults/<host>/ShippingKindReplay_SamplesTheDecisionTurn/`):

| Model, point | Arm | Slips | Folder |
| --- | --- | --- | --- |
| tiel step 4 Original | preset | 24 / 400 (6.0%) | 113/20261118-074200-674 |
| tiel step 4 Original | temp 0.4 | 5 / 400 (1.25%) | 113/20261118-083431-610 |
| tiel step 4 Original | temp 0.3 | 4 / 400 (1.0%) | 113/20261118-092302-306 |
| tiel step 4 QualifiedResult | preset | **0 / 400** | 113/20261118-101119-844 |
| 9b step 5 slip point | temp 0.8 | 1 / 394 (`Overtight`) | 112/20261118-074206-682 |
| 4b in the 9b slip context | preset | 0 / 928 Overnight slips | 85/20261118-081702-369 |

- tiel preset 24/400 agrees with 9/200 on 2026-11-17 (pooled 33/600, 5.5%). Temp 0.4 against preset: one-sided Fisher
  p about 0.0002; 0.3 against 0.4 is not distinguishable. Temperature is the only variable in these three arms.
- QualifiedResult (`TranscriptResume.AddQualifiedShippingKindMembers`) prepends a `qualifiedMembers` field listing
  `ShippingKinds.Standard/.Express/.Overnight/.Pickup` to the saved ShippingKinds.cs read result. Seeing the qualified
  form once before the first write removed the slip (0/400 against 24/400, p < 1e-6; the earlier 0/13 at rp 1.1 agrees).
- The 4b replay's 6 `typoInTurn` rows are not Overnight slips (see the note under the full-run table).

Per-request probes, 4b on 85, 9b slip context, `-Endpoint chat`, 2000 samples each, temp 0.6, top-k 20, repeat 1.0
(outputs in `c:\tmp\probe85\`):

| presence | top-p | min-p | `.O` first tokens |
| --- | --- | --- | --- |
| 0 | 1.0 | 0 | 3 (0.2%) |
| 1.0 | 1.0 | 0 | 17 (0.9%) |
| 2.0 | 1.0 | 0 | 94 (4.7%) |
| 1.0 | 0.9 | 0.1 | 0 |

This also shows `presence_penalty` is honoured per request on `/v1/chat/completions`.

Full runs, ShippingKindStepFinal steps 4+5, preset (tiel with `-StepWallClockMinutes 30`):

| Model | Steps passed | Overnight slips (first write) |
| --- | --- | --- |
| tiel x15 | 28 / 30 | 2 (`Oovernight`; step 4 failed at the cap, step 5 recovered and passed) |
| 9b x30 | 60 / 60 | 1 (`Ownight`) |
| 4b x30 | 57 / 60 | 0 (3 step 5 failures: `DeliveryEstimator.cs` does not call `IsExpedited`) |

The other tiel failure (step 5, `20261118-120715-515`) was not a slip and not a wrong result: turn 3's first attempt
streamed 330 placeholder tool calls (322 identical `ReadFile({"reason":"...","filePath":"..."})`) for 21 minutes until the
stream errored (likely the 16384 output-token cap); the client's one retry produced the two correct edits, which ran
after the 30-minute cap had passed, so the run reads NotConverged with "state check would have been Pass". It is the only
such turn in 8,624 scanned agent.logs. `Get-ShippingKindTypoCounts.ps1` counts every unknown `ShippingKinds.<Name>` as a
typo: invented members copied from `CarrierSelector` literals (None, AirFast, GroundPost), prose run-ons
("ShippingKinds.Let me"), so its headline rates overstate slips (9b step 5 showed 8/29 against 1 real slip). The 4b
replay's 6 rows are near misses of the helper `Normalize` (Normalized, Normalizes) and prose (`ShippingKinds.Kind`,
`ShippingKinds.Has`), none of Overnight.

Limits of this evidence:

- The penalty effect is measured on the 4b (321/3000 against 0/1000). It is not measured on the 9b: its
  slip-point replay was only run with penalties off (0/197), and its 3.6% first-write rate in earlier
  real runs is assumed, not shown, to be the penalties-on condition.
- Small-arm comparisons are suggestive, not significant: tiel penalties on against off, 10/48 against
  1/24 (p about 0.08); QualifiedResult against Original, 0/13 against 10/48 (p about 0.07); presence 1.0
  against 0, 1/1000 against 0/10000 (p about 0.09).
- The tiel sampler arms changed top-p and min-p together (and temperature differs from the first two
  rows), so no single cut is isolated. The 2026-11-18 tiel arms varied temperature alone (0.6 / 0.4 / 0.3).
- The temperature and QualifiedResult effects are measured at one replay point of one model (tiel step 4). Whether
  temp 0.4 lowers pass rates was not measured.

### Server-side recovery eval 2026-11-19 (plan_server_slip_recovery, Step 13)

ShippingKindStep, four arms of the eval-only `NearMissOptions` (auto-correct off/on x name display show/hide),
commit 0797212a. tiel at host preset (temp 0.6, top-p 0.9), 5 runs per arm; qwen3.5-9b-mtp at temp 1.0 / top-p 1.0,
15 runs per arm (plus 6 earlier runs at 0.6/0.9, not pooled).

| Model | Arm | Runs | Slipped runs | Classes |
| --- | --- | --- | --- | --- |
| tiel | 1 off+show | 5 | 0 | none |
| tiel | 2 off+hide | 5 | 3 | 2 failed with a near-miss hint (NotConverged), 1 pass after retyping |
| tiel | 3 on+show | 5 | 1 | 1 assisted pass |
| tiel | 4 on+hide | 5 | 3 | 3 assisted passes |
| qwen 1.0/1.0 | 1-4 | 15 each | 1 (arm 3, `Overtight`) | 1 assisted pass |

- **Auto-correct recovered 4 of 4 tiel slips and the 1 qwen slip.** Off, 1 of 3 tiel slips recovered, and the other
  two never converged (10 and 11 rejections). No run was "failed after a substitution" or "failed with no near-miss item".
- **A hint does not break the retype loop.** In the two failures the rejection stated the closest member, its letters,
  and the corrected line in code form (`case ShippingKinds.Overnight:`), yet the model re-sent its previous call
  byte for byte. Its own earlier call, with the wrong name, was the stronger pattern in context (the self-propagation
  of root cause 4). Hide mode cannot remove that, because the wrong name is in the model's own call.
- **qwen barely slips even at 1.0/1.0** (1 in 60 runs), so it is a poor model for this arm.
- `_meta` `nearMissSubstitutions` records appear under auto-correct on and never reach the model; hide mode never
  named the wrong name in a compile rejection.
- **Gaps seen:** `CreateFile` rejections carry the hint text but no `nearMisses` item (the model fell back to it
  after `ReplaceSnippet` failed); a `ReplaceSnippet` snippet-miss result echoes the model's `oldContent`, wrong name
  included.

## Why it matters

- These are not capability, reasoning or coherence failures; they look like them in whole-word logs
  (repeated identical rejected edits, a model "ignoring" a correct hint), so analyses blamed prompts and
  error text.
- More error text cannot fix them after the fact, and errors that repeat the bad name make them worse.
- Rates are 1-10% per first write, so small arms (20 runs) cannot tell a server or prompt change from
  sampling variance; several earlier "fixed" verdicts were within noise.
- The same trap can apply to any identifier that has a shorter fused prefix token than its standard
  split, which is nearly all of them; the risk at a given position is only known from a probe.

## Recommendation

Decided:

1. **Eval sampler preset:** repeat_penalty 1.0, presence_penalty 0, top-p 0.9-0.95, min-p 0.05-0.1,
   temp 0.6. Penalties cause slips in code (measured on the 4b, suggested on tiel, not measured on the
   9b). Tighter cuts helped tiel (4.5% at top-p 0.9 / min-p 0.1, 1.0% at top-p 0.8 / min-p 0.2), but
   those arms moved both cuts together, so which one matters is open. A run that passes `-Temperature`
   (0.8 in these batches) overrides the preset's 0.6, so the temperature part of the preset only applies
   to runs that do not pass it. Temperature 0.4 cut tiel's slips from 6.0% to 1.25% (2026-11-18); a tiel
   preset of 0.4 is a candidate once its effect on pass rates is checked.
2. **Evaluation rules** for model-eval analysis are in the `failure-analysis` skill (section "Token-path
   slips").
3. **Harness and tool fixes landed** during the investigation: replay early stop at the decision
   (7edd9e04, ~35x faster samples), the cross-edit verdict no longer claims a sibling "declares" a name it
   only uses (deb8c978, 6829655d), the `toolCall` echo is off by default (538e6f1b), `structuredDetail`
   no longer lists each diagnostic twice (e9c5c48f), corrected-line hint for CS0117/CS0103
   (679cb8b1, 09a6fe26).

Open (TODO.md):

4. **Recovery that needs no retyping:** errors that give position and corrected line without echoing the
   bad name, retry by reference, optional strict auto-apply. Built and measured 2026-11-19 (section above):
   the eval-only auto-correct recovers slips; hints and hiding the name do not break a retype loop; retry by
   reference was dropped. **Prevention before the first write** is now the
   strongest measured lever: a tool result that shows the qualified names (`ShippingKinds.Overnight`) took tiel
   from 24/400 to 0/400 (2026-11-18). Candidate: read and outline results for a type list its members in
   qualified form, so the model copies `[.][Over][night]` from context instead of composing it.
5. **Fine-stepped eval variant** for finer replay points.
6. A targeted logit ban works (above) but only where the intended identifier is known, and
   `/v1/responses` (the eval client endpoint) ignores `logit_bias` and `repeat_penalty`; not pursued.
7. **Sampler enforcement (TODO.md P2).** The preset has reverted unnoticed before (host 113 ran
   on defaults, a model loaded on demand can skip its preset), and "repeat penalty off for code" was
   already a recorded result months earlier (MEMORY_CLOSED: "Repeat Penalty A/B test results", "LM Studio
   sampling params for code"), so a written rule has not held. Options: the eval client sends the sampler
   per request on `/v1/chat/completions` (LM Studio: repeat_penalty and presence_penalty verified honoured
   there), or the harness reads the effective sampler per run and records or refuses a mismatch.
   Unsloth hosts (85u Qwen3.5-4B, 113u Ornith-1.5-9B, probed 2026-11-22; script and output in
   `<scratch>\unsloth-sampler-probe\`): `/v1/chat/completions` honours temperature, top_p, top_k, min_p,
   `repetition_penalty` and enable_thinking; `repeat_penalty` and presence_penalty (2.0, greedy) have no
   effect; no sampler field is echoed. `/v1/responses` honours temperature and top_p (both echoed) and
   ignores top_k, min_p, both penalty spellings and enable_thinking. So per-request enforcement on Unsloth
   needs the chat endpoint (or the host preset) for everything beyond temperature/top_p.
8. **Lower-temperature arm (run 2026-11-18).** tiel step 4: 6.0% at 0.6, 1.25% at 0.4, 1.0% at 0.3. Open: the
   effect on pass rates and on other models and replay points.

Tools for the next investigation: agent.log `LM Studio reasoning:` / `LM Studio streaming:` delta lines;
`scripts/Find-TypoTokenStream.ps1` (TODO: labels a typo with the channel of the turn's last delta);
`scripts/Probe-OvernightLogprobs.ps1` (logprobs at the decision, `-SampleCount`, `-Endpoint chat`,
`-LogitBias`, per-request sampler fields); tokenizing a name via the LM Studio Python SDK
(`lmstudio` package, `client.llm.model(key).tokenize(text)`); LM Studio trace logs print input tokens as
`prefill token: <id> = <text>`.

## Related work

A web search on 2026-11-17 (llama.cpp, LM Studio, Unsloth, Hugging Face, Reddit, Hacker News) found no
public report of this exact failure. Nearest items:

- arXiv 2601.14658, "Say Anything but This: When Tokenizer Betrays Reasoning in LLMs": models (Qwen3
  included) emit a different token sequence for the same text ("phantom edits"); masking the offending
  output ids cut the cases to 0-5%, called a superficial remedy. Ours is the case where the alternative
  start cannot be completed.
- arXiv 2506.19004, "Broken Tokens?": instruction-tuned models keep most of their accuracy on
  non-standard input splits. arXiv 2406.11687, "Tokenization Falling Short".
- LM Studio bug tracker #1389 (a preset passed through the API applies only structured output; sampler
  fields ignored) and #1463 (a model loaded on demand by an API request skips its saved settings).
- llama.cpp #23335 (draft-mtp changes output on Q4_K_M; expected per the maintainer). MTP was ruled out
  here (same rate on and off).
- Unsloth's QwQ guide recommends repeat penalty 1.0.

## Out of scope

- Renaming fixture members or reordering the fixture to dodge the trap: diagnostic only, not a fix that
  generalizes across code.
- A broad ban on `.X` tokens: breaks names whose canonical split starts with one (above).
- Reasoning-channel logprobs: LM Studio returns logprobs for output text only and renders a prefill after
  an empty closed think block, so the reasoning decision can only be approximated.

## Decisions

### Open item 7, sampler enforcement (todo review 2026-11-22)

- Route: verify and record, not per-request enforcement. The transcript Settings block (`RunSettings.cs:26`,
  built at `:265-311`) gets a per-field status (sent / echoed / declared / unverifiable); a temperature or top_p
  echo mismatch refuses the run (today only a log warning, `LmStudioAgentClient.cs:387-407`); the batch script
  refuses to run with unverifiable fields unless the operator passes an explicit accept flag, which is recorded
  in the transcript. Why: the eval client posts to `/v1/responses` with only temperature/top_p
  (`LmStudioAgentClient.cs:236-246`) and no tool-calling chat/completions client exists (the SSE reader is
  responses-specific, `:649`), so per-request penalties are Large; they are deferred until a slip-rate comparison
  needs enforced penalties.
- Note: presence/frequency are marked Verified when the host echoes them although the harness never sent them
  (the echo may be the host default); the per-field status should call these "echoed", not "sent".
- Acceptance: a transcript Settings block shows the per-field status, and a
  `LmStudioAgentClientSettingsTests` case with a mismatched echoed temperature is refused.
