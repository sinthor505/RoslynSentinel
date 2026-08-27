---
name: feedback-use-powershell-tool-not-bash
description: "Run build.ps1/roslynsentinel-vscode-control.ps1 via the native PowerShell tool, not Bash (Git Bash) — Bash mangles Invoke-WebRequest's JSON body and produces false \"not reachable\" reports"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 8cc40adc-02cd-47df-a2c3-a5257d5998e5
  modified: 2026-08-27T17:27:12.147Z
---

Invoke `build.ps1` and `roslynsentinel-vscode-control.ps1` through the native **PowerShell** tool,
not the **Bash** tool's `powershell -File ...` — even though both appear to run the same script.

**Why:** Discovered 2026-08-27 while verifying item 1 of a 5-item TODO batch (adding a `status`
check to the end of `build.ps1`). `roslynsentinel-vscode-control.ps1 status`, run via Bash, reported
the just-restarted VS Code Advanced.Http copy as "Process running but not reachable... (Object
reference not set to an instance of an object.)" — but the server's own log
(`bin-vscode/Advanced.Http/logs/http-host-*.log`) showed the exact same `ping` request completing
with HTTP 200 in under 20ms. Re-running the identical status check through the native PowerShell
tool immediately afterward reported it correctly as reachable. Root cause: shelling PowerShell out
through Git Bash mangles the JSON string literal `Invoke-WebRequest`'s `-Body` parameter receives
(quoting/escaping gets corrupted crossing the Bash→powershell.exe boundary), so the POST body sent
is malformed and the request fails client-side before it's a real HTTP round-trip — the server was
never actually unreachable. This is very likely the root cause (or a major contributor) to this
session's "MCP server down / ConnectionRefused all session" symptom noted in
[[project_overnight_todo_run_2026_08_27]] and earlier TODO entries — those diagnoses may need
revisiting with this in mind.

**How to apply:** For any script invocation that does its own HTTP calls, JSON body construction,
or otherwise passes a quoted/escaped string as a PowerShell argument (Invoke-WebRequest, Invoke-
RestMethod, anything with an inline `-Body`/`-Headers` literal), use the **PowerShell** tool
directly, never `Bash` → `powershell -Command`/`powershell -File`. Plain build/test invocations with
no embedded quoting (`build.ps1 -Flavor Basic -Mode Build -Force`) have run fine via Bash all session
and are probably not affected — the risk is specifically inline string literals with quotes/JSON
crossing the shell boundary. When in doubt, or when a script result looks suspicious/contradicts
other evidence (like a server log), re-run it via the native PowerShell tool before trusting the
Bash-tool result.
