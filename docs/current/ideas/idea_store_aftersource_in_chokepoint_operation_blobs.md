# Idea: Store AfterSource in chokepoint operation blobs

**Status:** NOT DESIGNED 2026-11-09. Owner decision during the `plan_stale_read_guard_followups` review: record the idea, do not schedule it; unimplemented.

## What happened

While reviewing Step 9h of [plan_stale_read_guard_followups.md](../plans/closed/plan_stale_read_guard_followups.md)
(the "Added, not loaded yet" zero-hit `Search` hint), the question came up whether the hint could read
the post-write text from the operation blob instead of the disk. It cannot: the blobs written by the
write chokepoint hold only the text from before the write.

Verified against the code on 2026-11-09:

- `OperationItemRecord.AfterSource` exists (`RoslynSentinel.Common/OperationItemRecord.cs:23`), but
  nothing reads it. A text search for `AfterSource` finds exactly four hits: the declaration and three
  setters in `RoslynSentinel.Tools.Advanced/AsyncifyTools.cs` at :1870 (`UpliftCallersCore`), :2899
  (`RunBridgePhaseAsync`) and :3022 (`RunUpliftPhaseAsync`), each `AfterSource = s.AttemptedSource`.
- The two shared chokepoint blob builders set only `BeforeSource`:
  - `OperationBlobHelper.WriteBlobForApplyAsync` (`RoslynSentinel.Common/OperationBlobHelper.cs:36-46`)
    builds one item per `result.SucceededFiles` entry and fills `BeforeSource` from `result.PreImages`
    (:39, :44).
  - `OperationBlobWriter.WriteApplyBlobAsync` (`RoslynSentinel.Common/OperationBlobWriter.cs:228-233`)
    does the same; it is reached from `ValidateAndApplyHelper.cs:214`.
- `ApplyChangesResult` carries `PreImages` (`ApplyChangesResult.cs:8`, :31) but no map of what was written.
- The text actually written exists at exactly one point: the write loop in
  `PersistentWorkspaceManager.ApplyProposedChangesAsync`. The EOL pre-pass
  (`ComputeEolPrePass`, call at `PersistentWorkspaceManager.cs:2098`) produces `contentsToWrite`, and the
  loop takes `contentsToWrite[filePath]` as `contentToWrite` (~:2155-2190) and writes that. It is
  not the same as the proposed `newContent` when the pre-pass changed line endings, so a caller cannot
  reconstruct it from its own inputs.
- Other Asyncify / `AsyncBatchEngine` paths build their own `OperationItemRecord` lists and set
  `BeforeSource` themselves (for example `AsyncBatchEngine.cs:581`, :636, :905). The brief counts about
  30 such sites across `AsyncifyTools` and `AsyncBatchEngine`; a text search for `BeforeSource =|PreImages`
  returns 26 matches in `AsyncifyTools.cs` and 10 in `AsyncBatchEngine.cs`, which includes reads and
  non-assignments, so treat 30 as an upper-bound estimate, not a counted number.

## The actual gap

An operation blob records where a file started but not where the server left it. Two consequences:

1. **Undo is blind to later edits.** `UndoLastApply` restores `BeforeSource`. If the file was changed
   again after the recorded operation (by this agent, another session, or a human), the undo silently
   overwrites that later work. The blob has nothing to compare the current disk text against.
2. **Forensics is half a diff.** To reconstruct what an operation did, a reader has the before text
   and must guess the after text from git history or the disk, neither of which is tied to the
   operation.

## Possible fixes (not decided)

Three slices, each independently shippable (the first two together deliver the value for engine and
anchor tools):

1. **Expose the written text.** Add a map to `ApplyChangesResult` (name open, for example
   `PostImages`) filled in the `ApplyProposedChangesAsync` write loop with `contentToWrite` for each
   file that was actually written. Deleted files get no entry (`AfterSource` stays null). No-op writes
   (loop `continue` on identical content) could record the same text as `BeforeSource` or be omitted;
   open.
2. **Copy it in the two shared builders.** `OperationBlobHelper.WriteBlobForApplyAsync` and
   `OperationBlobWriter.WriteApplyBlobAsync` set `AfterSource` from the new map. This covers every
   tool that goes through the chokepoint (engine tools and anchor tools).
3. **Optional: the self-building sites.** Migrate the Asyncify / `AsyncBatchEngine` sites that build
   their own items. Per `CLAUDE.md`, do it migration-first: leave a `// TODO` at each remaining site.
   Three of them already set `AfterSource` from `AttemptedSource`, which is the attempted text, not
   necessarily the text written after the EOL pre-pass; whether to realign them is part of this slice.

**Cost.** The blob roughly doubles in size (whole-file text stored twice per item). Blobs are written
on every successful mutating call, so this is the main trade-off. It touches `Common` only for slices
1-2 (`ApplyChangesResult`, `PersistentWorkspaceManager`, the two builders) and risks changing every
test that asserts on blob contents or size.

**Value.**

- `UndoLastApply` could compare the current disk text to `AfterSource` and refuse, or warn, when a later
  edit has landed in between.
- Exact per-operation before/after forensics without consulting git.
- Input for [proposal_cross_session_write_journal.md](../proposals/proposal_cross_session_write_journal.md):
  a journal that wants to say what each session wrote can use the stored after text instead of
  re-deriving it.

**Not needed for Step 9h.** The Step 9h hint reads the named file from disk instead. That is more
correct for the retry case it serves: `AfterSource` can be outdated by a later edit, whereas the
disk is the text the retry will actually search. So this idea does not block that step and should not
be pulled into it.

## Open questions

- **Blob retention and size limits.** Is there a per-blob or per-session cap today, and does doubling
  the size need one? Not investigated; check `OperationBlobWriter` retention before scheduling.
- **Refuse or warn on undo over a later edit?** Refusing is safe but strands a legitimate undo; warning
  keeps the old behaviour with a visible flag. A force parameter on `UndoLastApply` is the usual
  compromise but would be a new parameter to maintain. Undecided.
- **Which text counts as "unchanged"?** Comparing disk to `AfterSource` byte-for-byte will trip on
  EOL or BOM differences introduced outside the server; the comparison rule needs defining
  (probably the same normalization as the EOL pre-pass).
- **No-op writes and deletes** (see slice 1).

## Related

- [plan_stale_read_guard_followups.md](../plans/closed/plan_stale_read_guard_followups.md) - Step 9h, where
  this was decided against as the data source.
- [proposal_cross_session_write_journal.md](../proposals/proposal_cross_session_write_journal.md) -
  would consume the stored after text.
