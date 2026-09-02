# Model-Eval Cross-Run Pattern Analysis (2026-09-02)

Excavation of `ModelTestingResults\113` (165+ archived runs) per the prompt in
`docs/current/model_eval_pattern_analysis_prompt.md`. Pass/fail was re-derived
programmatically from `transcript.json` against the **current, real** assertions
in `WholeFileRewriteAgentTests.AssertFixApplied`, `PlanImplementVerifyAgentTests`,
and `AgentToolErrorAssertions` — not eyeballed, and not reusing older recorded
pass rates, which turned out to be stale (see §1).

## 0. Data inventory and housekeeping

| Test name | Runs found (113) | Notes |
|---|---|---|
| `Model_FixesWholeFileRewriteBug_MinimalGuidance` | 56 (53 canonical + 3 stray-only) | |
| `Model_FixesWholeFileRewriteBug_MinimalGuidanceDisambiguated` | 20 | |
| `Model_FixesWholeFileRewriteBug_PlanThenExecute` | 45 | |
| `Model_FixesWholeFileRewriteBug_ScriptedPlan` | 5 | |
| `Model_FixesWholeFileRewriteBug_PlanImplementVerify` | 39 (9 stuck in plan phase, no implement/verify) | |
| `Model_PlansWholeFileRewriteFix_PrefersCallingHelper` | 1 | |

**Stray top-level timestamp directories** directly under `113\`
(`20260831-141121`, `-155139`, `-163239`, `-170912`, `-175440`): confirmed to be
an outer "batch" wrapper containing the same nested
`<TestName>/<run-timestamp>/` structure as the canonical location — a leftover
archiving-path artifact from before whatever change now writes runs only to
the canonical location. Byte-for-byte identical to canonical runs where they
overlap. **3 MinimalGuidance runs exist only under the stray path**
(`20260831-073838-790`, `-074155-562`, `-074415-492`) and were folded into the
56-run MinimalGuidance analysis. No other unique runs live only under the
stray dirs.

**PlanImplementVerify's 9 "no implement/verify subdirectory" runs**: all have
a `plan/` folder but no `implement/`/`verify/`. One
(`20260901-035746-273`) failed because the fixture file genuinely did not
exist on disk yet when the plan phase's workspace loaded — a test-harness
setup/timing race, not a model failure. The other 8 show the plan phase
converging normally (readable, correct plan text) — the implement phase
simply never got created afterward. Root cause not found; flagged as an open
infra item (§6).

## 1. Pass-rate re-derivation — the previously recorded numbers are stale

Prior memory entries ([[project_disambiguated_prompt_n20_result]],
[[project_overnight_50run_sweep_2026_08_31]],
[[project_minimalguidance_reasoning_pattern_analysis]]) recorded MinimalGuidance
around 34% and Disambiguated around 40%. Those numbers were computed against
an **older version of `AssertFixApplied`** that scored "call the helper
directly, raising its accessibility" as a **failure**. The assertion was later
flipped to require exactly that (matching the real-world incident the fixture
models). Re-deriving pass/fail against the **current** assertion gives very
different numbers:

| Test variant | n (valid) | Strict pass (current assertion) | Mechanically-correct-fix rate (ignoring tool-error budget) |
|---|---|---|---|
| MinimalGuidance | 56 | **1/56 (2%)** | 16/56 (29%) |
| MinimalGuidanceDisambiguated | 20 | **0/20 (0%)** | 0/20 (0%) |
| PlanThenExecute | 45 | **19/45 (42%)** | 28/45 (62%) |
| ScriptedPlan | 5 | **5/5 (100%)** | 5/5 (100%) |
| PlanImplementVerify | 30 (9 excluded) | **14/30 (47%)** | 19/30 (63%) |

MinimalGuidance and Disambiguated are near a complete floor under the current
assertion, not ~34-40%. "Private priming" can no longer be meaningfully
recomputed as a pass/fail correlation — there's essentially no passing group
left. It's reframed below in terms of which wrong-fix bucket a run falls into.

ScriptedPlan's 5/5 and the ~45-65% mechanical-correctness rates for
PlanThenExecute/PlanImplementVerify still support "planning, not execution, is
the bottleneck" — the 29%→62% gap between MinimalGuidance and PlanThenExecute
is real and large.

## 2. New failure signatures (ranked by frequency)

### 2.1 Own-copy-of-shared-helper (new, dominant in MinimalGuidance/Disambiguated)

Frequency: MinimalGuidance 25/56 (45%), Disambiguated 11/20 (55% — *worse*
proportionally than the un-disambiguated prompt). Near-absent in ScriptedPlan
and PlanThenExecute.

The model's reasoning claims it's reusing `BlockEditHelpers.ReplaceBlockFormatted`,
but the actual `ApplyDiff` payload pastes a full second, still-`private`
definition of that method into `BlockConverter.cs` — `BlockEditHelpers.cs` is
never touched. Builds cleanly, model reports success confidently. This is the
dominant reason MinimalGuidance/Disambiguated fail — not thrashing or getting
lost, just confidently doing the wrong (but plausible) thing. **The
disambiguating sentence explicitly telling the model to call the helper
directly rather than copy its body did not reduce this pattern** (55% vs 45%,
noisy at n=20 but certainly not the intended large drop).

Evidence: `MinimalGuidance\20260831-080343-376`, `...\081952-910`,
`...\083233-487`; `MinimalGuidanceDisambiguated\20260831-195606-215`.

### 2.2 Ignores the reusable helper, inlines its own fix (new)

Frequency: MinimalGuidance 10/56 (18%), Disambiguated 9/20 (45%),
PlanThenExecute 4/45 (9%).

Model fixes the actual bug (removes the `ReformatWholeFile` call) but never
engages `BlockEditHelpers.cs` at all — writes a one-off inline replacement
that drops the padding requirement. Passes "no longer calls
ReformatWholeFile," fails "must reuse ReplaceBlockFormatted." **Got worse
under the disambiguated prompt** (18%→45%) even though combined 2.1+2.2 stayed
roughly flat — disambiguation shifted failures between buckets rather than
reducing them.

Evidence: `MinimalGuidance\20260831-103741-483` (6 clean turns, builds,
confident success report, never mentions `BlockEditHelpers.cs`).

### 2.3 Tool-error-budget false negative via ModifyModifier/ChangeAccessibility split (new, high-value)

Frequency: MinimalGuidance 15/56 (27% of all runs — 94% of its 16
mechanically-correct fixes), PlanThenExecute 9/45 (20%), PlanImplementVerify
3/30 (10%). **27 runs total (~16% of the corpus) produce a byte-perfect,
functionally correct fix and still fail the test.**

Mechanism: model calls `ApplyDiff` to switch the call site to
`BlockEditHelpers.ReplaceBlockFormatted` before raising its accessibility →
real `CS0122` (inaccessible due to protection level) — a reasonable ordering
mistake. It then calls `ModifyModifier` with `modifier: "public"`, which is
**rejected by design**: `"ModifyModifier does not handle accessibility
keywords (got 'public'). Use ChangeAccessibility instead..."`. The model
reliably follows this redirect on the very next turn and succeeds via
`ChangeAccessibility` — exactly the self-correction retry
`AssertWithinBudget`'s own doc comment says should be tolerated. But because
the CS0122 and the ModifyModifier rejection land on **two different tools**,
the **per-tool** cap (2) never trips, while the **total** cap (2) does the
moment any third, unrelated hiccup occurs (almost always a benign
zero-match `SearchSolutionText`, §2.5).

This `ModifyModifier`-rejects-accessibility-keyword error occurs in 28/56
MinimalGuidance runs (50%), 10/45 PlanThenExecute (22%), widely in
PlanImplementVerify implement-phase logs, but **0/20 Disambiguated and 0/5
ScriptedPlan** — absent wherever the model is told, or infers early, exactly
which tool to use for "raise accessibility." Disambiguated's 0% isn't a sign
of a working prompt — none of its 20 runs got far enough to attempt raising
accessibility at all (they duplicated the body or ignored the helper instead,
§2.1/2.2).

**This is a genuine, previously undocumented, high-value, easily actionable
finding**: `ModifyModifier` silently accepting an accessibility-keyword-shaped
argument and only failing at call-time with a redirect is a tool-signature
footgun. Every model that reaches for it this way *does* self-correct — it's
the harness's own total-error-budget cap that then fails the run, not the
model.

Evidence: `MinimalGuidance\20260831-082252-974` through `...\102848-496` (15
runs); `PlanThenExecute\20260831-225158-267`, `...\230039-214`,
`...\234008-721`, `...\235008-943`, `...\235252-646`, `...\235603-974`,
`...\20260901-003053-048`, `...\010254-967`, `...\010810-630`;
`PlanImplementVerify\20260901-072652-597`, `...\084810-345`,
`...\20260902-052529-399`.

### 2.4 Whole-file comment-out via ApplyDiff (known, fixed, confirmed no recurrence)

Exactly 1 occurrence in the entire corpus —
`PlanImplementVerify\20260902-062730-159\implement`, turn 2 — precisely the
incident that motivated commit `579ead4`. Searched every `agent.log` in the
corpus for the fixture's commented-out first line; found only in this run.
**No recurrence anywhere else, before or after the fix.**

Bonus detail: this run's implement-phase self-report ("Replaced
`ReformatWholeFile`... verified by a successful quickBuild") is also false —
describes a fix that was never applied. The independent **verify phase
correctly caught this**, returning `VERIFIED: FAIL` despite the implement
phase's confident self-report — concrete evidence for the value of
PlanImplementVerify's independent-judgment gate.

### 2.5 Guessed-name search thrashing / orientation-breaker trips (new, very high frequency, usually benign)

Frequency: MinimalGuidance 40/56 (71%), Disambiguated 15/20 (75%),
PlanThenExecute 32/45 (71%), widespread in PlanImplementVerify plan/implement
phases, 0/5 ScriptedPlan.

Before finding `BlockEditHelpers.cs`, the model consistently invents a
plausible-sounding but never-seen symbol name (`ReformatBlock`,
`ReformatFile`) and searches for it repeatedly via `SearchSolutionText` —
sometimes 5-9 times with zero matches — before the orientation breaker trips
after 3 consecutive zero-match calls and forces `ListAll`/`ListSolutionItems`,
which reliably finds the real helper within 1-2 turns. In &gt;90% of cases
this fully self-corrects and is purely a turn-budget tax (2-14 wasted turns),
not fatal on its own. Absent in ScriptedPlan (never asked to search). The same
underlying "guess a plausible name before using a discovery tool" reflex also
shows up as a wrong `LoadSolution` path guess in some PlanImplementVerify
implement-phase runs (§2.6) and the plan-phase FileNotFound case (§0) — reads
as one coherent behavior manifesting in three surface forms.

Evidence: `MinimalGuidance\20260831-085110-511` (15 of ~19 turns spent here
before recovering and succeeding mechanically), `...\080343-376`,
`PlanThenExecute\20260831-231038-082`.

### 2.6 Implement-phase wrong-workspace stumble → reliable timeout (new, PlanImplementVerify-specific)

Frequency: 5/39 (13%) of PlanImplementVerify implement-phase runs.

Distinct from §2.5's benign self-correction: first `ReadFile`/`LoadSolution`
targets a wrong/guessed path and gets a genuine `FileNotFoundException`. All 5
eventually self-correct the path — but **all 5 still end in
`TurnCapExceeded`/`WallClockCapExceeded`** rather than `ModelFinished`; the
stumble costs enough of the phase's tighter budget (25-turn/5-minute vs the
single-call variants' 40-turn/30-minute) that the run never gets to apply and
verify a fix. One of the five (`20260902-053553-299`) instead crashed on a raw
LM Studio serving error (`"Unterminated string in JSON at position 33179"`) —
infra fault, not model reasoning.

This is a genuine new cost of the fresh-context-per-phase design: each phase
re-discovers the workspace from scratch with no memory of the plan phase's own
successful navigation, and the implement phase's tighter budget leaves little
slack to absorb a stumble the single-call variants shrug off.

Evidence: `PlanImplementVerify\20260901-042404-655\implement`
(WallClockCapExceeded, 24 turns, 19m24s), `...\20260901-222149-093\implement`
(TurnCapExceeded, 25 turns), `...\20260902-005734-160\implement`
(WallClockCapExceeded), `...\20260902-024337-086\implement`
(TurnCapExceeded), `...\20260902-053553-299\implement` (LM Studio stream
error).

### 2.7 "Describes the fix, never executes it" / truncated-execution stop (new, PlanThenExecute-specific, rare but striking)

Frequency: 2/45 (4%) PlanThenExecute; 0 elsewhere.

`PlanThenExecute\20260831-231038-082` and `...\20260901-001750-918`:
immediately after `ReadFile` of `BlockEditHelpers.cs`, the next turn takes
6m32-33s to generate (vs 2-15s for every other turn in either run) and
produces a complete, correct prose description of the right fix — but **zero
tool calls**; the harness records `ModelFinished` and the run ends there,
`ApplyDiff` never called. A full corpus scan for any turn exceeding 60s across
MinimalGuidance/Disambiguated/PlanThenExecute/ScriptedPlan found only 5 such
turns total; these two (~6.5 min each, nearly identical) are 3-6x every other
slow turn, which still completed with a normal tool call. Strongly suggests an
LM-Studio-serving-layer generation-length/timeout artifact (output including a
trailing tool-call JSON block gets truncated after a stall, client treats the
truncated text as a finished plain-text turn) rather than a reasoning defect —
the reasoning content itself is correct. Distinct from "silent action
substitution" (no substituted action, just a missing one) and from "wrong
plan"/"lost in context" (9 and 13 turns deep, well within budget, correct
diagnosis).

Evidence: `PlanThenExecute\20260831-231038-082` (turn 9, 393s, 0 tool calls),
`...\20260901-001750-918` (turn 13, 392s, 0 tool calls).

## 3. Does "reasoning-vs-tool-call divergence" recur?

**No.** This was the single most-wanted answer from this analysis.

- Automated scan for the literal signature (an `ApplyDiff` payload where
  &gt;60% of non-blank lines are comment-prefixed) across all variants:
  **exactly 1 hit**, the already-known `20260902-062730-159` run.
- Broader heuristic scan (short-or-empty reasoning immediately following a
  tool error) surfaced 9 candidates; all 9 manually inspected are benign,
  correct, silent self-corrections (e.g. `ModifyModifier` rejection →
  wordless correct `ChangeAccessibility` call; `FileNotFound` → wordless
  correct `ListWorkspaceSolutions`/`LoadSolution` recovery). None show a
  stated plan diverging from the actual action — they show healthy
  compliance with an error message's explicit redirect.
- Two wrong-*target* mistakes noticed as a side effect
  (`ChangeAccessibility` called with `targetName: "BlockEditHelpers"`, the
  class, instead of the method, in `PlanThenExecute\20260831-225158-267` and
  `...\230707-146`) are plain wrong-target errors, not
  reasoning/action divergence.

**Conclusion**: the one known instance reads as a genuine one-off — most
plausibly triggered by the preceding `ModifyModifier` error landing at a
moment where the next completion happened to synthesize a bad edit rather
than a correct retry — with no evidence of a systematic divergence pattern
elsewhere in 165 runs, with or without a preceding tool error. Given n=1, this
is low-probability sampling noise, not a reproducible trigger condition. The
"is this ApplyDiff payload mostly comments" check is cheap to run
continuously if ongoing monitoring is wanted; it's effectively already what
the size-guard fix does at the tool layer.

## 4. Why hasn't scaffolding reliably improved outcomes?

1. **The raw numbers actually do show scaffolding helping a lot** (2%/0% →
   42%/47%/100%) — the "hasn't reliably improved" framing was itself
   calibrated against the stale pre-flip pass rates (§1). Under the current
   assertion, more scaffolding clearly and substantially outperforms none.
   The open puzzle narrows to: why do PlanThenExecute (42%) and
   PlanImplementVerify (47%) plateau well below ScriptedPlan's 100%, and
   below their own 62-63% mechanical-correctness rate.

2. **A large, uniform chunk of that shortfall is the §2.3 tool-error-budget
   artifact**, not a real defect. Plan-first framing and the plan/implement
   split dramatically increase how often the model correctly reasons its way
   to "raise accessibility, then call the helper," which dramatically
   increases exposure to the `ModifyModifier` footgun. More scaffolding →
   more correct attempts → more exposure to a downstream tool trap that then
   gets punished by an assertion designed to tolerate exactly this kind of
   self-correction. Fixing the planning bottleneck exposed a
   tool/assertion interaction that wasn't visible before.

3. **Scaffolding does introduce at least one genuinely new failure mode**:
   PlanImplementVerify's fresh-context-per-phase design costs 5/39 (13%)
   implement-phase runs a wrong-workspace stumble the single-call variants'
   larger budget would likely absorb (§2.6), and 9/39 (23%) an outright
   missing implement/verify phase, one confirmed cause being a
   fixture-file-not-yet-on-disk race. PlanThenExecute's "state your full plan
   first" instruction also correlates with the two extreme-latency
   "describes but never executes" incidents (§2.7) — suggestive at n=2, not
   conclusive.

4. **The specific ambiguity MinimalGuidanceDisambiguated targeted does not
   appear closed.** Both prompts are dominated by duplicating the helper's
   body (§2.1) or ignoring it (§2.2), not the accessibility-modifier
   confusion the disambiguating sentence targeted — and the disambiguated
   prompt's failure mix shifted slightly toward more of both (55%/45% split
   vs 45%/18%), not toward the intended behavior.
   **PlanThenExecute achieves 80% mechanical correctness (36/45) on this
   exact ambiguity using the identical disambiguating text** plus only a
   "state a plan first" instruction — suggesting the fix is less about *what*
   the prompt says and more about forcing an explicit, committed plan before
   any tool call, which single-call prompts don't structurally require.

## 5. Ranked summary

| Rank | Signature | New or known | Variants | Approx. frequency | Preceded by tool error? | Depth |
|---|---|---|---|---|---|---|
| 1 | Duplicates shared helper's body (§2.1) | New | MinimalGuidance, Disambiguated | 45-55% | No | Mid-late |
| 2 | Guessed-name search thrashing (§2.5) | New | All self-directed variants | 71-75%, usually benign | No | Early |
| 3 | ModifyModifier/ChangeAccessibility budget false negative (§2.3) | New | MinimalGuidance, PlanThenExecute, PlanImplementVerify | 27 runs (~16% of corpus) | Yes (CS0122, then rejection) | Mid |
| 4 | Ignores helper, inlines own fix (§2.2) | New (related to but distinct from the older documented "ChangeAccessibility-on-helper" pattern) | MinimalGuidance, Disambiguated, PlanThenExecute | 9-45% | No | Mid |
| 5 | Implement-phase wrong-workspace stumble → timeout (§2.6) | New | PlanImplementVerify only | 5/39 (13%) | Sometimes | Early, fatal via budget |
| 6 | Plan-phase stuck, no implement/verify (§0) | New (infra) | PlanImplementVerify only | 9/39 (23%) | 1 confirmed, 8 unexplained | N/A |
| 7 | Truncated-execution stop (§2.7) | New | PlanThenExecute only | 2/45 (4%) | No — anomalous latency | Mid |
| 8 | Whole-file comment-out via ApplyDiff (§2.4) | Known, fixed, confirmed no recurrence | PlanImplementVerify (1 pre-fix instance) | 1/165 | No | Early |
| 9 | Reasoning-vs-tool-call divergence | Known — searched exhaustively, not found to recur | PlanImplementVerify (1 instance) | 1/165 | Yes, in the one known case | Early |

On the older documented "model changes a helper's accessibility instead of
fixing the actual call site" pattern: in this corpus, `ChangeAccessibility`/
`ModifyModifier` calls are near-universally used *correctly* as part of the
§2.3 self-correction loop. The only wrong-target instances found were the two
class-vs-method `targetName` mistakes noted in §3 — a narrower, much rarer
variant. This pattern does not appear to be a major recurring failure mode
currently, though n=2 is too small to be confident this is a real reduction
rather than the pattern having moved into `ModifyModifier`-rejection retries
that now succeed.

## 6. Actionable next steps

1. **Loosen or restructure `AgentToolErrorAssertions.AssertWithinBudget`'s
   total cap**, or special-case the `ModifyModifier`→`ChangeAccessibility`
   redirect sequence as one logical retry rather than two independent tool
   errors. Flips ~27 runs (mostly MinimalGuidance) from FAIL to PASS with zero
   model/prompt change. Highest-leverage, lowest-risk fix identified.
2. **Have `ModifyModifier` route accessibility keywords to
   `ChangeAccessibility` internally** (or reject earlier/more informatively)
   rather than requiring the model to discover the redirect via a failed
   call every time. Hit by roughly half of MinimalGuidance's runs and a fifth
   of PlanThenExecute's — removing the trap outright likely raises real pass
   rates more than any prompt change tried so far.
3. **Re-run or re-word the MinimalGuidanceDisambiguated experiment** — its
   disambiguating sentence isn't moving the needle on the ambiguity it
   targets, and may be shifting failures sideways. PlanThenExecute's 80%
   mechanical correctness on the identical ambiguity, using the same text
   plus only a "write your plan first" instruction, points to forcing an
   explicit committed plan before the first edit as the more promising
   direction.
4. **Investigate the PlanImplementVerify plan→implement phase-transition
   drop** (9/39, 23%). One confirmed cause is a fixture-file-not-yet-on-disk
   race in `SetUp`; the other 8 need a repro (likely an unhandled
   exception/cancellation between `RunPhaseAsync` calls) since the plan phase
   itself converges normally. Currently pure lost signal (scored as neither
   pass nor fail).
5. **If PlanImplementVerify is retained, budget its implement phase more
   generously**, or give it workspace-path continuity from the plan phase —
   its fixed 25-turn/5-minute cap is tight enough that a stumble the other
   variants shrug off instead reliably burns an entire run (5/39, 13%).
6. **No action needed** on the whole-file comment-out guard (§2.4, confirmed
   fixed) or on reasoning-vs-tool-call divergence (§3, not found to recur —
   treat as noise unless a second instance surfaces).
7. **Genuine unresolved noise**: the 8 unexplained PlanImplementVerify
   plan→implement transitions and the exact trigger for the two 6.5-minute
   truncated-execution turns (§2.7) both look real and distinct, but no
   definitive root cause was found beyond "probably an infra/serving-layer
   timing issue."
