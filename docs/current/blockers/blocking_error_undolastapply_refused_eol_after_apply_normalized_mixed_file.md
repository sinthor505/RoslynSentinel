# `TagTestCategories` / `UndoLastApply` apply silently normalized mixed-EOL files, then undo refused the restore as an EOL change

**Status:** OPEN 2026-10-05. Not traced to source: the symptom and its inference from the undo error are confirmed, the code path that rewrote the CRLF lines is a hypothesis (no `.cs` file was read while writing this doc).

## What was being attempted

Dog-food run of the new `TagTestCategories` tool (`Tools.Basic/TestCategoryTaggingImpl.cs` ->
`Engines.Basic/TestCategoryApplyEngine.cs` + `Engines.Basic/TestCategoryTextEditor.cs`; commits
c8cbf599, 7e15069a, fe936467) against project `RoslynSentinel.Tests.Asyncify`, live Advanced stdio server,
2026-10-05.

1. Apply (changeId `6d172f4a`): 5 class-level and 4 method-level `[Category]` lines across
   `ConvertToAsyncBridgeTests.cs`, `FlagMigrationCandidateTests.cs`, `MigrationScanResultTests.cs`,
   `SentinelAsyncifyToolsTests.cs`. Result: status `applied`, failed 0.
2. Check: `git diff --stat` showed 9 insertions, 0 deletions.
3. Revert attempt: `UndoLastApply(changeId: 6d172f4a)`.

## The exact symptom

`UndoLastApply` refused and wrote nothing:

```
ApplyProposedChanges: refused - the change would alter the line endings of 2 existing file(s): '...ConvertToAsyncBridgeTests.cs' (before: LF, after: mixed (CRLF x 2, LF x 559)); '...FlagMigrationCandidateTests.cs' (before: LF, after: mixed (CRLF x 2, LF x 458))
```

(Paths elided as returned by the tool; both are under `RoslynSentinel.Tests.Asyncify/`.)

Supporting observations, all 2026-10-05:

- `git diff --stat` is NOT evidence of byte-level safety here. `core.autocrlf=true` and `.gitattributes`
  `text=auto`; `git ls-files --eol` reports `i/lf w/lf attr/text=auto`. Git normalizes CRLF to LF on
  diff, so a CRLF->LF rewrite of stray lines is invisible to it.
- Raw bytes of the working files after the apply: `ConvertToAsyncBridgeTests.cs` CR=0, LF=562, CRLF=0;
  `FlagMigrationCandidateTests.cs` CR=0, LF=462, CRLF=0. Both are pure LF now.

Arithmetic on the undo message: the "after" (the undo's target, i.e. the captured pre-image) is
"mixed (CRLF x 2, LF x 559)" for Convert. 559 + 2 = 561 = 562 - 1 inserted line. For Flag, 458 + 2 = 460
= 462 - 2 inserted lines. So the pre-image captured before the apply contained exactly 2 CRLF lines in
each file. The original files were mixed-EOL, and the apply wrote them back as pure LF.

## Source trace

Not traced. No `.cs` file was opened for this writeup (dog-fooding hook; the doc was assembled from
tool output and `docs/current/reference-code-file-write-paths-v1.md`). Names below are UNVERIFIED
entry points, cited without line numbers.

- `PersistentWorkspaceManager.ApplyProposedChangesAsync` (Common). Per the write-paths reference doc,
  step 8 is the EOL guard (`EolChangeGuard`, `ToolErrorCode.EolChangeRefused`) and step 9 "normalizes
  EOL to the file's dominant style" on write. The pre-image is captured at step 7 and used for
  `UndoLastApply`. (Doc claim, not re-checked against source.)
- `Common/ValidateAndApplyHelper.ValidateAndApplyAsync`: runs the same EOL check earlier "so tools get a
  structured error" (same doc).
- `UndoLastApply`: calls `WorkspaceFileEditImpl` directly per the doc's callers table; its restore path
  is the one that fired the refusal above.
- `Engines.Basic/TestCategoryTextEditor.cs` (`Apply`, `DetectEol`) and
  `Engines.Basic/TestCategoryApplyEngine.cs`: produce the new file text for `TagTestCategories`.
  Whether they route through `ValidateAndApplyAsync` or call the workspace manager directly is not known.

Ruled out / observed: the undo's refusal text proves the guard CAN classify the pre-image as mixed. It
does not prove why the same guard did not fire on the way in.

Hypotheses (all unverified):

1. The write path's dominant-EOL normalization (step 9) rewrote the 2 stray CRLF lines to LF. The inbound
   guard classifies "before" and "after" by dominant style (LF vs LF = no change) and so never saw it,
   while the undo's guard classifies exactly ("mixed (CRLF x 2, LF x N)") and refuses.
2. The in-memory `SourceText` the tool read from normalized the 2 CRLF lines before the edit, so the
   engine's input already differed from disk.
3. `TestCategoryTextEditor` split and re-joined lines using the detected (dominant) EOL, dropping the
   per-line EOLs of untouched lines.

Hypotheses 1 and 3 are not mutually exclusive. Distinguishing them needs a mixed-EOL repro (below) that
compares bytes at three points: tool output text, bytes handed to `ApplyProposedChangesAsync`, bytes on disk.

## What is and is not confirmed

Confirmed:

- The apply reported success (`applied`, failed 0) and left both listed files pure LF on disk (raw byte counts).
- The pre-image held 2 CRLF lines per file (inferred from the undo message and the line-count arithmetic).
- `UndoLastApply` refused with the error above and wrote nothing.
- The apply-side guard did not refuse the change; the undo-side guard refused the inverse of it.
- `git diff --stat` cannot show this class of change in this repo.

Not confirmed:

- Which component rewrote the CRLF lines (hypotheses above).
- Whether the files on disk were truly mixed before the apply, as opposed to the pre-image capture itself
  being wrong. The undo message is the only evidence; there is no pre-apply byte snapshot.
- Whether the other two Tests.Asyncify files (`MigrationScanResultTests.cs`, `SentinelAsyncifyToolsTests.cs`)
  were affected (the undo message names only two files, so probably not mixed).
- Whether tools other than `TagTestCategories` normalize mixed-EOL files the same way (step 9 is shared,
  so plausible).

## Why this matters

- (a) `UndoLastApply` cannot revert this batch. The files keep the new `[Category]` lines. Harmless in
  content, but undo is unusable for any mixed-EOL file, and the error does not tell the caller the mixed
  state came from the tool's own earlier write.
- (b) Earlier `TagTestCategories` batches may have silently normalized stray CRLF lines in mixed-EOL files:
  `Tests.SubAgent` (84c644e5), `Tests.Battery.Basic` (dad6c684), `Tests.Integration` (f9be87e7). That is
  undetectable through git here because of autocrlf. Their pre-images may still be in the operation blobs
  (`Common/OperationBlobWriter.cs`) and could be compared against current bytes.
- (c) The apply guard and the undo guard are inconsistent: the same file pair is "no EOL change" going in
  and "EOL change" coming out. An EOL guard that is not symmetric gives false assurance - "applied, failed 0"
  was the environment telling the agent that nothing about line endings had changed.

## Suggested direction (not implemented)

What the environment should do:

1. **Guard symmetry.** Apply and undo must use one EOL classifier and one comparison. If undo says
   "before: LF, after: mixed", the original apply must have been refused (or reported) as LF -> LF with
   N lines changed from CRLF. Prefer an exact per-line comparison (counts of CRLF/LF/CR) over a
   dominant-style label on both sides.
2. **Preserve per-line EOL for untouched lines.** A write that inserts lines must leave every untouched
   line's terminator byte-identical. Dominant-style normalization (step 9) should apply to the inserted
   lines only, or not at all for existing files that are already mixed. `TestCategoryTextEditor` must
   splice into the original text rather than split and re-join.
3. **Say so when it does normalize.** If a tool or the write path must change a stray terminator, the
   result should name the file and the count (e.g. "normalized 2 CRLF line(s) to LF") instead of reporting a
   clean `applied`. Consider a dry-run/preview field showing EOL deltas.
4. **Undo must not be dead-ended by the tool's own write.** If the pre-image is mixed and undo is the
   inverse of a recorded apply, undo should be allowed to restore the exact pre-image bytes (the guard
   protects against unintended changes, and a restore to the captured pre-image is intended by definition).
5. **Audit prior batches** (b above) by diffing operation-blob pre-images against current bytes for the
   three earlier commits.

Repro (to write, using a mixed-EOL fixture):

1. Create a `.cs` test fixture with a class whose bytes are mostly LF and exactly 2 lines ending in CRLF
   (write raw bytes, not through a tool that normalizes).
2. Load it in a workspace, run `TagTestCategories` (apply, `dryRun=false`) so it inserts one class-level
   and one method-level `[Category]` line.
3. Assert the post-apply bytes: the 2 original CRLF lines are still CRLF; inserted lines use a consistent
   EOL; no other terminator changed.
4. Run `UndoLastApply` with the returned changeId and assert it succeeds and the file is byte-identical to
   the original fixture.
5. Repeat for a second mutating tool that shares the write path (e.g. `ReplaceSnippet`) to confirm whether
   the normalization is tool-specific or in `ApplyProposedChangesAsync`.

Regression tests: put the repro above in `Tests.Basic` beside `WriteChokepointGuardrailTests.cs` (which
covers EOL refusal and line counts of the shared path but not mixed-EOL preservation), plus a
`TagTestCategories` engine test over `TestCategoryTextEditor` asserting byte-for-byte preservation of
untouched lines, and a guard-symmetry test: for any apply that succeeds, its undo must not be refused by
`EolChangeGuard`.

To unblock investigation: read `ApplyProposedChangesAsync` step 8-9 and the `EolChangeGuard` classifier,
`TestCategoryTextEditor.Apply` / `DetectEol`, and the `UndoLastApply` restore path (all via MCP tools), then
run the repro with byte capture at the three points listed under Source trace.

## Unrelated side observation

`Search(mode: method, query: "<name>")` returned all 6092 methods (a ~1.5 MB large result) instead of
filtering by name; `Search(mode: text)` worked. Separate defect, not investigated here.
