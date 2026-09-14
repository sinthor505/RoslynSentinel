# Finding: write-path tools report fabricated success on formatting-only no-op edits

**Status:** confirmed tool defect, not yet fixed. Found during self-run
`manual-selfrun-20260914-004605`, step 12.

## Context

Both `ReplaceSnippet` and `Member(operation: "replace")` returned `Success: true` — with a
real `changeId`, a bumped `workspaceVersion`, and "written to disk" language — for edits that
made **zero actual change to the file on disk**. Reproduced 3 times: 2x `ReplaceSnippet`, 1x
`Member(replace)`.

## Root cause

The common thread across all 3 reproductions: the submitted edit was judged
"semantically identical" to the existing code — a formatting/trivia-only change (e.g.
re-indentation, whitespace) with no AST-level difference from what's already there. Verified
independently via a raw `grep` on the worktree file directly (not through any MCP read path),
ruling out an MCP read-cache artifact reporting stale content back — the file genuinely never
changed.

Not traced to the exact source line this run, but the shared write path
(`ApplyProposedChangesAsync` in `RoslynSentinel.Common`, per `project_write_path_chokepoint_unified`
memory) is the only place all C# writes in the codebase funnel through, so the no-op detection
almost certainly lives there: some semantic-diff check short-circuits before the actual file
write when it decides "nothing meaningfully changed," but the tool's response path doesn't
distinguish that short-circuit from a genuine write, and reports the same success shape either
way.

## Why this matters

This is a "silent success with wrong data" failure — exactly the class CLAUDE.md's dogfooding
mandate exists to catch. A model trusting the tool's own `Success: true` has no signal that its
formatting-only edit never landed; it will move on believing the file matches its intent, and
any later diff/build step will silently disagree with that belief.

## Workaround

Bundle a genuine semantic/content change (e.g. real comment text, not just formatting) alongside
the formatting change in the same edit — this forces a real AST-level diff, so the no-op
short-circuit doesn't trigger and the write actually lands.

## Recommendation

- The no-op/semantic-identity check in `ApplyProposedChangesAsync` should either perform the
  write anyway (formatting changes are still real changes on disk, even if AST-equivalent), or
  report a distinct result shape (e.g. `Success: true, Written: false` or a dedicated
  `NoOpFormatting` directive) so the model can tell "nothing to do" apart from "your edit landed."
- Worth an explicit regression test: submit a formatting-only edit (re-indent an existing member
  with no other change) via both `ReplaceSnippet` and `Member(replace)`, and assert the file's
  on-disk bytes actually changed.

## Reference

- Session log: `C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md`, Step 12.
- Related memory: `project_write_path_chokepoint_unified`.
