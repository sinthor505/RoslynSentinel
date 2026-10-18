# Blocker workflow

**Status:** CURRENT 2026-10-17. Procedure moved out of CLAUDE.md; the trigger rule ("a tool failure is a
blocking finding") stays there.

1. Before investigating or fixing, check `MEMORY.md`, `git log`, `docs/current/CLOSED.md` and
   `docs/current/proposals/` for prior attempts - the gap may already be fixed, or tried and reverted.
2. Write `docs/current/blockers/blocking_error_<slug>.md` (slug from the real symptom, never generic;
   template in `docs/current/templates/`). The `blocker-writer` agent can draft it.
3. Dispatch an `implementer` to fix it (obeying the slice contract; `implementer-senior` if over the
   limits), confirm the fix, then restart the server with `McpServerControl` (see
   [reference_live_server_freshness.md](reference_live_server_freshness.md)) before resuming the original task.
4. When fixed: add a regression test, compare against the pre-existing-failure baseline, then in the
   same commit add a resolution note (root cause, fix, commit hash, tests) and move the doc from
   `docs/current/blockers/` to `docs/current/blockers/resolved/`.
5. If the workspace looks stale (a fix appears missing), reload the solution (`LoadSolution`) before
   concluding anything.
6. If test runs fail on file locks, kill only stray `testhost` processes that belong to your own session
   (identify by PID/command line); other sessions may share this machine.
