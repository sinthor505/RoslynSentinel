---
name: project_external_drift_hard_blocker_idea
description: Implemented 2026-09-01 — moved ListExternalDiskChanges/AcknowledgeExternalFileChanges into Admin-mode-gated SentinelAdminTools, added content-hash baseline layered in front of legacy drift detection, made drift a session-wide fatal SessionHaltedException instead of model-reconcilable; see docs/current/ideas/external-drift-hard-blocker.md
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-01T07:06:34.064Z
---

Filed `docs/current/ideas/external-drift-hard-blocker.md` in the RoslynSentinel repo, proposing to remove `ListExternalDiskChanges`/`ClearExternalDrift` from the model-visible tool set
entirely and make external drift a hard, terminal session blocker requiring out-of-band
(human/privileged) unblock, rather than a workflow the in-task model is asked to reconcile.
Motivated directly by [[project_planimplementverify_5run_result]]'s failing run, where the model
had no way to distinguish "this is your own successful write, mis-flagged" from "someone else
changed this file" and spiraled for 10+ turns trying to reconcile a warning that was, in that
case, entirely self-inflicted.

**Frequency check performed 2026-09-01**: grepped all of `ModelTestingResults/` for the drift
message text. Found in exactly 3 log files, all in the same `PlanImplementVerify` batch — the
failing run AND 2 of the 4 passing runs. So the warning firing is common within that batch (3/5),
not unique to the failure; the passing runs apparently shrugged it off without derailing. Zero
hits in any other test family's archived logs (`ScriptedPlan`, `MinimalGuidance`, `Disambiguated`,
`PlanOnly`, the 50-run sweeps) — but this should be read as "never observed/looked for," since none
of those runs' prior failure analyses record grepping for this phrase specifically. Confirms the
user's recollection that this had not previously been identified as a run-failure cause, while
leaving open whether it was silently present-but-harmless in other batches too.

**Why:** the user's core argument — weaker models used for simple, single-session refactors have
no legitimate scenario requiring external-change reconciliation; under the "no concurrent
sessions, all edits via MCP tools" invariant, real drift is an anomaly, not an ambiguous case to
reason through, so the model shouldn't be given tools to adjudicate it.

**How to apply:** treat this as a follow-on to the detection-bug fixes already queued in
[[project_planimplementverify_5run_result]] (FilePath separator canonicalization,
LoadSolutionAsync clearing _externalChanges) — this proposal explicitly must ship AFTER those, not
instead of them, since removing the model's self-service recovery path makes any remaining
false-positive far more expensive (full session stall instead of a derailed-but-sometimes-recoverable
run). Not yet implemented; no code changed.

**2026-09-01 update — hashing design added to the doc.** User asked whether rewording the drift
message (original "How to apply" item 3) actually fixes anything, or whether it's just cosmetic.
Answer: rewording alone doesn't stop the false positive from firing, only fixing detection does —
so the content-hash-baseline idea (path→content-hash map, populated at LoadSolutionAsync, updated
on write, compared on watcher events) was written up as the doc's real fix, replacing today's
fragile path-key+timestamp suppression logic which has three independent failure points (exact key
match, freshness window, eviction) rather than one. Also added: diagnostic logging at the exact
point OnFileSystemChanged decides to flag drift (recorded vs. computed hash, which code path fired)
as a cheap addition regardless of whether hashing is adopted in full — this is what would have made
the original bug traceable from one log line instead of a multi-turn trace. And: once hashing makes
a drift hit presumptively real, throwing an actual exception (not a soft ApplyChangesResult
failure) was added as the concrete form of "fatal" for proposal item 2, removing any residual path
for the agent loop to catch-and-retry around it.

**2026-09-01 — implemented, all 4 steps, via MCP tools per
[[feedback_dogfood_mcp_blocking_errors]].** (1) naming correction (3 sites) fixed. (2) content-hash
baseline (`_knownFileHashes` in `PersistentWorkspaceManager`) shipped layered in FRONT of the older
`_internalChanges`/`_externalChanges` mechanism, not replacing it — both kept deliberately, with
code comments at both declarations explaining the staged-migration relationship so a future session
doesn't mistake it for unfinished cleanup. (3) `ListExternalDiskChanges`/`AcknowledgeExternalFileChanges`
extracted to new `RoslynSentinel.Server.Basic/SentinelAdminTools.cs`, gated behind a new `"Admin"`
mode deliberately excluded from `AllModes` in all 4 server files (Basic/Advanced × Stdio/Http) —
Advanced needed no separate wiring since it delegates to Basic's registration function. (4) session-
wide fatal latch shipped as `_sessionHalted` (volatile bool) + new `SessionHaltedException`/
`ToolErrorCode.SessionHalted`, checked first in `ApplyProposedChangesAsync` before any other
validation, tripped by the same confirmed-drift branch that used to return a soft
`ApplyChangesResult` failure. Out-of-band clear path: `SentinelAdminTools.AcknowledgeExternalFileChanges`
now also calls the new `IWorkspaceHealthReporter.ClearSessionHalt()`; a new `IsSessionHalted` read
tool was added alongside it for visibility. Full-solution `fullBuild` came back 0 errors (18
pre-existing unrelated warnings). Not yet committed to git as of this memory update — see
[[feedback_use_powershell_tool_not_bash]]-adjacent gotcha: the MCP `Git` tool's `status` operation
timed out 3 consecutive times mid-session (documented in
`docs/current/blockers/blocking_error_git_status_timeout.md`), while plain `git status` via
PowerShell worked fine — confirms the MCP tool wrapper itself is broken, not git or the repo. User
gave a standing instruction mid-session to document-and-continue on further Git-tool failures rather
than hard-stopping per the usual dogfood policy, and separately approved bypassing the MCP tool for
the actual commit ("bypass and use normal git. The issue has been documented so we can address that
separately.").
