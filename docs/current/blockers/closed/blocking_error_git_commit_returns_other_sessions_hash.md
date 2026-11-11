# `Git(operation: commit)` returns another session's CommitHash and Message when commits race

**Status:** FIXED 2026-11-10 in <this slice> (commit hash: TODO orchestrator fills in after committing). `CommitHash`/`Message` of commit and `CommitHash` of revert are now derived from the commit itself, not from a HEAD readback.

## Resolution note
Root cause: `GitImpl.CommitAsync` ran `git commit`, then read HEAD back with `rev-parse HEAD` and
`log -1 --format=%B` (and `RevertAsync` did the same `rev-parse HEAD`), so a commit by another server
instance sharing the working tree that landed in between was reported as this call's result.

Fix (`RoslynSentinel.Tools.Basic/GitImpl.cs`, `GitTools.cs`):
- The hash comes from the `[<branch> <abbrev>]` summary line that `git commit` / `git revert` print for the
  commit they created (`DeriveNewCommitHashAsync`): the LAST line matching `^\[[^\]\r\n]*? ([0-9a-f]{4,40})\]`
  is expanded with `git rev-parse --verify --quiet <abbrev>^{commit}`, which fails on ambiguity instead of choosing.
  Root commits (`[master (root-commit) abc]`), detached HEAD (`[detached HEAD abc]`), amend, bracketed subjects and
  hook output are covered by tests.
- `Message` is read with `git log -1 --format=%B <fullhash> --` (explicit hash).
- Fallback when the summary is unusable: HEAD is accepted only if its parent chain proves it is the new commit
  (parent == pre-command HEAD; for amend, same parents as the old HEAD and a new hash; no parent on an unborn
  branch), judged against HEAD read before the command. Otherwise `CommitHash` is empty (never a guess), a log
  warning is written and `GitTools` adds a Warning finding to the success result. No DTO changed.
- `RemainingStaged` still reads the shared index after the commit and can legitimately differ if another session
  stages concurrently (it cannot be made exact without a repo lock); documented in a code comment.

Tests: `RoslynSentinel.Tests.Tools.Basic/GitCommitHashRaceTests.cs` (13 tests). A post-commit hook in the fixture repo
moves HEAD onto a foreign commit inside `git commit` / `git revert`, reproducing the race window deterministically;
each test asserts the precondition (HEAD really is the foreign commit) and that the returned hash resolves to the
commit with the submitted message and exactly the submitted files.

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
same response is, by the same mechanism, expected to describe the other commit. No error text is
produced; the tool gave no signal that the data was wrong.

## Source trace (before the fix)
`RoslynSentinel.Tools.Basic/GitImpl.cs`, `CommitAsync`:

- `git commit` ran via `RunGitAsync`; after success, a separate process ran `git rev-parse HEAD` for
  `CommitHash`, a second ran `git log -1 --format=%B` (implicitly HEAD) for `Message`, and a third ran
  `git diff --cached --name-only` for `RemainingStaged`.
- Any commit landing between the commit and those readbacks made `CommitHash` and `Message` describe the
  other commit. Two separate server processes are involved, so an in-process lock could not close the race.

`RevertAsync` had the same pattern (`git revert`, then `git rev-parse HEAD` into `CommitHash`). Its `Message`
came from `revertRaw.Stdout`, so it was not affected by the HEAD readback.

## Why this matters
CLAUDE.md tells agents to "report hashes exactly as `Git` returns them". A wrong hash propagated into reports,
plan trackers and `Co-Authored-By`/`Plan:`/`Closes:` follow-up work, and the response was a plain success, so
nothing prompted a cross-check. The environment is explicitly multi-session ("Peer sessions" in CLAUDE.md), so
concurrent commits are an expected case. Severity: wrong data returned, not data loss.
