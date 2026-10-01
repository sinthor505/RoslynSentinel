# Git(commit, scope: listed) cannot commit already-staged deletions/renames or tracked files under a gitignored path

Status: RESOLVED 2026-09-30 (found 2026-09-29 while committing the project reorganization, commit `354158e`).

## Symptom

The dog-food hook (`.claude/hooks/enforce-dogfood.ps1`) refuses any `Git(commit)` that lacks an
explicit `scope: "listed"` plus `files`, even when the index already holds exactly the intended
change set. With `scope: "listed"`, `CommitAsync` first calls `StageAsync`, which runs
`git add -- <files>` (`RoslynSentinel.Tools.Basic/GitImpl.cs`, `StageAsync`, the
`GitStageScope.listed` case, error returned at the `git add failed:` line ~830). Two classes of
path make that `git add` exit non-zero, so the commit never runs:

1. **A path whose deletion is already staged** (e.g. the old side of a rename staged earlier via
   `Git(stage, scope: all)`). The path is in neither the index nor the working tree, so git
   reports:

   ```
   git add failed: fatal: pathspec 'RoslynSentinel.Tests.ModelEval/AgentLoop/AgentSystemPrompts.cs' did not match any files
   ```

   Listing both sides of every rename, which is the natural way to name "all these files",
   guarantees this failure after any prior `stage`.

2. **A tracked file under a gitignored directory** (here: `TestResults/**.coverage` and
   `RoslynSentinel.Tests.Asyncify/TestResults/asyncify.trx`, tracked before `TestResults/` was
   added to `.gitignore`, now deleted on disk). `git add` stages the change but exits 1 with the
   advisory:

   ```
   The following paths are ignored by one of your .gitignore files:
   RoslynSentinel.Tests.Asyncify/TestResults
   TestResults
   hint: Use -f if you really want to add them.
   ```

   The tool treats the non-zero exit as failure and aborts before `git commit`. There is no
   input that gets these paths committed through the tool while the hook is active.

## Secondary defects

- **A failed call mutates state.** In case 2, `git add` had already staged all 292 changes
  before exiting 1, so the "failed" commit left the index fully staged. The next attempt then
  hit case 1. A `GitError` result does not mean nothing changed.
- **The error hides the cause.** In case 2 the ignored-path advisory is buried under ~300 lines of
  `LF will be replaced by CRLF` warnings, and the result was large enough (31878 bytes) to be
  offloaded to a large result, so the actual reason was not visible inline.

## Workaround used

1. `Git(unstage)` with no paths (plain mixed `git reset`) so that deletions become working-tree
   changes again. `git add -- <deleted path>` then stages the deletion correctly.
2. `Git(commit, scope: listed, files: [...])` with the three ignored-path artifacts removed from
   the list.

Result: 400 paths committed in `354158e`. The three `TestResults` deletions remain unstaged and
still cannot be committed through the tool.

## Suggested fix (not implemented)

- In the `listed` stage path, split the list by index state. Use `git add -- <untracked paths>` for
  new files only. Tracked paths, including deletions and paths under ignored directories, are
  handled by `git add -u -- <paths>` or by the pathspec on `git commit -- <paths>`, which already
  records tracked deletions. Skip paths that are already staged and absent on disk instead of
  passing them to `git add`.
- Treat `git add` exit 1 as success when stderr contains only the ignored-path advisory and every
  listed path is tracked. Otherwise, name the ignored paths in the error and state which call
  would work.
- Filter `LF will be replaced by CRLF` warnings out of `GitError` messages so the real cause stays
  inline and under the offload threshold.
- Hook: consider allowing a scope-less commit when the index was built by a prior
  `Git(stage, scope: listed)` in the same session, or accept a commit whose staged set exactly
  matches `files`.

## Regression test to add with the fix

In `RoslynSentinel.Tests.Battery/GitToolsSmokeTests.cs`: stage a rename with `scope: all`, then
commit with `scope: listed` naming both sides, and assert success. Separately, commit the deletion
of a tracked file under a gitignored directory with `scope: listed`, and assert success.

## Resolution (2026-09-30)

Fixed by `docs/current/plans/plan_git_tool_listed_scope_and_shell_parity.md`, delivered in phases:

| Phase | Commit | Summary |
| --- | --- | --- |
| 0 | `d8af128` | Temp-repo test helpers (capture, staged/head paths, gitignore, CRLF). |
| 1 | `b692dfc`, `d439eec` | Listed-scope stage/commit rewrite: paths classified (untracked / in index / head-only / missing) so only untracked paths go through `git add`; staged deletions and tracked-but-ignored paths now commit via `git commit --only`; missing, outside-repo and ignored paths are refused atomically before any mutation; `CleanGitStderr` strips LF/CRLF noise and caps length; `RemainingStaged` reported; `files` implies `scope=listed`. Regression tests cover both repro cases from this doc. |
| 2 | `59e3599` | Dog-food hook: scope-less, file-less commit allowed when something is staged; `files`/`paths` alone count as an explicit listing. |
| 3 | `c124b51` | Read-side parity: NUL-safe status with rename origin and conflict labels, `maxEntries`, `nameOnly`, `stat`. |
| 4 | `e3852f9` | Single `ref` parameter, reset requires an explicit ref, idempotent `createBranch`, `pull --no-rebase`, non-interactive git env, amend of a pushed HEAD refused. |
| 5 | `0ecc527` | `abort` operation, `InProgress` in status, conflict messages mention abort, `mainline` for merge-commit revert. |
| 6 | `002384a` | Specific error codes (`GitPathNotFound`, `GitIgnoredPath`, `GitNothingToCommit`, `GitOperationInProgress`, `GitRefRequired`) with `Detail`; `GitError` kept as fallback. |

Secondary defects: a failed call no longer leaves a half-staged index (atomic refusal before `git add`),
and the real cause stays inline because CRLF warnings are filtered from `GitError` messages.
The suggested "accept a commit whose staged set matches" hook idea was implemented as the Phase 2 rule above.
