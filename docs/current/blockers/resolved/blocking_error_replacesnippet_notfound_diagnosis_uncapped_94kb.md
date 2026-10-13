# `ReplaceSnippet` NotFound diagnosis is uncapped (94 KB) and strands a small model on identical-branch edits

**Status:** RESOLVED 2026-10-13 (see "Resolution"). Was OPEN 2026-10-12. Both causes are traced to source. The turn-3 NotFound was a false negative: `lineAfter` is checked against the line after the match's *first* line (`ContextHelper.FilterByAdjacentLinesPredicate`), so it filtered out both exact matches. The 94 KB comes from `ContextHelper.FormatAllMatchedDiagnosis`, which has no cap. Both are latent, not regressions (see "Root cause"). Proposed replacement selectors: `docs/current/proposals/proposal_snippet_match_selectors_withinmember_matchline.md`.

## What was being attempted

Slice B of `plan_error_codes_follow_cause.md`, run through the `SubAgent` tool (Qwen3.5 9B on LM Studio, run `20261012-085159-072-f711c4a4`, `disposeWorktree: false`). Four relabels in `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` of the form `Outcome = EditOutcome.CannotEdit` -> `EditOutcome.SourceInvalid`. Two sites (`AddMemberAsync`, `AddTopLevelTypeAsync`) were unique and succeeded on turn 2. The other two (`InsertMemberAfterAsync` ~2458, `InsertMemberBeforeAsync` ~2560) are textually identical 10-line branches, also identical to a third branch in `AddAttributeAsync` that must not change.

The run ended `stopReason: RepeatedToolFailure` after 19 turns with two of four engine edits made. The remaining three edits (two branches and one test rename) were completed by an `implementer` agent.

## The exact symptom

Turn 2, `ReplaceSnippet(action: apply)` with the 10-line branch as `oldContent` (the tool did the right thing):

```
errorCode: Ambiguous
ReplaceSnippet apply for '...MemberRefactoringEngine.cs' failed: oldContent is ambiguous (2 matches): "var newMember = ..."
Provide lineBefore and/or lineAfter using one of the exact values below (copy verbatim, do not retype from memory) to select the intended match:
Match 1 (line 2458): lineBefore="" lineAfter="if (newMember == null)"
Match 2 (line 2560): lineBefore="" lineAfter="if (newMember == null)"
Matches 1 and 2 have identical surrounding lines and cannot be disambiguated by lineBefore/lineAfter alone; use a different tool or a longer oldContent.
```

Turn 3, the model followed that advice ("a longer oldContent": it appended the blank line and the next line `var trailingIssue = DetectMultipleMemberDeclarations(newMemberSource, "addMember")`). It also passed `lineAfter` set to that same `var trailingIssue ...` line (`lineBefore` empty). Both calls (After and Before) came back as:

```
"offloaded":true,"sizeBytes":94408,"isError":true,
"message":"Result is 94408 bytes (threshold: 15360). Use GetLargeResult(resultId: \"f1dbd2a1020a447ea2a7572285cc1934\") to page through results."
```

The model's `GetLargeResult` page (turn 4) shows the real content, `errorCode: NotFound`, totalChars 103668:

```
An exact match could not be located for the provided oldContent (verbatim required): "var newMember = ... "addMember")". Re-read the file and copy oldContent exactly ...
7 of 7 lines in contextSnippet were located individually in the file, but not in the arrangement you supplied. Each is a candidate to copy back verbatim:
snippet line 1 was located at source line 2458: "var newMember = SyntaxFactory.ParseMemberDeclaration(newMemberSource);"
snippet line 1 was located at source line 2560: ...
snippet line 4 was located at source line 192: "return new DocumentEditResult"
snippet line 4 was located at source line 205: "return new DocumentEditResult"
... (one line per hit, across the whole file)
```

Turns 5-19: the model re-read the code, retried `ReplaceSnippet` (turn 13: `oldContent is 2598 chars excluding leading indentation (limit 2000)`; turn 15: another NotFound), then switched to `Member(operation: replace)` with a body fragment and repeated the identical rejected call four times (turns 12, 17-19: `newMemberSource is not a valid member declaration`).

## Source trace

- `RoslynSentinel.Common/ContextHelper.cs:439-457`, `FormatAllMatchedDiagnosis`: emits one `snippet line N was located at source line M` entry for every `(snippet line, source line)` hit, ordered, with no cap on entries and no cap on total length. Boilerplate snippet lines (`return new DocumentEditResult`, `{`, `};`, `FilePath = filePath,`) match dozens to hundreds of lines in a 5,000-line file, which produces the 103,668-character message. Observed: the same 94,408 bytes for both the After and Before calls, consistent with a deterministic per-line dump.
- The 15,360-byte offload threshold then converts the whole error into `offloaded: true` with only a `resultId`. The model sees no error code, no hint and no first line of the message. The actionable text ("copy oldContent exactly") is on page 1 of a result the model has to page for.
- The Ambiguous message already gives line numbers, but says only "use a different tool or a longer oldContent". A longer `oldContent` (which the model tried) is the path that exploded. The "different tool" is not named.
- `ReplaceSnippet` has no parameter that selects a match by enclosing member or by occurrence (`lineBefore`/`lineAfter` are the only selectors, and they are blank-line-sensitive here).

## Root cause

### 1. `lineAfter` is anchored to the match's first line, not its last (false-negative NotFound)

- Transcript (`C:\Users\Administrator\source\repos\RoslynSentinel-TestRuns\SubAgent\20261012-085159-072-f711c4a4\subagent\Logs\transcript.json`), turn 3:
  - `oldContent` is 446 chars, 12 lines, LF-only. Lines 1-10 are byte-identical to the turn-2 call. Line 11 is truly empty, and line 12 is the `var trailingIssue` line at 8 spaces.
  - Compared against `git show 56342ce4~1:RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs` (LF-only), every line matches in indentation, trailing whitespace, terminator and blank separator.
  - Run through the engine's `\r?\n` regex, the text matches exactly twice, at the After and Before sites (source lines 2458-2469 and 2560-2571).
- `RoslynSentinel.Common/ContextHelper.cs:717-749`, `FilterByAdjacentLinesPredicate`:
  - It takes `lineIndex = sourceText.Lines.GetLinePosition(match.Start).Line` (`:721`), then checks `lineAfter` against `sourceText.Lines[lineIndex + 1]` (`:733`).
  - For a multi-line `oldContent`, that line is the snippet's own second line (`if (newMember == null)`), never the line after the snippet. Both exact matches were filtered out.
  - `FindAllExactSnippetMatches` then returned empty and `FindExactSnippetPosition` threw NoMatch. `DiagnoseNoMatch` then reported "located individually, but not in the arrangement you supplied", which is false here: the arrangement was correct.
- The same defect makes the turn-2 Ambiguous message misleading.
  - It reported `lineAfter="if (newMember == null)"` for both matches. That is snippet line 2, not the line after the match.
  - So two textually identical multi-line matches always look like they have "identical surrounding lines", whatever surrounds them.
- Turn 15 is the same misuse: a 2-line `oldContent` with `lineBefore`/`lineAfter` set to lines inside the snippet.

### 2. `FormatAllMatchedDiagnosis` has no cap (94 KB)

- `ContextHelper.cs:439-457` emits one entry per `(snippet line, source line)` hit, with no `Take`, no per-line limit and no total-length limit.
- `ContextErrorBuilder.BuildNoMatch` (`ContextErrorBuilder.cs:42-58`) appends it unchanged.
- `DiagnoseNoMatch` (`ContextHelper.cs:629-647`) tests each trimmed line on its own (`Contains`, then whitespace-collapsed), drops lines under 4 chars and counts each snippet line once (`Distinct`). "N of N located" therefore says nothing about order or contiguity.
- The nearest-match suggestion (`LineSimilarityScore`, `:423-433`) feeds only the partial and zero paths.
  - The all-matched path has no closest-contiguous-window search.
  - That is the case where a "copy this verbatim" candidate would help most.

### Regression or latent?

Both are latent. Neither appears in `CLOSED.md` or `blockers/resolved/`.

- The `lineAfter` anchor arrived in `38a867568931c047605d5eb67769979b4ca6302a` (2026-05-02, "feat: enhance contextSnippet disambiguation with lineBefore/lineAfter ...") and has measured from the match-start line ever since.
  - `plan_linebefore_lineafter_single_line_contract.md` describes it as "immediately after contextSnippet". That holds only for single-line snippets.
- The all-matched dump arrived in `b48971fe62d645d568b08d1ffa287b941d5d9aa1` (2026-09-15, "Fix contextSnippet "not found" wording and add orienting guidance on no-match").
  - No cap was ever present. Before that commit, NotFound was a bare "not found" message.
- The short 2-4-candidate output seen in other runs comes from `DescribeAmbiguousCandidates` (`ContextHelper.cs:655`) and `FormatPartialMatchDiagnosis`. Both are bounded and still reachable. Neither covers the all-lines-located case.

## What is and is not confirmed

Confirmed (transcript turns 2-4 and 15, source above, git history):
- the Ambiguous message text;
- the turn-3 `oldContent` and `lineAfter` values and their exact match against the pre-change file;
- the first-line anchoring in `FilterByAdjacentLinesPredicate`;
- the 94,408-byte NotFound result;
- the per-hit dump format and its lack of a cap;
- the repeated-identical `Member` call on turns 12 and 17-19.

Not checked: the offload layer that applies the 15,360-byte threshold.

The `output` field returned by `SubAgent` was empty for this run too (separate known defect, see TODO).

## Why this matters

A multi-site edit where several branches are textually identical is routine in this codebase (dozens of `return new DocumentEditResult { ... }` blocks). The environment told a small model to "use a longer oldContent", and when it did, returned an error too large to read. The only recovery was a different tool (a line-anchored diff) that the error never named. This is a tool-surface gap, not a model fault. It also wastes the strongest recovery signal: the Ambiguous message already knows the two line numbers.

## Suggested direction (not implemented)

0. **Fix or retire the adjacent-line filter.**
   - The minimal fix is to measure `lineBefore` from the match's first line and `lineAfter` from its *last* line. `DescribeAmbiguousCandidates` must report the same lines.
   - When the filter removes every exact match, report "oldContent matched N times; none had lineBefore/lineAfter = ..." instead of falling through to the no-match diagnosis.
   - The larger direction, replacing `lineBefore`/`lineAfter` on `ReplaceSnippet` with selectors copied from the Ambiguous message (`withinMember`, `matchLine`), is in `docs/current/proposals/proposal_snippet_match_selectors_withinmember_matchline.md`.
   - This item alone would have made the turn-3 call succeed.
1. **Cap `FormatAllMatchedDiagnosis`** (`ContextHelper.cs:439`): list at most ~5 hits per snippet line and ~2 KB in total, drop lines that match more than N source lines (boilerplate), and keep the summary ("7 of 7 lines located individually") plus the actionable instruction first. Apply the same cap review to `FormatPartialMatchDiagnosis` and `FormatZeroMatchedDiagnosis`. The error must stay under the 15,360-byte offload threshold so the model sees `errorCode` and the hint inline.

Follow-up items (separate slices, in priority order):

2. **Anchor by enclosing member or occurrence.** Add `withinMember: "InsertMemberAfterAsync"` (and/or `occurrence: 2`) to `ReplaceSnippet`, so a textually identical branch can be selected without lengthening `oldContent`. The Ambiguous error should list the enclosing member per match ("Match 1 (line 2458, in InsertMemberAfterAsync)") so the model can copy the value.
3. **Name the working fallback in the Ambiguous message.** Replace "use a different tool or a longer oldContent" with the concrete options: `withinMember` (once item 2 exists), or `ApplyUnifiedDiff` with a hunk anchored on the reported line numbers. Do not name a gated tool without stating how to enable it (see memory `feedback_dont_name_gated_tools_in_agent_docs`); `ApplyUnifiedDiff` is in the on-demand `moveExtract` toolset.
4. **Make the `Member(operation: replace)` rejection point at the right tool.** When `newMemberSource` fails to parse as a member declaration and looks like a statement/body fragment, say that `Member(replace)` replaces a whole member and suggest `ReplaceSnippet` (with `withinMember` once available) for an edit inside a body.
5. **Break the SubAgent loop on repeated identical calls sooner.** `RepeatedToolFailure` fired only after four identical `Member` calls (turns 12, 17-19). Detecting an identical (tool, arguments) failure on the second occurrence, and returning a message to the model that says "this exact call already failed; change the approach", would save turns for weak models. Also relevant: the empty `SubAgent` `output` field on a non-converged run should carry the last `ReasoningContent`.

## Resolution

Fixed across: b66e0d49 (lineAfter checked against the match's last line), 43f8de55 (distinct "filtered out all of them" error), 100fb9a5 (regression tests), 16f5f179 (LineBlockAligner shared with DiffEngine), b97400ab + 89171ed6 (FormatAllMatchedDiagnosis capped: boilerplate threshold 8 hits, 5 hits per line, 2000 chars; tests), and the closest-contiguous-window hint (DiagnoseNoMatch / FormatClosestWindowHint, commit of this note). Not done: proposal phases 3-4 (withinMember/matchLine selectors, deprecation).
