# Design: add ReplaceSnippet — a size-capped oldContent/newContent edit tool, anchored via ContextHelper — and gate ApplyUnifiedDiff

**Status:** drafted 2026-09-08, updated 2026-09-08 (see Resolved decisions), not yet implemented.
Motivated by two live eval-run bugs against `ApplyUnifiedDiff` in `plan-eval-defect-remediation-v2`
(2026-09-08 17:49 and 17:58 logs). `ApplyUnifiedDiff` (today's diff-hunk-syntax tool) is not
deleted or renamed — it moves to the gated `SentinelWholeFileWriteTools.cs` surface alongside
`ApplyDiff`, and a new tool `ReplaceSnippet` takes its former slot on the default surface. See
Resolved Decisions below.

1. Anchor collision — a hunk matched the wrong of two near-identical sibling methods
   (`docs/current/blockers/finding_applyunifieddiff_anchor_collision_duplicate_trivia.md`).
2. Phantom whole-method insertion — a hunk with a header that understated its own body size
   (2 declared removals, 159 actual `+` lines) was applied structurally as submitted, duplicating
   the file's tail. Root-caused as a model mistake, but enabled by two tool-side gaps: no size cap
   on this tool, and `DiffHunkAnalyzer`'s `HeaderCountsMatchBody: false` finding for that exact hunk
   was only logged server-side, never returned to the caller.

## Problem framing

`ApplyUnifiedDiff` accepts arbitrary-size unified-diff text with `@@` headers whose line-count
claims the engine deliberately does not enforce (`DiffEngine.cs:227-233`, by design, to tolerate
stale/miscounted headers from callers). That tolerance is the right call for genuine drift, but it
means a hunk whose *body* doesn't match its header is applied exactly as literally written, with no
structural check that the counts add up. Combined with no size ceiling, a single malformed hunk can
silently splice in hundreds of lines. Separately, anchor matching in `ReanchorHunk` uses its own
heuristic window-search, distinct from the exact/ambiguity-checked anchoring `ContextHelper` already
provides and that other tools already rely on — so `ApplyUnifiedDiff` doesn't benefit from the
ambiguous-match detection that already exists in the codebase.

The tool's own description already tells callers to use `WriteFile(ReplaceFile)` for whole-file
rewrites and `RenameSymbol`/`ChangeSignature`/etc. for structural, multi-file-aware edits. In
practice, every observed failure mode was a hunk trying to do more than "a small, localized edit"
— which is what this tool is meant to be, per the user's framing: intended for small edits that no
other tool covers. This design assumes there is no legitimate case for using this tool to submit an
edit larger than roughly 10-20 lines / 200 characters — anything bigger already has a better-fit
tool, and forcing the boundary at the tool layer converts a documentation recommendation into a
structural guarantee.

## Proposed change

Add a new tool `ReplaceSnippet` on the default MCP surface, taking an exact-text
`oldContent`/`newContent` pair anchored via the same `ContextHelper.FindSnippetPosition` mechanism
other tools already use, under a hard size cap. `ApplyUnifiedDiff` moves to the gated surface
(see Resolved Decisions) rather than being deleted — the diff-hunk-header failure class (mismatched
header/body counts) doesn't exist in `ReplaceSnippet` because there's no header to mismatch, but the
old tool is kept available for reactivation.

### New tool: ReplaceSnippet (replaces ApplyUnifiedDiff's `filepath` + `unifiedDiff` pair)

```csharp
[McpServerTool(Name = "ReplaceSnippet")]
public async Task<ToolResult<object>> ReplaceSnippet(
    [Description(ToolParams.Reason)] string reason,
    [ExternalInputRequired(DataTag.Action)] ProposedChangeAction action,
    [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
    [ToolOption(ToolOptionTag.OldContent, required: true)] string oldContent,
    [ToolOption(ToolOptionTag.NewContent, required: true)] string newContent,
    string? lineBefore = null,
    string? lineAfter = null,
    [ToolOption(ToolOptionTag.ValidateOnApply)][Description(ToolParams.ValidateOnApply)] bool validateOnApply = true,
    [Description(ToolParams.ReturnDiff)][ToolOption(ToolOptionTag.ReturnDiff)] bool returnDiff = false,
    CancellationToken cancellationToken = default)
```

- `oldContent` — verbatim text to find and replace, matched exactly (same semantics as
  `ContextHelper`'s `contextSnippet`: literal substring match first, falling back to
  whitespace-normalized matching — see `ContextHelper.cs:39-103`).
- `newContent` — verbatim replacement text. May be empty (pure deletion). May be longer than
  `oldContent` (net insertion) as long as the total edit stays under the size cap (below) —
  covers small "insert a new line/statement adjacent to an anchor" cases without needing diff
  syntax.
- `lineBefore` / `lineAfter` — optional disambiguation, passed straight through to
  `ContextHelper.FindSnippetPosition`, exactly as it already works for other tools' callers. Not
  new mechanism, just newly wired into this tool.
- No more `@@` headers, no more `+`/`-`/` ` line prefixes, no more per-hunk line-count metadata to
  get wrong. Multi-location edits in one call are explicitly out of scope (see Open questions) —
  callers make one call per location, same as `RenameSymbol`-adjacent tools already expect for
  scoped edits.

### Size cap

Hard reject (not a warning) before any anchoring is attempted, when either:
- `oldContent` line count > 20, or
- `oldContent` character count > 200, or
- `newContent` character count > 200 (independently — covers pure-insertion calls where
  `oldContent` is a short/empty anchor but `newContent` smuggles in a large block)

Rejection is a `ToolErrorCode.InvalidArgument` result (not an exception — consistent with the
existing `filepath`/`oldContent`/`newContent`-required checks in this same method), with a message
naming the better-fit tool per situation:

> "ReplaceSnippet: oldContent/newContent exceeds the size limit for a small localized edit
> (max 20 lines / 200 chars each). For a whole-file rewrite, use WriteFile(operation=ReplaceFile).
> For a structural change (rename, signature, extract), use the matching Roslyn tool
> (RenameSymbol, ChangeSignature, ExtractMethodSafe, Member, etc.). For multiple small edits in
> the same file, call ReplaceSnippet once per edit."

Exact thresholds (20 lines / 200 chars) are the user's own framing of intended scope; both are
configurable constants (`MaxOldContentLines`, `MaxContentChars`) rather than hardcoded literals, so
they can be tuned from eval data without a signature change.

### Anchoring and ambiguity

In `ReplaceSnippet`, replace `DiffEngine.ApplyDiff(oldText, unifiedDiff)` with a direct call through
`ContextHelper.FindSnippetPosition(sourceText, oldContent, lineBefore, lineAfter)`, then splice
`newContent` in at `[pos, pos + oldContent.Length)`. This is a straight reuse of existing,
already-tested logic (`RoslynSentinel.Tests\ContextHelperTests.cs`) — no new anchoring algorithm.

Ambiguity handling comes for free from `ContextHelper` (`ContextHelper.cs:211-217`): a
`ToolAmbiguousMatchException` is thrown with the match count and a prompt to supply
`lineBefore`/`lineAfter`, which the tool method catches and maps to a `ResultError` the same way
`ToolNotFoundException` is presumably already mapped elsewhere (check `ToolErrorMapper` — every
other `ContextHelper` caller already relies on this mapping existing). This directly fixes the
anchor-collision bug: instead of silently matching the wrong of two near-identical methods, the
call now fails loudly with "2 matches found, provide lineBefore/lineAfter" and the model can
retry precisely, the same recovery path it already used successfully elsewhere in the 17:49 log
when `ApplyUnifiedDiff` rejected a genuinely bad hunk with a clear message.

### What gets deleted / moved / left alone

- `DiffEngine.ApplyDiff` (unified-diff-hunk parsing, `ReanchorHunk`, `DiffHunkAnalyzer`) is
  unchanged in logic, except for the response-surfacing addition in Resolved Decision 3. It's used
  by both `ApplyDiff` and (post-move) `ApplyUnifiedDiff`, both now living in
  `SentinelWholeFileWriteTools.cs`, gated off the default surface per
  `project_wholefilewrite_gating_overnight_result_2026_09_08` and kept intentionally for
  reactivation, not deleted.
- `ApplyUnifiedDiff`'s method body moves verbatim from `SentinelWorkspaceTools.cs` to
  `SentinelWholeFileWriteTools.cs` — no logic change, just relocation (see Resolved Decision 1 for
  dependency-satisfaction check).
- `ToolOptionTag.UnifiedDiff` / `ToolParams` entries used by `ApplyUnifiedDiff` stay — the tool
  isn't deleted, only relocated, so its parameters remain live. New `ToolOptionTag`/`ToolParams`
  entries are needed for `ReplaceSnippet`'s `oldContent`/`newContent`.

## Resolved decisions

1. **Two coexisting tools, not a rename.** `ApplyUnifiedDiff` (today's diff-hunk-syntax tool,
   unchanged) moves as-is from `SentinelWorkspaceTools.cs` into `SentinelWholeFileWriteTools.cs`,
   joining its sibling `ApplyDiff` off the default MCP surface (that class carries no
   `[McpServerToolType]` attribute, which is the gating mechanism — see
   `project_wholefilewrite_gating_overnight_result_2026_09_08`). It is kept, not deleted, for
   reactivation if eval data later shows a genuine need for true multi-hunk diff-syntax edits.
   The new tool takes the `ApplyUnifiedDiff` slot on the default surface under the name
   `ReplaceSnippet` in `SentinelWorkspaceTools.cs`. This also resolves the naming-confusion risk
   raised earlier (a model inferring diff-hunk-syntax input from the name "unified diff"): that
   name only exists on the gated, opt-in surface now, so the risk is confined to callers who
   deliberately re-enabled it. `ReplaceSnippet` carries no such competing prior.
   Migration mechanics: move the `[McpServerTool(Name = "ApplyUnifiedDiff")]` method body verbatim
   into `SentinelWholeFileWriteTools.cs` (constructor already has `_diffEngine`, `_workspaceManager`,
   `_validationEngine` — check `_symbolNavigationEngine`/`CompilerErrorLookupHelper` usage in the
   `apply` branch is satisfied by that class's existing fields), then add the new `ReplaceSnippet`
   method in `SentinelWorkspaceTools.cs` per the signature below.
2. **Single edit location per call — confirmed.** No `oldContent[]`/`newContent[]` arrays for now;
   revisit only if eval data shows models frequently want to batch trivial same-file edits and
   repeated calls prove to be a real cost (extra round-trips, not correctness).
3. **`DiffHunkAnalyzer` observability gap — confirmed, to be fixed.** Surface
   `HeaderCountsMatchBody: false` (and any other `DiffHunkAnalyzer` finding) in `ApplyDiff`'s and
   `ApplyUnifiedDiff`'s response even on a successful apply, not just via server-side log
   (`DiffEngine.cs:58-62` currently only logs a warning). Add the report (or a summarized form of
   it) to the success `ToolResult<object>.Data` payload so the calling model sees it directly
   instead of it being invisible outside server logs. This applies to both gated diff-hunk tools
   post-move and is independent of `ReplaceSnippet`, which has no header to mismatch in the first
   place.

## Reference

- Eval logs: `lmstudio_logs/plan-eval-defect-remediation-v2 - 2026-09-08 17.49.md`,
  `... 17.58.md`
- Related findings: `docs/current/blockers/finding_applyunifieddiff_anchor_collision_duplicate_trivia.md`,
  `docs/current/project_diffengine_trailing_blank_anchor_fix.md`
- Existing anchoring mechanism being reused: `RoslynSentinel.Common\ContextHelper.cs` (`FindSnippetPosition`, `FindAllSnippetMatches`)
- Current tool implementation: `RoslynSentinel.Server.Basic\SentinelWorkspaceTools.cs:539-660`
