# UndoChange: a last-only, compile-validated, creation-aware undo (replaces UndoLastApply)

**Status:** APPROVED 2026-11-21. The owner settled last-only semantics, validate-before-write, delete-on-undo for created files and the rename to UndoChange in a todo review; nothing is built.

## Motivation

`UndoLastApply` (`WorkspaceTools.cs:347` registers it; the body is `WorkspaceFileEditImpl.UndoLastApply`,
`WorkspaceFileEditImpl.cs:348-511`) was built when edits were held in memory and applied by changeId. The
server now writes through on every edit, after a compile check, so the tool's shape no longer matches how
the server works:

1. **It can revert any retained changeId, however old.** It reads the blob for the id, keeps every item with
   `Outcome == Succeeded && BeforeSource != null`, and writes each file's `BeforeSource` back whole. An
   edit made several changes ago is undone by overwriting every later edit to the same file with a stale
   copy. The only protection is the two refusal guards below, which detect that the file *differs* from the
   blob's `AfterSource`, not that a newer *operation* touched it.
2. **It writes the restored tree with no compile validation.** `ApplyProposedChangesAsync` is called with
   `rollbackOnPartialFailure: true, exactRestore: true` and no `validateChanges`; the parameter defaults to
   `false` (`IWorkspaceMutator.cs:26`). Every other edit tool validates before it writes (`ReplaceSnippet`
   `WorkspaceFileEditImpl.cs:1581`, `CreateFile` `:2116`), so undo is the one write path that can leave the
   solution not compiling.
3. **It cannot undo a created file.** A created file's blob item has a null `BeforeSource`, so the
   `BeforeSource != null` filter drops it and the call returns `NoReversibleItems`
   (`WorkspaceFileEditImpl.cs:378`; pinned by `UndoLastApplyTests.cs:101`
   `..._BlobHasSucceededItemWithNullBeforeSource_ReturnsNoReversibleItemsAsync`). The chokepoint itself
   documents the intent as the opposite: `PersistentWorkspaceManager.cs:2435-2437` says a null pre-image
   "means the file did not previously exist (undo should delete it rather than restore content)". The
   undo side never implemented that.
4. **Its recovery path for creates/writes is not reachable by a model.** `CreateFile` discards the
   `BlobWriteResult` (`WorkspaceFileEditImpl.cs:2138`) and returns the `ApplyChangesResult`, which has no
   changeId field (`ApplyChangesResult.cs`: no match for `ChangeId`). `WriteFile`
   (`WholeFileWriteTools.cs:112`) and `DeleteFile` (`:170`) do the same. The existing test says so in a
   comment: "changeId isn't exposed on ApplyChangesResult directly -> recover it from the blob filename"
   (`CreateFileDeleteFileTests.cs:381`). Hypothesis: I found no other route that hands a model the changeId
   of a create/write/delete, which would make point 3 moot in practice until the id is surfaced (see Open
   question 2).

Existing guards, which stay (verified):

- `UndoConcurrentEditConflict` (`WorkspaceFileEditImpl.cs:519-575`): refuses when the edit attribution
  ledger shows another agent wrote a file after the applier. Callers with no agent id are never refused.
- `UndoDiskDriftConflict` (method at `:585-644`; called at `:397`): refuses when the file on disk differs
  from the item's `AfterSource` (EOL style and a leading BOM ignored via `NormalizeForUndoCompare`,
  `:647`) or is missing. Items with no `AfterSource` are not checked. (The brief cited "~560"; that line
  falls inside the concurrent-edit guard. The drift guard is at 585.) Commits `d0e40aa3`, `1c175adc`,
  `e2fc869f` are from the brief and were not re-checked.

Owner observation (2026-11-21, not a measured count): the tool is effectively unused, and with an
always-validate-then-write server a change several edits back is unlikely to need undoing; normal edits or
a git revert are more practical.

## Proposal

### Decided

1. **Semantics: last-only.** `UndoChange(changeId)` refuses a changeId unless its blob is the newest
   (non-undone, see Open question 3) blob for every file it touched. This is stricter than the drift guard
   and sits in front of it; the drift guard stays because it also catches writes that produce no blob
   (raw disk edits, a change from before a restart, a tool that builds its own items).
   - Mechanism: scan `.roslynsentinel/operations/` for blobs newer than the target (order by the
     `{tool}_{yyyyMMddTHHmmssZ}_{fraction}_{changeId}.json` name, `OperationBlobWriter.cs:117-120`) whose
     items list any of the target's file paths with an outcome other than Skipped/Failed/RolledBack.
     `NotFoundEvidence.LatestBlobWriters` (`NotFoundEvidence.cs:205-275`) is the precedent for a
     path-to-newest-blob scan, but it is windowed to 60 minutes and capped at `MaxBlobsOpened`; undo must
     not be windowed, because an old blob with a newer one must still be refused. The folder is per
     solution root, so writes by other windows are covered.
   - Refusal text is facts and options only (CLAUDE.md tool-text rule): the newer changeId(s), their
     tools, the shared file(s), and the options (undo the newer changeId first, newest first; or edit by
     hand; or use the Git tool).
2. **Validation before write.** Undo passes `validateChanges: true` so the restored tree is compiled before
   anything is written. On new compile errors the undo is refused, nothing is written, and the error lists
   the compiler errors (facts-only; the options are a hand edit or reverting the dependent change first).
3. **Created files are deleted by undo.** The blob must record that an item was created, not infer it from a
   null `BeforeSource`:
   - Inference is unsafe: `PersistentWorkspaceManager.cs:2443-2449` also sets a null pre-image when the
     pre-image read *failed* ("record null so the caller knows undo for this specific file is
     unavailable"). Deleting a file because a read failed would destroy a file that existed.
   - Add an explicit created marker to `OperationItemRecord` (`OperationItemRecord.cs`, 54 lines, has no
     such field today), set from a chokepoint signal that distinguishes "did not exist" from "could not
     read" (shape for the plan to decide; `ApplyChangesResult` has no field for it today).
   - Undo passes created paths as `deletePaths` (parameter exists, `IWorkspaceMutator.cs:26`) when disk ==
     `AfterSource` (the drift guard applies to them), in the same call as any restored files so
     `rollbackOnPartialFailure` covers the mix. Validation then covers the deletion too, so deleting a
     created type that other code now references is refused with the compile errors.
4. **Rename `UndoLastApply` to `UndoChange`, migration-first.** New name is the tool; hints, descriptions and
   allow-lists move. Whether the old name survives as an alias for one release is open; recommendation:
   no alias (the tool is unused, and an alias keeps two names in prompts and journals).

### Rename surface (verified; every item must move)

- Tool registration `WorkspaceTools.cs:347`, description at `:349` ("Reverts files from a previous apply
  back to their pre-apply state..."). `DeleteFile`'s description also says "Undoable via UndoLastApply"
  (`WholeFileWriteTools.cs`, the `[Description]` above `DeleteFileTool`).
- `ToolCallHint.Call("UndoLastApply", ...)`: **8 call sites in 6 files** (the brief said "at least 8
  files"): `AppliedChangeSummary.cs:143`, `AdvancedRefactoringTools.cs:1134`, `CoreRefactoringTools.cs:402`,
  `QualifyCrefsReport.cs:111`, `TestCategoryApplyReport.cs:111`, and `WorkspaceFileEditImpl.cs`
  (`:1120`, `:1654`, `:2011`).
- Name-keyed registries a rename silently breaks if missed: `AgentToolAllowLists.cs:13` (eval lean list),
  `ToolClassRegistry.cs:55` (`ClaudeLeanToolNames`; a miss makes the tool vanish from claude-lean with no
  startup error), `ErrorPolicyIntentTable.cs:152`, `StaleReadPolicyRatchetTests.cs:24` (allow-list key
  `WorkspaceFileEditImpl.cs::UndoLastApply`, keyed by method name), `ToolEffectList.cs:59`
  (Tests.ErrorVariants), `TranscriptResumeTests.cs:515-516`, `ClaudeLeanModeTests.cs` (2 matches).
- Totals: 127 text matches in `.cs` files across 35+ files (including the log message at
  `OperationBlobWriter.cs:160`, and comments), plus 66 non-`.cs` files with 194 occurrences, of which the
  live ones are `.claude/settings.json`, three skills (`peer-coordination`, `tiptoe`, `wrapup`),
  `scripts/Measure-OrientationCost.ps1` and about 20 open docs; the rest sit in `closed/`, `archive/` and
  `CLOSED.md`.
- Test classes named for the old tool: `UndoLastApplyTests` (12 tests), `UndoLastApplyConcurrentEditTests`.
- A rename inside the existing `WorkspaceTools` class does not need a new `[McpServerToolType]` class, but
  it needs a server restart, and previously saved transcripts and eval baselines keep the old name.

## Alternatives considered, not pursued

- **Keep any-changeId semantics, rely on the drift guard (status quo).** Lost: the owner wants a stricter
  rule, and the guard only compares text, so it cannot tell "an older change whose file is untouched since"
  from "an older change shadowed by a newer one that happened to restore the same text". It also cannot
  explain the refusal in terms of operations.
- **Reverse-patch undo of an older change, leaving later edits intact.** Not discussed by the owner; listed
  so the next reader knows it exists. It would need a three-way merge on top of whole-file blobs and is
  the opposite of "normal edits or git revert are more practical".
- **Delete the tool.** Not chosen; the owner kept it and redesigned it. Every apply result still advertises
  an undo (`AppliedChangeSummary.cs:143`), and a created file has no other one-call reversal. I have no
  usage measurement; hypothesis: counting `UndoLastApply` calls in the tool-experience journals and eval
  transcripts would confirm "effectively unused".
- **A `force` parameter to bypass the last-only or validation refusal.** Not proposed. Tool text carries
  facts and options only, and a bypass parameter is the footgun this redesign removes; the refusal names the
  hand-edit and Git routes instead.
- **A single-file restore operation on the Git tool.** Per the brief, `Git` has revert/reset but no
  single-file restore (not re-verified: the Git tool was not loaded for this doc). It would not cover
  uncommitted edits, which is what undo is for.

## Decisions

Dated 2026-11-21 (owner, todo review).

| Question | Choice | Why |
| --- | --- | --- |
| Which changeIds may be undone? | Last-only: refuse a changeId that is not the newest blob for every file it touched; the drift guard stays. | The tool is effectively unused; with the always-validate-then-write server an edit several changes back is unlikely to need undoing, and normal edits or a git revert are more practical. |
| Validate the restored tree? | Yes, before writing; refuse the undo if it introduces new compile errors, with a facts-only refusal listing the errors. | Undo is the only write path without the compile gate; a refused undo is cheaper than a broken solution. |
| What does undo of a created file do? | Deletes it when disk == AfterSource (drift guard applies); the blob records the item as Created. | A created file has null BeforeSource today, so undo returns NoReversibleItems; deleting is the true inverse of a create. |
| Tool name | Rename `UndoLastApply` to `UndoChange`, migration-first; hints and descriptions move. Alias for one release: open, recommended no. | The old name promises "last apply" semantics that the redesign now actually enforces; the new name is neutral about the changeId argument. |
| Surface the changeId from `CreateFile`, `WriteFile`, `DeleteFile` and the diff tools? (open question 2) | In scope: they return the changeId and the undo hint (a `ChangeId` on `ApplyChangesResult`; DTO change, see the dto-conventions skill). | Without it the creation undo above is unreachable for a model. |

Acceptance:

- Test (a): an older changeId is refused when a newer blob touched the same file; the file is unchanged.
- Test (b): a compile-breaking undo is refused with nothing written (assert on-disk text is unchanged and
  the refusal lists the errors).
- Test (c): undo of a `CreateFile` change removes the file, using the changeId `CreateFile` returned.
- Test (d): a plain last edit is undone and the file text equals its pre-apply text.
- No remaining `UndoLastApply` reference anywhere except closed docs (`closed/` folders, `archive/`) and
  `CLOSED.md`: `Search` for the literal across `.cs`, plus a text search over `.claude/`, `scripts/` and
  `docs/current/` outside those locations, returns nothing.

Coverage note (planner responsibility): existing tests cover the *old* paths only: real revert
(`UndoLastApplyTests.cs:136`), no-op revert (`:182`), renamed file (`:243`), EOL/BOM byte-exactness
(`:304`, `:331`, `:366`), delete-then-undo (`CreateFileDeleteFileTests.cs:363`) and the attribution guard
(`UndoLastApplyConcurrentEditTests.cs`). None exercises last-only, validate-before-write or delete-on-undo,
so a green run of them says nothing about the new paths. Beyond (a)-(d) the plan should also require: the
drift guard still fires after the last-only check passes (a raw disk edit); a mixed undo (one edited file
plus one created file) is atomic; a created-file marker is not set when a pre-image read fails
(`PersistentWorkspaceManager.cs:2449`); and the updated `:101` test still returns `NoReversibleItems` for a
null `BeforeSource` item that is not marked created. Tests that stack several edits and then undo an
older id will start failing by design; that is expected, not a regression.

## Open questions

1. **Alias for the old name for one release?** Recommended no. Open; decide in the plan.
2. **Surfacing the changeId of `CreateFile`, `WriteFile`, `DeleteFile` and the diff tools.** RESOLVED
   2026-11-21: in scope (see Decisions). Decision 3 is unusable by a model until these return the changeId
   and an undo hint (Motivation point 4). The `[Produces(DataTag.ChangeId)]` on
   `DeleteFileTool` suggests the intent already exists.
3. **Does a newer blob that has an `.undone` marker count as "newer"?** Recommended no: undoing newest-first
   then makes the next older change the newest, so a chain can be unwound one step at a time. `MarkUndone`
   (`OperationBlobWriter.cs:319`) and `IsUndone` (`:311`) exist for this. Undo writes no blob, so the marker
   is the only record. Not decided.
4. **Does `validateChanges: true` compose with `exactRestore: true`**, and what error `Code` does a
   validation refusal carry through the existing `revertResult.IsError` branch (it returns
   `revertResult.Code!`)? Unverified; the plan must read `PersistentWorkspaceManager`'s validation branch
   before relying on it. Also: which `ToolErrorCode` the last-only refusal uses (both existing guards use
   `ConcurrentEditRefused`, which describes a different cause).
5. **Scan cost and bound.** Reading every retained blob (`RuntimeStateRetention.OperationsMaxCount`) to find
   newer writers of a path may be slow; a path-to-changeId index is an option. Unmeasured.
6. **Scoped operation ledger.** The tool calls `RecordUndo(changeId)` unconditionally, and the ledger can
   cascade when the id opened a scoped operation. Whether a ledger-opening changeId always has a blob, and
   how last-only applies to it, is unchecked.
7. **Staged writes.** `proposal_staged_writes.md` does not design a staged undo; if staging lands, this
   tool's "newest blob" definition needs to say how staged applies appear.

## Cost / risk

- **Touches:** `WorkspaceFileEditImpl` (undo body plus two guards), `OperationItemRecord`,
  `OperationBlobHelper` and the chokepoint (created signal), `CreateFile`/`WriteFile`/`DeleteFile` result
  shape (Open question 2), the rename surface above, and tests. New refusal paths need error codes and
  facts-only text.
- **Could break:** a missed name-keyed registry silently removes the tool from a surface
  (`ToolClassRegistry.cs:55`, `AgentToolAllowLists.cs:13`); the stale-read ratchet keyed by method name
  (`StaleReadPolicyRatchetTests.cs:24`); tests that stack edits then undo an older id; transcripts and eval
  fixtures that contain the old tool name.
- **Makes harder:** undoing an older change now requires undoing the newer ones first, or editing by hand.
  Accepted by the owner. A compile-checking undo is slower (a compile per call) and can refuse a revert the
  user wants; the refusal lists the errors and the manual routes.
- **Concurrency:** the last-only scan and the write are not atomic across windows; the drift guard remains
  the backstop for a write that lands between them.
- **Effort:** Medium.

## Related

- `proposals/proposal_staged_writes.md` - staged undo not designed.
- `proposals/proposal_cross_session_write_journal.md` - the cross-session record that last-only overlaps.
- `ideas/idea_store_aftersource_in_chokepoint_operation_blobs.md` - built (it is why `AfterSource` exists for
  the drift guard); the idea doc still sits in `ideas/` rather than `ideas/closed/`.
- `blockers/closed/blocking_error_undolastapply_refused_eol_after_apply_normalized_mixed_file.md` - why
  `exactRestore` exists.
- `TODO.md` carries the READY entry linking here; when this is built it moves to `CLOSED.md`.
