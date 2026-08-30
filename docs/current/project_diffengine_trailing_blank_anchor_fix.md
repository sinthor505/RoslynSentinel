---
name: project_diffengine_trailing_blank_anchor_fix
description: DiffEngine.ApplyDiff phantom trailing-blank-line bug fixed; model diff headers proven untrustworthy; DiffHunkAnalyzer diagnostic added
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-30T06:16:34.941Z
---

Fixed in commit af7a9ab (2026-08-29): `DiffEngine.ApplyDiff` had a phantom
trailing-blank-line anchor bug in `ReadHunkBody`. A blank line at the very end
of a diff's raw text (trailing newline, or immediately before the next `@@`
header) was structurally ambiguous with a genuine blank context line —
`IsContextOrRemovalLine` treats zero-length lines as implicit blank context,
so an unguarded trailing blank became a phantom anchor requirement that
defeated an otherwise-exact hunk match inside the 60-line reanchor window.
Root-caused from a real model-eval transcript: hunk 1 was purely additive,
hunk 2's lines still existed shifted by exactly hunk 1's insertion count, and
should have matched within `HunkReanchorWindow` — but didn't, because of the
phantom anchor, not because the match was genuinely out of range.

**Why:** A prior turn in the same investigation had wrongly concluded "not a
bug" — the user pushed back with the reasoning above and forced the real root
cause to be found. The fix is heuristic/structural (scan for the next `@@` or
end of input as the hunk boundary), NOT header-count-based.

**Important finding — do not "fix" this differently later:** model-generated
diff hunk headers (the `@@ -oldStart,oldCount +newStart,newCount @@` line)
cannot be trusted for their declared old/new line counts, even when the body
content and stale-but-nearby line numbers are otherwise fine. Proven with a
real transcript where one hunk's header claimed 7/7 lines but the body had
3/3, and another claimed 6/15 but the body had 4/19. A header-count-trusting
rewrite of `ReadHunkBody` was attempted and reverted after it regressed the
real-transcript regression test for exactly this reason.

**How to apply:** Any future change to `DiffEngine`'s hunk-boundary detection
must keep scanning structurally (next `@@` header or end of input), not trust
declared counts. `DiffHunkAnalyzer` (new file,
`RoslynSentinel.Common/DiffHunkAnalyzer.cs`) is a diagnostic parser that
reports per-hunk declared-vs-actual counts and flags mismatches — it's wired
into `DiffEngine.ApplyDiff` via constructor-injected `ILogger<DiffEngine>`
(DI-resolved automatically via the existing `AddSingleton<DiffEngine>()`
registration; `NullLogger<DiffEngine>.Instance` fallback keeps the ~28
existing parameterless `new DiffEngine()` test call sites working). Every
`ApplyDiff` call logs a warning when the analyzer finds issues; a
`DiffApplyException` always logs the full report. Use this analyzer's output
first when investigating any future `ApplyDiff` failure instead of manually
re-deriving line-count bookkeeping by hand.

See also [[project_searchmode_literal_override_bug]] for the related dogfood
investigation this bug was found during.
