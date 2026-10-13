# Reference: with/without-MCP editing comparison harness

**Status:** CURRENT 2026-10-13. Setup path dry-run verified for all six tasks; a full agent run and the scorer have not yet been exercised end to end.

## Purpose
Get a rough, repeatable measure of how a headless Claude Code session performs the same C# task with the RoslynSentinel MCP tools versus built-in tools only. Tasks are invented from current symbols (every historical plan checked was already implemented), so they run against any pinned commit.

## How it works
- `scripts/tool-comparison/tasks.psd1` - six tasks: D1 (discovery), R2 (reference finding), M2 (modify members), N1 and N2 (rename), S1 (change signature). Each has a verbatim prompt and an objective check set.
- `scripts/Invoke-ToolComparison.ps1` - per run: `git archive` of the pinned commit into `<Root>\<stamp>-<task>-<arm>\work` (no history, no uncommitted changes); strips `docs/`, `.claude/`, `scripts/`, `ClaudeFiles/`, `.vscode/`, `CLAUDE.md`; writes a neutral CLAUDE.md; commits a throwaway baseline; pre-builds; runs `claude -p` with stream-json output; scores.
- `scripts/Score-ToolComparison.ps1` - re-runnable. Writes `score.json` and appends to `<Root>\results.csv`. Edit tasks: build, regex checks, out-of-scope files, diff size, optional tests. Answer tasks: file-level precision/recall against a gold set computed from the pristine copy. All tasks: turns, cost, tokens, tool calls by name, failed tool calls.
- Arms: `plain` = Bash/Read/Edit/Write/Glob/Grep, empty MCP config. `mcp` = RoslynSentinel MCP + `Bash(dotnet:*)`, built-in file tools denied. `both` = everything. All use `--strict-mcp-config`, `--setting-sources project`, and no `.claude/` in the copy, so no hook or user setting differs between arms.
- The MCP arm launches the server through `scripts/roslynsentinel-mcp-launch.ps1` from the main repo, pointed at the arm's copy with `--solution`. The server binary is built from the main tree, not the pinned commit; `run.json` records `MainTreeDirtyPaths`.

## Usage
```powershell
# Check the workspace without spending anything
.\scripts\Invoke-ToolComparison.ps1 -TaskId R2 -Arm mcp -SetupOnly -SkipPrebuild
# One real run per arm (same model, same commit)
.\scripts\Invoke-ToolComparison.ps1 -TaskId N2 -Arm plain -Model sonnet -Commit <sha>
.\scripts\Invoke-ToolComparison.ps1 -TaskId N2 -Arm mcp   -Model sonnet -Commit <sha>
# Re-score a finished run
.\scripts\Score-ToolComparison.ps1 -RunDir C:\tmp\toolcmp\<run> -RunTests
```
Run tasks sequentially. Pin `-Commit`; the default is the current HEAD.

## Gotchas
- **Verified baselines** (commit 1785f33c): D1 gold 12 hits in 11 files (regex-derived, eyeball once); R2 gold 9 files, matching the explorer survey; M2 has 15 `{ex.Message}` uses in GitImpl.cs; N1 old name 188 matches; N2 9 qualified calls and 67 `ValidateAndApplyAsync(` matches. Setup re-measures and flags mismatches against the manifest.
- The helper's `CancellationToken` is not its last parameter (`deletePaths` and `describeValidationFailure` follow), so S1 shifts positional arguments for any caller that passes it positionally.
- Claude CLI 2.1.296 has no `--max-turns`; the caps are `-MaxBudgetUsd` (default 5) and `-TimeoutMinutes` (default 30).
- `-RunTests` uses `dotnet test --no-build --filter FullyQualifiedName~<class>`; the filter syntax is unverified under NUnit 5, so treat test results as informational.
- Five tool-surface tests are red on baseline (see memory `reference_known_failing_tests`); do not run the full suite as an acceptance check.
- The `mcp` arm needs `LoadSolution` and tool gating handled by the agent; `claude-lean` omits ChangeSignature and MoveMember, so S1 relies on `ReplaceSnippet` unless the agent enables a toolset.
- Cost, token and tool-call fields depend on the stream-json event shape (`result` event, `tool_use` / `tool_result` blocks); confirm on the first real run.
- A single run per arm is noisy; repeat before drawing conclusions.
