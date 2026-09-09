# Finding: ApplyUnifiedDiff hunk mis-anchored when two sibling methods share near-identical trailing trivia

**Status:** confirmed tool defect, not yet fixed. Filed under `blockers/` per the dog-fooding
process — found live during eval run `plan-eval-defect-remediation-v2`, 2026-09-08 17:49.

## Context

An eval agent was editing `EngineResultWrapper.cs` as part of Phase 1 of
`plan-eval-defect-remediation-v2.md` (adding a `BuildOutcome`-aware accessor). It submitted a
two-hunk `ApplyUnifiedDiff` call. Hunk 2 was rejected with `DiffApplyFailed`.

## Root cause

`EngineResultWrapper.cs` has two sibling methods, `TryGetData` and `Data`, whose trailing trivia
is near-identical:

```
        return this._data!;
    }

```

The hunk's context lines matched this trailing block, but the diff engine anchored to the *wrong*
occurrence (matched `TryGetData`'s tail when the model intended `Data`'s, or vice versa — see the
transcript for the exact hunk). The anchor-matching logic apparently doesn't verify uniqueness
across the whole file before committing to a match location, or picks the first match without
checking for ambiguity.

This is a distinct bug from the one fixed in `docs/current/project_diffengine_trailing_blank_anchor_fix.md`
(commit af7a9ab), which addressed anchoring against a trailing *blank line* at end-of-file/end-of-block.
Here the anchor text is non-blank, real code — it's just duplicated near-verbatim across two
methods in the same file. The existing fix does not cover this case.

## Confirmed non-issue: hunk atomicity works correctly

Worth noting because the eval agent initially got this wrong itself: when hunk 2 failed to
anchor, hunk 1 (which had a valid, unique anchor for the new `BuildOutcome` enum) was **not**
silently applied. The agent assumed partial application, re-read the file via `ReadFile`, found
it completely unchanged, and corrected its own assumption before proceeding. `ApplyUnifiedDiff`
is atomic across hunks within one call — that part is working as designed.

## Recommendation

- Anchor-matching should verify the matched context is unique within the target file (or at
  minimum within the target member, if the hunk is scoped to one) before applying, and return a
  clear "ambiguous anchor, N matches found" error rather than either silently picking one or
  generically failing with `DiffApplyFailed`. An explicit ambiguity error would let the model
  self-correct by widening its context window, the same way it already does for
  not-found-anchor errors.
- Consider extending `DiffHunkAnalyzer` (added alongside the af7a9ab fix) to flag this ambiguous-anchor
  case specifically, since it already exists for anchor diagnosis.
- Not yet reproduced in isolation / no code fix attempted yet — this doc is the live-eval finding,
  follow-up is to write a focused repro (two methods with identical trailing braces/return
  statements) and confirm before touching `DiffEngine.cs`.

## Reference

- Eval log: `lmstudio_logs/plan-eval-defect-remediation-v2 - 2026-09-08 17.49.md`
- Related: `docs/current/project_diffengine_trailing_blank_anchor_fix.md`
- Memory: `project_diffengine_anchor_collision_duplicate_trivia`
