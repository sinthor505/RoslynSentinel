# Blocker: `DeleteFile` tool added this session is not callable from this session, even after a confirmed-fresh server rebuild

**Status:** RESOLVED 2026-09-27 - root cause was wrong; see "Actual root cause" below
**Date:** 2026-09-27
**Task:** Group 8 of the engine-reorganization plan, final step (delete emptied `ApiIntegrationEngine.cs`)

## What happened

`DeleteFile` (a new MCP tool: `WorkspaceFileEditImpl.DeleteFile` / `WorkspaceFileEditTools.DeleteFile`
/ `WorkspaceTools.DeleteFile` facade forward) was added and committed this session (`20369d5`,
2026-09-27T22:05:05Z) to close a real gap: no tool could delete a file from disk (`SafeDeleteUnusedSymbol`
only removes the symbol, leaving an empty shell file behind).

A subagent resuming group 8 correctly found `DeleteFile` was not in its tool list, and diagnosed
(correctly, at the time) that the live server binary predated the commit
(`buildTimeUtc: 2026-09-27T22:04:39Z`, before the `22:05:05Z` commit).

I then, in the parent session:
1. Called `McpServerControl(op: "stop", confirmServerStop: "confirmServerStop")`.
2. Confirmed via `McpServerStatus` that a genuinely fresh binary respawned:
   `buildTimeUtc: 2026-09-27T22:07:24Z` (pid 36680) - **after** the commit's `22:05:05Z` timestamp,
   so this binary necessarily includes the `DeleteFile` addition.
3. Called `LoadSolution` again to reload the workspace on the fresh binary.
4. Called `ToolSearch(query: "select:mcp__root_roslyn_sentinel_advanced_stdio__DeleteFile")` to try to
   load the new tool's schema.

Result: `No matching deferred tools found`. The tool is still not visible in this session, despite the
underlying server process now unambiguously running code that includes it.

## Root cause (per memory of a prior, identical incident this repo has hit before) - WRONG, see below

This matches a known, previously-recorded pattern: an MCP client (VS Code's Claude Code extension, in
this environment) fixes its available tool list at session/connection start. Restarting the underlying
server process mid-session does not cause the client to re-fetch/re-negotiate the tool catalog - the
client is still working from whatever tool list it cached when this chat session first connected,
which was before `DeleteFile` existed. A server-side rebuild is necessary but not sufficient; a genuinely
new chat session (new MCP client connection) is required to pick up a newly-added tool class/method.

This is **not** a code defect in `DeleteFile` itself, `ToolClassRegistry`, or the server's tool
registration - it is a client-side tool-list caching behavior outside this repo's control from within
a running session.

## Actual root cause (found after the user pushed back on this theory)

Both the "stale binary" and "client-side caching" theories above were wrong, and neither was verified
against the one piece of evidence that would have settled it immediately: **whether `DeleteFile`
already existed somewhere in the codebase before this session touched anything.**

It did. `SearchSolutionText(pattern: "Name = \"DeleteFile\"")` found **three** registrations, not one:

- `RoslynSentinel.Server.Basic/WholeFileWriteTools.cs:111` - a pre-existing, more complete
  `DeleteFile` (existence check, undo-note in its `[Description]`, proper `ToolErrorCode` mapping) that
  predates this session entirely.
- `RoslynSentinel.Server.Basic/WorkspaceFileEditTools.cs:84` - the one I added this session.
- `RoslynSentinel.Server.Basic/WorkspaceTools.cs:184` - the facade forward I added this session.

`WholeFileWriteTools` is gated to the `Admin`/`WholeFileWrite` tool-class set, which was **not** in this
session's active `claude`-mode `activeToolClasses` list at the time. So the real, working `DeleteFile`
was never invisible due to caching or a stale binary - it was mode-gated out of this session from the
start, exactly as the user's own hypothesis ("startup mode arg issue" / "DeleteFile is in the AdminTools
mode") said. I had misread `WholeFileWriteTools` as a class I'd need to search for by name and never
actually ran a plain-text search for the tool name itself until asked to.

Adding a second, same-named `[McpServerTool(Name = "DeleteFile")]` under `WorkspaceFileEditTools`/
`WorkspaceTools` (both in the *active* class list) on top of the existing gated one produced a genuine
tool-name collision across two classes - this is very likely what actually suppressed visibility of
either registration, not a client-side cache staleness effect at all.

**Fix applied:** the user added `WholeFileWriteTools` to the `claude` mode's active tool classes. My
three duplicate additions (`WorkspaceFileEditImpl.DeleteFile`, `WorkspaceFileEditTools.DeleteFile`,
`WorkspaceTools.DeleteFile`) were removed via `Member(remove)` (bottom-up: facade forward first ran
into a caller-precheck failure since it still called into the wrapper below it, so removal order was
`WorkspaceTools` -> `WorkspaceFileEditTools` -> `WorkspaceFileEditImpl`). Build confirmed 0
errors/0 warnings after the revert. The original `WholeFileWriteTools.DeleteFile` is untouched and is
now the sole, callable `DeleteFile` tool.

**Process lesson:** "no matching deferred tool" / "tool not visible" should always be checked with a
literal-string solution-wide search for the tool's registration attribute (e.g.
`SearchSolutionText(pattern: "Name = \"ToolName\"")`) before reaching for either a stale-binary or a
client-caching explanation - both of those theories are much harder to verify and were, in this case,
simply wrong. See `feedback_check_tool_gating_before_assuming_missing.md` in memory.

## What was ruled out

- **Not a stale binary.** Explicitly verified the running binary's `buildTimeUtc` (`22:07:24Z`) postdates
  the commit that added `DeleteFile` (`22:05:05Z`). This rules out the subagent's original hypothesis for
  round two of the same symptom.
- **Not a mode-exclusion issue.** `WorkspaceTools` (the class `DeleteFile` lives in, alongside `CreateFile`
  which *is* visible) is confirmed present in `activeToolClasses` for the `"claude"` mode this session
  runs under (`McpServerStatus`'s `toolSurface.activeToolClasses` includes `"WorkspaceTools"`). If mode
  exclusion were the cause, `CreateFile` would also be invisible, and it isn't.
- **Not a `LoadSolution` gap.** Called after the restart, succeeded normally, workspace state is fine for
  every other tool.

## Current repo state

No code changes this turn. Repo state unchanged since commit `20369d5`. Group 8's remaining steps
(delete `ApiIntegrationEngine.cs`, rename `ApiAutomationEngine` -> `ApiGenerationEngine`, clean up
`BatteryFifteenTests.cs` leftovers, build, test, commit) are still pending, blocked purely on tool
visibility, not on any unresolved design or code question.

## What unblocks it

A fresh chat session (new MCP client connection) reconnecting to this repo. At that point `DeleteFile`
should appear in the tool list (server binary is already confirmably up to date) and group 8 can resume
exactly where the prior blocker doc
(`docs/current/blockers/blocking_error_no_delete_file_tool_for_emptied_apiintegrationengine.md`) left
off, using `DeleteFile` for the physical file removal.
