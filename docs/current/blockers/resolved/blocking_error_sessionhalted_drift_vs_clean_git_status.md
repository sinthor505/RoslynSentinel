# Blocker: SessionHalted fired on SentinelCallToolResult.cs, but `git status` shows the tree clean

**Status:** RESOLVED 2026-09-20. Not an environment defect - real drift correctly detected from
discarded external prototype edits. See `## Status` at the end for the full resolution.

## Symptom

Calling `Member(operation: "addTopLevelType", filepath: "RoslynSentinel.Common/SentinelCallToolResult.cs", ...)`
to add a new `ServerInfo` record (first step of the envelope field-promotion plan) failed with:

```json
{"success":false,"error":{"errorCode":"SessionHalted","message":"Member failed: Session halted: external file drift was detected on a tracked file. This session cannot safely continue. Stop and report to the user/operator.","detail":"Session halted: external file drift was detected on a tracked file. This session cannot safely continue. Stop and report to the user/operator."}}
```

## Cross-check performed before treating this as real (per project memory - SessionHalted has
false-positived before against git status with zero overlap)

`ListExternalDiskChanges` (called immediately after the halt) returned 5 files:

```
RoslynSentinel.Common/SentinelCallToolResult.cs      <- the exact file the halted call targeted
RoslynSentinel.Server.Basic/SentinelRefactoringTools.cs
RoslynSentinel.Server.Basic/RefactoringSignatureTools.cs
RoslynSentinel.Basic/RefactoringSignatureImpl.cs
RoslynSentinel.Common/OperationResult.cs
```

`Git(operation: "status")` (called in the same turn) returned:

```json
{"success":true,"branch":"master","isClean":false,"staged":[],"unstaged":[],"untracked":["docs/current/proposal_envelope_field_promotion.md"],"isTruncated":false}
```

**This is the opposite of the known false-positive pattern.** Previously, SessionHalted fired with
*zero* overlap against git-reported changes (see memory
`project_externaldrift_false_positive_cross_check_gitstatus`). Here there IS overlap in spirit -
`SentinelCallToolResult.cs` is the file both the halt and my in-flight edit target - but `git status`
says the working tree is clean (`isClean: false` only because of one untracked `.md`, `unstaged: []`,
`staged: []`). The session's start-of-conversation git snapshot (captured before this turn) also
showed `RefactoringSignatureTools.cs`/`SentinelRefactoringTools.cs` as modified (` M`) and
`RoslynSentinel.Common/OperationResult.cs` as untracked (`??`) - matching 3 of the 5 drifted files -
but by the time `Git status` ran just now, none of that shows up as pending changes anymore.

Two readings, neither confirmed at write time:
1. A concurrent session (per `project_concurrent_sessions` memory: this repo can have multiple
   Claude/VS sessions editing simultaneously) committed those changes between the start-of-session
   snapshot and now, and the workspace's in-memory view of `SentinelCallToolResult.cs` is stale
   relative to what's now on disk/in git history - a real drift, correctly caught.
2. The drift detector is comparing against a stale in-memory baseline of its own (e.g. from before a
   commit landed) and `git status` clean is the ground truth - a false positive, but NOT the same
   shape as the previously-seen zero-overlap case, so the existing memory doesn't establish this one
   is safe to ignore.

**Resolved (operator confirmation):** reading (1)'s spirit, refined - the operator was prototyping the
envelope shape changes on disk directly (outside the MCP server) while designing this same plan
earlier in the session, then discarded those edits without ever routing them through the server. The
files ended up back at their original (git-clean) content, which is why `git status` shows nothing
pending - but the server's long-running process observed the on-disk mtime/content change and back-
change independently of any commit, so its in-memory workspace snapshot is (correctly) no longer
trusted relative to disk, even though the net diff is now zero. This is real drift, correctly
detected, and git-clean is not proof of no-drift-occurred because git only sees the final state, not
the intermediate external write the server's watcher caught. Not a detector bug.

## Why this blocks

I have an in-flight design (`Member(addTopLevelType, ServerInfo, ...)` targeting
`SentinelCallToolResult.cs`) that has NOT been applied (the call failed before any write). No edit is
in-flight to finish per CLAUDE.md's "finish any in-flight edit" clause - the call itself never landed.
Per CLAUDE.md's dogfooding/failure-doctrine section: a tool failure/gap is a blocking finding. Do not
retry speculatively, do not route around it with shell tools (the PreToolUse hook also independently
blocks `git log`/`git status` via Bash, correctly redirecting to the `Git` MCP tool, which I used).

## What would resolve this

- Confirm (from the operator/user side, or via a fresh `LoadSolution`/workspace reload) whether
  `RoslynSentinel.Common/SentinelCallToolResult.cs` on disk right now matches what this session's
  workspace has in memory - i.e. is this real drift from a concurrent session's commit, or a stale
  drift-detector baseline.
- If real drift: reload the workspace (or restart the server) so the in-memory solution matches disk,
  then resume the envelope field-promotion plan from Step 1.
- If false positive: identify why `ListExternalDiskChanges` flagged these 5 specific files while
  `git status` reports clean, since that gap is itself worth fixing in the drift detector (per
  CLAUDE.md's failure doctrine: an environment defect, not a model error).

## Status

Resolved - not an environment defect. Cause: external, outside-the-server prototype edits (later
discarded) to the drifted files during earlier design discussion in this same session, which the
server's live file watcher correctly flagged even though the net on-disk diff is now zero.
`AcknowledgeExternalFileChanges` cleared the latch; a subsequent `LoadSolution(forceReload: true)`
resynced the in-memory workspace with disk. Envelope field-promotion plan
(`C:\Users\Administrator\.claude\plans\inherited-orbiting-toucan.md`) resumed successfully afterward.

## Related

- `blocking_error_member_remove_false_not_found.md` (resolved) - the next blocker hit immediately
  after this one was cleared, in the same Step 1 sequence; unrelated root cause (a `Member(remove)`
  outcome-swallowing bug, not drift-related).
