# `LoadSolution` fails with `FileNotFoundException` for `Microsoft.CodeAnalysis.Workspaces.MSBuild` in this session's per-window stdio instance

**Status:** RESOLVED 2026-09-18, same session. User restarted VS Code directly (out-of-band, not
requested as a tool call) and separately gave explicit authorization to rebuild/restart the server
going forward if needed. The restart produced a new per-window instance (`15396-d87a019a`, single
build, no concurrent sibling build) which does contain
`Microsoft.CodeAnalysis.Workspaces.MSBuild.dll` - confirmed via direct filesystem check before
retrying. `LoadSolution('RoslynSentinel.slnx')` against the new instance (PID 7612) succeeded:
`{"success":true,"data":"Solution loaded: RoslynSentinel.slnx."}`. This is strong (though not
exhaustively confirmed - see below) support for the file-copy-race hypothesis: the earlier failure
came from two per-window builds (`13004-d87a019a`, `32628-d87a019a`) racing within the same ~19-second
window: `00:27:28` vs `00:27:47`; last night's fix was simply a clean, non-concurrent rebuild, not a
code or config change.

**Not fully confirmed:** the race hypothesis was not root-caused further inside
`roslynsentinel-mcp-launch.ps1`/`build.ps1`'s copy step (e.g. confirming a non-atomic copy or missing
lock file) - only the presence/absence of the assembly and the near-simultaneous build timestamps
were checked, both before and after. If this recurs with two per-window builds racing again, that
would be strong enough independent confirmation to skip re-deriving it next time; until then this
remains the leading hypothesis rather than a fully traced root cause.

## What was being attempted

First step of tonight's ledger implementation work: `LoadSolution(solutionPath: "RoslynSentinel.slnx")`
to get a loaded solution before touching any code, per the normal `SolutionNotLoaded` precondition
every other MCP tool in this server enforces.

## The exact result

```json
{
  "serverBinaryPath": "C:\\Users\\Administrator\\source\\repos\\RoslynSentinel\\bin-vscode\\32628-d87a019a\\Advanced\\RoslynSentinel.Server.Advanced.dll",
  "serverPid": 27624,
  "success": false,
  "error": {
    "errorCode": "Exception",
    "message": "LoadSolution 'RoslynSentinel.slnx' failed unexpectedly (FileNotFoundException): Could not load file or assembly 'Microsoft.CodeAnalysis.Workspaces.MSBuild, Version=5.9.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35'. The system cannot find the file specified."
  }
}
```

## Root cause, traced to source rather than assumed

Per this repo's failure doctrine (never stop at the surface), the actual file state was checked before
concluding anything:

- `bin-vscode\32628-d87a019a\Advanced\` (the exact `serverBinaryPath` directory this session's server
  process reports) does **not** contain `Microsoft.CodeAnalysis.Workspaces.MSBuild.dll` - confirmed via
  a direct filesystem search, not inferred from the error message alone.
- A sibling per-window instance folder, `bin-vscode\13004-d87a019a\Advanced\`, **does** contain that
  exact assembly.
- `roslynsentinel-vscode-control.ps1 status` (the documented status tool per
  `project_vscode_control_script` memory) reports both instances as `running`, built 1 second apart:
  `13004-d87a019a: built 09/18/2026 00:27:47` vs `32628-d87a019a: built 09/18/2026 00:27:28`.
- The latest commit in `git log` (`45a10db`, 2026-09-17T18:31:30-07:00) predates both build timestamps,
  so this is not simply a stale-binary-vs-newer-commit situation
  (`feedback_stale_server_before_rebuild`'s usual case) - both instances *are* freshly built relative to
  the code, just not identically.

**Working hypothesis, labeled as a hypothesis per this repo's citation discipline:** two per-window
`build.ps1`-driven builds launched close together (00:27:28 and 00:27:47, per
`project_per_session_mcp_server_isolation`'s per-`%VSCODE_PID%` keyed build) raced on a shared
publish/copy step and one lost a file-copy race for this specific assembly, leaving `32628-d87a019a`'s
output directory short one DLL that its sibling has. This has not been confirmed against the build
script's actual copy logic - only the resulting file-presence asymmetry and the near-simultaneous
timestamps were verified this session. A maintainer should check
`roslynsentinel-mcp-launch.ps1`/`build.ps1`'s publish/copy step for a missing lock or non-atomic copy
before accepting this as the confirmed cause.

## Why this stopped short of a fix rather than being resolved outright

The two remaining recovery options both go through
`scripts/roslynsentinel-vscode-control.ps1` (`build` or `restart` against the `32628-d87a019a`
instance) - the exact process this session is currently connected to via MCP. Tonight's authorization
is narrow: bypass the *specific blocked operation* via shell, document it, and continue with MCP tools
for everything else. It does not extend to rebuilding or restarting the live connected server process
mid-session without a separate "rebuild approved" (per `feedback_rebuild_approved_means_kill_and_rebuild`)
- doing so risked disrupting the very session doing the investigating, which is exactly the kind of
action this session's hard-stop criteria (anything touching shared/live process state) was meant to
catch. Investigation stopped here rather than proceeding to `build`/`restart` unauthorized.

## What unblocks it

A human (or a future session with explicit "rebuild approved") should run
`scripts/roslynsentinel-vscode-control.ps1 build` (or `restart`) against the `32628-d87a019a` instance,
then re-run `LoadSolution`. If the assembly is present after that, this is very likely the file-copy
race hypothesis above; if it's still missing after a clean rebuild, that rules the race hypothesis out
and points at something wrong in the publish/copy step itself, which would need its own follow-up.

## Impact on tonight's session

All planned work for this session depended on a loaded solution (every MCP tool that isn't
`LoadSolution`/`Git`/status checks returns `SolutionNotLoaded` otherwise). With this blocked, tonight's
scope narrows to producing the plan doc (`docs/current/plans/plan_scoped_operation_ledger.md`, a `.md`
file outside the Roslyn-workspace scope boundary per CLAUDE.md, so unaffected by this) and this blocker
doc. No `.cs` implementation work can proceed through the dogfooded MCP path until this is resolved.

## Related

- `feedback_stale_server_before_rebuild` (memory) - the usual "binary older than latest commit" case;
  explicitly ruled out here since both timestamps postdate the latest commit.
- `project_per_session_mcp_server_isolation` (memory) - the per-`%VSCODE_PID%` keyed build/process
  model this instance folder naming comes from.
- `feedback_rebuild_approved_means_kill_and_rebuild` (memory) - the standing authorization phrase this
  doc deliberately did not invoke without being given.
