# Finding: the loaded solution path is lost across a server restart, and nothing hands it back

**Status:** OPEN 2026-10-01. Fix not decided; the user proposed recommendation 1.

## Context
Reported in session `cc715aa0`'s tool-experience journal (`.claude/journal/`, 2026-10-01):

- 17:36 `- Search: first call failed SolutionNotLoaded after a server restart; the error named
  LoadSolution, but its required solutionPath was not guessable (needed the .slnx path).`

That session's call log at 17:35 shows 2 `Search` calls failing with `SolutionNotLoaded`,
then 2 `LoadSolution` calls, the first of which failed. Recovery took 4 calls.

## What is broken
A server restart discards the in-memory workspace. The next tool call fails with:

> `No solution is loaded. Call LoadSolution with a .sln, .slnx, or .csproj path.`

The message names the right tool, but not the value of its one required parameter
(`solutionPath`). The model has to recall or guess the path. In this repo the path is
non-obvious: the solution file is `RoslynSentinel.slnx` (XML format) and there is no `.sln`.

## Root cause
Traced to source:
- The message is a fixed string thrown from
  `PersistentWorkspaceManager.GetCurrentSolutionAsync`
  (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1048`). A similar fixed string exists
  in `BuildEngine.RunFullBuildAsync` (`RoslynSentinel.Engines.Basic/BuildEngine.cs:139`).
- `McpServerControl(op: "stop")` (`RoslynSentinel.Tools.Basic/AdminTools.cs:63`) returns
  `"Stopping. VS Code will rebuild the server binary and spawn a fresh instance on its next tool
  call."` It says nothing about the workspace being dropped or how to restore it, although
  `AdminTools` already holds `_workspaceManager` (`AdminTools.cs:17`) and could read the
  loaded path.
- Not traced: which kind of restart session `cc715aa0` went through. It could have been
  `McpServerControl(stop)`, a VS Code relaunch, or a crash. Only the first passes through a
  server code path before the process dies.

## Why it matters
CLAUDE.md tells agents to stop the server whenever live behaviour contradicts the source, so
restarts are routine. Each one currently costs a failed call plus a path guess. A weak model
may guess wrong repeatedly, or load a different solution (a worktree's, for example) without
noticing. Per the failure doctrine, the environment knew the right value at shutdown and
threw it away.

## Recommendation
Ranked by leverage. 1 is the user's proposal; 2 covers restarts that 1 cannot.

1. **Stop message hands the path forward.** `McpServerControl(stop)` should name the loaded
   solution and give the exact next call, for example:
   `Stopping. The workspace will be discarded. After reconnecting, call
   LoadSolution(solutionPath: "C:\...\RoslynSentinel.slnx") before any other tool.`
   The path is then in the model's context at the moment it is needed. This is cheap, and
   correct for the deliberate-restart case CLAUDE.md prescribes. If nothing is loaded, say so.
2. **SolutionNotLoaded names candidates.** For restarts that never call `stop` (VS Code
   relaunch, crash, a future idle-shutdown timer), only the new process can help. Options,
   still open:
   - (a) Persist the last loaded solution path to a per-instance file (for example under
     `.roslynsentinel/`) when `LoadSolution` succeeds, and quote it in the `SolutionNotLoaded`
     message.
   - (b) Run the same discovery as `ListWorkspaceSolutions` from the server's launch directory
     and list what it finds.

   (a) is exact but must cope with per-window instances
   (`roslynsentinel-mcp-launch.ps1` keys instances by `%VSCODE_PID%`). (b) needs no state but
   may list several candidates.
3. Not recommended: reloading the last solution automatically at startup. It hides the
   restart, delays the first call by a full solution load, and loads a solution the caller
   may not want.

## Out of scope
The `BuildEngine` and `MsToolAugmentEngine` copies of the "no solution" wording should take
the same fix, but were not reviewed one by one.
