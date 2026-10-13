# `ReplaceSnippet` schema invites anchor-only edits, NotFound error buries the culprit, description never points to `Member`

**Status:** OPEN 2026-10-12. Code locations traced to source (below); turn-level evidence is taken from the run-folder analysis supplied with the brief and was not re-walked turn by turn for this doc.

## What was being attempted

A SubAgent run with a qwen 9B model, run folder
`C:\Users\Administrator\source\repos\RoslynSentinel-TestRuns\SubAgent\20261012-234859-620-867b849d\subagent`
(subfolders `Build`, `Logs`, `Worktree`). The run ended `TurnCapExceeded` at 30 turns. It made 12
`ReplaceSnippet` calls, 11 of which failed. This doc covers three environment defects in how
`ReplaceSnippet` communicates; other findings from the same run are out of scope (see end).

## The exact symptom

Server rejection when an edit carries only anchors (turns 11, 13, 26):

```
batchEdits[i] (<file>): oldContent is required.
```

`InvalidArgument` when `filePath` was combined with `batchEdits` (turn 8).

NotFound after a one-character slip in a 25-line `oldContent` (turn 9): the error is ~9 KB, lists ~40
lines of "line N -> source line M" matched output, and only afterwards names line 29 and the closest
source line 677 as the mismatch. The model re-sent the identical block at turn 12; NotFound recurred
at turns 20 and 27.

Turns 16-28: after a size rejection the model announced a switch to `Member` at turns 17, 22 and 27
but only called it at turn 28, having burned the cap.

## Source trace

### 1. Schema invites anchor-only edits

- `RoslynSentinel.Common/ToolParams.cs:45` -- `LineBefore` is described "Line before contextSnippet."
  (and `LineAfter` likewise). `ReplaceSnippet` has no `contextSnippet` parameter; its target is
  `oldContent`. The description points at a nonexistent parameter. (`ContextSnippet`/`LineBefore` are
  shared constants reused by tools that do have `contextSnippet`.)
- The commented-out richer `OldContent` text in the same file already says "If oldContent matches more
  than once in the file, use lineBefore/lineAfter to disambiguate"; the live constant is only "Verbatim
  text to find and replace using literal substring match." so that relationship is not conveyed.
- The batch item declares no required fields, so an edit with only `filePath`/`lineBefore`/`lineAfter`
  is schema-valid.
- `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs:709` -- rejects with
  `batchEdits[{i}] ({edit.FilePath}): oldContent is required.`, which does not say anchors only
  disambiguate `oldContent`.
- `filePath` has no description; the "do not combine with batchEdits" note lives only in the
  `batchEdits` text, so a model reading `filePath` alone has no warning (turn 8).

### 2. NotFound buries the culprit

- `RoslynSentinel.Common/ContextHelper.cs:629` `DiagnoseNoMatch`, formatted by
  `FormatNoMatchDiagnosis` (`ContextHelper.cs:533`); callers at `ContextHelper.cs:210` and `:314`.
  `RoslynSentinel.Common/ContextErrorBuilder.cs:38` `BuildNoMatch` assembles the message.
- Ordering puts the matched-line list before the non-matching line, so the actionable fact is last.
  Hypothesis (not re-measured): the size is dominated by that matched list.

### 3. Description does not point to `Member`

- `RoslynSentinel.Tools.Basic/WorkspaceTools.cs:153` -- description reads "Replaces one exact text
  block with another in a file, for small localized edits. Prefer a structural Rosly..." and does not
  state the size cap (a per-model setting, 2000 chars in this run) nor name `Member`. Only the
  turn-16 rejection text names `Member`.

## What is and is not confirmed

Confirmed against source: the `LineBefore` text and line, the missing `contextSnippet` parameter on
`ReplaceSnippet`, the `oldContent is required.` text and line, the `ReplaceSnippet` description text
start. Not re-verified here: per-turn contents and the ~9 KB / ~40-line figures (from the run
analysis); the rest of the `WorkspaceTools.cs:153` description beyond the quoted prefix.

## Why this matters

A weak model cannot infer from the schema that `oldContent` is mandatory and anchors are secondary, nor
from the error that anchors are not a substitute; three turns were lost to that alone. The NotFound
output hides its one useful sentence in a 9 KB block, so the model cannot tell what to change and
resends the same text. The description withholds the escape hatch (`Member`) until after a failure,
and the model then took 12 turns to act on it. Per the failure doctrine these are all environment
defects: schema, error text and description did not communicate what the tool needs.

## Suggested direction (not implemented)

1. **Schema/error.** Make `oldContent` required in the batch item schema. Reword `LineBefore`/`LineAfter`
   for `ReplaceSnippet` to "Single line adjacent to oldContent; only needed when oldContent matches
   more than once." (needs a `ReplaceSnippet`-specific constant or reword of the shared one, since the
   shared text is correct for `contextSnippet` tools). Add a `filePath` description saying not to
   combine it with `batchEdits`. Reword the error at `WorkspaceFileEditImpl.cs:709` to "oldContent is
   required; lineBefore/lineAfter only disambiguate it."
2. **NotFound.** In `DiagnoseNoMatch`/`BuildNoMatch`, list the non-matching lines first, show the
   character-level difference for the nearest line, and cap the matched list at 3 entries or drop it.
3. **Description.** Append to the `ReplaceSnippet` tool description: "For edits larger than the limit
   or whole-member rewrites use Member(operation: replace)." Consider stating the configured cap.

Alternatives not chosen: none evaluated yet (open). Cost: schema change affects every client's cached
tool schema; error-text changes may break existing tests that assert old wording; the shared
`LineBefore` constant is used by other tools, so do not edit it blindly.

## Acceptance criteria

- Schema test: the generated `batchEdits` item schema lists `oldContent` as required, and `filePath`
  has a description mentioning `batchEdits`.
- Error-text test: an edit with only anchors yields exactly the new "oldContent is required;
  lineBefore/lineAfter only disambiguate it." text.
- NotFound ordering test: for a multi-line `oldContent` with one differing character, the error lists
  the non-matching line and the character-level difference before any matched-line entries, and the
  matched list has at most 3 entries (or is absent); total length is well under the current ~9 KB for
  a 25-line input.
- A description test (or snapshot) asserts the `ReplaceSnippet` description names `Member`.

## Out of scope

Other findings from the same run are tracked separately: the `Member` compile gate behaviour,
contradictions in the brief, `Build` scope / harness restore, and the turn-budget warning.
