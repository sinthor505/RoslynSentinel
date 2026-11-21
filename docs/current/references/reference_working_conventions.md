# Reference: working conventions (build, docs, text, scripts)

**Status:** CURRENT 2026-11-20. Moved out of CLAUDE.md "Working conventions"; the short rules stay there.

## Purpose
The lower-frequency conventions. Consult when touching tests, docs, scripts or text encoding.

## How it works
- **Tests:** NUnit 5.0, not 4.6. `await` `Assert.ThrowsAsync`/`CatchAsync`/`DoesNotThrowAsync` (an
  un-awaited assert asserts nothing); `TestDelegate`/`AsyncTestDelegate` are gone. Full conventions:
  [reference_nunit5_test_conventions.md](reference_nunit5_test_conventions.md).
- **Not-found errors:** rule out scope, name-shape, container and kind mistakes before claiming absence,
  state what was searched and name the next call:
  [reference_not_found_recovery_ladder.md](reference_not_found_recovery_ladder.md).
- **Doc lifecycle:** `docs/current/TODO.md` is open-items-only; resolved entries go to `CLOSED.md`. A finished
  plan, proposal or finding gets its Status line updated and moves to its folder's `closed/`.
  `docs/current/archive/` and every `*/closed/` folder are not active documentation.
- **Worktrees:** exclude `*/Worktree/` folders (PlanStepRunner harness clones) from diffs, searches and
  reviews; their git output is not authoritative.
- **Text:** ASCII-only punctuation in comments, docs, commit messages and prompts (`-`/`--`, straight
  quotes, `<=`/`>=`); non-ASCII becomes mojibake across the file -> server -> MCP -> harness chain.
- **Scripts:** before hand-rolling a shell loop for a repeated task, check `scripts/` for a front-door
  `.ps1` (`roslynsentinel-modeleval.ps1`, `roslynsentinel-planstep.ps1`, `build.ps1`, ...).
  `Get-TelemetryRollup.ps1` rolls the per-instance tool telemetry up per tool; see
  [reference_server_telemetry.md](reference_server_telemetry.md).
- **Solution:** `RoslynSentinel.slnx` (there is no root `.sln`).

## Scratch root and repo hygiene

- **Rule:** no tool writes into the repo root. A new output folder goes under `<scratch>`.
- **Scratch root:** `<scratch>` = env `ROSLYNSENTINEL_SCRATCH_ROOT` (must be absolute; a relative or unexpanded
  `%VAR%` value is an error), else `<parent of the repo>\RoslynSentinel-ScratchDir` (a repo at a drive root uses
  the temp folder). Resolved by C# `RoslynSentinel.Common.ScratchRoot` (`RoslynSentinel.Common/ScratchRoot.cs`) and
  PowerShell `scripts\roslynsentinel-scratch.ps1` (dot-sourced), same rules.
- `ROSLYNSENTINEL_RESULTS_ROOT` (absolute) overrides only the results folder.
- A testhost running from `<scratch>\build\...` finds the repo through `RoslynSentinel.Common.RepoRootLocator`: env
  `ROSLYNSENTINEL_REPO_ROOT` first (the eval scripts export it), then a walk-up to `RoslynSentinel.slnx`.

| Folder | Holds |
| --- | --- |
| `<scratch>\ModelTestingResults\<host-suffix>\...` | model-eval and errorvariants results |
| `<scratch>\build\<suffix>` and `<scratch>\build\ev_<suffix>` | eval and test build output |
| `<scratch>\TestRuns\PlanStepRunner\<timestamp>` and `<scratch>\TestRuns\SubAgent\<runId>` | run folders |
| `<scratch>\TestResults\parallel-run` | `scripts\Test-Parallel.ps1` output |
| `<scratch>\probe-inputs` | unchanged |

Pinned eval checkouts stay outside the scratch root (under `C:\rs-eval`; override with
`ROSLYNSENTINEL_EVALQUEUE_CHECKOUTS_ROOT`) and build inside the checkout at `<checkout>\_scratchbuild_<suffix>`.

| State | Location | Limit | When swept |
| --- | --- | --- | --- |
| Large results | `<repo>\.roslynsentinel\largeresults` | 7 days | at solution load |
| Operation blobs | `<solution root>\.roslynsentinel\operations` | 14 days, newest 5,000 kept; blobs younger than 24 h never deleted | at solution load |
| Validation files | `<solution root>\.roslynsentinel\validation` | 7 days | at solution load |
| Build instances | `<solution root>\.roslynsentinel\build\<instance>` | 7 days (the current instance is kept) | when the first Build/RunTest of a server resolves its artifacts path |
| Server logs | beside the server log file | 14 days or 50 files; the current log and crash.log are never touched | at logging start |

- Implementation: `RoslynSentinel.Common/RuntimeStateRetention.cs` (constants at its top). Sweeps never throw.
- Hygiene guard: `scripts\Test-RepoHygiene.ps1` is read-only and reports `UNEXPECTED-ROOT <name>` (a top-level
  entry neither tracked nor on its known-ignored list), `UNTRACKED <path>` and `IGNORED-DATA <n> files` (ignored
  `*.json`/`*.csv`/`*.trx` that nothing is known to keep). It exits 0 unless `-Strict`. Add a name to its known
  lists only for something a tool legitimately creates.
- It runs as a SessionStart warning on `startup` and `clear` via `.claude\hooks\repo-hygiene-session-start.ps1`
  (registered in `.claude\settings.json`), silent when clean. It is a warning only and is not part of
  `build.ps1`. Untracked files may belong to a peer session sharing the tree.

## Gotchas
- Builds and tests share `bin/`/`obj/` and block on file locks, so run one at a time. For several
  projects use one solution-scope run or sequential project runs.
