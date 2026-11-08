# Live MCP server can't be used to verify a newly-added `GitOperation` enum value — not a code bug, an environment/process-lifecycle gap

**Status:** OPEN (informational/process finding, not a defect in the shipped fix) — found
2026-09-12 while implementing `Git`'s `branch`/`checkout`/`push`/`fetch`/`pull` operations.

## What happened

After adding `branch`, `checkout`, `push`, `fetch`, `pull` to `GitOperation`
(`RoslynSentinel.Common/ToolEnums.cs`) and wiring dispatch in `SentinelGitTools.Git`
(`RoslynSentinel.Server.Basic/SentinelGitTools.cs`), calling the live session's own
`Git(operation: "branch")` tool — the one served by the already-running
`RoslynSentinel.Server.Advanced.exe` process this session is instructed never to restart — failed:

```
Tool call failed unexpectedly (JsonException): The JSON value could not be converted to
RoslynSentinel.Common.GitOperation. Path: $ | LineNumber: 0 | BytePositionInLine: 8.
```

`Git(operation: "status")` on the same live server succeeded normally.

## Root cause

Not a bug in the fix. The live server process was started before this session's edit landed on
disk. `ReadFile`/`GetMethodSource`/`Build`/`ModifyEnum` all operate through the Roslyn workspace
(re-parses source from disk on demand), so they correctly show the new enum member and compile it
cleanly. But `[McpServerTool] Git(...)`'s actual dispatch runs inside the **already-JITed,
already-loaded** `RoslynSentinel.Common.dll` in that long-lived process — a `JsonStringEnumConverter`
bound to the old `GitOperation` type, which has no `branch` member yet. No .NET process picks up an
enum member added to a recompiled DLL on disk without restarting.

This matches the standing memory note "`LoadSolution` doesn't rebind server binary — loading a
worktree's `.slnx` into an already-running server still runs the OLD binary's code" — the same
category of drift, here from an in-place source edit rather than a workspace switch.

## Why this isn't logged as a defect in the Git fix itself

The fix was verified correct via an isolated, freshly-built copy of the server (built to a scratch
output directory, run standalone against a disposable throwaway git repo, driven directly over
MCP stdio JSON-RPC) — every new operation (`branch` list/create, `checkout`, `checkout -b`, `fetch`
against a repo with no remote) round-tripped correctly with clean, non-leaking error text. `Build`
(fullBuild) reports 0 errors both before and after. The live-session tool call failure is solely a
symptom of this session's own live process predating the edit, not of the edit being wrong.

## Why this is worth recording anyway

An agent working under this repo's "never restart the live server" constraint (as this session was
explicitly instructed) has no in-session way to positively confirm a schema/enum-shaped tool change
against the actual served MCP surface — it must fall back to a side-channel (standalone process +
scripted stdio client), which is exactly the kind of shell-adjacent workaround the dog-fooding
policy tries to make unnecessary. Two possible environment-level improvements, for whoever picks
this back up:

1. A documented, sanctioned way to spin up a disposable, non-live instance of the server for
   verification purposes without restarting the primary one (this session improvised one; making
   it a supported pattern would remove the improvisation).
2. Server-side: detect a `JsonException` specifically on the `operation` parameter's enum
   conversion and mention that the server process may be stale relative to on-disk source, rather
   than a bare deserialization error — today's message gives no hint that "restart the server" (not
   "fix your call") is the actual remedy, which is a trap for exactly the audience (weak models)
   this repo is built for.

## Related

- `docs/current/blockers/blocking_error_git_requires_loaded_solution.md` — the defect this session's
  `branch`/`checkout`/`push`/`fetch`/`pull` work was layered on top of; its repo-root walk-up fix
  (defect 1) was already committed and is unaffected by this note.
- Memory: `feedback_stale_server_before_rebuild`, `project_loadsolution_does_not_rebind_server_binary`.
