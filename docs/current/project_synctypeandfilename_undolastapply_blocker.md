---
name: project_synctypeandfilename_undolastapply_blocker
description: "SyncTypeAndFilename picks first-declared type not target type (OPEN); the UndoLastApply half is explained + fixed 2026-09-10 by the run-398 A3 blob-sanitization fix"
metadata: 
  node_type: memory
  type: project
  originSessionId: 9753a928-0f10-43d1-ae64-dbc23ac190cf
  modified: 2026-09-10T07:05:37.753Z
---

`SyncTypeAndFilename` renamed `DocumentationTools.cs`/`GitTools.cs` to match the FIRST type
declared in the file (`DocReadResult`, `GitStatusEntry`) instead of the target type named in the
`docCommentId` (`SentinelDocumentationTools`, `SentinelGitTools`, both declared last). Confirmed via
`^public class` grep order in both files.

`UndoLastApply` then failed to revert either bad rename — both changeIds (`382873af`, `5837afe0`)
returned `NoReversibleItems`, even though the apply had genuinely succeeded and a solution was
loaded (both preconditions the error message names as the likely cause were verified false).
User has independently seen other agents hit `UndoLastApply` "no changes to undo" / changeId not
honored on other tools too — this looks like a general gap in changeId lookup or reversible-item
recording, not something specific to file renames.

Worked around with manual filesystem `mv` (git mv failed — git wasn't yet tracking the bad name).

**Why:** no MCP-tool path existed to fix the MCP tool's own mistake; per
[[feedback_dont_rm_scratch_files_outside_mcp]] policy, manual fallback needs to be the narrow
"only way to accomplish the task" case, which applied here since the tool created the bad state
directly via filesystem I/O rather than a git-aware move.

**How to apply:** don't trust `SyncTypeAndFilename`'s output filename on any multi-type file without
verifying it targeted the right type; don't rely on `UndoLastApply` as a safety net for rename ops
specifically — verify manually before/after. Full repro and impact:
docs/current/blockers/blocking_error_synctypeandfilename_wrong_type_undolastapply_no_reversible_items.md

---

## Update 2026-09-10 — the `UndoLastApply` half is explained and fixed

The run-398 A3 defect is the general gap this memory suspected. Root cause: operation blobs are
named `{toolName}_{ts}_{changeId}.json`, and 17 call sites in `SentinelAdvancedRefactoringTools`
passed *slashed* operation names (`"WrapRange/region"`, `"Inline/method"`, …). `Path.Combine`
resolved the blob into a non-existent `operations/WrapRange/` subdirectory, the write threw
`DirectoryNotFoundException`, and `WriteAsync`'s catch swallowed it into a return string no caller
inspected. Five tools — `WrapRange`, `ExtractMembers`, `SyncInterface`, `Inline`, `MoveType` —
returned changeIds `UndoLastApply` could never resolve, while reporting `status:"applied"` with a
confident undo instruction.

So this reframes the memory: it was never a changeId *lookup* bug. The blob simply did not exist,
and the two error messages actively misdiagnosed it. `NoOperationBlobFound` said "ensure the apply
completed successfully" — the apply had completed in every recorded occurrence, including this
one. Both messages were rewritten in commit `e3d32b8`.

Fixed at four levels (commit `e3d32b8`): sanitize in `OperationBlobWriter`, rename the 17 literals
to underscores, surface the failure as a typed `BlobWriteResult` instead of a sentinel string, and
trip a new non-resettable `IUnrecoverableBreaker` that refuses every subsequent mutation. A
changeId is now **withheld** when the blob write fails — one `UndoLastApply` cannot resolve is
worse than none — and `AppliedChangeSummary.Note` says "written to disk, but NOT reversible."

**Still open:** the `SyncTypeAndFilename` wrong-type half. That is a separate target-selection bug
and is unaffected by the A3 fix. Note also that renames legitimately produce blobs with no
pre-images, which is a *different* condition (`NoReversibleItems`, not `NoOperationBlobFound`) —
`GetOperationDetail` is now the recommended way to tell the two apart.
