# Git MCP tool cannot target a repo/worktree other than the currently loaded solution's

**Status:** OPEN — blocking a merge operation, task halted per CLAUDE.md dog-fooding policy.

## What happened

Attempting to merge master into the PlanStepRunner run branch
`eval-defect-remediation-v2-auto-20260912-223709-796` (to bring in two same-day upstream fixes,
commits `85341e94` and `255363d7`, before resuming plan step 5) required inspecting the state of
that branch's worktree at
`C:\Users\Administrator\source\repos\RoslynSentinel-TestRuns\PlanStepRunner\20260912-223709-796\04-phase2-repro-and-trivia-intent\Worktree`
— a different git working tree from the one the MCP server's loaded solution operates on (this
session's loaded solution is `RoslynSentinel.slnx` in the main repo, currently on `master`).

`git -C "<worktree path>" merge master` failed:

```
error: Your local changes to the following files would be overwritten by merge:
	RoslynSentinel.Basic/RefactoringEngine.cs
Please commit your changes or stash them before you merge.
Aborting
```

This is expected — that worktree has uncommitted changes left over from step 04's halted run
(the abandoned `TriviaEditIntent`/`Scratch.cs` fix attempt documented in
`blocking_error_plan_step_unreachable_verification_gate.md`). Before deciding whether to discard,
stash, or commit those changes, I needed to see exactly what they are — `git status --short` on
that worktree.

`git status`/`status --short --branch` on that worktree path is blocked by
`.claude/hooks/enforce-dogfood.ps1`, which requires `status`/`log`/`diff`/`stage`/`commit`/`revert`
to go through the MCP `Git` tool. But the MCP `Git`(`operation: "status"`) tool call, when
attempted, returned the **main repo's** status (`branch: "master", isClean: true`) — it has no
parameter to target an arbitrary other repository or worktree path. There is no way to ask it
"what is the status of the repo at `<this other path>`."

## Root cause

The `Git` MCP tool operates exclusively on whatever solution/repo is currently loaded into the
server's workspace (`PersistentWorkspaceManager`'s active solution). It has no `repoPath`/
`workingDirectory`-style parameter. The `enforce-dogfood.ps1` hook, meanwhile, pattern-matches on
the git subcommand name (`status`, `log`, etc.) regardless of what path or `-C`/`--git-dir` flag
the shell command targets — it cannot distinguish "operating on the loaded solution's repo" from
"operating on an unrelated repo/worktree elsewhere on disk."

Together this means: **there is no compliant way to inspect git status/log/diff for any repo other
than the one currently loaded**, including PlanStepRunner's own per-step worktrees, which are a
core, expected part of this tool's own workflow (per CLAUDE.md: "`*/Worktree/` folders under a
PlanStepRunner run are harness clones"). The CLAUDE.md exemption list (branch, push, checkout,
worktree, stash) covers *operations*, not *the fact that a worktree isn't the loaded solution* —
`status`/`diff` on a worktree path falls in neither an MCP-covered case (tool can't do it) nor a
documented shell exemption (the op itself, `status`, is nominally covered).

## What unblocks it

Options, not a decision:

1. **Add a `repoPath`/`workingDirectory` parameter to the `Git` MCP tool** for `status`/`log`/`diff`,
   so PlanStepRunner worktree inspection can go through it like everything else. This is the
   dog-fooding-consistent fix and the most durable one, since worktree inspection is a recurring
   need for this exact tool (per `worktree-diff-sentinel`'s whole purpose).
2. **Extend the hook's exemption list** to explicitly allow `status`/`log`/`diff` (read-only,
   non-mutating) via shell when the path is outside the currently loaded solution's root — mutating
   ops (`stage`/`commit`/`revert`) would still need to stay blocked or gain the same `repoPath`
   parameter, since those are exactly the ones this policy most needs to chokepoint.
3. **Load the target worktree as the active solution temporarily**, then use the `Git` tool
   normally, then reload the original solution — works today with zero tool changes, but is heavy
   (a full `LoadSolution` round-trip) for a one-off status check, and per existing memory
   (`feedback_loadsolution_does_not_rebind_server_binary`) loading a different solution has its own
   history of surprising behavior worth being cautious about.

## Immediate consequence

The merge of master into the run branch is stalled. The uncommitted changes in the step-04 worktree
(likely `RoslynSentinel.Basic/RefactoringEngine.cs` plus `Scratch.cs` from the abandoned fix
attempt) have not been inspected, so I have not discarded, stashed, or committed them — per the
safety protocol, unfamiliar uncommitted state should be investigated, not blindly overwritten.
Ending the turn here rather than rewording the blocked command to slip past the hook.
