# Cross-session write journal: attribute file changes to the server instance that made them

**Status:** PROPOSED 2026-11-05, REVISED 2026-11-09. Attribution layer only; behaviour on drift is now owned by plan_stale_read_guard_followups.md (F1 option d) and a cross-process write lock (F7). Read "Revision 2026-11-09" first; it overrides the sections below where they disagree. File:line citations in the original text come from a 2026-10-13 audit and are stale.

## Revision 2026-11-09 (owner-approved direction)

- **Premises that changed.** `ToolCallContext` (`Common/ToolCallContext.cs`, AsyncLocal, set at the tool
  boundary) now carries agent id, agent type, tool name, reason and start sequence for every call, so the
  "no AsyncLocal; add a request filter" plumbing below is already built. Operation blob items now carry
  `AppliedByReason`, `AppliedByAgentId`, `AppliedBySessionId` and `AppliedByAgentType`
  (`Common/OperationItemRecord.cs`, filled in `OperationBlobWriter.WriteAsync`), and `NotFoundEvidence`
  already reads them across windows. Still true of blobs: written after the files are on disk, coverage gaps
  (see "Blob coverage" below), full source per item, no retention, no schema version. The journal is still
  the right attribution store; blobs stay the payload/undo store and an optional fallback for older history.
- **Append point.** Inside `ApplyProposedChangesAsync`, reading `ToolCallContext.Current`, before the files
  are written, and inside the cross-process write lock (below) so a sibling that takes the lock next always
  finds the entry. No new parameters, no new request filter.
- **Lookup point.** NOT in `OnFileSystemChanged`: the watcher callback must stay cheap or the 64 KB buffer
  overflows (`reference_file_watcher_and_drift_detection.md`). Look up lazily when building a refusal
  (`StaleRead` and the per-file drift refusal) and in `ExternalFileDrift(List/Status)` / `Git(status)`, or
  at the latest in the debounce step.
- **Attribution hook as built (2026-11-09).** The message builder for refusals is `WriteRefusal.StaleReadError` in
  `RoslynSentinel.Common/WriteRefusal.cs`, and it carries the marker `// TODO per-file attribution: name the writer that changed
  each file once proposal_cross_session_write_journal.md lands`. That is the one place to call the journal lookup: it receives
  `result.FailedFiles` (path -> "changed on disk since you last read it" / "changed in the workspace since you last read it"), so the
  lookup is per failed file and the writer's name can be appended to each entry. `FoldPendingError` in the same file has no
  marker (a `FoldPending` file is not attributed to a writer; it only says the workspace lags disk). The drift halt this
  proposal's Motivation talks about is gone (step 8c of plan_stale_read_guard_followups): there is no `SessionHalted` from drift to
  explain any more, so the journal's value is the explanation text on `StaleRead` and in `ExternalFileDrift(List/Status)` /
  `Git(status)`. The cross-process write lock and holder sidecar (F7, plan steps 9a-9f) ARE built; see the next bullet and "Lock and holder sidecar as built".
- **Auto-adopt vs annotate: settled as annotate only.** Under F1 option (d) a sibling's change to a file the
  agent never read or edited never refuses or halts (the debounce fold already adopts the content); a change
  to a file it read or edited is refused per agent with `StaleRead` whoever wrote it. The journal supplies
  only the explanation line ("changed by session <sid>, agent <id>, via <tool> <age> ago: <reason>").
- **Write lock (complementary, not an alternative).** The original rejected a lock file because the goal was
  explanation. The lock's goal is different: make the stale check atomic with the write across servers and
  stop two servers interleaving writes. Design: an OS-held exclusive handle on `.roslynsentinel/write.lock`
  in the working tree (released by the OS if the process dies), holder metadata (session, agent, tool,
  reason, since) for the busy message, bounded wait then a non-latching `WriteLockBusy` error, held from
  pre-image capture through the journal append, file writes and blob write. Per working tree (files are per
  tree); the journal can stay under the git common dir. `.roslynsentinel/` is gitignored as a whole since
  2026-11-09. Specified in plan_stale_read_guard_followups.md F7. AS BUILT the lock does NOT cover the blob write
  (it is released when the chokepoint returns) and there is no journal append yet; see the next bullet.
- **Lock and holder sidecar as built (2026-11-09) - the attribution hook for `WriteLockBusy`.**
  [plan_stale_read_guard_followups.md](../plans/closed/plan_stale_read_guard_followups.md) Step 9 (9a-9f) built
  `RoslynSentinel.Common/CrossProcessWriteLock.cs`: an OS-held `FileShare.None` lock on
  `<solution root>/.roslynsentinel/write.lock`, taken inside `_solutionLock` after `CheckConcurrentEditConflict` and
  `RevalidateAfterForeignWriteAsync` and before pre-image capture, released in the chokepoint's `finally` after the in-memory
  resync. The holder is written to a SIDECAR `write.lock.holder.json` (`WriteLockHolder(SessionId, AgentId, ToolName, Reason,
  SinceUtc)`; a waiter cannot read the exclusive holder's lock file) and deleted on release; a waiter that times out (10 s at the
  chokepoint) reads it best-effort and `WriteRefusal.TryMap` returns `WriteLockBusy` naming agent, tool and since-time, or "holder
  unknown" when the sidecar is missing. What this gives the journal: (1) the busy message is the first cross-session
  attribution the server emits, and it is LIVE state (who holds the lock now), not history; (2) the sidecar is the one place a
  journal reader can learn "a sibling is writing right now" without scanning entries; (3) the lock is the natural append point:
  an entry appended inside it before the files are written is always visible to the sibling that acquires next. What it does
  NOT give: it is not a record of who changed a file (the sidecar is deleted on release, one holder at a time), the blob write
  happens after release so a sibling can read a file before its blob exists, and the lock serializes only RoslynSentinel
  processes (editors, `git` and shell writes take none; `EolFileWriter` and the no-solution format fallback are excluded with
  `// TODO` markers). Reference: `reference_file_watcher_and_drift_detection.md` section 7.
  The journal must not take a second OS lock for its append: the lock is not re-entrant (rule b in `reference_architecture_map.md`,
  "Write chokepoint"), so the append belongs inside the existing chokepoint hold, not in its own acquire.
- **Writes are not atomic today.** `FileIoHelper.WriteAllTextAsync` is `File.WriteAllTextAsync` under the
  in-process `FilePathLock` (truncate, then write), so another process can read a partial file. F7 has the
  debounce fold take the write lock briefly before reading disk. Temp-file-plus-replace would also fix it,
  but the watcher treats Renamed events as drift, so that needs its own design.
- **Blob coverage (measured 2026-11-09).** 45 direct `ApplyProposedChangesAsync` sites in 9 production
  files; the chokepoint writes no blob itself. 35 reach a blob write (19 in the same method, 16 via a caller
  or Asyncify summary chain), 5 confirmed without (`UndoLastApply` WorkspaceFileEditImpl.cs:142,
  `RetryFailedChanges` -> PWM.RetryFailedChangesAsync, CommentingTools BulkComment seed phase :146,
  MsToolAugmentEngine sort-usings :623 and format :768), 5 untraced (AsyncifyTools
  RunHandlerConvertPhaseAsync cleanup applies :3268-3350). `GitImpl` writes no blob and bypasses the
  chokepoint; file-changing operations: stash apply/pop/push, reset --merge, reset soft/mixed, checkout,
  revert, pull, worktree add/remove. A chokepoint-level journal append covers all 45 sites; Git needs its
  own append.
- **Dropped open questions:** the AsyncLocal-coverage question (answered by `ToolCallContext`) and
  auto-adopt (above). Still open: Git mutations, liveness, retention, worktree root, clock handling.

## Motivation

Several RoslynSentinel server instances run against the same repo at once (one stdio process per
window, each launched from its own `<repoHash>-<8 hex>` instance folder by
`scripts/roslynsentinel-mcp-launch.ps1`, lines 141-177). They share a working tree but not memory:

- A server's `FileSystemWatcher` (`PersistentWorkspaceManager.SetupWatcher`, lines 709-725) cannot tell
  a sibling session's write from an editor or shell write. Both land in `_externalChanges`
  (line 956), and a later write to a flagged file halts the session (`SessionHalted`,
  lines 1509-1513). Self-writes are filtered by `_internalChanges` and `_knownFileHashes`, which are
  per-process, so a sibling's write is "external" by construction.
- `git status` shows dirty files with no indication of which session, or why. The `reason` argument
  every tool call must supply (`ToolCallReason`, mandatory, at least 10 chars with a space,
  `RoslynSentinel.Common/ToolCallReason.cs:14`) is discarded after the call.
- Existing operation blobs (`.roslynsentinel/operations/`) do not fill the gap: `Reason` is null in
  every blob inspected (0 matches for `"Reason": "` on disk; no blob-building path receives the tool
  `reason`), there is no session id, they are written after the files are on disk, and they carry
  full source text rather than hashes.

The goal is to let a server answer "who wrote this file, when, and why" and to tell a sibling write
from a genuinely external one.

## Proposal

### Journal location and layout (decided direction, details open)

One append-only JSONL file per server instance, under the git common dir so it is never a dirty file
and is shared by worktrees:

`<git-common-dir>/roslynsentinel/sessions/<sid>.jsonl`

`<sid>` is the instance folder name (`<repoHash>-<token>`), derived in C# from
`AppContext.BaseDirectory` (the exe lives in `<instance>\Advanced\`). Runs outside the launch wrapper
(HTTP fallback, test hosts) fall back to a generated GUID. A restarted instance gets a new token, so a
new sid, which is correct: its in-memory state is gone.

One writer per file means no cross-process locking and no corruption from concurrent appends. Readers
merge all files. The repo root is rejected as a location because the file would show as untracked and
needs a drift-detector exclusion, which is the problem being solved.

### Entry format

Line 1 is a header: `{"v":1,"sid":...,"pid":...,"processStartUtc":...,"binaryPath":...}`.
Later lines are entries:

`{"v":1,"ts":...,"tool":...,"reason":...,"changeId":...,"files":[{"path":...,"hashBefore":...,"hashAfter":...,"op":"write|delete|create"}]}`

- Hashes, not content. Content stays in the operation blobs, linked by `changeId` where one exists
  ("undo available").
- `reason` is capped in length and ASCII-sanitised.
- Git mutations log commit hashes so entries can later be marked resolved.

### Where the append happens

At the write chokepoint, `PersistentWorkspaceManager.ApplyProposedChangesAsync` (line 1439), **before**
files are written, with `hashAfter` computed from the content about to be written. Reasons:

- The blob writer runs after the apply returns ("outside the chokepoint by design",
  `docs/current/references/reference_code_file_write_paths.md:97`), so the file is on disk before any entry
  exists. A sibling's watcher event (500 ms debounce) can fire first and find nothing.
- Blob coverage is incomplete: no blob for `UndoLastApply`, `RetryFailedChanges`,
  `MsToolAugmentEngine` format/using-sort, and Git operations; the Asyncify/`AsyncBatchEngine` batch
  paths were only partly traced.

The chokepoint signature does not carry tool name or reason, and there is no `AsyncLocal` in the
codebase. Rather than add parameters across 50 call sites in 11 files, set an
`AsyncLocal<ToolCallContext>` (tool name, reason) once at the tool boundary, in a request filter
beside the existing `AddDriftFilter` (`ServiceRegistrationExtensionsBasic.cs:513`), and read it in the
chokepoint. When the context is absent (internal callers), log `tool: null`.

Git mutating operations (`GitImpl`: commit, restore, checkout, reset, revert, stash pop) bypass both
the chokepoint and the blobs and need their own append.

### Drift-detector integration

In `OnFileSystemChanged`, before `_externalChanges.Add` (line 956), hash the on-disk file and look it
up in the other sessions' journals:

- Equals another session's `hashAfter`: record as `sibling session <sid>: <reason>` and adopt the
  new content (refresh `_knownFileHashes`, reload the document) instead of flagging external. Whether
  to adopt automatically or only annotate is an open question below.
- No match: external (editor, built-in Edit, shell). The journal cannot name an external writer.

`hashBefore` makes interleaved edits by two sessions to one file visible, which a single hash cannot.

### Reporting

- `Git(status)` and `ExternalFileDrift(List/Status)` annotate each dirty file with last writer sid,
  reason, and liveness (live / dead / unknown). Files with no journal match read "external (no known
  session)".
- The journal records every server write, not only watched `.cs`/`.csproj`/`.sln`, so non-source
  files written via the chokepoint are attributed too (whether `WriteFile` on Markdown/JSON goes
  through the chokepoint is unverified).

### Liveness and cleanup

- Liveness: header `pid` + `processStartUtc` (start time guards against PID reuse). A missing instance
  directory also means dead, but a present one proves nothing: the launch sweep only removes folders
  at least 3600 s old with no live server exe (launch script lines 192-276, `$minimumAgeForSweep` at
  line 208; the comment there says 15 s, which disagrees with the code), so a dead session's folder can
  linger.
- Cleanup: drop a session's journal when the session is dead and every file it touched is clean
  (committed or reverted), so journal lifetime follows the git tree. A crashed session's entries stay
  until then, which is the point.
- Failure policy: any journal I/O failure degrades to a logged warning; it never blocks or halts an
  edit.

## Alternatives considered, not pursued

- **Shared SQLite DB (WAL) in `.git/roslynsentinel/`.** Gives transactions, atomic per-file leases
  and cheap queries. Rejected for now: adds a native dependency and a schema that must tolerate
  sessions running different binary versions; WAL does not work on network filesystems; stale rows
  need heartbeats and GC. Attribution and reconciliation need none of the atomicity. Revisit if the
  goal becomes conflict prevention (leases) rather than explanation. LocalDB was dismissed outright
  (Windows-only, install burden).
- **Journal in the repo root.** Shows as untracked, needs gitignore and a drift-detector exclusion.
- **Extend the operation blobs instead of a new journal.** Attractive (one writer, existing
  `changeId`, `UndoLastApply`/`GetOperationDetail` already read them) but: writes land after the
  files, coverage has the gaps listed above, no retention exists (blobs from 2026-06-08 remain),
  writes are not atomic, there is no schema version, and each blob holds full source so a hash lookup
  means scanning hundreds of large files. The blob `Reason` also needs the same `AsyncLocal` plumbing,
  so reuse saves nothing there. Keep blobs as the payload/undo store; link by `changeId`.
- **Add `toolName`/`reason` parameters to `ApplyProposedChangesAsync`.** 50 call sites in 11 files for
  data an ambient context carries equally well.
- **Shell/process-level locking or a lock file per repo.** Serialises sessions rather than informing
  them; wrong goal. (2026-11-09: adopted separately as F7 for atomicity, not attribution; see Revision.)

## Open questions

- Auto-adopt a sibling's write that matches a journal `hashAfter`, or only annotate and still
  require acknowledgement? Auto-adopt removes the false-positive halts but means trusting the journal.
- Clock handling: ordering across files relies on timestamps. Fine on one machine; does the repo ever
  live on a share?
- Does the `AsyncLocal` survive every path into the chokepoint (batch tools, background tasks,
  `RetryFailedChanges`)? Untraced.
- `Git` mutations: which hook point in `GitImpl`, and how to record resulting file hashes cheaply
  after a checkout/reset touching many files.
- Journal retention if a session dies with dirty files nobody ever commits.
- Worktrees: is `git rev-parse --git-common-dir` the right root for sessions in different worktrees
  of one repo, or should the journal be per-worktree?
- Whether the `Outcome` serialisation drift in old blobs (`"succeeded"`, `1`, `"Succeeded"`) breaks
  loading in `UndoLastApply`/`GetOperationDetail` is unverified; it affects only the `changeId` link,
  not the journal.

## Cost / risk

- Touches the write chokepoint (hot path, every edit): appends add I/O and must be cheap and
  non-blocking. A bug here can affect all mutating tools, so failure must degrade silently.
- New request filter and `AsyncLocal` at the tool boundary; the central filter pipeline order matters
  (see memory `reference_central_mcp_request_filter_pipeline`).
- Drift detector change alters when `SessionHalted` triggers; needs tests for sibling-match,
  no-match, and journal-unavailable cases.
- Adds on-disk state under `.git` that must tolerate concurrent versions of the format (hence the `v`
  field and ignore-unknown-fields readers).
- Reason text is model-written; it stays local under `.git` but should still be length-capped.

### Separate defects found during the audit (not part of this proposal)

Worth their own TODO entries:

- No retention for `.roslynsentinel/operations/` blobs.
- `.roslynsentinel/` is ignored only by the blanket `*.json` gitignore rule. (FIXED 2026-11-09: `/.roslynsentinel/` ignored.)
- `UndoLastApply` cannot undo a file creation (null `BeforeSource` is filtered out; returns
  `NoReversibleItems`), and `UndoLastApply` itself writes no blob.
- `Reason` is never populated on successful blob items. (Superseded: `AppliedByReason` is populated from `ToolCallContext`.)

## Related

- `docs/current/references/reference_code_file_write_paths.md` (write paths, blob writer "outside the chokepoint").
- `docs/current/references/reference_architecture_map.md` (chokepoint, request pipeline, lock-order and re-entrancy rules).
- [plan_stale_read_guard_followups.md](../plans/closed/plan_stale_read_guard_followups.md) (Step 9: the cross-process write lock and holder sidecar, fold-on-read, zero-hit Search hint).
- `docs/current/references/reference_file_watcher_and_drift_detection.md` sections 7-9 (lock, fold-on-read, zero-hit hint as built).
- [idea_external_drift_hard_blocker.md](../ideas/closed/idea_external_drift_hard_blocker.md) (existing drift idea; not re-read for this proposal).
- Memories: `project_per_session_mcp_server_isolation`, `project_externaldrift_false_positive_cross_check_gitstatus`,
  `project_concurrent_sessions`, `project_operation_blob_json_gotchas`,
  `reference_central_mcp_request_filter_pipeline`.
