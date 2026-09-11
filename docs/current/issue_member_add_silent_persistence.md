---
name: issue-member-add-silent-persistence
description: "Member(add) reported full success (changeId, 'Written to disk' note, correct changedContent) but the file was byte-for-byte unchanged, twice in a row"
metadata:
  node_type: memory
  type: project
---

# `Member(add)` — silent-persistence bug, root cause theorized but not yet confirmed live

**Status:** OPEN. Root cause not confirmed by live repro this session, but a concrete, code-level
gap was found that would produce exactly this symptom. Do not "fix" the no-op guard directly without
first confirming this theory — the near-identical incident in
[[project_run398_defect_remediation_a1_a5]] / `docs/obsolete/blockers/blocking_error_applydiff_silent_noop_false_success.md`
already shows that guessing at this guard produces a wrong fix: that guard (`preImage == newContent`)
was working correctly both times: the *real* bug was elsewhere feeding it identical before/after
content it shouldn't have received.

## Symptom (from `docs/current/finding_mcp_tool_desc_revision_blockers.md` #3)

Two consecutive `Member(operation: "add", ...)` calls adding the `CodemodKind` enum to
`RoslynSentinel.Common/ToolEnums.cs` each returned full success:

```json
{"success": true, "data": {"summary": {"changeId": "...", "status": "applied",
  "note": "Written to disk. Call UndoLastApply(...) to revert if needed."},
  "changedContent": "...correct expected enum text..."}}
```

— valid `changeId`, `workspaceInSync: true` — but a subsequent `Grep`/re-read of the target file
found it byte-for-byte unchanged. Not reproduced a third time in the same session; no repro attempt
recorded here yet either (see "Next steps").

## Confirmed: shared write path, not `add`-specific

`Member`'s `add`/`replace`/`remove` operations all funnel through the same chokepoint
(`RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs`):
`ValidateAndApplyAsync` (private wrapper) → `RoslynSentinel.Common.ValidateAndApplyHelper.ValidateAndApplyAsync`
→ `PersistentWorkspaceManager.ApplyProposedChangesAsync` → real write at
`FileIoHelper.WriteAllTextAsync`. They differ only in which `RefactoringEngine` method computes
`UpdatedText` beforehand. **This resolves the open question in the original finding doc: whatever
the root cause is, `replace` and `remove` share the exact same exposure as `add`.**

`changedContent` in the tool's response is not read back from disk or diffed against anything — for
`add` it's literally the caller's own `newMemberSource` echoed back
(`SentinelRefactoringTools.cs:475,529`), so a correct-looking `changedContent` in the response proves
nothing about what actually landed on disk.

## Root-cause theory: two independent reads of "the same" file, one stale

`PersistentWorkspaceManager.ApplyProposedChangesAsync` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1320-1341`)
captures its own `preImage` via a **fresh disk read** right before writing:

```csharp
preImages[key] = await FileIoHelper.ReadAllTextIfExistsAsync(key, cancellationToken);
```

Then its no-op guard (line 1381) skips the write entirely — while still marking `succeeded.Add(filePath)`
and returning a real `changeId` + "Written to disk" note — if the proposed new content matches that
preImage exactly:

```csharp
if (preImage == newContent) { ...; succeeded.Add(filePath); noOp.Add(filePath); continue; }
```

and a second fast path (lines 1394-1413) does the same after `NormalizeWhitespace()`-based comparison
if both sides parse as valid C#.

But `updated.UpdatedText` (what becomes `newContent` here) is computed *upstream*, by
`RefactoringEngine.AddMemberAsync`/`InsertMemberAfterAsync`/`InsertMemberBeforeAsync`
(`RoslynSentinel.Basic/RefactoringEngine.cs:1146-1149`), against a **separate, earlier read**:

```csharp
var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
```

— i.e. the in-memory Roslyn workspace's current document text, not a fresh disk read at the same
instant. If the in-memory workspace document is stale relative to what's actually on disk (plausible
given this repo's own documented pattern of multiple concurrent sessions/tools editing the same
solution — see [[project_concurrent_sessions]] — or simply a workspace that hasn't picked up an
external change yet), then:

1. `AddMemberAsync` computes `UpdatedText` by inserting the new member into its (stale) in-memory
   view of the file.
2. If disk has *already* independently reached that same end state (e.g. another session/tool made
   the identical edit moments earlier, or the in-memory workspace was already ahead of what
   `AddMemberAsync` assumed as its base and the insertion happens to reproduce the current disk
   content) — or, in the failure-shape actually observed, if the in-memory document is stale in the
   *other* direction and disk already reflects a target state that coincidentally matches
   `UpdatedText` — `ApplyProposedChangesAsync`'s fresh-disk `preImage` read comes back equal to
   `newContent`.
3. The no-op guard fires, `succeeded`/`changeId`/"Written to disk" are all reported, but no write
   ever happens — and critically, the caller has no way to distinguish this from a real successful
   write, because the *intended* content was never verified to be what's now on disk versus what
   was already there before the call.

This is speculative in its exact trigger (stale-in-which-direction), but the structural gap — two
independent file reads, from two different sources (Roslyn in-memory workspace vs. fresh disk read),
feeding into a single equality-based no-op guard — is confirmed by direct code inspection and is
sufficient on its own to produce this symptom class, independent of whatever specific timing
triggered it in the original report.

## Confirmed not the cause

- **Not a swallowed write exception**: `FileIoHelper.WriteAllTextAsync` failures are not added to
  `succeeded`; the `add`/`replace`/`remove` shapes all propagate real I/O exceptions normally.
- **Not a null-preImage/read-failure fallthrough**: if `ReadAllTextIfExistsAsync` throws,
  `preImages[key]` is explicitly set to `null` (line 1335), which safely fails the `preImage == newContent`
  no-op check (null != non-null newContent) and also skips the whitespace-normalize branch (gated on
  `preImage != null`) — both fast paths correctly fall through to a real write in this case, so a
  pre-image read failure is not the trigger.
- **Not a `changeId`/operation-blob integrity issue**: that's a separate, already-fixed bug
  (`ValidateAndApplyHelper.cs`, see [[project_run398_defect_remediation_a1_a5]], commit `e3d32b8`) —
  that fix withholds `ChangeId` when the operation blob write fails, which is a distinct failure mode
  (undo becomes unresolvable) from this one (file bytes never change at all despite a valid changeId).

## Next steps (for whoever picks this up)

- Attempt a live repro: call `Member(operation: "add", ...)` against a scratch file, deliberately
  introducing staleness between the in-memory workspace and disk (e.g. edit the file externally via
  a raw file write or a second tool session between `LoadSolution`/`GetCurrentSolutionAsync` and the
  `Member` call) and check whether the no-op guard fires incorrectly.
- If reproduced: the fix should make the mismatch loud rather than silent — e.g. have
  `ApplyProposedChangesAsync` compare its own fresh-disk `preImage` against the workspace's own
  in-memory document text *before* trusting `UpdatedText` as "the diff from current state," and throw
  or refresh-and-retry on mismatch, rather than only ever comparing `preImage` to `newContent`.
- If not reproducible via this path: re-open a fresh live-tool session and retry the exact original
  repro (add `CodemodKind`-shaped enum via `Member(add)` twice in a row) to see if it's still
  possible to trigger organically; if two attempts don't reproduce it, downgrade this from "confirmed
  gap" to "theoretical gap, unconfirmed in practice" in this doc and consider it lower priority than
  currently written.
- Once actually fixed and reproduced-then-fixed, add a regression test in
  `RoslynSentinel.Tests.Battery` alongside `BlobIntegrityInvariantTests.cs`/`UndoLastApplyTests.cs`
  replaying the confirmed repro, then move this doc's content into `docs/current/CLOSED.md`.
