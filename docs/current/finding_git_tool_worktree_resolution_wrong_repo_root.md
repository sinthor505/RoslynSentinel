# Finding: `Git` tool's status/diff/commit resolve against the wrong repo root inside a worktree — silently, not an error

**Status:** confirmed tool defect, not yet fixed. Full writeup already exists as
`docs/current/blockers/blocking_error_git_tool_commit_reports_clean_tree_worktree.md` — this
doc is a pointer, not a duplicate.

## Summary

Reproduced 6 times across self-run `manual-selfrun-20260914-004605` (steps 02, 07, 08, 09, 10,
12): `Git(operation: "status"/"diff"/"commit")`, called from inside a worktree, silently
resolves against the **primary repo's checked-out branch** (`master`) instead of the actual
worktree path — reporting a plausible, well-formed, completely wrong result (`isClean: true`,
empty diff, or `nothing to commit, working tree clean`) with no error, even while shell
`git status --short` in the same directory shows real staged/unstaged changes. Suspected root
cause: `Git`'s status/diff/commit operations resolve their repo root from a fixed/default
location rather than `_workspaceManager.GetSolutionRoot()`/the currently loaded worktree path.

This is **distinct** from the already-tracked `docs/current/TODO.md` entry ("`Git` tool missing
branch/push/checkout/worktree/stash — forces a shell fallback"), which is about operations `Git`
doesn't implement at all — this defect is about existing operations silently resolving against
the wrong repo.

Workaround used throughout: shell `git`, per the standing session-scoped bypass authorization,
after independently confirming real state via `git status --short`.

## Reference

- Full detail, impact, and the `commit`-blocking reproduction:
  `docs/current/blockers/blocking_error_git_tool_commit_reports_clean_tree_worktree.md`.
- Earlier reproductions (status/diff only, steps 02/07/08/09/10/12):
  `C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md`.
- Related but distinct: `docs/current/TODO.md` (`Git` tool missing branch/push/checkout/worktree/
  stash entry).
