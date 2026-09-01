# Idea: Remove external-drift reconciliation tools; make drift a hard blocker

Status: **finalized, ready to implement**. Filed 2026-09-01, finalized 2026-08-31.

## Naming correction (finalized 2026-08-31)

This doc originally referred to the reconciliation tool as `ClearExternalDrift`. The real tool —
confirmed live in `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:431` — is
**`AcknowledgeExternalFileChanges`**. The wrong name is also baked into two other real places, not
just this doc, which is itself a small demonstration of the class of confusion this idea is meant
to reduce:
- The refusal message the model actually reads on drift
  (`PersistentWorkspaceManager.cs:1091`, inside `ApplyProposedChangesAsync`).
- `DeleteFile`'s tool `[Description]` (`SentinelWorkspaceTools.cs:1051`).
- A battery test name, `ClearExternalDrift_Always_DoesNotThrow`
  (`RoslynSentinel.Tests.Battery/BatteryTwentyTests.cs:370`).

All three must be corrected to `AcknowledgeExternalFileChanges` as part of this implementation,
regardless of the rest of the proposal — they're wrong today independent of whether drift becomes
a hard blocker. Every other place below uses the real names: `ListExternalDiskChanges` and
`AcknowledgeExternalFileChanges`.

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

A model given `ListExternalDiskChanges`/`AcknowledgeExternalFileChanges` and a refusal message
that says "call ListExternalDiskChanges to review... or call AcknowledgeExternalFileChanges to
acknowledge and overwrite anyway" is being asked to run a full reconciliation workflow: notice the
warning, enumerate what
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

1. **Move `ListExternalDiskChanges` and `AcknowledgeExternalFileChanges` into a new `Admin` tool
   group, off by default.** The server already gates whole tool *classes* behind named `--mode`
   groups (`ServerStartupHelpers.ParseArgs`/`AddRoslynSentinelToolsBasic`/
   `AddRoslynSentinelToolsAdvanced` — see `ServiceRegistrationExtensionsBasic.cs:97-154`), resolved
   from `--mode=<comma-separated-groups>` (default `"all"`, which expands to the variant's
   `AllModes` set). Both target tools currently live inside `SentinelWorkspaceTools`, which is
   registered wholesale under the `"Workspace"` mode — group-gating at that granularity would also
   hide the rest of `SentinelWorkspaceTools` (`LoadSolution`, `ReadFile`, `ApplyDiff`, etc.), which
   must stay model-visible. Since `WithTools<T>()` only filters at the class level, not per-method,
   the two tools must move to a **new `SentinelAdminTools` class**, registered only under a new
   `"Admin"` mode:
   - Add `SentinelAdminTools` (Server.Basic; Server.Advanced references Basic so it inherits this
     for free — see [[project_advanced_extends_basic]]) containing exactly these two
     `[McpServerTool]` methods, moved out of `SentinelWorkspaceTools`.
   - Register it in `AddRoslynSentinelToolsBasic` behind `if (activeModes.Contains("Admin"))`,
     mirroring the existing per-mode blocks.
   - **Critically, do NOT add `"Admin"` to `AllModes`** in `ServerStdio.cs`/`ServerHttp.cs` (Basic
     and Advanced both declare their own `AllModes`) — `--mode` defaults to `"all"`, which expands
     to literal `AllModes` membership, so an omitted `Admin` entry makes the group reachable only
     via an explicit `--mode=Admin` or `--mode=Workspace,Admin,...`. This is the intended
     off-by-default gate, not an oversight — it's how every other currently-unregistered tool class
     in that file is already kept out of `"all"` (see the commented-out `WithTools<>()` lines).
   - This is also the mechanism named for gating any future restricted/operator-only tool, not just
     these two — `Admin` is the group going forward, not a one-off flag.
2. **On detected drift, fail hard and terminally, session-wide.** The write attempt throws a fatal
   exception (once detection is trustworthy — see hash-baseline prerequisite below, a genuine hit
   is presumptively real, so this is not a soft `ApplyChangesResult` failure a caller could
   catch-and-retry around) and flips a session-wide latch. Every subsequent mutating tool call
   — not just retries against the same file — fails immediately off that latch, with the same
   short, non-actionable-by-design message: drift was detected on a tracked file, the session
   cannot safely continue, stop and report to the user/operator. No path forward, no suggested tool
   to call next, deliberately, so there's nothing for the model to loop on. Scoping the block to
   only the one drifted file would let the model route around it by editing something else, which
   contradicts this proposal's own premise — a genuine external touch under the
   single-session/no-concurrent-actors assumption is evidence the session's whole view of disk may
   be untrustworthy, not just that one path.
3. **Unblocking is out-of-band**: a human, or a separate privileged tool/CLI flag not exposed to
   the normal agent loop, reviews the actual disk change and clears both the drift flag and the
   session-wide latch. This preserves `ClearExternalFileChanges()`'s existing mechanism in
   `PersistentWorkspaceManager` — just moves who/what can invoke it outside the model's own tool
   surface (via the `Admin` group above, or a non-MCP path — CLI flag/admin endpoint — since even
   an `Admin`-gated MCP tool is still something *some* model role could call; exact mechanism is an
   implementation detail, not decided here).

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

**Design (finalized: layered in front, not a replacement — see "Layering decision" below)**:
maintain a `path → content hash` map (SHA-256 or a fast non-cryptographic hash like xxHash; either
is cheap relative to the I/O already being done) as the source of truth for "did this file actually
change from what RoslynSentinel last knew."
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

## Decisions (finalized 2026-08-31, closing what were open questions)

- **Blocker scope**: session-wide latch, not per-file (see proposal item 2 above). A confirmed
  drift hit fails every subsequent mutating tool call for the rest of the session, not just retries
  against the drifted path.
- **Reconciliation tools for legitimate concurrent-editing use cases**: not a separate opt-in
  toolset — folded into the general-purpose `Admin` mode (proposal item 1). `Admin` is off by
  default (omitted from `AllModes`) and is the mechanism for gating any future restricted/
  operator-only tool, not a bespoke flag for this one case.
- **Hash-baseline layering**: layer in front of `_internalChanges`/`_externalChanges`, do not
  replace them, in this pass. The hash check becomes the first gate `OnFileSystemChanged`
  evaluates; the existing path-key/timestamp suppression logic stays as-is behind it, unchanged.
  Add a code comment at both `_internalChanges`/`_externalChanges`'s declaration
  (`PersistentWorkspaceManager.cs:36,40`) and the new hash-map's declaration explaining the
  relationship — the hash map is the authoritative content-based check; the older path-key/
  timestamp fields remain only as an unreplaced legacy mechanism the hash check now sits in front
  of — and flag the older mechanism as a candidate for future removal once the hash-based gate has
  run in production long enough to trust fully replacing it. This is deliberate: it heads off a
  future session re-discovering and re-flagging the two-systems overlap as if it were an
  unintentional bug, when it's actually a known, intentional staged migration.

## Implementation order

1. Naming correction (3 sites: refusal message, `DeleteFile` description, battery test name) —
   independent of everything else, do first.
2. Hash-baseline gate, layered in front of existing detection, plus the diagnostic logging — this
   is what makes the hard-blocker escalation in step 4 safe to ship.
3. `SentinelAdminTools` extraction + `Admin` mode wiring (Basic and Advanced) — removes the two
   tools from the default model-visible surface.
4. Session-wide fatal-exception blocker on confirmed drift + out-of-band clear path.

Step order matters: shipping 3-4 before 2 would make a false positive unrecoverable by the model
*and* still just as likely to fire as today.
