---
name: blocking-error-applydiff-silent-noop-false-success
description: "ApplyDiff (changesetFormat=diff) reported success + bumped WorkspaceVersion but wrote nothing to disk; git diff confirms zero changes"
metadata:
  node_type: memory
  type: project
---

# Blocking error — ApplyDiff silent no-op with false success

**Status:** FIXED (2026-09-01). Root cause found and fixed in `RoslynSentinel.Common/DiffEngine.cs`
(`ApplyDiffCore`). Unblocks [[project_runtest_tool_implementation]]
(docs/current/plan-runtest-tool-v1.md) Task 1.

## Root cause
The exact repro diff's hunk header was a bare `@@` with no `-oldStart,oldCount +newStart,newCount`
line numbers (visible in the "Exact diff payload used" section below — it opens with a lone `@@`
line). `ApplyDiffCore`'s header regex (`^@@\s+\-(\d+),?(\d*)\s+\+(\d+),?(\d*)\s+@@`) requires those
numbers and never matched. The outer loop only recognizes a hunk when the header regex matches, so
a non-matching `@@` line fell through and was treated as ordinary, inert diff text — every
following `+`/`-`/context line in that "hunk" was scanned as plain text too, since nothing ever
re-entered hunk-processing mode. The result: `ApplyDiffCore` returned the input text completely
unchanged, with no exception.

That unchanged text then reached `PersistentWorkspaceManager.ApplyProposedChangesAsync`, whose
no-op guard (`preImage == newContent`) correctly detected "nothing to write" and — correctly, given
its input — reported success and skipped the write. `workspaceVersion` still incremented because
that guard runs after the version bump. Every symptom in the original report (success:true,
validationResult clean, workspaceVersion incremented, zero bytes changed) followed directly from
this one silent parse gap; the no-op guard itself was not defective; it was hidden bad input.

`DiffHunkAnalyzer` (the diagnostic meant to catch exactly this class of malformed-header issue) uses
the same strict regex, so it also found zero hunks and zero malformed lines for this diff —
`HasFindings` was `false`, meaning even the analyzer's own warning path stayed silent.

## Fix
In `ApplyDiffCore`, any line starting with `@@` that fails the strict header regex now throws
`DiffApplyException` immediately (`RoslynSentinel.Common/DiffEngine.cs`), instead of silently
falling through as body text. A malformed/underspecified hunk header now fails loudly with a
message describing the expected format, rather than producing a false-success no-op.

Regression test added: `DiffEngineTests.ApplyDiff_BareAtAtHeaderWithNoLineNumbers_ThrowsInsteadOfSilentNoOp`
(`RoslynSentinel.Tests.Basic/DiffEngineTests.cs`), replaying this doc's exact file content and diff
payload and asserting `DiffApplyException` is now thrown. Full `RoslynSentinel.Tests.Basic` suite
run post-fix: 218/220 passed, the 2 failures (`AddSummaryComment_PreservesBlankLineBeforeTarget...`,
`GetLargeResult...T5...`) are unrelated pre-existing/flaky tests not touching `DiffEngine`.

Not fixed by this change, and out of scope: `DiffHunkAnalyzer` still can't flag a bare-`@@` header
either (same strict regex) — it simply won't be reached for this case anymore since `ApplyDiffCore`
now throws first. Left as-is since the throw now makes analysis moot for this specific failure mode.

## Retry confirmation (pre-fix)
Re-ran the exact repro below (same file, same diff payload) at user request to check for a fix.
Same result: `success:true`, `workspaceVersion` incremented again (2→3, having already gone 1→2 on
the first attempt), `validationResult.success:true`. `Git(operation:diff,
paths:"RoslynSentinel.Common/ToolEnums.cs")` again returned `{"success":true,"diff":"","filesChanged":0}`.
`GetFileOutline` on the file again lists only the original enums — `TestOutcome`/`TestResultsFilter`
still absent, `lineCount` still 182 (unchanged from the pre-edit baseline). Not fixed; deterministic
across two independent attempts against the same file/diff.

## Retry confirmation
Re-ran the exact repro below (same file, same diff payload) at user request to check for a fix.
Same result: `success:true`, `workspaceVersion` incremented again (2→3, having already gone 1→2 on
the first attempt), `validationResult.success:true`. `Git(operation:diff,
paths:"RoslynSentinel.Common/ToolEnums.cs")` again returned `{"success":true,"diff":"","filesChanged":0}`.
`GetFileOutline` on the file again lists only the original enums — `TestOutcome`/`TestResultsFilter`
still absent, `lineCount` still 182 (unchanged from the pre-edit baseline). Not fixed; deterministic
across two independent attempts against the same file/diff.

## Symptom
Called `ApplyDiff` (`changesetFormat: diff`, `action: apply`) against
`RoslynSentinel.Common\ToolEnums.cs` to append two new enums (`TestOutcome`, `TestResultsFilter`)
after the existing `BuildVerifyLevel` enum, using a small unified diff with 3 lines of context on
each side (unchanged surrounding lines: the `BuildVerifyLevel` block and the `// ── Content
hashing ──` header/`ContentHashPurpose` enum).

**Response received (verbatim):**
```json
{"serverVersion":"1.0.0.0","serverBuildTimeUtc":"2026-09-01T08:39:04.6489996Z","success":true,"data":{"success":true,"succeededFiles":["c:\\Users\\Administrator\\source\\repos\\RoslynSentinel\\RoslynSentinel.Common\\ToolEnums.cs"],"failedFiles":{},"summary":"Applied 1 changes successfully (0 delete(s)). 0 failures.","workspaceInSync":true,"workspaceVersion":2,"validationResult":{"success":true,"diagnostics":[]}},"hasMorePages":false}
```
`workspaceVersion` incremented from 1 (after an unrelated prior `CreateFile` call) to 2, as
expected for a real mutation. `workspaceInSync: true`. `validationResult.success: true`, no
diagnostics. Every signal in the response says the write succeeded cleanly.

## Actual state (verified two independent ways, both after the call returned)
1. `ReadFile` (`RoslynSentinel.Common\ToolEnums.cs`, lines 165-182) shows only the original
   `BuildVerifyLevel`/`ContentHashPurpose` enums — no `TestOutcome`/`TestResultsFilter` anywhere in
   the file.
2. `GetFileOutline` on the same file lists every enum in the file and `TestOutcome`/
   `TestResultsFilter` are absent from the symbol list entirely.
3. `Git(operation: diff, paths: "RoslynSentinel.Common/ToolEnums.cs")` → `{"success":true,"diff":"","filesChanged":0}`.
   Zero-diff confirms this isn't a read-path staleness issue (e.g. in-memory Roslyn workspace ahead
   of a stale ReadFile cache) — the actual bytes on disk are unchanged from HEAD.

This was caught immediately afterward because a subsequent `CreateFile` call for a file that
referenced the (never-added) enums failed compilation with CS0246/CS0103 — i.e. the *next* tool
call's own validation is what surfaced this, not this call's own (false) success report.

## Confirmed not the cause
- **Not a stale-server issue** — `serverBuildTimeUtc: 2026-09-01T08:39:04Z` matches the currently
  running build; no evidence of the [[project_stale_server_before_rebuild]] pattern.
- **Not a different file being written** — `succeededFiles` names the exact correct absolute path.
- **Not a read-path cache/staleness bug** — confirmed via `git diff` directly against the working
  tree, independent of any RoslynSentinel read tool.
- **Not `CreateFile`** — a separate `CreateFile` call in the same session (for
  `RoslynSentinel.Common\GroupedCountSummary.cs`) correctly landed on disk and shows as untracked
  in `git status`. The bug appears isolated to the `ApplyDiff` / `changesetFormat: diff` path
  specifically, not the write-path chokepoint generally ([[project_write_path_chokepoint_unified]]
  suggests all mutating tools share one chokepoint, which would make an isolated-to-ApplyDiff bug
  surprising — worth checking whether `changesetFormat: diff` has its own pre-chokepoint step that
  `files` format and `CreateFile` skip).

## Repro
1. Load `RoslynSentinel.slnx`.
2. Call `ApplyDiff` with `changesetFormat: "diff"`, `action: "apply"`,
   `filepath: "RoslynSentinel.Common\\ToolEnums.cs"`, and a unified diff inserting a new section
   after the existing `BuildVerifyLevel` enum (context = the `BuildVerifyLevel` closing brace/blank
   line and the following `// ── Content hashing ──` comment line — i.e. an insertion between two
   existing, unmodified hunks, not a modification of existing lines).
3. Observe `success: true`, `workspaceVersion` incremented, `validationResult.success: true`.
4. `ReadFile`/`GetFileOutline` the same file (or `git diff`) — the new content is absent; file is
   byte-identical to before the call.

Exact diff payload used (may help reproduce — hunk was a pure insertion, no deletions, 3 lines of
unchanged context per side, matching `ApplyDiff`'s documented re-anchoring tolerance so a
line-number mismatch alone shouldn't explain a total no-op rather than a mis-anchored-but-applied
edit):
```diff
@@
 [JsonConverter(typeof(JsonStringEnumConverter))]
 public enum BuildVerifyLevel
 {
     noBuild, quickBuild, fullBuild
 }
 
+// ── RunTest ───────────────────────────────────────────────────────────────────
+
+public enum TestOutcome
+{
+    Passed, Failed, Skipped, NotExecuted
+}
+
+[JsonConverter(typeof(JsonStringEnumConverter))]
+public enum TestResultsFilter
+{
+    all, failed, skipped
+}
+
 // ── Content hashing ───────────────────────────────────────────────────────────
```

## Impact
Silent data loss risk: any caller trusting `ApplyDiff`'s own success response (as designed — the
tool's whole contract is "success means it's on disk") would proceed as if the edit landed, then
hit confusing downstream failures (as happened here) or, worse, never notice at all if nothing
downstream happens to reference the missing symbol. This is more severe than a normal apply
failure because there's no error to react to — every field in the response actively asserts
success.

## Next steps (for whoever picks this up)
- Check whether `ApplyDiff`'s `changesetFormat: diff` path has a separate write/commit step from
  `changesetFormat: files` and `CreateFile`, and whether that step can no-op after
  `validateOnApply` succeeds but before the actual disk write (e.g. an exception swallowed between
  validation and the file-write call, or a hunk re-anchor that resolves to "no change needed" by
  mistake for a pure-insertion hunk).
- Once fixed, re-run the exact repro above to confirm, then move this file to
  `docs/obsolete/blockers/`.

## Task status when blocked
[[project_runtest_tool_implementation]] Task 1 (`RunTest` engine) — `GroupedCountSummary.cs`
created successfully (via `CreateFile`, unaffected). `TestRunEngine.cs` creation was attempted via
`CreateFile` and failed at pre-apply validation (CS0246/CS0103 on `TestOutcome`/
`TestResultsFilter`) because this `ApplyDiff` call had silently failed to add those enums —
`TestRunEngine.cs` was never written to disk (the failed `CreateFile` call did not create a partial
file). No half-modified files are on disk right now; `TestRunEngine.cs` does not exist yet.
