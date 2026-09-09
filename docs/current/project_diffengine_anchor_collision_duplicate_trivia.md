---
name: project_diffengine_anchor_collision_duplicate_trivia
description: ApplyUnifiedDiff hunk targeted wrong method when two sibling methods share near-identical trailing trivia; distinct from the earlier trailing-blank-line fix
metadata:
  type: project
---

`ApplyUnifiedDiff` mis-anchored a hunk in `EngineResultWrapper.cs` (eval run 2026-09-08 17:49, `plan-eval-defect-remediation-v2`) because two sibling methods (`TryGetData` and `Data`) end with near-identical trailing trivia (`return this._data!;\n    }\n\n`). The hunk's context matched the wrong occurrence and was rejected as `DiffApplyFailed`.

This is a different bug class from `project_diffengine_trailing_blank_anchor_fix` (af7a9ab), which fixed anchoring against a trailing blank line at end-of-file/end-of-block. This case is duplicate near-identical anchor text **across two distinct methods in the same file**, not a trailing-blank issue — the existing fix does not cover it.

Confirmed side effect worth noting: `ApplyUnifiedDiff` is atomic across hunks in a single call — when hunk 2 failed to anchor, hunk 1 (which had a valid unique anchor) did NOT get silently applied. The eval agent initially assumed partial application, self-verified via `ReadFile`, found the file completely unchanged, and corrected itself before proceeding. Confirms the tool's atomicity is working as intended; the bug is purely in anchor selection when multiple near-duplicate anchors exist in one file.

**How to apply**: When diagnosing a `DiffApplyFailed` rejection, check whether the hunk's context lines are unique within the target file — not just within the target method. If not, the model needs a wider/more distinguishing context window, or the diff engine needs a way to disambiguate (e.g. anchor scoped to a named containing member, similar to how `project_diffengine_trailing_blank_anchor_fix` added `DiffHunkAnalyzer` for diagnosis). Not yet fixed — no code change made for this case as of 2026-09-08.
