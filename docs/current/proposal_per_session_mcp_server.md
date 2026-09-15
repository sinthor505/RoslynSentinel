# Per-session MCP server: isolating concurrent VS Code / Claude Code instances

## Motivation

RoslynSentinel's Advanced stdio server is registered in `C:\Users\Administrator\.mcp.json`
(machine-local, user-level config — corrected 2026-09-14; not `~/.claude.json` as earlier drafts of
this proposal assumed), pointing at one fixed path:
`C:\Users\Administrator\source\repos\RoslynSentinel\bin-vscode\Advanced\RoslynSentinel.Server.Advanced.exe --include-tools=...`.
Every VS Code window and every Claude Code session on the machine spawns that same binary from that
same path — there is exactly one `bin-vscode\Advanced\` on disk (confirmed: only `Advanced\` and
`Advanced.Http\` exist under `bin-vscode\` today).

That binary is rebuilt today only by `scripts/build.ps1`'s `Invoke-VSCodeStdioRebuild`
(`scripts/build.ps1:303-339`, region "VS Code server restart"). Its own header comment already
states the assumption this proposal has to break: "stdio has no standalone process to stop/restart,
since VS Code's MCP client spawns and owns that process itself per connection" (`build.ps1:306-309`).
That's true for a single window. It stops being true the moment two windows are open at once,
because `Invoke-VSCodeStdioRebuild` stops *any* running process matching `RoslynSentinel.Server.Advanced`
at that exact path before rebuilding (`build.ps1:320-325`) — killing every other session's live
connection, not just the one being worked on.

Concretely: a session editing RoslynSentinel's own source (nearly every session, per `CLAUDE.md`'s
dog-fooding mandate) cannot rebuild and pick up its own change without tearing down every sibling
session's server mid-conversation. There is no isolation between "my window's server" and "the
machine's server."

## Decided design

### 1. Registration stays machine-local, not moved into the repo

`C:\Users\Administrator\.mcp.json` keeps a single entry — this is **not** being converted to a
repo-tracked, project-scoped `.mcp.json` committed alongside the source. Rejected explicitly: a
repo-tracked file would need independent re-registration in every other clone of this repo on the
machine (see `project_concurrent_sessions.md` for why multiple clones/sessions against this repo is
a normal state here, not an edge case). One machine-local registration whose launch command does
per-instance isolation internally is simpler than N registrations doing the same thing.

### 2. The registered command becomes a wrapper script

`C:\Users\Administrator\.mcp.json`'s command for this server changes from invoking
`bin-vscode\Advanced\RoslynSentinel.Server.Advanced.exe` directly to invoking a new wrapper,
e.g. `scripts/roslynsentinel-mcp-launch.ps1`. VS Code/Claude Code launches this script per stdio
connection exactly as it launches the `.exe` today — same trigger, same lifecycle, no change to
*when* a launch happens, only to *what* runs first.

### 3. Per-instance isolation via `%VSCODE_PID%`

Confirmed present in the actual process environment: `VSCODE_PID` was observed set (e.g. `1216`)
inside a Claude Code session spawned under VS Code, via `env` inside that session. Per the
environment's own description, it is the parent VS Code window's process ID, stable for the life of
that window and distinct across separate windows — exactly the per-window key this needs.

The wrapper derives a per-instance ID from `%VSCODE_PID%`, with the repo path folded in as a
secondary key (guards against the same user-scoped registration being invoked from a different repo
clone on the same machine, where a bare PID alone wouldn't distinguish which repo's server should be
running). Output goes to a per-instance subfolder, kept under the existing `bin-vscode/` root rather
than a new sibling top-level folder — same location as today, just one level deeper:

```
bin-vscode/<instance-id>/Advanced/RoslynSentinel.Server.Advanced.exe
```

replacing today's single `bin-vscode/Advanced/`. Each window gets its own binary, its own output
directory, and — because `dotnet build -o` writes there — its own on-disk lock, so one window's
rebuild can never contend with another's running process the way `Invoke-VSCodeStdioRebuild`'s
existing kill-before-build does today. `bin-vscode/Advanced.Http/` (the separate, still-shared
standalone HTTP fallback copy — see `project_http_fallback_broken_by_mode_default_flip`) stays
exactly where it is, as a sibling of the new `<instance-id>` folders; the two can never collide
since instance IDs are PID-derived, not the literal string `Advanced.Http`.

### 4. Wrapper always builds, every launch — no staleness check

The wrapper runs a `dotnet build` into that instance's output directory before every launch, with no
mtime/timestamp comparison to decide whether a rebuild is needed. A "check source mtime vs. binary
mtime, skip if not stale" design was considered and rejected as unneeded complexity: MSBuild's own
up-to-date check already makes a no-op incremental build cheap (seconds), so a second freshness
heuristic on top of it would just be another thing to get wrong for no measured benefit.

**Open question:** whether the wrapper should call into `Invoke-VSCodeStdioRebuild`
(`build.ps1:310-339`) refactored into a shared, callable function/script, or duplicate its
`dotnet build $vscodeProject -c Debug -o $vscodeOutDir --nologo -v quiet` invocation
(`build.ps1:329`) directly. Reuse avoids the two build paths drifting apart (flag list, config,
error handling); duplication is simpler to land first but creates exactly the kind of
copy-and-diverge risk this repo's own conventions warn against elsewhere. Not decided — flagged for
implementation time.

### 5. New `McpServerControl` MCP tool

**Superseded from the design below** (kept for history) — final, implemented shape: added as a 4th
method in `SentinelAdminTools` (`RoslynSentinel.Server.Basic/SentinelAdminTools.cs`), gated behind
`--mode=Admin`/`--include-tools` like the class's other tools. Kept fully separate from the
existing `McpServerStatus` tool (`SentinelServerStatusTools.cs`), which stays unchanged — that tool
is always-available and side-effect-free, with its own tests and cross-references in ModelEval logs
that made merging it not worth the churn.

Final op list — much smaller than originally proposed:

- **`status`** — reports this instance's PID and binary path. Always available, no confirmation
  needed.
- **`stop`** — terminates *this* process only (no PID parameter, no cross-instance targeting — the
  PID-reuse guardrail problem the original design flagged below is moot once a tool only ever
  targets itself). Requires `confirmServerStop='confirmServerStop'` (case-insensitive) or is
  refused. VS Code auto-respawns a dead server process on its next tool call — confirmed, the same
  mechanism the old `Invoke-VSCodeStdioRebuild` relied on — and the wrapper script (point 4)
  rebuilds fresh on that respawn, so `stop` is the only op needed; there is no separate
  `restart`/`rebuild`/`start` op (all considered and dropped — see reasoning below).

Dropped entirely from the original proposal:
- **PID-targeting param** — removed; self-stop only, since VS Code owns spawning and there's no
  reachable scenario for a session to target a different instance.
- **`restart`** — redundant once `stop` + the client's auto-respawn + the wrapper's always-rebuild
  already produce the same effect.
- **`rebuild`** — same reasoning; the wrapper always rebuilds per launch (point 4), so a dedicated
  rebuild op would duplicate that path for no additional capability.
- **`start`** — no reachable scenario where a session needs to start an instance that isn't already
  running and won't auto-respawn on its own.
- **`status`/`list` showing multiple instances** — out of scope for the tool; use
  `scripts/roslynsentinel-vscode-control.ps1 status` instead, which enumerates all instance folders
  from the outside.

## Cost / what this touches

- `C:\Users\Administrator\.mcp.json`'s command line for this server (machine-local config, not in the repo).
- A new `scripts/roslynsentinel-mcp-launch.ps1`.
- `scripts/build.ps1`'s `Invoke-VSCodeStdioRebuild` region, if its build logic gets refactored into a
  shared callable rather than left standalone (see point 4's open question).
- `SentinelAdminTools.cs` gains a new tool; `Admin` mode's tool count grows by one.
- On-disk layout: `bin-vscode/<instance-id>/Advanced/` becomes a new pattern of subfolder under the
  existing `bin-vscode/` root, self-pruned by each launch's best-effort sweep (see "Cleanup: stale
  instance folders" above) rather than growing unbounded. The old fixed `bin-vscode/Advanced/` path
  itself becomes dead once the wrapper is in place, unless something is still expected to point at a
  fixed, non-instanced path; `bin-vscode/Advanced.Http/` is unaffected (see point 3).
- Risk: a bug in "respond first, then exit" ordering silently breaks every session's next tool call
  after any `stop`, since the client would be left waiting on a response that never arrives before
  the process disappears. This is the single highest-blast-radius part of the design and should be
  tested in isolation before folding into normal use.

## Alternatives considered, not pursued

- **Project-scoped `.mcp.json`.** Rejected — see point 1. Would require independent registration per
  repo clone rather than the wrapper doing isolation once, centrally.
- **Staleness-checked rebuild (mtime comparison).** Rejected — see point 4. Adds a heuristic where
  MSBuild's own incremental build already solves the cost problem.
- **`rebuild` as a distinct op.** Rejected — see point 5. Redundant once the wrapper always rebuilds.

## Cleanup: stale instance folders

Resolved: the wrapper sweeps `bin-vscode/` on **every launch**, before building its own instance's
folder. It deletes each `<instance-id>`-shaped subfolder with its own `Remove-Item -Recurse -Force
-ErrorAction SilentlyContinue` call (skipping the unrelated `Advanced.Http/` sibling — see point 3) —
**one call per subfolder, not one bulk `Remove-Item -Recurse ./bin-vscode`.** A single top-level
recursive delete over the whole `bin-vscode/` root was considered and rejected: `Remove-Item` isn't
built to tolerate in-use children partway through a recursive delete — a file locked by another
currently-running session's server (the common case, not the exception, since sibling windows are
routinely open) can abort the whole call or leave a partially-deleted tree, rather than cleanly
skipping just that folder. Per-folder deletion with `-ErrorAction SilentlyContinue` costs nothing
extra in practice (a handful of folders, not a deep/wide tree) and fails closed per-folder instead of
open across the whole root. Any folder whose `.exe` is still locked by a running process fails its
own delete silently (the OS refuses to remove a file with an open handle) and is left in place for a
future sweep once that process exits. This mirrors the same tolerate-if-still-running shape
`Invoke-VSCodeStdioRebuild` already uses when it checks for and stops a locking process before
rebuilding (`build.ps1:320-325`), just without the explicit process check — here the delete attempt
itself doubles as the "is this one still live" test. No separate cleanup script or scheduled task is
needed; every window's own startup keeps the directory bounded to "currently open windows plus
whatever's mid-shutdown."

## Open questions — resolved

- **"Respond first, then exit" mechanism**: resolved. Read from the local MCP C# SDK clone (checked
  out at the tag matching the installed NuGet version): `McpSessionHandler.HandleRequestAsync` awaits
  the tool handler and then awaits `SendMessageAsync`'s own flush under a send lock before the
  request-handling task completes — no separate SDK hook needed. `McpServerControl`'s `stop`
  schedules `Environment.Exit(0)` via a detached `Task.Run` + short `Task.Delay` as defense-in-depth,
  not as the correctness mechanism.
- **Shared build logic vs. duplicated `dotnet build` line**: resolved as duplicated. The wrapper
  duplicates the one-line `dotnet build` invocation rather than refactoring
  `Invoke-VSCodeStdioRebuild` into a shared callable — that function was deleted outright in the same
  change (point 2's "the old fixed path becomes dead"), so extracting a shared helper only to delete
  one of its two callers a few steps later would have been wasted motion.
- **Guardrail on PID-targeting**: moot. The PID param was dropped entirely (see point 5) — `stop`
  only ever targets the calling process itself, so there is no PID-reuse risk to guard against.

## Status

Implemented and cut over 2026-09-14: `scripts/roslynsentinel-mcp-launch.ps1` (wrapper),
`McpServerControl` (`SentinelAdminTools.cs`), `build.ps1`'s stdio-copy rebuild logic retired,
`roslynsentinel-vscode-control.ps1`'s `status` verb reworked to enumerate per-instance folders, and
`C:\Users\Administrator\.mcp.json`'s registered command switched from the `.exe` directly to the
wrapper (same `--include-tools=...` args and `env` block, preserved as-is). See
`reference-roslynsentinel-mcp-launch-v1.md` for the launch wrapper's living reference. Takes effect
on this server's next stdio (re)connect per session.
