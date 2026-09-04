---
name: dont-rm-scratch-files-outside-mcp
description: Never delete a RoslynSentinel-tracked scratch file with plain rm/Remove-Item mid-session — it halts all mutating MCP tools for the rest of the session
metadata: 
  node_type: memory
  type: feedback
  originSessionId: b36b7e80-314b-4a64-a7a9-10327c472699
  modified: 2026-09-04T06:25:58.104Z
---

Deleting a file that RoslynSentinel's MCP server has touched (created via `CreateFile`/`ApplyDiff`,
or just loaded into the solution) using a non-MCP tool (Bash `rm`, PowerShell `Remove-Item`, etc.)
triggers the server's external-drift detector and returns `errorCode: SessionHalted` on the very
next mutating tool call — and every one after that for the rest of the session. Read-only tools
(GetFileOutline, GetDiagnostics, ReadFile) keep working; only writes are blocked.

**Why:** confirmed live 2026-09-03 while investigating
[[project_methodsignature_null_default_bug]] — created two scratch repro files via `CreateFile`,
deleted them with Bash `rm` once done with them, and the next `MethodSignature`/`CreateFile` call
immediately halted with "external file drift was detected on a tracked file. This session cannot
safely continue." Matches the design intent in
[[project_external_drift_hard_blocker_idea]] (drift-as-hard-blocker), so this is likely
by-design/unrecoverable within the session, not a transient bug — don't bother retrying.

**How to apply:** when a scratch/throwaway file was created via a RoslynSentinel MCP tool, delete it
with the `DeleteFile` MCP tool, not a shell command — even for files you know are safe to remove.
If a session is already halted this way, stop mutating-tool attempts (per
[[feedback_dogfood_mcp_blocking_errors]]) and start a fresh session rather than trying to route
around it; the halt does not self-heal.
