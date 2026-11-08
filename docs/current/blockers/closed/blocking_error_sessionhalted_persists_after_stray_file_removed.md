# Blocking error — `SessionHalted` persists on `WriteFile` even after the drifted file was removed and `GetWorkspaceHealth` reports clean

**Status:** OPEN — reported per docs/current/feedback_dogfood_mcp_blocking_errors.md, waiting for
fix/confirmation. Blocks all further implementation of the `WorkspaceReadNavigationTools` trial
slice (docs/current/plan_split_workspace_refactoring_tools_for_di.md) in this session.

## What happened

A different agent session (see
`blocking_error_self_inflicted_write_tool_triggered_drift_halt.md`) created
`RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs` via the generic `Write` tool (not
MCP), triggering the external-drift guard. That session's own `DeleteFile` retry failed with
`SessionHalted`, as expected/designed.

I picked up the task in **my own, separate MCP connection** (never made the original out-of-band
write myself). I first confirmed the halt is not client-session-scoped: my own `DeleteFile` call
against the same stray file **also** failed with `SessionHalted`. Per the documented bypass clause
(MCP tools may be bypassed only when they are the only way to unblock the tool itself — here,
`DeleteFile` was itself blocked by the very drift it exists to clean up), I removed the stray file
with Bash `rm` directly. `git status --short` confirmed only the expected non-code changes remained
(the plan-doc edit and one blocker doc) — the stray `.cs` file was gone.

I then re-checked `GetWorkspaceHealth`:
```json
{"isOperational":true,"hasLoadedSolution":true,"projectCount":11,"documentCount":383,
 "loadErrors":[],"summary":"Workspace operational. 11 project(s) loaded, 383 document(s). No load errors.",
 "staleDocumentCount":0,"requiresReload":false}
```
Fully healthy, no drift indicated anywhere in this payload. A subsequent `GetFileOutline` (read)
also succeeded normally.

I then attempted the actual next step of the task — recreating the file correctly via MCP
`WriteFile(operation: CreateFile, ...)` — and got:
```json
{
  "success": false,
  "error": {
    "errorCode": "SessionHalted",
    "message": "WriteFile for '...WorkspaceReadNavigationImpl.cs' failed: Session halted: external file drift was detected on a tracked file. This session cannot safely continue. Stop and report to the user/operator."
  }
}
```

## Impact

- The drift halt is **sticky/latched** in a way `GetWorkspaceHealth` does not surface: health
  reports zero drift, zero stale documents, fully operational — yet the very next mutating call
  (`WriteFile`) is refused for the same reason as before the cleanup.
- This makes the halt effectively unrecoverable from *any* session once tripped, even by a session
  that never performed the offending write and even after the offending artifact is fully removed
  from disk and confirmed absent via `git status`. This is a stronger claim than the original
  blocker doc's ("a fresh session is required") — a fresh session (mine) reached the identical
  dead end.
- Blocks all further MCP-based implementation work in this session (and, if the guard is
  process/server-wide rather than truly per-session, potentially any session against this server
  instance until it is restarted).

## Confirmed NOT the cause

- Not the stray file still existing — `git status --short` confirms it is gone; only
  `docs/current/plan_split_workspace_refactoring_tools_for_di.md` (modified) and this task's own
  blocker docs (untracked `.md`) remain.
- Not a stale/different server binary — `serverBuildTimeUtc` on every response in this session is
  `2026-09-06T03:09:37Z`, matching the build I verified earlier in this same session for the
  FindReferences fix.
- Not a solution-load problem — `GetWorkspaceHealth` and `GetFileOutline` both succeed normally
  against the loaded solution.
- Not specific to `CreateFile` vs some other operation — this was a fresh `CreateFile` for a path
  that does not currently exist on disk (confirmed via the prior `rm`), not a `ReplaceFile`/retry
  against a leftover path.

## What I did NOT do

Did not retry `WriteFile` again expecting a different outcome, did not attempt another Bash-level
workaround to force the write (that would be using non-MCP tools to *complete* the task itself, not
to unblock a tool — the bypass clause does not extend that far), and did not attempt to reset/clear
whatever internal state is latching the halt (e.g. no direct manipulation of `.roslynsentinel/`
metadata). Stopping and reporting per the Major Blocker policy.

## Additional recovery attempts (per user guidance)

The user reported that reloading the solution has historically cleared this halt, and pointed to
a possible `AcknowledgeExternalFileChanges` tool as an alternative. Both were checked:

- **`AcknowledgeExternalFileChanges` / `ListExternalDiskChanges` do not exist as callable MCP
  tools** in this build. Searched the full exposed tool surface (including a broad listing of
  every tool starting with `List`) — neither name appears as an independently invokable tool.
  They are referenced only in other tools' `[Description]` prose (e.g. `DeleteFile`'s description:
  "refused if the file was modified externally since the last sync (see
  ListExternalDiskChanges/AcknowledgeExternalFileChanges)"). This is a documentation/implementation
  mismatch: the description promises a recovery mechanism that isn't actually shipped.
- **`LoadSolution(solutionPath: "RoslynSentinel.slnx", forceReload: true)` was called** —
  confirmed necessary since `LoadSolution` is a no-op when the same solution path is already
  loaded unless `forceReload:true` is passed. Result: `{"success":true,"data":"Solution loaded:
  RoslynSentinel.slnx."}`.
- **Immediately retried `WriteFile(operation: CreateFile, filepath:
  "RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs", ...)` after the forced reload.**
  Result: identical failure —
  ```json
  {"success":false,"error":{"errorCode":"SessionHalted",
    "message":"WriteFile for '...WorkspaceReadNavigationImpl.cs' failed: Session halted: external
    file drift was detected on a tracked file. This session cannot safely continue. Stop and
    report to the user/operator."}}
  ```
  `serverBuildTimeUtc` unchanged (`2026-09-06T03:09:37Z`), confirming this was tested against the
  same running server instance, not a stale binary.

**Conclusion: `forceReload:true` does NOT clear the latched halt**, contradicting the expected
historical behavior. Both levers the user identified (`forceReload` and
`AcknowledgeExternalFileChanges`) are now ruled out — the former was tried and failed, the latter
does not exist as a real tool.

## Next step

No further recovery paths are known from the client side. Waiting for guidance — likely candidates
are (a) a server restart, (b) a dedicated halt-reset tool that needs to be implemented (matching
the promise already made in `DeleteFile`'s description text), or (c) the halt state may need to be
inspected/cleared server-side (e.g. in-memory flag not tied to solution reload at all). Once
resolved, move this file to `docs/obsolete/blockers/`.
