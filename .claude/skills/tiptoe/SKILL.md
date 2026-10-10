---
name: tiptoe
description: Keep working in small, safe, interruptible steps until the usage limit cuts the session off or the context rolls over. For low-risk work (research, running tests, small changes) where an abrupt stop does no damage. Use when the user says "tiptoe", usually after wrapup, or asks to keep going until the limit.
---

# Tiptoe

Goal: keep making progress with no further supervision, where being cut off at *any* moment
(rate limit, context rollover) loses nothing and breaks nothing. The session is expected to end
abruptly; design every step for that.

Follow CLAUDE.md throughout (dog-fooding MCP tools for C# and git, PowerShell for shell,
ASCII-only punctuation, `Git` tool with scope `listed`).

## 1. Enter the mode (once)

1. Create the marker `.claude/usage/<sid8>.tiptoe` (Write tool; empty or one line is fine;
   create the folder if missing). `<sid8>` is the 8-char id in this session's journal filename
   (`.claude/journal/<date>_<sid8>.md`). The usage hook reads it and switches its limit notices
   from "run wrapup" to "keep going, stay interruptible".
2. Build the work queue: the NEXT list in `.claude/handoff/LATEST.md`'s log if one exists
   (i.e. you just ran wrapup or resumed from one), otherwise what the user asked for. Order it
   safest-first.
3. Create or reuse the progress log `.claude/handoff/<yyyy-MM-dd>_<sid8>.progress.md`: queue on
   top, then one line per finished step. This file, not this chat, is the recovery point.

## 2. What counts as a safe step

A step qualifies only if **all** hold:
- Small: one or two tool calls to finish, one symbol/file/test group.
- Compiles at both ends: the tree builds before and after. No step may leave a half-applied
  multi-file change; if a change needs several coordinated edits, use one atomic batch call or
  skip it.
- Reversible: no deletes of tracked files, no force/destructive git, no `reset`, no push, no
  server restarts (`McpServerControl`), no migrations of data you cannot regenerate.
- Verified: `Build` (and `RunTest` on the touched area) passes before you call it done.

Good fits: reading and documenting, running tests and recording results, triaging failures,
writing blocker/finding docs, adding a test, small renames and signature fixes, doc and
TODO updates. Not a fit: large refactors, cross-project moves, anything the user must decide,
anything whose partial completion is worse than not starting.

## 3. The loop

For each queue item, in order:
1. Do the step.
2. `Build`; run only the relevant tests. Red and not fixable in one more small step: revert
   only *your* files for that step (`UndoLastApply`), mark the item `PARKED: <why>` and move on.
3. Commit green work right away: `Git` scope `listed`, only files you changed, message ends with
   the attribution trailer. Never commit a non-compiling tree. Small commits are the point.
4. Append one line to the progress log (done / parked, commit hash) and drop the item from the
   queue. Do this *before* starting the next step.

Do not ask the user questions and do not stop to report between steps; park what needs a
decision and keep going. A tool failure that blocks progress gets its
`docs/current/blockers/blocking_error_<slug>.md` per CLAUDE.md, then that item is parked and
you continue with unrelated items.

## 4. Limit awareness

- The usage hook injects a notice at 85% and 95% of the 5-hour window. At 85%: keep going but
  prefer the smallest remaining items. At 95%: only steps that are one call from done.
- You can also read the live numbers in `.claude/usage/usage.json` (`five_hour.used_percentage`,
  `resets_at` as epoch seconds). Do not poll it; the hook tells you.
- If the window resets mid-session the percentages drop; keep going.
- Context rollover: after compaction, re-read the progress log first, then continue from its
  queue. Nothing else needs to survive.

## 5. Finishing

When the queue is empty or only parked items remain: make sure the tree is clean or precisely
described, write a final handoff per the `wrapup` skill's template (skip its build/commit steps
if nothing is uncommitted), and end the turn with 3-5 lines: steps done, items parked and why,
first thing to do next.
