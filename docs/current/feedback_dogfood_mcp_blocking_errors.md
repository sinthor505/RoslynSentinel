---
name: feedback-dogfood-mcp-blocking-errors
description: "RoslynSentinel MCP server dog-fooding policy — all tool errors/gaps/reachability failures are blocking; stop, document, wait for user fix signal"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 8cc40adc-02cd-47df-a2c3-a5257d5998e5
  modified: 2026-08-28T04:04:46.623Z
---

To push the RoslynSentinel MCP server to production-ready, dog-fooding it (and fixing what it
exposes) now outranks completing the task at hand. Use RoslynSentinel MCP tools for **all** future
work in this repo — reads, writes, search, everything — even when a plain Read/Edit/Bash call
would be faster or more obviously correct. See [[feedback_use_roslyn_sentinel_tools_first]] for the
prior (softer) version of this preference; this entry supersedes it with a hard stop-and-report
rule for failures instead of a silent fallback.

**Why:** chokepointing every read/write through the MCP tools is the only way to surface the
intermittent bugs that don't show up in a single isolated call — in-memory-vs-on-disk solution
drift, bugs that need several tool calls or a specific sequence to trigger, edge cases that only
appear under real usage. Falling back to Read/Edit/Bash whenever a tool is inconvenient hides
exactly the failures this effort exists to find.

**Classification — all three of these are blocking failures, not warnings to route around:**
- Reachability errors (server unreachable / appears offline / no tools loaded)
- Tool gaps (a needed capability doesn't exist yet)
- Tool errors (a call fails, returns wrong data, or behaves inconsistently)

**How to apply — on any blocking failure:**
1. Finish the in-flight local edit if one is active (don't leave a file half-modified), then stop
   advancing the task's goals. Do not retry the failed tool speculatively and do not silently fall
   back to non-MCP tools to route around it.
2. Write `docs/current/blockers/blocking_error_<slug>.md` (slug derived from the actual symptom —
   never reuse a generic name like `_foo` across different failures, since multiple blockers can be
   open at once and must not collide/overwrite each other).
3. End the turn. The user investigates/fixes the issue in a separate chat using that file, to keep
   this session focused on the original task.
4. Do not resume the blocked task until the user explicitly says the issue is fixed and to
   continue. Don't proactively re-poll or re-check server/tool health in the meantime.
5. Once resolved, move the file from `docs/current/blockers/` to `docs/obsolete/blockers/` (mirrors
   the existing current/obsolete docs-tier convention already used elsewhere in this repo).

**Known reachability flake (already diagnosed, not a fresh bug to re-investigate each time):**
MCP server access can fail / appear offline / show no tools loaded, typically on the *first* tool
attempt right after a `/compact`. The second or third attempt usually succeeds. This has been
identified as harness-side flakiness (session/tool-list reconnect timing), not a server-side bug —
still worth noting in the blocker file if it recurs, but don't treat it as equivalent in severity to
a genuine tool logic error. To troubleshoot a suspected instance: check whether
`RoslynSentinel.Server.Basic.exe` is actually running, and check the newest timestamp-slugged log
file under `C:\Users\Administrator\source\repos\RoslynSentinel\bin-vscode\Advanced\logs\` (a fresh
one is created each server startup).

**Bypass clause:** MCP tools may only be bypassed when they are the *only* way to fix a bug or
implement a feature (e.g., editing the RoslynSentinel source itself to patch the tool that's
failing). This is narrow — prefer documenting-and-stopping over bypassing whenever the task can
simply wait for a fix.

**Tradeoff acknowledged:** this makes even trivial lookups go through MCP tools instead of instant
Read/Grep calls. That's accepted as the cost of exposing drift/edge-case bugs; do not "optimize" by
reintroducing ad hoc non-MCP fallbacks for convenience.
