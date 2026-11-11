# `Git(operation: commit)` returns another session's CommitHash and Message when commits race

**Status:** OPEN 2026-11-10. Root cause traced to source (HEAD readback after `git commit` in `GitImpl.CommitAsync`); no fix built, no regression test yet.

## What was being attempted
Two agents (separate MCP server instances sharing one working tree) each ran
`Git(operation: commit, scope: listed)` with an explicit file list, about one second apart on 2026-11-10:

- Orchestrator (phase E group 3, 7 files): commit `2c45fa49bc435a15cddeead4cbdf325a659ca395`, 17:25:46.
- Main session (proposal doc, 1 file): commit `6724a834be2ae4430e08b9bed29bb5446047e286`, 17:25:47.

Both commits landed correctly. `Git(show, nameOnly)` confirmed each commit contains exactly its own files.

## The exact symptom
The orchestrator's `Git(commit)` response was a success (`IsError = false`) but carried the other
session's commit in `CommitHash`:

```text
CommitHash: 6724a834be2ae4430e08b9bed29bb5446047e286   (returned to the orchestrator)
CommitHash: 2c45fa49bc435a15cddeead4cbdf325a659ca395   (what the orchestrator actually created)
```

The orchestrator reported the wrong hash until it cross-checked with `Git(show)`. `Message` in the
same response is, by the same mechanism, expected to describe the other commit (see "What is and is
not confirmed"). No error text is produced; the tool gives no signal that the data is wrong.

## Source trace
`RoslynSentinel.Tools.Basic/GitImpl.cs`, `CommitAsync` (starts line 3103):

- Line 3276: `git commit` runs via `RunGitAsync(gitRoot, commitArgs, ...)`; the exit code is checked at 3277.
- Line 3309: after success, a separate process runs `git rev-parse HEAD` to obtain `CommitHash`. This
  is not tied to the commit just made: it reads whatever HEAD is at that moment.
- Line 3328: a second separate process runs `git log -1 --format=%B` (again implicitly HEAD) to fill
  `Message`.
- Line 3333: a third process runs `git diff --cached --name-only` to fill `RemainingStaged`; this
  reads the shared index after the commit, so it can also include or omit paths because of the other
  session's staging activity.
- Line 3344: the result is assembled from these three independent readbacks.

Any commit that lands between line 3276 and lines 3309/3328 makes `CommitHash` and `Message` describe
the other commit. The window is several process spawns wide (three `git` invocations on Windows), so a
~1 second gap between two commits is enough. Two separate server processes are involved, so an
in-process lock (for example around `CommitAsync`) cannot close the race.

`RevertAsync` (starts line 3389) has the same pattern: `git revert` at line 3426, then
`git rev-parse HEAD` at line 3435 into `newHash`, returned as `CommitHash = newHash` at line 3442
(only when `noCommit` is false). A racing commit between the revert and the readback returns the wrong
hash there too. Its `Message` comes from `revertRaw.Stdout`, so it is not affected by HEAD readback.

Other `rev-parse HEAD` hits in the file (lines 2329 and 3648) use `--abbrev-ref` for the branch name,
not for a commit hash, and are out of scope for this defect.

## What is and is not confirmed
Confirmed:
- Both commits exist, each with exactly its own files (`Git(show, nameOnly)`).
- The orchestrator's response returned the main session's hash `6724a834...`.
- The source readback pattern above (lines 3309, 3328, 3333, 3435-3442), read directly from
  `GitImpl.cs`.

Not confirmed:
- That the orchestrator's `Message` field also described the other commit. It follows from line 3328 but
  was not captured verbatim in the report.
- That `RevertAsync` has produced a wrong hash in practice; this is inferred from source only.
- The exact ordering of the three readbacks relative to the other session's commit; the observed
  result is consistent with the other commit landing between lines 3276 and 3309.
- Whether the libgit2-backed reader seam (used by other Git operations in recent commits) is involved.
  `CommitAsync` here shells out via `RunGitAsync`, so it was not traced further.

## Why this matters
CLAUDE.md tells agents to "report hashes exactly as `Git` returns them". A wrong hash therefore
propagates into reports, plan trackers and `Co-Authored-By`/`Plan:`/`Closes:` follow-up work, and the
response is a plain success, so nothing prompts a cross-check. The environment is explicitly
multi-session ("Peer sessions" in CLAUDE.md), so concurrent commits are an expected case, not an edge
case. Severity: wrong data returned, not data loss; the commits themselves are correct.

Workaround until fixed: when another session may be committing, cross-check the returned hash with
`Git(log)` or `Git(show)` before reporting it.

## Suggested direction (not implemented)
Derive the hash from the commit itself instead of reading HEAD afterwards:

1. Parse the `[<branch> <abbrev>]` summary line of `git commit` stdout and expand it with
   `git rev-parse --verify <abbrev>^{commit}`. An abbreviated hash could in principle be ambiguous
   with a racing commit, but `--verify` fails on ambiguity rather than silently choosing, which is
   preferable to the current behaviour. Alternatively, resolve `HEAD` before the commit and, after it,
   walk back from HEAD to the commit whose parent is that pre-commit value (handles a racing commit
   landing on top, but not one landing in between; the stdout parse is simpler and more direct).
2. Read `Message` with `git log -1 --format=%B <hash>` using that resolved hash, not implicit HEAD.
3. Apply the same fix to `RevertAsync` (derive the new hash from the `git revert` output, or from the
   resolved parent chain, instead of `rev-parse HEAD`).
4. `RemainingStaged` reads the shared index and can legitimately differ if another session stages
   concurrently; consider documenting that, since it cannot be made exact without a repo lock.

Add a regression test that commits concurrently from two processes (or simulates an intervening
commit between `git commit` and the readback, for example through a seam or a hook) and asserts that
the returned `CommitHash` resolves to a commit whose message equals the submitted message and whose
file list equals the submitted file list.
