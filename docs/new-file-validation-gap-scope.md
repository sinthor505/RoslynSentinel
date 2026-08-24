# Scope: new-file validation gap

## The gap, precisely

`ApplyProposedChangesAsync` (PersistentWorkspaceManager.cs:938-953) runs
`ValidationEngine.ValidateChangesAsync` on the whole change set *before*
writing anything to disk — but only for files that already exist as a
`Document` in the current solution. The static core
(ValidationEngine.cs:90-106) does this per file:

```csharp
var documentId = baseline.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
if (documentId == null)
{
    continue; // <-- brand-new file: silently skipped, never compiled
}
```

If *every* file in a change set is new, `affectedProjectIds` stays empty
and the whole call returns `Success = true` (ValidationEngine.cs:108-112) —
validation passes trivially with nothing actually checked.

The file then gets written to disk unconditionally by the write loop
(PersistentWorkspaceManager.cs:1067-1107), and is only added into
`CurrentSolution` afterward, with zero compile-check, by
`ApplyInMemoryDocumentUpdatesAsync` (PersistentWorkspaceManager.cs:1268-1292).

Net effect: a brand-new file with a compile error (bad syntax, unresolved
type, wrong namespace) is written to disk and silently accepted into the
solution. It's first caught only when something else touches it — a
later edit to that file, or the next full MSBuild reload/build.

This is confirmed as live behavior, not hypothetical: ~13 real call sites
pass `validateChanges: true` expecting protection
(SentinelWorkspaceTools.cs:775; SentinelAsyncifyTools.cs:495, 1539, 1570,
1784, 2152, 2272, 2485, 2535, 2671, 2958, 3056), and none of them get it
for new files.

## Proposed fix

Change the `continue` branch in `ValidationEngine.ValidateChangesAsync`
(the static core) to add the new file into the candidate solution instead
of skipping it — mirroring what `ApplyInMemoryDocumentUpdatesAsync`
already does post-write:

```csharp
if (documentId == null)
{
    var project = FindContainingProject(baseline, filePath); // needs extracting/sharing
    if (project == null)
    {
        // can't validate — no project owns this path. Keep today's
        // pass-through behavior for this one file, but this is the
        // one case that legitimately can't be fixed here.
        continue;
    }

    var newDocId = DocumentId.CreateNewId(project.Id);
    candidate = candidate.AddDocument(newDocId, Path.GetFileName(filePath),
        SourceText.From(newContent), filePath: filePath);
    affectedProjectIds.Add(project.Id);
    continue;
}
```

This makes the new file participate in the candidate compilation, so
`GetDiagnostics` will surface its errors exactly like it does for edits
to existing files.

`FindContainingProject` currently lives as a `private static` method on
`PersistentWorkspaceManager` — it would need to move somewhere shared
(e.g. a small static helper in Common) since `ValidationEngine`'s static
core takes a bare `Solution`, not a `PersistentWorkspaceManager`.

## What this does NOT fix

- **A new file whose project can't be inferred** (no existing project's
  directory contains the path — e.g. a file for a project that doesn't
  exist yet). This is inherent: there's no compilation to check the file
  against. Pass-through has to remain the behavior here; at most this
  could downgrade from silent to a logged warning (mirroring the
  existing `_logger.LogWarning("New .cs file ... does not belong to any
  project...")` in `ApplyInMemoryDocumentUpdatesAsync`).
- **Multi-new-file change sets with cross-references** (new file A calls
  a symbol defined in new file B, same change set). The loop above adds
  documents one at a time into `candidate`, so by the time all changes
  are applied, `candidate` does contain both — this should actually work
  correctly already, since `candidate` accumulates across the whole
  `foreach`. Worth a regression test once implemented, not a blocker.
- **Non-`.cs` files.** Validation is compile-diagnostics based; it has no
  meaning for non-C# files, and neither the current code nor this
  proposal changes that.

## Suggested next step

Small, isolated change, testable in isolation:
1. Extract `FindContainingProject` to a shared static location.
2. Update `ValidationEngine.ValidateChangesAsync`'s static core per above.
3. Add a test: validate a change set containing only a new file with a
   deliberate compile error (e.g. references an undefined type) against
   a real project from `TestSolutionFixture`, assert `Success == false`
   with a diagnostic. Add a sibling test with a new file that's valid,
   asserting `Success == true` (guards against over-tightening).
4. Existing `ValidateChanges_FileNotFound_ReturnsErrorReport` in
   BatteryTenTests.cs (currently flagged/failing, expects RS001 for a
   file with no containing project) would very likely start passing as a
   side effect, since that's exactly the "no containing project → still
   pass-through" branch above — but its current assertion expects an
   RS001 diagnostic that this proposal doesn't add (it only proposes
   staying pass-through for that case, matching the doc comment's
   original intent). Would need re-checking once implemented: either add
   an explicit RS001 diagnostic for the no-containing-project case too
   (closes the gap further, but changes today's "allow silently"
   default for genuinely un-attributable files), or update that test's
   expectation to match the narrower fix. Worth deciding explicitly
   rather than letting the test's current assertion drive the design.

Not implemented — this is a scope/plan only, per the user's request to
scope rather than build it out in this pass.
