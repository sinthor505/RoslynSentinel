---
name: reason_param_reveals_toolchoice_and_selfcorrection
description: Reviewing the mandatory reason param across 13 completed .113 PlanImplementVerify runs surfaced tool-choice fidelity gaps and self-correction moments invisible from tool names/results alone
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T06:30:28.987Z
---

Reviewed the `reason` argument on every tool call across the 13 `.113` runs (of a 20-run batch)
that completed all three phases and got `VERIFIED: PASS` (see the same batch's 75%
completion-rate / 100% pass-rate-when-completed finding, not yet its own memory as of this
writing — 5/20 runs failed via implement-phase timeout or a missing phase log, never reaching
verify). The `reason` param was made mandatory across all MCP server tools per
[[project_reason_param_enforcement_result]]; this is the first time it's been read back in bulk
across a real batch rather than just checked for presence.

**Finding 1 — reasons expose self-correction that tool names/results hide.** Run
`20260905-042228-925`'s reason sequence: apply accessibility change → apply main fix → **"Fixing
unintended change to em-dash in BlockEditHelpers.cs summary comment"** → "Reading original
BlockEditHelpers.cs to get the exact character used" → corrective diff applied. Without the
`reason` text this is indistinguishable from routine `ReadFile`/`ApplyDiff` noise; with it, it's
a clear catch-and-fix of an unintended side effect (some encoding/character substitution in a
doc comment) mid-run.

**Finding 2 — reasons reveal tool-choice fidelity gaps for a single fixed operation.** All 13
runs needed to do the identical thing ("make `ReplaceBlockFormatted` internal"), but the *tool*
chosen to express that intent varied even though the reason text stayed nearly identical:
- `ApplyDiff` (raw text edit) — ~8/13 runs, the plurality
- `ChangeAccessibility` (the purpose-built tool for exactly this) — only 1/13 (`015209-583`)
- `ModifyModifier` — 2/13 (`015209-583`, `013507-133`)
- `Member` — 1/13 (`020545-599`)

`ChangeAccessibility` is in the 11-tool `MinimalTools` allowlist every one of these runs was
using, so availability isn't the gap — the model defaults to a generic diff over the
semantically-correct tool most of the time. This mirrors the schema-design lesson from
[[project_modifymodifier_accessibility_footgun]] (closed by removing accessibility keywords from
`ModifyModifier`'s enum) but shows the *opposite* tool being under-used rather than the wrong one
being reached for — worth treating as a possible follow-up tool-design nudge, not just a
one-off observation.

**Finding 3 — reasons flag likely retry-after-failure without needing turn-by-turn transcript
diffing.** `20260905-030830-702`'s second `ApplyDiff` reason reads "Fixing bug... **Use fully
qualified name** BlockEditHelpers.ReplaceBlockFormatted" immediately after a first `ApplyDiff`
with an unqualified name — strongly implying the first attempt didn't resolve/compile and the
model self-corrected. The eventual `Build` succeeded, which would fully mask this retry if only
counting terminal pass/fail; the reason text is what surfaces it cheaply.

**Finding 4 — reasons distinguish requested-fix from scope-creep cleanup.** `020545-599` and
`033908-461` both went on to remove the now-dead `ReformatWholeFile` method (`SafeDeleteUnusedSymbol`
and `ApplyDiff` respectively) with reasons explicitly citing cleanup, not the original ask. Good
hygiene, but confirm `AssertFixApplied` tolerates the extra deletion rather than only checking the
two originally-named changes — untested as of this writing.

**Why this matters**: the `reason` param was added/enforced as an accountability/audit mechanism
([[project_reason_param_enforcement_result]]), but this is evidence it's also a cheap analysis
signal for model-eval specifically — tool-choice-fidelity and self-correction rate are both
things that were previously invisible without manually reading full transcripts turn-by-turn, and
both are now greppable directly from `reason` strings across a whole batch in seconds.

**How to apply**: when reviewing future model-eval batches, grep `"reason":"..."` per phase before
reading full transcripts — it's a fast triage pass for (a) whether the model reached for the
purpose-built tool vs. a generic fallback for known operations, and (b) whether a reason sequence
shows a correction/retry pattern worth a closer look, even when the run's terminal result is a
clean pass. Consider adding a lightweight batch-level report that tallies reason-implied
tool-choice-for-known-operations across a whole `-Repeats N` run, since this was done by hand here
and would generalize well to a script.
