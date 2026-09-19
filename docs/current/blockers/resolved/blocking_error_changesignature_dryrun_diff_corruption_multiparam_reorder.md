# `ChangeSignature` dry-run returns a corrupted diff (one-line-shifted hunks) on a multi-parameter removal, while reporting `success: true`

**Status:** OPEN - found 2026-09-19, while executing
`docs/current/plans/plan_remove_dead_symbol_session_check.md`, removing the unused `sessionId`
parameter from `DiscoveryEngine.PreviewRenameImpactAsync` (a 9-parameter method, dropping index 7).
Not a hard stop: the dry run was recognized as untrustworthy before anything was written, and a
precise manual `ReplaceSnippet` was used instead. Session continued.

## The call

```json
{"reason": "drop unused sessionId param from DiscoveryEngine.PreviewRenameImpactAsync",
 "filepath": "RoslynSentinel.Basic/DiscoveryEngine.cs",
 "methodName": "PreviewRenameImpactAsync",
 "parameters": [{"originalIndex":0},{"originalIndex":1},{"originalIndex":2},{"originalIndex":3},
                 {"originalIndex":4},{"originalIndex":5},{"originalIndex":6},{"originalIndex":8}],
 "dryRun": true, "returnDiff": true}
```

(8 of the original 9 parameter indices kept, index 7 - `sessionId` - dropped.)

## What came back

`"success": true`, `"dryRun": true`, plus a genuine, correctly-worded warning about 5 call sites it
could not rewrite:

> WARNING: 5 call site(s) could not be automatically reordered and must be fixed manually:
> DiscoveryEngineTests.cs:419 (Could not re-locate this call site in the pending document after an
> earlier edit.); DiscoveryEngineTests.cs:438 (...); DiscoveryEngineTests.cs:464 (...);
> DiscoveryEngineTests.cs:495 (...); DiscoveryEngineTests.cs:511 (...)

That warning alone would be a reasonable, actionable result. The problem is the accompanying diff.

## The diff is corrupted - a systematic one-line downward shift

Each hunk pairs a `-` line that is a real, correct old parameter-declaration line with a `+` line
containing unrelated content from **further down in the method body** - i.e. every `+` line is off
by one relative to its paired `-` line, and the offset compounds hunk over hunk. Representative
excerpt (paraphrased line numbers, verbatim content):

```
@@ -658 +658 @@
-        FilePathWrapper filePath = default,
+FilePathWrapper filePath = default, string? symbolName = null, ... CancellationToken cancellationToken = default)
@@ -659 +659 @@
-        string? symbolName = null,
+    {
@@ -660 +660 @@
-        string? contextSnippet = null,
+        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
```

The pattern continues for the full method: what should be a small, localized signature edit (one
parameter deleted, everything else unchanged) renders as if the entire multi-line parameter list
collapsed onto one line and every following body line shifted against the wrong original line. Read
literally, the diff looks like it mangles the method body - it does not (the actual applied fix,
done manually afterward, touched only the signature line), but there is no way to tell that from the
diff's own content without independently re-reading the file.

## Why this was not applied

Stated reasoning at the time (this session): the dry run "succeeded" per its status field, but the
diff itself was visibly wrong - collapsing a multi-line parameter list into one line and misaligning
every subsequent hunk is not what a correct single-parameter-removal diff looks like, regardless of
what the status field claims. Treated as untrustworthy output rather than attempted-and-reverted;
declined to apply and did a manual, precise `ReplaceSnippet` targeting only the actual signature
line instead, given the small blast radius (one method, one dependent caller file).

## Why this is a blocking finding, not a footnote

Per `CLAUDE.md`'s failure doctrine, a `"status": "dry_run_ok"`/`"success": true` result whose
attached diff is internally inconsistent (parameter-list lines mapped to unrelated body lines) is
exactly the kind of "success-shaped response conceals a real defect" pattern already documented for
`ChangeSignature` in `blockers/resolved/blocking_error_changesignature_silent_noop_on_valid_constructor.md`.
A weaker or less careful model has no structural reason to notice the diff is wrong rather than
merely unfamiliar-looking, and applying a dry run whose rendered preview doesn't match the real
edit is precisely the scenario a dry-run/preview affordance exists to prevent. This is a distinct
defect from that sibling doc and from
`blocking_error_changesignature_interface_implementation_no_cascade.md` (also found this session,
also on `ChangeSignature`, but a call-site-cascade gap rather than a rendering bug) - three separate
confirmed `ChangeSignature` defects in one session is itself worth noting as a signal about this
tool's overall reliability relative to its stated feature surface.

## What unblocks it

A maintainer needs to read `ChangeSignature`'s diff-rendering path (likely inside the same
hand-rolled `RefactoringEngine.ChangeSignatureAsync` implementation referenced by the sibling
`ChangeSignature` blocker docs) to find where the unified-diff hunks are constructed for a
multi-line parameter-list edit specifically. The one-line-shift pattern strongly suggests an
off-by-one in how the pre-edit and post-edit line arrays are zipped together when the edit spans
multiple physical lines (each parameter on its own line) rather than a single line - worth checking
whether the renderer assumes a 1:1 line correspondence between old and new text and breaks when the
parameter list is reformatted onto a different number of lines as part of the same edit.

A minimal repro: call `ChangeSignature(dryRun: true, returnDiff: true)` on any method whose
parameter list spans 3+ physical lines, removing one parameter from the middle, and inspect whether
the returned diff's `-`/`+` pairs correspond to matching content or are shifted.

## Related

- `docs/current/blockers/resolved/blocking_error_changesignature_silent_noop_on_valid_constructor.md` -
  same tool, same "success-shaped response hides a real defect" pattern, different failure mode
  (silent no-op vs. corrupted diff).
- `docs/current/blockers/blocking_error_changesignature_interface_implementation_no_cascade.md` -
  same tool, same session, different defect (call-site cascade gap, not a rendering bug).
- `docs/current/plans/plan_remove_dead_symbol_session_check.md` - the task this defect was found
  during.
- `RoslynSentinel.Basic/DiscoveryEngine.cs` (`PreviewRenameImpactAsync`),
  `RoslynSentinel.Server.Basic/SymbolRelationshipImpl.cs` - final state uses the manual
  `ReplaceSnippet` fix described above, not the `ChangeSignature` dry run.
