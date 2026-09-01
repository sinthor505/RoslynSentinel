# Idea: Remove external-drift reconciliation tools; make drift a hard blocker

Status: **proposed** — not scheduled. Filed 2026-09-01.

## Context

[project_planimplementverify_5run_result.md](../project_planimplementverify_5run_result.md)'s
one failing run spent turns 13-23 (of 24) stuck in a reload loop triggered by a false-positive
"External file changes detected... Modified externally since last sync" drift warning — itself
caused by confirmed bugs (`FilePath` never canonicalizing `/` vs `\` separators, and
`LoadSolutionAsync` never clearing `_externalChanges`). Those bugs need fixing regardless (see
that memory file's "How to apply" 1-3). This idea is a separate, additive question raised while
discussing further hardening: should the model be exposed to drift *reconciliation* at all, even
once detection is correct?

**Frequency check (2026-09-01):** grepped all of `ModelTestingResults/` for the drift-warning
phrases (`External file changes detected`, `Modified externally since last sync`). Hits appear in
exactly 3 log files, all within the same `PlanImplementVerify` batch — the failing run
(`20260901-042404-655`, 3 occurrences) and **two of the four passing runs**
(`20260901-040624-847`, 5 occurrences; `20260901-042022-675`, 1 occurrence). So the warning firing
is not unique to the failure — it's common (3/5 runs in this batch), but only the failing run
latched onto it and spiraled; the passing runs apparently noticed it, didn't act on it, and moved
on without harm. Zero hits anywhere else in the archived tree (`ScriptedPlan`, `MinimalGuidance`,
`Disambiguated`, `PlanOnly`, the 50-run sweeps) — but none of those runs' failure analyses record
having grepped for this phrase specifically, so "never a cause elsewhere" should be read as "never
observed/looked for," not a confirmed absence.

## The argument

A model given `ListExternalDiskChanges`/`ClearExternalDrift` and a refusal message that says "call
ListExternalDiskChanges to review... or call ClearExternalDrift to acknowledge and overwrite
anyway" is being asked to run a full reconciliation workflow: notice the warning, enumerate what
changed, inspect whether it touched files it cares about, read those files, judge whether the
change is its own recent edit vs. someone else's, and decide whether it's safe to proceed. That's
a reasonable ask for a capable agent operating in a genuinely concurrent-editing environment. For
this server's primary target — smaller/weaker models doing single-session, non-concurrent
refactors — it's a workflow built for a scenario (legitimate concurrent external edits) that
essentially never applies, and the tooling gives no way to distinguish "this is actually someone
else's change" from "this is your own write that the detector mis-flagged." The failing run is a
direct demonstration: the model had zero chance of correctly reconciling something that was, in
fact, its own successful edit.

Under the stated operating assumption — single session, all edits via MCP tools, no concurrent
external actors — a genuine external change is not an ambiguous case to reason through. It's an
anomaly: something touched a tracked file outside the only sanctioned write path. That should
never be something the in-task model is asked to adjudicate; it should stop the session.

## Proposal

1. **Remove `ListExternalDiskChanges` and `ClearExternalDrift` from the model-visible tool set**
   (i.e., not registered for the coding-agent role/tool group). Reconciliation stops being
   something the model can attempt mid-task.
2. **On detected drift, fail hard and terminally** — the write attempt (and ideally the rest of
   the session) returns a short, non-actionable-by-design message: drift was detected on a tracked
   file, the session cannot safely continue, stop and report to the user/operator. No path
   forward, no suggested tool to call next — deliberately, so there's nothing for the model to
   loop on. Once detection is trustworthy (see hash-baseline prerequisite below), a genuine hit
   is presumptively real — worth throwing an actual exception rather than returning a soft
   `ApplyChangesResult` failure, so there's no residual path for the agent loop to catch-and-retry
   around it. A fatal, session-ending failure is the correct response to a presumed-real anomaly
   under the no-concurrent-sessions invariant.
3. **Unblocking is out-of-band**: a human, or a separate privileged tool/CLI flag not exposed to
   the normal agent loop, reviews the actual disk change and clears the flag. This preserves
   `ClearExternalFileChanges()`'s existing mechanism in `PersistentWorkspaceManager` — just moves
   who/what can invoke it outside the model's own tool surface.

## Prerequisite: content-hash baseline (replaces path-key/timestamp detection)

This makes false positives much more expensive (no self-service recovery), so it must ship
**after**, not instead of, the detection fixes already queued in
[project_planimplementverify_5run_result.md](../project_planimplementverify_5run_result.md):
`FilePath` separator canonicalization and `LoadSolutionAsync` clearing `_externalChanges`. But
those two fixes alone patch specific instances of a structurally fragile detection design — a
content-hash baseline is the stronger fix that makes the message actually *true* rather than just
less confusing when it fires, and is what makes the hard-blocker/fatal-error escalation above safe
to ship at all.

**Why the current design is fragile even once the two known bugs are fixed.** Self-write
suppression today (`PersistentWorkspaceManager.OnFileSystemChanged`) depends on three things all
holding at once: the watcher event's path key must exactly match the write's recorded key (the
separator bug), the event must arrive within `_internalChanges`' ~5-second freshness window, and
the dictionary entry must not have already been evicted. Any one of these breaking again in the
future (a new normalization bug, a slow write on a loaded system, a debounce timing edge case)
reproduces the same false-positive class through a different mechanism. Fixing today's two known
causes doesn't close the class of bug, just today's two instances of it.

**Design**: maintain a `path → content hash` map (SHA-256 or a fast non-cryptographic hash like
xxHash; either is cheap relative to the I/O already being done) as the source of truth for "did
this file actually change from what RoslynSentinel last knew."
- **Populate** the map for every tracked file during `LoadSolutionAsync` (one hash pass at load,
  replacing whatever was there before — this is also where the fix must ensure the map is fully
  reset, not just added-to, to avoid recreating bug #1's "stale state survives reload" shape).
- **Update** the map for a path immediately after a successful write in `ApplyProposedChangesAsync`
  — hash `newContent`, which is already in memory, no extra I/O.
- **On a watcher `Changed`/`Created` event**, compute the on-disk file's current hash and compare
  to the map's recorded hash for that path (both looked up via the same normalized-path key,
  closing the separator-mismatch class regardless of whether `FilePath` itself is ever
  re-broken): if equal, this is a no-op or an echo of a write already accounted for — do not flag
  drift. If different, this is real, hash-confirmed content drift — flag it, and (per proposal
  item 2) fail hard.
- This also removes the timestamp/freshness-window race entirely: there is no "the internal-change
  record expired before the watcher event arrived" case, because the comparison is content-based,
  not recency-based. A write that completes slowly, or a watcher event that's debounced longer than
  5 seconds, still resolves correctly.

**Cost**: negligible. Hashing content already held in memory (write side) or already read from disk
for the existing content-comparison branch (watcher side, which already does an equivalent
`File.ReadAllText` today) adds microseconds. Memory cost for the map is ~32 bytes/file (SHA-256) or
8 bytes/file (a 64-bit non-cryptographic hash) times tracked-file count — irrelevant even for a
10,000-file solution (~320KB worst case).

**Diagnostic logging, ship alongside this regardless of outcome**: log the exact comparison at the
point `OnFileSystemChanged` decides to flag a path as drift — the normalized path key used, the
recorded hash vs. the computed on-disk hash, and which code path (map miss vs. hash mismatch) drove
the decision. This is cheap and would have made the original bug traceable from one log line
instead of the multi-turn trace it actually took to diagnose. Independent of whether the
hash-baseline redesign above is adopted in full.

## Open questions (not yet resolved)

- Exact mechanism for "stop the rest of the session" — reject just the one tool call, or flip a
  session-wide flag that fails all subsequent write tools too?
- Whether a genuinely legitimate concurrent-editing use case (larger/more capable models, multi-
  session) should get a separate, opt-in tool group that restores the reconciliation tools —
  rather than deleting them outright, gate them behind a config flag defaulting to off.
- Whether the hash-baseline map should replace `_internalChanges`/`_externalChanges` outright or
  sit alongside them as an additional check — replacing is cleaner (one source of truth) but is a
  larger, riskier change than layering the hash check in front of the existing refusal logic as a
  first pass.
