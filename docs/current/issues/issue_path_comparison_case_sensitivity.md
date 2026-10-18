# Case-sensitive path comparison: analyzer guardrail and FilePathLock comparer - OPEN

**Status:** OPEN 2026-10-17. Two path-comparison traps (FilePathWrapper vs PathComparison.Comparer, FilePathLock platform comparer); neither has an automated guard.

## Summary
Moved verbatim from the restructured TODO.md.

## Moved from TODO.md (2026-10-17)

### Analyzer / source-scan guardrail for case-sensitive path comparison - not started

**Found:** 2026-10-02, while fixing `blockers/blocking_error_path_lookup_case_sensitive_drive_letter_replacesnippet_file_not_found.md`
(phase 5a of `design_read_chokepoint.md` step 5). The root cause was a static-type trap:
`FilePathWrapper` has OrdinalIgnoreCase `Equals`/`==`, but its `implicit operator string` (->
`.Absolute`) and any `.Absolute ==` / `string.Equals(a, b)` silently become case-sensitive, so every
hand-rolled `Documents.FirstOrDefault(d => d.FilePath == path)` is a latent copy of the bug. Phase 5b
(the ~110 LINQ lookups plus ~310 `GetSolutionAsync` sites) removes the existing instances; nothing
stops a new one. Proposed: a Roslyn analyzer (or a test that scans production source) flagging
(a) `==`/`Equals` between `Document.FilePath`/`FilePathWrapper.Absolute`/`string`-converted wrappers
without `PathComparison.Comparer`, and (b) `Solution.Projects...Documents` scans for a path outside
`DocumentLookup`. Not built: the fix plan said to record it here instead.

### `FilePathLock` uses a platform-conditional comparer - left case-sensitive on Linux

**Found:** 2026-10-02, same fix. Phase 5a added `PathComparison.Comparer` (OrdinalIgnoreCase, the
same comparer Roslyn's `GetDocumentIdsWithFilePath` uses on every OS) and applied it to
`PersistentWorkspaceManager`'s `_internalChanges`, `_pendingChanges` and the `_externalChanges`
`Distinct()`. `FilePathLock` was deliberately left alone: its platform-conditional comparer is a
separate locking-semantics decision (two spellings of one file can take two different locks on a
case-sensitive OS). Revisit whether it should use `PathComparison.Comparer` too.
