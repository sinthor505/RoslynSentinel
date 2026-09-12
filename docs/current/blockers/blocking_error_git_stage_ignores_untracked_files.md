# Blocking error — `Git(operation: "stage")` silently ignores untracked files

**Status:** OPEN, but **the diagnosis below is wrong** — see the 2026-09-12 amendment first.

## Amendment 2026-09-12 — could not reproduce; the real defect is different

Re-tested on current master. `Git(operation:"stage", files:"a,b,c")` **does** stage untracked
files correctly — seven paths including four brand-new ones staged in one call, with nothing
dropped and nothing extra swept in.

Note the repro below calls the parameter **`paths:`**. The tool's actual parameter is **`files:`**
(`paths:` exists, but only on `operation:"diff"`). The overwhelmingly likely explanation is that
the original call passed `paths:` to `stage`, where it was **silently ignored** — so the tool fell
back to its default of staging tracked changes only, which matches the reported symptom exactly.

That makes this an *unknown-parameter-silently-ignored* defect, not an untracked-file defect, and
it is a worse bug than the one originally filed: a mistyped or misremembered parameter name should
be a hard validation error naming the valid parameters, never a silent behaviour change. A weak
model that confuses `paths`/`files` between two operations of the same tool gets a `success:true`
with the wrong result — the silent-wrong-behaviour class that is hardest to recover from.

**A separate, confirmed defect found the same day:** `stageAll:true` silently **overrides** an
explicit `files` list. Calling `stage` with five named files plus `stageAll:true` ran `git add -A`
and staged eleven, including unrelated pre-existing untracked docs. Two parameters that each
determine the file set, with one silently winning, is the actual footgun here.

Both are covered in the fix brief; see `blocking_error_git_requires_loaded_solution.md` and the
`Git` entry in `docs/current/TODO.md`. The original write-up is kept below for the record.

---

**Original status:** OPEN — reported per docs/current/feedback_dogfood_mcp_blocking_errors.md, waiting for fix/confirmation.

## Symptom

`Git(operation: "stage", paths: ...)` returns `success:true` and a `staged` list, but that list
only ever contains files that were already tracked-and-modified. Any path that is currently
**untracked** (a brand-new file) is silently dropped — not staged, not reported as an error, not
flagged as a partial failure.

## Repro

Working tree state before the call (`git status`):
```
staged: []
unstaged: []
untracked: [
  "RoslynSentinel.Basic/TestRunEngine.cs",
  "RoslynSentinel.Common/GroupedCountSummary.cs",
  "docs/current/plan-runtest-tool-v1.md",
  "docs/current/project_cs0122_fix_confirmed_2run_batch.md",
  "docs/obsolete/blockers/blocking_error_applydiff_silent_noop_false_success.md"
]
modified (unstaged): [
  "RoslynSentinel.Common/DiffEngine.cs",
  "RoslynSentinel.Common/ToolEnums.cs",
  "RoslynSentinel.Tests.Basic/DiffEngineTests.cs",
  "docs/current/project_applydiff_capable_agent_feedback.md"
]
```

**Call 1** — comma-separated mixed list including untracked + tracked-modified paths:
```
Git(operation: "stage", paths: "RoslynSentinel.Basic/TestRunEngine.cs,RoslynSentinel.Common/GroupedCountSummary.cs,RoslynSentinel.Common/ToolEnums.cs,docs/current/plan-runtest-tool-v1.md")
```
Result: `success:true`. Resulting `staged` list = the 4 *tracked-modified* files
(`DiffEngine.cs`, `ToolEnums.cs`, `DiffEngineTests.cs`, `project_applydiff_capable_agent_feedback.md`)
— none of which were in my requested path list except `ToolEnums.cs`. The untracked files
(`TestRunEngine.cs`, `GroupedCountSummary.cs`, `plan-runtest-tool-v1.md`) were not staged and
remained in the `untracked` bucket.

**Call 2** — single untracked-only path, to isolate whether the mixed list or the comma-format was
the cause:
```
Git(operation: "stage", paths: "RoslynSentinel.Basic/TestRunEngine.cs")
```
Result: `success:true`. Staged set is **unchanged** — still the same 4 tracked-modified files as
before, `TestRunEngine.cs` still shows up under `untracked`. No error, no warning, no indication
the requested path was not honored.

## Impact

`Git(operation:"stage")` cannot be used to stage new files at all in the current build — only
pre-existing tracked files with local modifications. Since `success:true` is returned regardless,
an agent has no signal that the stage was a no-op for its actual target and may proceed to
`commit` believing new files are included when they are not (silent under-commit — e.g. committing
`ToolEnums.cs`'s change without the new `TestRunEngine.cs`/`GroupedCountSummary.cs` files that
depend on it, or without the plan doc it accompanies).

## Confirmed NOT the cause

- Not a comma-format parsing issue — reproduced identically with a single bare path.
- Not specific to one file — reproduced for both a `.cs` file and (implicitly, same call) a `.md`
  file in the mixed-list case.
- Not a stale-server issue — this is the same session/server instance that just successfully
  applied the `ApplyDiff` hunk-header fix moments earlier (workspaceVersion 8→9 in between).

## What I did NOT do

Per policy, did not fall back to a non-MCP `git add`/Bash/PowerShell to work around this — staging
in the repo the agent is dogfooding against is not "the only way to fix/implement" the actual task
(RunTest tool implementation), so the narrow bypass exception does not apply here. Paused instead
of guessing at a workaround (e.g. re-trying with absolute paths, `git add -A` semantics, etc.)
since further blind retries risk landing on an accidental correct-looking result without actually
understanding the gap.

## Next step

Waiting for confirmation/fix before resuming. Once resolved, move this file to
`docs/obsolete/blockers/`.
