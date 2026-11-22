# ReplaceRange: replace or delete a block of lines identified by its first and last line text

**Status:** APPROVED 2026-11-21. The owner settled placement (claude-lean core plus the eval lean list), the shared apply helper, the result type and the eval scope in a todo review; nothing is built.

## Motivation

Raised 2026-11-09 as a TOOL-GAP. There is no tool that replaces or deletes a large contiguous block
without re-sending the block's text:

- **`ReplaceSnippet` re-sends the old text, and is size-capped.** Each edit carries the whole `oldContent`,
  capped at 100 lines and 2000 characters for old and new content alike, 20 edits per batch
  (`ReplaceSnippetOptions.cs:14-19`; enforced by `DescribeExceededSnippetSizeBounds`,
  `WorkspaceFileEditImpl.cs:1024`). Evidence from the TODO entry (figures are from the brief and the
  2026-11-09 TODO, not re-measured): removing a ~275-line commented-out block (`ApplyDiffWithConfirmationCode`
  in `WorkspaceTools.cs`) took one `ReplaceSnippet` batch of 7 verbatim chunks, about 14 KB re-sent. That is
  consistent with the cap: 7 chunks of at most 2000 characters is 14 KB. The block no longer exists in
  `WorkspaceTools.cs` (a text search finds only comments and tests that mention the name), so the run cannot
  be replayed; the cap arithmetic can.
- **`Member(replace)` keeps the leading trivia** of the member it replaces, so a commented-out block that
  sits above a member survives the replace. **`Member(remove)` refuses a method that has callers**, which
  is the case when removing the code that a block of comments used to be.
- **Verbatim re-sending is the failure mode for weak models** (CLAUDE.md mission; the owner notes weak
  models already struggle with `ReplaceSnippet`'s many optional parameters: `oldContent`, `lineBefore`,
  `lineAfter`, `withinMember`, `matchLine`, `batchEdits`, `validateOnApply`, `returnDiff`, see
  `ReplaceSnippet` at `WorkspaceFileEditImpl.cs:1333`). Environment fix: a tool whose only text input about
  the old code is two single lines, so a transcription error in a 275-line block cannot occur.

Owner decision (2026-11-09): a NEW tool, not more optional parameters on `ReplaceSnippet`. It supersedes
the parked `EditLines` idea (`ideas/idea_editlines_and_anchor_attributes.md`; dropped 2026-11-10 per
`CLOSED.md`, the idea file itself still sits in `ideas/` rather than `ideas/closed/`).

## Proposal

### Shape (decided)

```
ReplaceRange(reason, filePath, firstLineText, lastLineText, newContent)
```

- All parameters mandatory (`reason` is the standard `ToolCallReason`). `newContent` may be the empty
  string; an empty `newContent` deletes the range.
- `firstLineText` and `lastLineText` are the verbatim text of the first and last line of the range, not line
  numbers. The names are chosen so a model does not pass an integer (a numeric line is stale after any
  earlier edit; text re-anchors).
- Each anchor must match **exactly one** line in the file. A match count other than one is refused with
  facts (the count and the line numbers of every match). That is what makes the tool stale-line-number safe.
- The range is inclusive of both anchor lines and is replaced by `newContent`.
- Same chokepoint, compile-before-write and undo as `ReplaceSnippet`. No size cap.
- Result: the existing `ReplaceSnippetResult(ReplacementResult, ValidationReport, DiffContent, ChangeId)`
  (`ReplaceSnippetResult.cs:11`). The summary states the matched line numbers and the number of lines
  replaced. No new DTO.

### Placement (decided)

A **claude-lean CORE tool beside `ReplaceSnippet`**, reversing the 2026-11-09 sketch that put it on demand.
It is also added now to the eval lean list `AgentToolAllowLists.Lean`
(`AgentToolAllowLists.cs:11-15`; the XML summary at `:9` says "The lean 18-tool surface" and must become 19).
The owner expects it to be used more than `ReplaceSnippet` because it does not need the whole `oldContent`.

Name-keyed registries a new tool must join (verified to exist, whether each is mandatory is a hypothesis the
plan must settle by reading the failing test or the consumer): `ToolClassRegistry.cs:53`
(`ClaudeLeanToolNames`), `ErrorPolicyIntentTable.cs:132` (`ReplaceSnippet` is `Dispatch("action", ...)`;
ReplaceRange has no `action`, so a fixed entry like `UndoLastApply`'s `Fixed(E)` at `:152` is the likely
shape), `AgentIdentityCapture.cs:32` (`MissingTermArgument`: `ReplaceSnippet` maps to `"oldContent"`),
`ToolArgumentValidator.cs:711,744` (parameter and enum aliases), `EditImpactAnalyzer.cs:26` (`NamedTools`).
Per CLAUDE.md a tool needs the `[McpServerToolType]` class entry plus the `ToolClassRegistry` entry plus the DI
block; `ReplaceSnippet`'s own class is `WorkspaceTools` (`WorkspaceTools.cs:144`), so adding the tool to it
needs no new class, but the server needs a restart.

### Apply tail extraction (decided)

`ReplaceSnippet`'s apply tail is extracted into a shared helper. The tail is `WorkspaceFileEditImpl.cs`
**1581-1663** (the brief said "~1560-1680"; the `validate` branch is `:1563-1579`, and the
real apply tail ends at the return at `:1658-1663`). ReplaceRange computes the new file text from the anchors
and hands a `DocumentChangeSet` (`:1551-1554` shows how `ReplaceSnippet` builds one) to the helper.

What the tail does today, so the extraction is scoped honestly (the owner's list named four pieces; there
are more):

1. `ApplyProposedChangesAsync(..., validateChanges: validateOnApply, staleReadPolicy: RefuseIfObservedStale)`
   (`:1581`).
2. `WriteRefusal.TryMap(...)` (`:1584`).
3. `NearMissAutoCorrector.TryAutoCorrectAsync` (`:1595`) - a rejected edit corrected and re-applied.
4. `TryLiftRenameAsync` (`:1601`) - the experimental rename lift, documented in the code as off by default.
5. The rejected-edit and applied-edit memories (`_rejectedEdits.IsRepeat/Remember` `:1621-1622`,
   `_appliedEdits.Remember` `:1649`) and `ForgetFileMemories` (`:1637-1644`). All are keyed by
   `(path, oldContent, newContent)`; ReplaceRange has no `oldContent`.
6. `ComposeCompileRejectionAsync` (`:1308`, called at `:1626`).
7. `OperationBlobHelper.WriteBlobForApplyAsync("replace_snippet", ...)` (`:1650`), the summary, the
   `UndoLastApply` hint (`:1654`) and the optional diff (`BuildDiffFromPreImages`).

Which of 3-5 the helper takes as options and which ReplaceRange simply does not use is open (Open question
1). `ReplaceSnippetBatch` carries a near copy of the same tail (`:1943-2011`); extracting for it too is out of
this proposal's scope but the helper's shape should not preclude it.

### Name of the undo hint

The success summary names the undo tool. `proposal_undo_change_last_only_validated.md` renames
`UndoLastApply` to `UndoChange`; ReplaceRange's summary must use whichever name is live when it lands, and
the hint is generated through `ToolCallHint.Call(...)` like the other sites so it moves with the rename.

## Alternatives considered, not pursued

- **More optional parameters on `ReplaceSnippet` (for example `startLine`/`endLine`, or a
  `deleteBetween` mode).** Lost (owner, 2026-11-09): `ReplaceSnippet` already has eight optional parameters
  and weak models struggle with it; a new parameter that changes what `oldContent` means makes that worse.
- **`EditLines`** (search/replace by line, `ideas/idea_editlines_and_anchor_attributes.md`). Parked on
  2026-08-31 and dropped 2026-11-10 in favour of this proposal; it never specified the exactly-once anchor
  rule.
- **Line numbers as the anchors (`startLine`, `endLine` integers).** Lost: stale after any earlier edit in
  the same turn or by another agent; the text anchors fail loudly instead of cutting the wrong block. The
  parameter names `firstLineText`/`lastLineText` also stop a model passing an int.
- **`Member(remove)` / `Member(replace)` for the block.** Lost: `replace` keeps the leading trivia that holds
  the commented-out block; `remove` refuses a method with callers. Neither can take out text that is not a
  member.
- **A dedicated "delete block" tool plus a "replace block" tool.** Lost: empty `newContent` deleting is one
  rule and one tool; two tools doubles the schema in the lean budget.
- **On-demand placement (the 2026-11-09 sketch).** Reversed 2026-11-21 (owner): the tool is expected to be
  used more than `ReplaceSnippet`, so a hidden tool is a tool nobody finds (CLAUDE.md: a gated tool a model
  cannot see is a gap in the tool surface).
- **Narrow A/B against `ReplaceSnippet`.** Lost (owner): ReplaceRange joins a general edit-tool comparison
  instead.

## Decisions

Dated 2026-11-21 (owner, todo review).

| Question | Choice | Why |
| --- | --- | --- |
| Where does ReplaceRange live? | A claude-lean CORE tool beside `ReplaceSnippet`, and also on the eval lean list (`AgentToolAllowLists.cs`) now. Accepted cost: lean-eval baselines before and after are not comparable; the claude-lean schema budget (raised to 46,500 chars in d2dcdc33) will likely need a bump. | The owner expects it to be used more than `ReplaceSnippet` because it does not need the whole `oldContent`. This reverses the 2026-11-09 on-demand sketch. |
| How does it reach the write chokepoint? | Extract `ReplaceSnippet`'s apply tail (`WorkspaceFileEditImpl.cs` ~1560-1680: `ApplyProposedChangesAsync` with `RefuseIfObservedStale`, `WriteRefusal.TryMap`, `ComposeCompileRejectionAsync`, `OperationBlobHelper.WriteBlobForApplyAsync`) into a shared helper; ReplaceRange computes the new text from the anchors and passes a `DocumentChangeSet`. | One compile-before-write and undo path, not two that drift; a copy would also duplicate the stale-read ratchet entry. |
| What does it return? | Reuse `ReplaceSnippetResult(summary, validation, diff, changeId)`; the summary states the matched line numbers and lines replaced. No new DTO. | The result already carries what the undo hint and diff need; a new DTO adds schema and a second shape for callers to learn. |
| How is it evaluated? | No narrow A/B. ReplaceRange goes into a general edit-tool comparison (separate, not designed, Large). | A two-tool comparison measures only this pair; the owner wants the whole edit-tool set compared on the same tasks (TODO.md carries that item). |

Acceptance (unit tests unless stated):

- An inclusive range whose two anchors each match once is replaced; with empty `newContent` it is deleted.
- An anchor matching zero lines or several lines is refused with facts (the counts and every matching line
  number); nothing is written.
- A range whose replacement breaks the compile is rejected before any write; the file on disk is unchanged.
- A real apply returns a `changeId` that the undo tool reverts (the file text equals its pre-apply text).
- All existing `ReplaceSnippet` tests stay green after the tail extraction.
- The schema budget test (`ClaudeLeanSchemaBudgetTests.cs`, `AdvancedTotalBudget` and `BasicTotalBudget`,
  both 46500 at `:39` and `:41`) is updated for the new tool's schema, with the raise noted in the
  constant's comment block as the file's convention requires.

Coverage note (planner responsibility): the extraction changes the path every `ReplaceSnippet` apply takes,
and the `ReplaceSnippet` tests are the only protection for it. They cover the tail only as far as each test
reaches (rejected edits, near-miss correction, duplicate detection, lift); the plan should list which
`ReplaceSnippet` tests exercise each of tail steps 1-7 above and add a test for any step none reaches, rather
than treat "ReplaceSnippet tests stay green" as proof the extraction is behaviour-preserving. The reviewer
should confirm that mapping. The new tests additionally need: a stale-read refusal (the ratchet,
`StaleReadPolicyRatchetTests.cs`, keys by method name, so the new method needs an entry or a policy), an
inverted range (last anchor above the first) and a one-line range (both anchors equal, one match).

## Open questions

1. **Which tail steps does ReplaceRange take?** Near-miss auto-correction, rename lifting and the
   rejected/applied-edit memories are all tied to `oldContent`/`newContent` text. Recommendation (not
   decided): omit the lift and both memories in v1, since there is no `oldContent` to key them on, and
   decide near-miss correction separately. The compile-rejection text also names `ReplaceSnippet`
   explicitly in its headline (`:1627`), so the helper needs the tool name as a parameter.
2. **Anchor matching strictness.** "Verbatim" is the owner's word. Whether comparison ignores leading and
   trailing whitespace and line endings (as `ReplaceSnippet`'s match tier ignores line endings,
   `ResolveEditMatch`, `:1155`) is undecided. Hypothesis: an exact-whitespace rule will refuse models that
   drop indentation, which is the failure this tool exists to remove; a trimmed comparison trades that for
   more multi-matches on lines such as `}`.
3. **Search scope of `lastLineText`.** The owner's rule is that each anchor matches exactly once in the file.
   A block's last line is often a common line (`}`, `*/`), which would be refused as ambiguous. Whether
   `lastLineText` is searched only after the first anchor is undecided; that would be a change to the stated
   rule and needs the owner.
4. **Does `newContent` get the indentation pass** (`NewContentIndenter.ReindentAsync`, `:1536`) that
   `ReplaceSnippet` applies to every match? And the EOL normalisation (`ResolveEol`/`NormalizeEol`,
   `:1539-1540`)? The chokepoint normalises EOL for changed lines regardless, so EOL is probably covered;
   indentation is a decision.
5. **No `validate` dry run.** `ReplaceSnippet` has `action: apply | validate` and `validateOnApply`
   (`:1335`, `:1344`). The owner's shape has neither, so ReplaceRange always validates and always writes.
   Whether a dry run is wanted is unasked.
6. **Guards `ReplaceSnippet` runs before the apply that ReplaceRange may skip or need**:
   `DescribeNoTypeLeft` (`:1057`, called at `:1541`; refuses an edit that removes the last type in a file; a range delete of a
   whole class body is exactly that), `DescribeEmptyFile` (`:1114`), and the leaked-markup check
   (`:1017`). The first matters most: deleting a 275-line range could empty a file.
7. **Pointing the size-cap refusal at ReplaceRange.** The over-cap error at `:1449-1463` gives advice via
   `WriteToolAdviceHelper.AdviseForOversizedEdit`. With ReplaceRange on the surface that advice could name it
   for block deletes. Not requested; it is the environment change that routes a model that hit the cap to the
   new tool.
8. **The schema budget bump size.** Headroom is about 525 chars (`ClaudeLeanSchemaBudgetTests.cs` comment:
   45975 measured against 46500). A five-parameter tool with a description is likely to exceed that;
   hypothesis, not measured. The new constant is "measured + at least 300, rounded up to 500" by the file's
   own convention.
9. **Lean-eval comparability.** Accepted by the owner; the plan should record the dates of the last lean
   baselines before the list changes so later readers know which numbers straddle the change.

## Cost / risk

- **Touches:** `WorkspaceFileEditImpl` (extraction plus the new method), `WorkspaceTools` (the tool and its
  description), `AgentToolAllowLists`, `ToolClassRegistry`, the name-keyed registries above, the schema
  budget test, and new tests. No new DTO.
- **Could break:** `ReplaceSnippet` itself, because the extraction rewrites its apply path (the largest
  risk; see the coverage note). The claude-lean schema budget test fails until raised. Lean-eval baselines
  before and after the list change are not comparable (accepted).
- **Makes harder:** the lean surface is one tool larger, so every claude-lean session pays the schema
  tokens, and `ReplaceSnippet` versus `ReplaceRange` becomes a choice a model must make; the descriptions
  need to separate "small exact text" from "a block between two lines".
- **No size cap** means a wrong anchor pair can delete a very large range in one call. The mitigations are
  the exactly-once rule, the compile gate and the undo; there is no preview step (Open question 5).
- **Effort:** Medium.

## Related

- `TODO.md` - the TOOL-GAP entry that raised this (2026-11-09) and the general edit-tool comparison entry
  (Large, not designed); both link here.
- `proposals/proposal_undo_change_last_only_validated.md` - the undo tool this result's hint points at.
- `ideas/idea_editlines_and_anchor_attributes.md` - the parked predecessor.
- `findings/finding_tool_surface_usage_review.md` - records ReplaceRange and MoveFile as decided core tools
  and the 46,500-char budget.
- `proposals/proposal_move_file_tool.md` - the other new core tool competing for the same budget.
