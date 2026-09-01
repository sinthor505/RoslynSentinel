# Blocking error — `Git(operation: "stage")` silently ignores untracked files

**Status:** OPEN — reported per docs/current/feedback_dogfood_mcp_blocking_errors.md, waiting for fix/confirmation.

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
