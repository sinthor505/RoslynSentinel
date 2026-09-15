# `scripts/build.ps1`'s VS Code rebuild steps compute wrong `bin-vscode` paths after the script's own relocation to `scripts/`

**Status:** RESOLVED 2026-09-15. User confirmed the relocation to `scripts/` was intentional
("i moved the script, can we just fix the paths in it?"), authorizing the path-math fix described
in "What unblocks it" option 1 below.

## Resolution

Fixed `$repoRoot = $PSScriptRoot` → `$repoRoot = Split-Path $PSScriptRoot -Parent` in all four
relocated scripts, plus the sibling-script cross-references that broke as a result:

- `scripts/build.ps1:96` (`$repoRoot`), and the two VS Code rebuild functions' output dirs
  (`Invoke-VSCodeStdioRebuild`, `Invoke-VSCodeServerRestart`) now resolve correctly as a result.
  Also fixed its sibling-script reference to `roslynsentinel-vscode-control.ps1` (was joined off
  `$repoRoot`, now off `$PSScriptRoot` since both scripts are siblings in `scripts/`).
- `scripts/roslynsentinel-vscode-control.ps1:58` (`$repoRoot`), plus its sibling reference to
  `build.ps1` (same `$repoRoot` → `$PSScriptRoot` fix).
- `scripts/roslynsentinel-planstep.ps1:176` (`$repoRoot`) and its `$ImplRepo` default parameter
  value (line 151), which had the identical bug pattern.
- `scripts/roslynsentinel-modeleval.ps1:214` (`$repoRoot`).

Checked the other candidates named in "What unblocks it" below:
`RoslynSentinel.Tools.PlanStepRunner/Program.cs` and `DotnetProcess.cs` mention `build.ps1` only in
a comment explaining they deliberately do NOT call it (to avoid the heavyweight full-solution
rebuild) — no path assumption to fix. `docs/current/reference-vscode-control-script-v1.md`
references `build.ps1` only prose-descriptively (what the `build` command delegates to), not as a
hardcoded path — no fix needed there either.

Live server verification (the original task this blocker blocked): rebuilt and restarted the
`bin-vscode` copies via the fixed `build.ps1 -Force` (hit and resolved an unrelated MSB3027 file
lock from a live `RoslynSentinel.Server.Advanced.exe` holding `RoslynSentinel.Common.dll`, fixed by
killing that PID immediately before the rebuild). Final `RunTest` call against the freshly rebuilt
server (solution scope, filter `FullyQualifiedName~RunTestTests`) returned
`runSucceeded: true, totalCount: 11, passedCount: 11, failedCount: 0`, with
`RoslynSentinel.Tools.PlanStepRunner` correctly absent from `projectSummaries` — confirming both
the `TestRunEngine.cs` fix and the `IsTestProject` heuristic fix this blocker had stalled
verification of.

## Original report (2026-09-14)

Found mid-session while verifying a `TestRunEngine.cs` fix (see "Task this blocked" below).
Environment/process defect per `CLAUDE.md`'s failure doctrine — the script's own `$repoRoot`
computation silently broke when the file moved, and its error output described a path that was
never the real location rather than surfacing the mismatch.

## What happened

`build.ps1` was relocated from the repo root to `scripts/build.ps1` by a concurrent process during
this session — `git status` at session start already showed multiple in-flight changes, and repo
memory (`project_concurrent_sessions.md`) confirms this repo may have more than one session editing
it at once. Commit `745a616` ("Moved scripts for... `roslynsentinel-planstep.ps1`... `roslynsentinel-vscode-control.ps1`...")
is the likely vehicle, though `build.ps1` itself isn't named in that commit's own subject line —
worth confirming directly with whoever ran it.

The script computes its notion of repo root directly from its own location:

```powershell
$repoRoot = $PSScriptRoot
```

(`scripts/build.ps1:96`) and then joins the VS Code rebuild output paths off that:

```powershell
$vscodeOutDir = Join-Path $repoRoot 'bin-vscode\Advanced'
```

(`scripts/build.ps1:311`, `Invoke-VSCodeStdioRebuild`) and identically for the HTTP copy:

```powershell
$vscodeOutDir = Join-Path $repoRoot 'bin-vscode\Advanced.Http'
```

(`scripts/build.ps1:348`, `Invoke-VSCodeServerRestart`).

While the script lived at repo root, `$PSScriptRoot` *was* the repo root, so this worked. Now that
the file lives in `scripts/`, `$PSScriptRoot` resolves to `.../RoslynSentinel/scripts`, so every
path this script computes off `$repoRoot` — not just the two called out here, `$docsDir`,
`$warningsBaseline`, `$testsBaseline`, and the `$flavorToProject` project paths at minimum — points
one directory level too deep.

Observed symptoms across two invocations in this session:

1. First invocation, run from an old cached working-directory reference to the former repo-root
   path: reported `"VS Code stdio copy build failed (exit 1)"` (the warning text emitted at
   `scripts/build.ps1:334`). The exit code came from `dotnet build` targeting a project path that
   was itself miscomputed off the wrong `$repoRoot`, not from a real compile failure in
   `RoslynSentinel.Server.Advanced`.
2. Second invocation, from the corrected `scripts/` location: went further off the rails and
   reported the exe simply **does not exist**, at
   `scripts\bin-vscode\Advanced\RoslynSentinel.Server.Advanced.exe` — a path that was never the real
   build output location under either the old or new layout. The real `bin-vscode/` (containing
   `Advanced/` and `Advanced.Http/`, both with binaries as recent as today ~17:20) lives directly
   under repo root, untouched by any of this. Nothing was deleted; the directory simply became
   unreachable by the relocated script's own path math.

## Root cause

Traced to source, confirmed by direct inspection, not a hypothesis: `$repoRoot = $PSScriptRoot`
(`scripts/build.ps1:96`) is a one-line assumption that the script's own file lives at repo root.
That assumption was true until the file was moved and was never updated as part of the move. This
is a plain path-math bug, not a symptom of anything deeper — confirmed by directly listing both
`bin-vscode/` (present, populated, at repo root) and `scripts/` (contains `build.ps1`, no
`bin-vscode` sibling) rather than trusting either warning message's own claimed location.

## Why this matters / impact — task this blocked

Mid-verification of a real `TestRunEngine.cs` fix: `RunTest`'s `scope: "solution"` path was silently
discarding all but the last-finished test project's results because parallel `dotnet test`
sub-invocations shared one TRX file path. Fix gives each project its own TRX file and adds
aggregated `ProjectSummaries`. The fix is source-complete — `dotnet build` succeeds with 0 errors,
and `RunTestTests` passes 11/11 when `RunTest` is scoped to a single project
(`RoslynSentinel.Tests.Battery`).

What's left is a live end-to-end confirmation that the fix's `IsTestProject` heuristic no longer
false-positives on `RoslynSentinel.Tools.PlanStepRunner` (a non-test project that transitively
references a test project) — which requires calling the live MCP server's `RunTest` tool, and that
live server is a separately-built `bin-vscode` copy that only gets refreshed by this same broken
rebuild step. With `Invoke-VSCodeStdioRebuild` computing the wrong output/project paths, there is no
way to get the live server to pick up the fix and complete this check.

## What I did NOT do

Did not edit `scripts/build.ps1`'s `$repoRoot` logic myself. A concurrent session appears to be
mid-reorganization of the scripts layout (per the relocation itself and commit `745a616`'s stated
scope), and a unilateral fix here could collide with further changes already in flight elsewhere —
e.g. if `bin-vscode/` is also being moved under `scripts/`, or if other callers of the old
repo-root `build.ps1` path are about to be updated together. Per `CLAUDE.md`'s dog-fooding policy
this stops here rather than routing around it with a manual `xcopy`/hand-edit and a shell-driven
rebuild.

## What unblocks it

Either:

1. **Confirmation `scripts/` is the intended permanent location**, in which case
   `$repoRoot = $PSScriptRoot` (`scripts/build.ps1:96`) should become
   `$repoRoot = Split-Path $PSScriptRoot -Parent` (or equivalent), and the other files that assume
   `build.ps1` lives at repo root should be checked at the same time rather than one-by-one as each
   breaks — `Grep` for `build.ps1` from repo root turns up at least
   `scripts/roslynsentinel-vscode-control.ps1`, `docs/current/reference-vscode-control-script-v1.md`,
   and `RoslynSentinel.Tools.PlanStepRunner/Program.cs` (plus `DotnetProcess.cs`) as other
   candidates worth checking for a hardcoded old-path assumption.
2. **Or confirmation the relocation was unintentional / already being reverted**, in which case
   `build.ps1` moves back to repo root and no source change is needed — the rebuild+restart+final
   verification this session was mid-task on can then proceed as-is.

Whoever resolves this should also confirm whether `745a616` actually moved `build.ps1` or whether a
different, not-yet-committed concurrent edit did — the commit's own subject line names
`roslynsentinel-planstep.ps1` and `roslynsentinel-vscode-control.ps1` but not `build.ps1` by name.

## Related

- `docs/current/blockers/blocking_error_live_server_stale_after_enum_addition.md` and
  `blocking_error_git_stage_scope_schema_stale_server.md` — same downstream symptom class (live
  server can't be refreshed/verified against), different upstream cause (there: process predates a
  source edit; here: the rebuild script itself can no longer find what it's rebuilding).
- Memory: `project_concurrent_sessions.md` (multiple sessions may edit this repo simultaneously),
  `feedback_stale_server_before_rebuild.md` (check bin-vscode DLL mtime vs. git log before deep-diving).
