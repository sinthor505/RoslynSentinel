# ReplaceBlockFormatted accessibility gap costs 67.5% of all ApplyDiff failures (2026-09-05)

Quantified follow-up to [[project_reason_param_reveals_toolchoice_and_selfcorrection]]'s Finding
2 (models default to `ApplyDiff`/raw edits over the purpose-built `ChangeAccessibility` tool
~8:1 for a fixed operation), using the newly-built [[reference_parse_agent_log_script]] to
aggregate `ToolErrorCountsByName` and per-call `ResultOrError` text across every archived run
under `ModelTestingResults\113` (995 runs total, all test variants).

## Headline numbers

| Metric | Value |
|---|---|
| Total `ApplyDiff` failures, all 995 runs | 566 |
| ...of which mention `ReplaceBlockFormatted` | **382 (67.5%)** |
| — as `CS0103` ("name does not exist in current context") | 201 |
| — as `CS0122` ("inaccessible due to its protection level") | 81 |
| — via `ApplyDiff: ... introduces new compiler errors` (post-apply compile guard) | 299 blocks total (mixed CS codes) |
| — via `ApplyDiff pre-apply validate failed` (pre-apply guard) | remainder of the 382 |
| Runs affected at least once | 206 / 995 |
| Runs affected, by test type | PlanImplementVerify 126, MinimalGuidance 59, PlanThenExecute 35, MinimalGuidanceDisambiguated 25 |

Two-thirds of every `ApplyDiff` rejection across the entire historical dataset — every test
variant, every host, every repeat — traces back to one recurring sequencing mistake around one
symbol: the model calls `ReplaceBlockFormatted` from `BlockConverter.cs` before it has changed
that method's accessibility from `private`/`internal` (whatever the fixture's default is) to
something the caller can actually reach, or references it before checking it's in scope at all.

## Why this happens (mechanism, not just count)

The `WholeFileRewriteBug` family of fixtures (`MinimalGuidance`, `MinimalGuidanceDisambiguated`,
`PlanThenExecute`, `PlanImplementVerify` — everything that isn't `ScriptedPlan`) all pose a
variant of the same task: stop duplicating a whole-file-rewrite pattern and instead reuse
`BlockEditHelpers.ReplaceBlockFormatted`, which requires exposing that helper to its new caller.
A model has two ways to sequence this:

1. **Correct-in-one-shot**: call `ChangeAccessibility` (or include the modifier change in the
   same `ApplyDiff` hunk as the call site addition) before/atomically-with adding the call.
2. **Sequenced-and-caught**: add the call to `ReplaceBlockFormatted` first, get rejected by
   either the pre-apply Roslyn diagnostic pass (`CS0103`, if the name isn't even resolvable from
   that scope) or the post-apply compile guard (`CS0122`, if it resolves but access is denied),
   read the error, then go back and fix accessibility, then retry the call-site edit.

Both guards are working exactly as designed — [[project_wholefile_commentout_guard_added]] and
the general validate-before-apply pipeline exist precisely to catch a broken edit before it lands
— so this isn't a tool bug. It's a **model tool-choice/sequencing gap that the guardrails convert
into a visible, retryable error** instead of a silent wrong-state file. The cost is measured in
wasted turns and tool-error-budget consumption
([[project_per_tool_error_budget_added]]), not correctness.

## Why `PlanImplementVerify` is hit hardest (126/655 runs, the largest share of any test type)

The three-phase split's "implement" phase re-derives its own execution plan from the "plan"
phase's prose output rather than continuing the same tool-call context a single-call test would
have. If the plan phase's own prose doesn't explicitly sequence "change accessibility, *then* add
the call" (plausible — [[project_reason_param_reveals_toolchoice_and_selfcorrection]] shows even
single-context runs get this wrong most of the time), the implement phase has no built-in reason
to self-correct the ordering before its first attempt, whereas a single continuous run at least
has the *option* of noticing and pre-empting it mid-reasoning.

## Relationship to the existing tool-choice finding

[[project_reason_param_reveals_toolchoice_and_selfcorrection]] found the *symptom* — models pick
`ApplyDiff` over `ChangeAccessibility` ~8:1 for the identical "make `ReplaceBlockFormatted`
internal" operation, across 13 hand-reviewed runs. This finding quantifies the *cost* of that
pattern at full-dataset scale: it isn't a stylistic quirk, it's the single largest concrete
failure/retry driver in the entire tool-error dataset.

## Orientation-breaker note (adjacent, not part of this finding)

`SearchSolutionText` shows the highest raw error count (1372) in the same aggregate, but 373 of
those are the deliberate orientation-breaker trip message ("Orientation breaker tripped: 3
consecutive SearchSolutionText calls returned no matches... call ListAll/ListSolutionItems
instead") firing as designed after 3 consecutive no-match searches, not a real search failure —
the genuine no-match count is 999. Not part of this finding; noted so a future reader doesn't
double-count it as a bigger problem than `ReplaceBlockFormatted`.

## Suggested follow-up (not yet decided/actioned — see TODO.md)

Candidate interventions, roughly cheapest-to-most-invasive:
1. **Prompt-level nudge**: add an explicit sequencing hint to the fixture prompts that mention
   reusing `ReplaceBlockFormatted` — e.g. "check/change its accessibility before wiring the new
   call site" — and re-run a batch to see if it measurably shifts the `ApplyDiff`-vs-
   `ChangeAccessibility` ratio or the CS0103/CS0122 counts down. Cheapest, but risks being a
   fixture-specific patch rather than a general tool-design fix (same class of caveat as
   [[project_directive_error_messages_wiggle_room_theory]] — unconfirmed until tested).
2. **Schema/description nudge on `ChangeAccessibility`**: the tool is already in every relevant
   run's tool allowlist (confirmed in the earlier finding — availability isn't the gap), so this
   would mean strengthening its `[Description]` to more assertively claim the "make X internal"
   use case away from `ApplyDiff`, mirroring the fix already applied to
   [[project_modifymodifier_accessibility_footgun]] (closed by narrowing `ModifyModifier`'s enum
   so it couldn't be reached for accessibility changes at all) — except here the direction is the
   opposite (steer *toward* a tool, not away from one), so the same mechanism may not transfer
   directly.
3. **Compile-guard error message enrichment**: when a `CS0103`/`CS0122` rejection's message
   mentions a symbol whose only accessibility problem is fixable via `ChangeAccessibility`,
   consider whether the tool-layer error text could suggest that specific tool by name (matching
   the spirit of [[feedback_agent_friendly_error_messages]]) rather than leaving the model to
   infer the fix path from a raw Roslyn diagnostic — untested whether this measurably improves
   self-correction speed vs. just being redundant with what the model already infers correctly
   most of the time (it does eventually recover in most cases; the cost is wasted turns/budget,
   not unrecovered failures).

None of these have been implemented or A/B tested yet — this doc records the quantified problem
so a future session can pick one lever and measure it, the same way
[[project_modifymodifier_accessibility_footgun]] and
[[project_directive_error_messages_wiggle_room_theory]] did for their respective findings.
