---
name: reference_parse_agent_log_script
description: "Parse-AgentLog.ps1 (repo root) turns agent.log transcripts into structured per-turn PowerShell/JSON objects for cross-run analysis — use instead of manual grep/reconstruction"
metadata:
  node_type: memory
  type: reference
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T11:06:36.509Z
---

`Parse-AgentLog.ps1` (repo root, committed `861cd10`) parses one or many `agent.log` files (the
`FlushingFileLoggerProvider` transcript every model-eval run produces — see
[reference_model_eval_procedure.md](./reference_model_eval_procedure.md)) into structured objects: per-run `ToolsExposedCount`,
`UserPrompt`, and `Turns[]`, each turn carrying `ReasoningText`, `ContentText`, `ToolCallCount`,
and `ToolCalls[]` with `ToolName`/`Args` (already JSON-parsed, so `.reason` is directly queryable)
/`Success`/`DurationTime`/`ResultOrError` (also JSON-parsed). Also rolls up `TotalToolErrors` and
`ToolErrorCountsByName` per run.

**Use this instead of hand-grepping/reconstructing agent.log text** — a prior 13-run manual
transcript review (surfacing tool-choice fidelity gaps) is exactly the kind of work this script now
automates.

## What it does NOT do

Does not determine pass/fail — that's NUnit's own assertion outcome and isn't written into
agent.log itself. Cross-reference against console/TestResult.xml output if you need pass/fail
joined to this data; treat this script as purely "what did the model actually reason and call,
per turn."

## Usage

```powershell
# One run
.\Parse-AgentLog.ps1 -LogPath .\ModelTestingResults\113\Model_AppliesSevenChainedRefactors\20260905-104034-728\agent.log

# Every run under a rung's results directory (recursive) — piped analysis
.\Parse-AgentLog.ps1 -LogPath .\ModelTestingResults\113\Model_AppliesSevenChainedRefactors | `
    ForEach-Object { $_.Turns.ToolCalls } | Where-Object ToolName -eq 'ApplyDiff' | `
    Select-Object -ExpandProperty Args | Select-Object -ExpandProperty reason

# Cache a whole batch as JSON for repeated offline querying without re-parsing
.\Parse-AgentLog.ps1 -LogPath .\ModelTestingResults\113 -OutDir .\ModelTestingResults\_parsed
```

`-LogPath` accepts a single file or a directory (searched recursively for `agent.log`). `-AsJson`
emits JSON to stdout instead of PowerShell objects. `-OutDir` writes one
`<TestName>_<RunTimestamp>[_<Phase>].json` file per run.

## Known edge cases it already handles (found parsing the full historical `ModelTestingResults\113`
tree, 993 runs, before committing)

- **Empty (0-byte) `agent.log`** from a crashed/killed run before the logger flushed its first
  line — `Get-Content -Raw` returns `$null`, not `""`, for a genuinely empty file, which crashes
  `[regex]::Matches($null, ...)`. Skipped with a warning, not fatal to the whole batch.
- **`PlanImplementVerify`-style nested phase folders**
  (`<TestName>\<RunTimestamp>\<plan|implement|verify>\agent.log`) — one extra directory level
  versus every other test's flat `<TestName>\<RunTimestamp>\agent.log` layout. Detected via the
  phase-name leaf folder; `TestName`/`RunTimestamp` still resolve correctly and the phase is
  captured in a `Phase` field.
- **Duplicated-timestamp archiver quirk**: some `PlanImplementVerify` runs have an extra
  `<TestName>\<ts>\<ts>\<phase>\agent.log` wrapping (two timestamp folders back to back) — a
  pre-existing archiver artifact, not something the parser silently papers over incorrectly;
  detected by pattern-matching two consecutive timestamp-shaped folder names and collapsing to the
  real `RunTimestamp`.

## Two PowerShell gotchas hit while building it

- **`[ordered]@{}` + integer keys**: assigning `$orderedDict[$intKey] = ...` when `$intKey` is an
  `[int]` not yet present is interpreted by `OrderedDictionary`'s indexer as a **positional insert
  index**, not a dictionary key — throws `ArgumentOutOfRangeException` once that "index" exceeds
  the current Count. Use a plain `@{}` hashtable for any dictionary keyed by turn number (or any
  non-positional key), and re-sort by the key afterward if order matters for output.
- **`-match`/`-notmatch` default to non-Singleline**: `.` does not match `\n` unless the pattern
  includes the inline `(?s)` flag (or `[regex]::Match(..., [RegexOptions]::Singleline)` is used
  directly). Silently truncates a captured group to just its first line whenever the underlying
  text is legitimately multi-line — e.g. this log format's reasoning text, `\n`-escaped JSON
  source content, and pasted user prompts are all frequently multi-line within one logical record.

See also [reference_model_eval_procedure.md](./reference_model_eval_procedure.md) for the
front-door `roslynsentinel-modeleval.ps1` script this pairs with (that one *runs* the batches; this
one *analyzes* the resulting logs).
