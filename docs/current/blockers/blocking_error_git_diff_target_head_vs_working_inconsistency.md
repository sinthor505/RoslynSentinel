# `Git` tool's `diff` operation returns a false-empty result for `target: "HEAD"` when real changes exist

**Status:** CONFIRMED against real shell `git`, not yet fixed. (Narrowed after direct verification --
see "Verification against real git" below; an earlier draft of this doc also flagged a second,
apparent `target: "working"` staleness that direct verification DISPROVED -- see that section for why
it was a false lead, not a real defect.)

## What was being attempted

While wrapping up an unrelated, already-fixed issue (see the sibling
`blocking_error_renamesymbol_corrupts_unrelated_string_literals.md` in this same directory, linked
below under Related -- that issue is DONE/being closed and is not the subject of this doc), I needed
to scope a `git commit` to exactly the files this session had actually touched. The working copy has
115 unstaged files (many pre-existing from earlier work this session, not all mine), so before
staging anything I tried to build an accurate picture of what was genuinely dirty using the MCP `Git`
tool's `status` and `diff` operations -- specifically for `RoslynSentinel.Basic/RefactoringEngine.cs`,
a file I had just edited via `ReplaceSnippet` in this same session.

## The exact symptom

1. `Git(operation: "status")` -> `RefactoringEngine.cs` was not clearly visible in the returned
   unstaged-file list. The response reports `totalUnstagedCount: 115` but returns only 10 entries per
   call, with `isTruncated: true` and no `offset`/`limit`/`page` parameter anywhere in the tool's
   schema to page through the remainder. There is no way to retrieve a complete unstaged-file list for
   a 115-file dirty tree in one call, and no documented mechanism to retrieve the rest.

2. `Git(operation: "diff", target: "HEAD", paths: "RoslynSentinel.Basic/RefactoringEngine.cs")` ->
   returned:
   ```json
   {"success":true,"diff":"","filesChanged":0}
   ```
   An empty diff, i.e. the tool is asserting the file is byte-identical to `HEAD`.

3. Immediately afterward, with no edits made in between, `Git(operation: "diff", target: "working",
   paths: "RoslynSentinel.Basic/RefactoringEngine.cs")` (same file) -> returned a non-empty diff
   showing real, substantive changes: `RenameInComments`/`RenameInStrings` flag changes plus a
   doc-comment edit, all in the `988-1030` line range.

4. To break the tie between two contradictory tool outputs, I read the file directly (Read tool, used
   here only as a read-only diagnostic, not as a substitute for any mutating operation) at
   `RefactoringEngine.cs:988-1030`. The on-disk content genuinely contains my edits
   (`RenameInComments = false`, the new doc comment). This confirms the `target: "HEAD"` diff's empty
   result in step 2 was wrong -- the file is not identical to `HEAD` -- and the `target: "working"`
   diff in step 3 was at least partially right.

This was observed using the MCP `Git` tool's `status`/`diff` operations specifically. These are
operations the `Git` tool implements (per CLAUDE.md's table); this is not one of the
branch/push/checkout/worktree/stash carve-outs that legitimately fall back to the shell.

## Verification against real git (shell, unblocked mid-session)

The user unblocked shell access specifically to cross-check the MCP `Git` tool against real `git`.
Results:

- `git status --short` (real shell) lists `RoslynSentinel.Basic/RefactoringEngine.cs` as modified
  (`M`), consistent with the MCP tool's `status` reporting `totalUnstagedCount: 115` -- the file WAS
  in the dirty set, it just wasn't in the 10 entries returned/visible from that call. This is a
  pagination/visibility gap, not `status` lying about the file's state -- see below.
- `git diff HEAD -- RoslynSentinel.Basic/RefactoringEngine.cs` (real shell) returned a real,
  non-empty diff, byte-for-byte matching what the MCP tool's `diff target: "working"` call had
  returned in step 3. This **confirms `target: "working"` was correct** and **confirms
  `target: "HEAD"` (step 2, empty result) was wrong** -- real git agrees with `target: "working"`,
  not with `target: "HEAD"`.
- The apparent second defect (step 5 in an earlier draft of this doc: the "Data flow analysis" /
  "// Error:" restoration not showing in a later `diff target: "working"` call) was investigated
  further and is a FALSE LEAD, not a tool defect: `git show HEAD:RoslynSentinel.Basic/RefactoringEngine.cs`
  proves HEAD's actual committed content at those lines already read `"Data flow analysis"` and
  `"// Error: {snippetError}"` -- i.e. that text was never corrupted in any commit. The corruption
  existed only transiently in the *uncommitted working tree* (left by an earlier, still-uncommitted
  rename operation this session was cleaning up). Restoring it made the working tree match HEAD
  exactly at that spot, so a correctly-functioning diff SHOULD show nothing there -- and
  `target: "working"` was right to show nothing. There were only ever two diff results to explain
  (steps 2 and 3), not three, and only step 2 (`target: "HEAD"`) is a real defect.

## Root cause -- NOT YET TRACED TO SOURCE

I did not read the `Git` tool's implementation to find the code path responsible for `target: "HEAD"`
returning an empty/wrong diff. Nothing below should be read as a confirmed cause -- both are
hypotheses pending verification against source:

- **(a) `target: "HEAD"` diffed against the index, not the working tree.** The empty result in step 2
  is consistent with a `--cached`-style comparison (working-tree-vs-index, where the index still
  matches HEAD because nothing was staged) being mislabeled or miswired as a HEAD-vs-working-tree
  comparison. Real `git diff HEAD` (no `--cached`) returns the same content as `target: "working"`,
  so if the MCP tool's `target: "HEAD"` path is intended to reproduce `git diff HEAD`, it is
  currently doing something else -- most plausibly comparing against the index/last-stage rather than
  the working tree.
- **(b) `status`'s 10-entry cap has no pagination parameter.** Separately from the `diff` defect,
  `status` truncates to 10 of 115 entries with `isTruncated: true` and no `offset`/`limit`/`page`
  parameter on the tool's schema to retrieve the rest. The file WAS correctly counted in
  `totalUnstagedCount`, so this is a real gap (no way to enumerate a large dirty tree in bounded
  calls) rather than incorrect data, but it still meant `status` alone could not be used to build a
  complete, accurate file list for scoping a targeted commit.

Both are unconfirmed as to exact code path; the implementation has not been read.

## Why this blocks (per CLAUDE.md failure doctrine)

CLAUDE.md's failure doctrine states a tool failure is a blocking finding when it is unreachable,
returns wrong data, or has no equivalent operation, and directs: "finish any in-flight edit, stop
advancing the task, write a blocker doc, and end the turn. Do not retry speculatively, do not route
around it with shell tools, and do not resume until told the issue is fixed."

Here, `diff target: "HEAD"` gave a false "nothing changed" result for a file with real, substantive
changes -- confirmed wrong against both real shell `git diff HEAD` and direct file-content
inspection. `target: "HEAD"` is the option whose name most directly matches how a caller would
naturally ask "what changed since the last commit," so a model following the CLAUDE.md-prescribed
MCP-only workflow (no shell diagnostic fallback available) would have no way to detect the lie and
would either commit based on a false "nothing to commit" signal or silently omit a genuinely-changed
file from a targeted commit. Separately, `status`'s 10-entry cap with no pagination parameter meant it
alone could not build a complete file list for a 115-file dirty tree. Together these blocked safely
scoping `git commit` to exactly the files this session touched, so no staging or committing was
attempted before this doc was written, per the doctrine above.

## What would resolve this

- Trace the `Git` tool's `diff` implementation to find what `target: "HEAD"` actually compares against
  (working tree vs. index vs. a cached blob) and why it differs from `target: "working"` and from real
  `git diff HEAD` -- confirm or refute hypothesis (a) above against source.
- Fix `target: "HEAD"` so it agrees with `target: "working"` and with real `git diff HEAD` for the same
  file at the same point in time (once confirmed, `target: "working"` needs no change -- it was
  correct in every case checked here).
- Add pagination (`offset`/`limit`, or equivalent) to `Git(operation: "status")` so a dirty tree larger
  than 10 files can be fully enumerated in a bounded number of calls, and document how to page through
  `isTruncated: true` results -- currently no such parameter exists on the tool's schema.
- Add a regression test that: writes a change to a tracked file via a mutating MCP tool
  (`ReplaceSnippet` or similar), then calls `Git(operation: "diff", target: "HEAD", ...)` and asserts
  it returns a non-empty diff matching the file's actual changes (a real `git diff HEAD` run in the
  test's temp repo is a good oracle) -- this would have caught the defect found here directly.

## Related

- `docs/current/blockers/blocking_error_renamesymbol_corrupts_unrelated_string_literals.md` -- the
  unrelated, already-resolved issue being wrapped up when this defect was found; linked here only for
  session continuity, not because it shares a root cause with this one.
