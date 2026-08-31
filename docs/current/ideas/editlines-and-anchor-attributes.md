# Idea: EditLines tool + Anchor attributes

Status: **parked** — not scheduled. Filed 2026-08-31.

## Context

Model-eval testing showed agents consistently struggling with `ApplyDiff`.
Research into LLM diff generation confirms this is a common failure mode
industry-wide (line-count sensitivity, context-window hallucination on the
"before" lines, rigid unified-diff syntax, cascading line-shift errors across
multi-hunk patches). Tools like Aider avoid this by using search/replace
blocks matched with deterministic string algorithms instead of asking the
model to track line numbers.

**However**: several `ApplyDiff`/`DiffEngine` bugs (hunk/line-number
miscalculation) were fixed since this idea was raised — see
[project_applydiff_fixes_unblocked_model_eval.md](../project_applydiff_fixes_unblocked_model_eval.md)
and [project_diffengine_trailing_blank_anchor_fix.md](../project_diffengine_trailing_blank_anchor_fix.md).
Those fixes already unblocked a model that was previously stuck. Revisit
whether either idea below is still worth building only after further
model-eval runs show ApplyDiff is still a bottleneck post-fix.

## Idea 1: `EditLines` tool (search/replace style)

Take `originalLineContent` (exact lines to replace) and
`replacementLineContent` (the new lines) instead of a unified diff. Matched
via deterministic string search, not model-computed line numbers/hunk
headers — sidesteps line-count bookkeeping and diff-syntax errors entirely.

Design risks to resolve if this gets picked up:
- Non-unique match (`originalLineContent` appears more than once in the
  file) — must fail loudly with a count, not guess which occurrence.
- Zero match (whitespace/indentation mismatch) — fail loudly with a clear
  diagnostic, ideally a near-match suggestion.
- Multi-file / multi-hunk in one call — decide whether to support batching
  or force one call per edit.

This is the more promising of the two ideas and directly targets the
"reproducing the before-text" failure mode. Worth prototyping first if
ApplyDiff issues resurface.

## Idea 2: Anchor attributes (block-level identity markers)

Similar to `BulkComment`'s `ContentHash`, run a pass that assigns a unique
anchor id to each syntactic block (member, if/else, for, try/catch, ...).
Two delivery options considered:
- Write an actual attribute into the source on disk (rejected — pollutes
  the codebase, requires cleanup/round-tripping).
- Inject in-memory only: when a file is loaded from disk, a helper
  generates and attaches anchors to the in-memory tree so all tools see
  them and the agent can reference them in output, but nothing is persisted
  to disk.

More speculative and a bigger lift than Idea 1. Anchors help a model
*locate* a block, but ApplyDiff's actual observed failures were in
*reproducing* the before-text for a hunk — a problem Idea 1 addresses more
directly. Only worth pursuing if a concrete gap shows up that `EditLines`
doesn't cover (e.g., agents struggling to specify *which* block to edit,
not how to spell its contents).

## Next steps (when revisited)

1. Re-run model-eval against current `ApplyDiff` to see if the fixed bugs
   resolved the practical failure rate.
2. If failures persist, prototype `EditLines` first (smaller, more directly
   targeted).
3. Only design anchor attributes if a distinct "can't locate the block"
   failure mode shows up that `EditLines` doesn't fix.
