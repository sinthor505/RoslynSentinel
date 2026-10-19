# Plan: refuse caller-authored writes to files changed since the server last returned their content

**Status:** DRAFT 2026-10-18. Process-wide observed-content ledger that makes WriteFile, ApplyDiff (files format) and DeleteFile refuse a stale target with a non-latching, per-file `StaleRead` error; not yet implemented, runs beside the external-drift latch.

## Problem

The external-drift latch cannot see a stale-read edit. It trips only when a write or delete target is
already in `_externalChanges` (`PersistentWorkspaceManager.ApplyProposedChangesAsync`, drift check at
~1542), and `_externalChanges` is filled by the file watcher. Three holes remain, all of which end in a
caller overwriting content it never saw:

1. **Debounce window.** The watcher adds an entry and re-arms a 500 ms timer; until the timer folds the
   disk text into `CurrentSolution`, an external change is invisible to the in-memory workspace, and a
   write that lands inside the window can overwrite it. (`reference_file_watcher_and_drift_detection.md`,
   section 3.)
2. **Watcher misses and overflow.** `OnWatcherError` sets `_watcherOverflowed` and forces a reload; it
   adds no drift entries. Events dropped by the 64 KB buffer produce no entry at all.
3. **Blind overwrite of a changed file.** The latch has no notion of "the caller read version A, the file
   is now version B". `_knownFileHashes` (PWM line 80) is the hash of what the server last loaded or
   wrote, not what any caller read, and the debounce sync does not refresh it.

The audience is weak and self-hosted models. When the latch does fire it halts the whole session
(`_sessionHalted`) and needs `ExternalFileDrift` List/Acknowledge, which is a heavy recovery for the
common case "re-read the file and redo the edit". The wanted behavior is a small, structured, per-file
refusal that names the file and says "re-read it", clears itself when the caller re-reads, and never
latches.

## Decision

Add a process-wide **observed-content ledger** to `PersistentWorkspaceManager`: for each path, the
SHA-256 (`ComputeContentHash`, PWM 415-419, the same function behind `_knownFileHashes`) of the text this
server last returned to a caller. Selected write tools opt in to a stale-read check inside the write
chokepoint; the check compares the ledger hash to BOTH the current in-memory document text and the disk
pre-image and refuses if either differs.

Decided (from the brief; not re-litigated here):

1. Store: `ConcurrentDictionary<FilePathWrapper,string>` named `_observedHashes`. The name is deliberately
   distinct from the existing `_ledger` (see Background: that is the MoveMember scoped operation ledger).
2. `RecordObserved(path, text)` is called explicitly from `ReadFile` (whole-file, line-range and member
   reads all count as observing the whole file, including the non-document and disk fallbacks),
   `GetMethodSource`, and the per-document loop of Search text mode. NOT recorded: `GetFileOutline`,
   `ListAll`, `InspectSymbol`, engine reads, Git output. Search text mode only; widening later is cheap.
3. One new optional parameter on `ApplyProposedChangesAsync`: `StaleReadPolicy { None, RefuseIfObservedStale }`,
   default `None`, so the 63 existing call sites compile unchanged (migration-first). Inside
   `_solutionLock`, for each write or delete target that has a ledger entry, compare to both the in-memory
   document text hash and the hash of the disk pre-image. Refuse if either differs.
   - Opt in v1: `WriteFile` (ReplaceFile), `ApplyDiff` files format, `DeleteFile`.
   - Out of scope v1: anchor-based tools (`ReplaceSnippet`, `ApplyDiff` diff format, `ApplyUnifiedDiff`)
     and engine tools (`Member`, `RenameSymbol`, `MoveMember`, ...). Listed as follow-ups.
   - Exempt: `UndoLastApply` (`exactRestore`), `CreateFile`, and any path with no ledger entry
     (unobserved target allowed; the result carries `observed:false`).
4. After a successful chokepoint write, refresh the ledger entry only for paths already in the ledger, so
   an agent is not refused for its own change.
5. Runs beside the existing latch. A follow-up narrows the latch to per-file refusals once the ledger has
   proven itself.
6. Refusal shape: a failed `ApplyChangesResult` with `RefusalCode = ToolErrorCode.StaleRead`
   (precedent: `EolChangeRefused`, PWM ~1639), mapped by the tool to a `ResultError` that names the file(s)
   and says to re-read. It does not set `_sessionHalted`.

Recommended (mine, open for review): `RecordObserved` lives on a new narrow interface
`IObservedContentLedger`, composed into `IWorkspaceManager`. See "Interface impact".

## Background: prior design that overlaps, conflicts or is superseded

Content-only reading of five docs. Nothing here is superseded by this plan outright; three need updating
and one conflicts in rationale.

- **`plan_scoped_operation_ledger.md`: different feature, name collision only.** It is the MoveMember call
  site tracker (`IScopedOperationLedger` / `ScopedOperationLedgerEngine`, field `_ledger`, `IsBlocked` per
  target at PWM ~1513). It is not a read ledger and does not record what callers read. Overlap is limited
  to (a) it added a per-target check at the same chokepoint, which is the precedent for where the
  stale-read check goes, and (b) the `FakeWorkspaceManager` lesson: a cross-cutting member must have a real
  implementation in the fake or tests hit an `InvalidCastException` (the `UndoLastApply` cast). The fake
  already mirrors the `IsBlocked` gate (`FakeWorkspaceManager.cs` ~85-97). New names here avoid `ledger`
  alone: `_observedHashes`, `IObservedContentLedger`.
- **`references/reference_file_watcher_and_drift_detection.md`: authoritative on the watcher and latch;
  anticipates this plan.** Its last Gotcha says "Nothing records which files a caller has READ ...
  Read-tracking is under design." This plan is that design. It also documents the debounce bug (the sync
  does not refresh `_knownFileHashes`) that this plan lists as a separate small bug. Step 7 updates it.
- **`reference-code-file-write-paths-v1.md`: chokepoint step list needs one more step.** It enumerates
  chokepoint steps 1-11 and the callers table. The stale-read check becomes a step between pre-image
  capture (7) and the EOL guard (8). It also records the one sanctioned direct write
  (`MsToolAugmentEngine.FormatDocumentSafeAsync` with no solution loaded); that path never reaches the
  chokepoint, so the ledger neither guards nor refreshes it. Step 7 updates it.
- **`design_read_chokepoint.md`: tension, not contradiction.** It wants one read gate (`IWorkspaceReader`
  with a mandatory `ReadSource`), mostly so staged writes can make `Committed` and `IncludeStaged` diverge.
  This plan does not wait for it: the reader is solution-granular (`GetSolutionAsync` has no path, 404
  non-test callers in 70 files by `Search(mode: callers)`, plus 42 test references), so a gate there
  cannot know which file a caller saw. We take three explicit `RecordObserved` calls instead. Cost: a new
  read tool must remember to call it; nothing compiler-enforced, same "convention, not enforcement" status
  as the write paths. Its line citations are stale; do not copy them. If staged writes land, the ledger
  needs per-snapshot hashes (see Risks).
- **`ideas/external-drift-hard-blocker.md` (SUPERSEDED 2026-10-09 by
  `plan_external_file_drift_tool_and_halt_stamping.md`): conflicts in rationale.** It argued for a
  session-wide latch with deliberately no actionable path and rejected per-file blocking because a model
  could route around it; it also assumed a single session with no concurrent actors. This plan adds a
  per-file, actionable, non-latching refusal, and its follow-up would narrow the latch. The superseding
  plan already introduced the actionable `ExternalFileDrift` tool, so the "no actionable path" premise is
  already gone. The "model routes around it" worry is answered here by making the refusal only a
  precondition of the same tool call (re-read, then repeat) rather than a second blocking mechanism.
  Narrowing the latch (follow-up F1) still contradicts that idea and needs its own decision then.

## Execution rules

- Follow `templates/plan.md` order. One slice at a time, compile-green after each, dispatched to
  `implementer` (depth tier 0 unless a step says otherwise) and followed by a `test-writer` step.
- Dispatch sequentially, never in parallel, for C# edits. One `Build` / `RunTest` at a time.
- Limits (`.claude/implementer-tiers.json`, depth tier 0): files <= 3, edits <= 10, acceptance checks <= 1,
  tool calls <= 25. Each step below states its measured shape against these.
- Migration-first: no existing signature changes shape; the new parameter is optional and appended.
- Compare test results against the known-failing baseline (`reference_known_failing_tests`) and report
  only new failures.
- Any step that finds the chokepoint text differs from the citations here must trust the source and fix
  the plan, not the other way round (line numbers are as of 2026-10-18, PWM is 2402 lines).

## Interface impact (measured)

`Search(mode: implementations, includeTests: true)` on 2026-10-18:

| Interface | Implementers | Declaration |
| --- | --- | --- |
| `IWorkspaceMutator` | 2: `PersistentWorkspaceManager` (PWM:25), `FakeWorkspaceManager` (Fake:12) | `Common/IWorkspaceMutator.cs:12` |
| `IWorkspaceReader` | 2: same two | `Common/IWorkspaceReader.cs` |
| `IWorkspaceManager` | 2: same two; composes `ISolutionProvider, IManualCircuitBreaker, IAutomaticCircuitBreaker, IUnrecoverableBreaker, IWorkspaceHealthReporter, IWorkspaceMutator, IRateLimiter, ISymbolResolver, IWorkspaceReader` | `Common/IWorkspaceManager.cs:14` |

- **`StaleReadPolicy` parameter** lands on `IWorkspaceMutator.ApplyProposedChangesAsync`: 3 declarations
  to edit (interface, PWM 1471, `FakeWorkspaceManager.cs:79`). Callers: 63 references in 18 files
  including tests; 47 production hits in 10 files (AsyncifyTools 18, AsyncBatchEngine 11,
  WholeFileWriteTools 5, WorkspaceFileEditImpl 4, PWM 2, MsToolAugmentEngine 2, CommentingTools 2,
  ValidateAndApplyHelper 1, WorkspaceProjectManagementImpl 1, plus the `IWorkspaceReader.cs:6` cref).
  The parameter is appended after `exactRestore` with a default, so none of them change. No
  `Mock<IWorkspace...>` / `Substitute.For<IWorkspace...>` usage was found by text search (patterns
  `Mock<IWorkspace`, `Substitute.For<IWorkspace`, `Mock<ISolutionProvider`), so expression-tree
  optional-argument breakage is not expected; the fake is the only test double.
- **`RecordObserved` placement. Recommendation: new `IObservedContentLedger`, composed into
  `IWorkspaceManager`.** Blast radius: 1 new file, 1 line in `IWorkspaceManager`, and 2 implementers
  (PWM, fake). The three consumer classes already hold `IWorkspaceManager` (`WorkspaceFileEditImpl.cs:32`,
  `WorkspaceReadNavigationImpl.cs:49`, `WholeFileWriteTools.cs:15`), so no constructor changes. Rejected:
  adding it to `IWorkspaceReader` (same 2-implementer radius, but the reader is the read-gate for solution
  state with a mandatory `ReadSource`; `CloneDetectionEngine` is its only consumer outside DI per the
  architecture map; mixing mutable bookkeeping into it works against `design_read_chokepoint.md`'s "grow
  accessors only from real call shapes"); adding it to `IWorkspaceMutator` (recording is a read-side act,
  and it would couple read tools to the write interface's churn).
- `ToolErrorCode` (`Common/SentinelCallToolResult.cs:42`) needs a new `StaleRead` constant. Text search for
  `StaleRead|_observedHashes|RecordObserved|ObservedContent` found no existing use.

## Steps

Order is: store, composition, check, refresh, recording, opt-in, docs. Recording (steps 4a/4b) lands before
opt-in (step 5), so the check exists but is inert until a tool both records and opts in.

### Step 1 - ledger store, shared hash, `RecordObserved` (no callers)
- **Files (3):** `Common/IObservedContentLedger.cs` (new), `Common/ContentFingerprint.cs` (new),
  `Common/PersistentWorkspaceManager.cs`.
- **Symbols:** `IObservedContentLedger.RecordObserved(FilePathWrapper path, string text)`;
  `ContentFingerprint.Compute(string)` (public static, body moved from `ComputeContentHash` PWM 415-419 so
  the fake can share the exact function; `ComputeContentHash` delegates to it); PWM `_observedHashes`,
  `RecordObserved`, an internal `TryGetObservedHash(path, out hash)`; PWM declares the interface.
- **Change:** ~6 edits. PWM implements the interface but `IWorkspaceManager` is not yet composed, so the
  fake is untouched and the build stays green. `_observedHashes` is NOT cleared on reload (comparison is by
  content, so a stale entry just fails the compare; see open question 6).
- **Call-site measurement:** 0 callers (new members). `ComputeContentHash` private, PWM-internal callers
  only (`_knownFileHashes` updates at 634 via `PopulateKnownFileHashes`, ~1779, 1835/1840): unchanged
  behavior.
- **Tier:** depth 0 (3 files, ~6 edits, 1 acceptance).
- **Done when:** `Build` 0 errors; `ContentFingerprint.Compute` returns the same lowercase hex SHA-256 as
  before for a fixed string.
- **Out of scope:** `IWorkspaceManager` composition, the fake, any caller, the chokepoint.
- **Test-writer step (T1):** unit tests for `ContentFingerprint.Compute` (known vector, empty string,
  non-ASCII) and for PWM `RecordObserved` overwrite / last-writer-wins / concurrent writers (does not
  throw, final value is one of the inputs). Covers the store only; says nothing about the check.

### Step 2 - compose into `IWorkspaceManager`, fake implementation, `StaleRead` code
- **Files (3):** `Common/IWorkspaceManager.cs`, `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`,
  `Common/SentinelCallToolResult.cs`.
- **Change:** ~4 edits: add `IObservedContentLedger` to the base list; fake implements it with a real
  `ConcurrentDictionary` and `ContentFingerprint.Compute` (not a stub, per the `IScopedOperationLedger`
  precedent); add `ToolErrorCode.StaleRead`.
- **Call-site measurement:** `IWorkspaceManager` implementers 2 (PWM already done in step 1, fake now); no
  other implementer or mock exists (measured above). `ToolErrorCode` constants are string consts; no
  exhaustive switch over them was searched, so the implementer runs the build to confirm.
- **Tier:** depth 0.
- **Done when:** full-solution `Build` 0 errors (the Tests project must compile).
- **Out of scope:** the chokepoint parameter; any recording call.
- **Test-writer step (T2):** fake parity: after `RecordObserved`, the fake's stored hash equals PWM's for
  the same text (shared `ContentFingerprint`).

### Step 3a - `StaleReadPolicy` parameter and the pre-write check (default `None`)
- **Files (3):** `Common/IWorkspaceMutator.cs`, `Common/PersistentWorkspaceManager.cs`,
  `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`.
- **Symbols:** `enum StaleReadPolicy { None, RefuseIfObservedStale }` declared in `IWorkspaceMutator.cs`
  beside the only API that uses it (keeps this step at 3 files); new last parameter
  `StaleReadPolicy staleReadPolicy = StaleReadPolicy.None` after `exactRestore` on all 3 declarations.
- **Change:** ~8 edits. In PWM, after the pre-image capture loop (~1585-1602) and BEFORE the EOL guard
  (~1620): when the policy is `RefuseIfObservedStale`, for each write and delete target with a ledger
  entry compare `ledgerHash` with `ContentFingerprint.Compute(preImage)` (disk; a null pre-image counts as
  a mismatch) AND with the hash of the current document text (`CurrentSolution` lookup by file path; no
  document means disk-only). On any mismatch return, before anything is written, a failed
  `ApplyChangesResult` listing every stale file in `FailedFiles`, `Summary` prefixed
  `StaleRead: ...` with the "re-read" instruction, `PreImages: preImages`,
  `RefusalCode: ToolErrorCode.StaleRead`, mirroring the EOL refusal at ~1639. Whole batch is refused
  (all-or-nothing, derived). `_sessionHalted` is not touched. The fake gets the same check (its
  `preImages` are in-memory, so the fake compares to the document text).
- **Call-site measurement:** 63 references / 47 production hits (listed above) compile unchanged because
  the parameter is defaulted and appended. The existing signature already has `cancellationToken` before
  `deletePaths`, so appending after `exactRestore` does not break the "CancellationToken last" convention
  any further than it is already broken.
- **Tier:** depth 0, but at the edge. The chokepoint is a 467-line method that the implementer must read,
  which eats the 25 tool-call budget. If the dispatcher judges the budget will not hold, send it to
  `implementer-senior`, or split the fake into its own slice; do not widen the tier.
- **Done when:** full-solution `Build` 0 errors; with the default policy behavior is byte-identical (no
  existing test changes).
- **Out of scope:** post-write refresh (3b), any tool passing the policy, error mapping in tools.
- **Test-writer step (T3a):** see Test strategy rows A-F. Must include real-PWM tests (via
  `TestSolutionFixture`), not only fake-backed ones, because the fake's check is a separate copy of the
  logic and a green fake test says nothing about the production path.

### Step 3b - ledger refresh after write, entry removal on delete, rollback restore
- **Files (1-2):** `Common/PersistentWorkspaceManager.cs` (+ the fake if it mirrors writes).
- **Change:** ~5 edits, at the existing `_knownFileHashes` sites: after the write-side
  `_knownFileHashes[filePath] = ComputeContentHash(contentToWrite)` (~1779) set the ledger entry to the
  same hash IF `_observedHashes` already contains the path (never create entries here); after the delete
  `_knownFileHashes.TryRemove` (~1656) remove the ledger entry (derived: a deleted file has nothing to
  compare; a later re-create is `CreateFile`, which is exempt); on rollback (~1817, hash restores at
  1835/1840) restore the ledger entry to the pre-image hash if one existed. This applies to EVERY
  successful chokepoint write, not only opted-in ones (decision 4); see open question 1 for the cost.
- **Why `contentToWrite`:** the written text is EOL-normalized (`EolUtilities.NormalizeEolOfChangedLines`,
  ~1745) and the in-memory resync re-reads the file from disk (`ApplyInMemoryDocumentUpdatesAsync`,
  `FileIoHelper.ReadAllTextAsync`), so in-memory text equals disk text after a write. The ledger must hold
  the hash of what reached disk, not `newContent`, or a mixed-EOL file would be refused on the next write
  after the agent's own edit.
- **Tier:** depth 0 (1-2 files, ~5 edits).
- **Done when:** `Build` 0 errors.
- **Out of scope:** creating ledger entries from writes; any tool change.
- **Test-writer step (T3b):** rows G-I in Test strategy (own-write refresh, mixed-EOL, delete, rollback).

### Step 4a - record from `ReadFile`
- **Files (1):** `Tools.Basic/WorkspaceFileEditImpl.cs` (`ReadFile` ~301-461,
  `ReadFileWithoutSolutionAsync` ~194-267, `TrimRangeToInlineBudget` 269-299 for reference).
- **Change:** ~5 edits, one `RecordObserved(path, wholeFileText)` per branch that returns file content:
  whole-file, line-range (hash the whole file text, not the trimmed slice), non-document disk fallback,
  the no-solution path, and the `memberName` branch. The member branch uses
  `_symbolNavigationEngine.GetMemberSourceAsync` and returns before any document lookup, so it needs one
  extra whole-file text fetch (`GetDocumentTextAsync` or a disk read) just to hash it: a real extra cost,
  stated rather than hidden.
- **Rules:** hash the raw file text as obtained, BEFORE line-numbering or any display transformation. The
  large-file branch that returns an outline via `_readNav.GetFileOutline` returns no content and must NOT
  record. Record only on success.
- **Call-site measurement:** `ReadFile` is a tool entry point; no internal callers changed. The 4 other
  chokepoint calls in this file (`UndoLastApply` 126, `ReplaceSnippet` 694, `ReplaceSnippetBatch` 933,
  `CreateFile` 1034) are untouched.
- **Tier:** depth 0.
- **Done when:** `Build` 0 errors.
- **Out of scope:** `GetMethodSource`, Search, outline; any opt-in.
- **Test-writer step (T4a):** rows J-K in Test strategy.

### Step 4b - record from `GetMethodSource` and Search text mode
- **Files (1):** `Tools.Basic/WorkspaceReadNavigationImpl.cs` (`GetMethodSource` 94-189,
  `SearchSolutionText` 459-667; `GetFileOutline` 191-244 stays untouched).
- **Change:** ~3 edits. `GetMethodSource`: record the whole file text of the document it resolved.
  Search: inside the per-document loop, only for documents that contributed at least one match to the
  returned result (not every scanned document, and not documents scanned after the `maxResults` early exit),
  so hashing cost is bounded by matches rather than solution size.
- **Thread-safety:** the search is a nested `Parallel.ForEachAsync`; `_observedHashes` is a
  `ConcurrentDictionary`, so concurrent `RecordObserved` is safe; the implementer must not add a lock.
- **Tier:** depth 0.
- **Done when:** `Build` 0 errors.
- **Out of scope:** widening to `GetFileOutline`, `ListAll`, `InspectSymbol`.
- **Test-writer step (T4b):** rows L-M in Test strategy.

### Step 5 - opt in `WriteFile`, `ApplyDiff` (files format), `DeleteFile`; map the refusal
- **Files (1-2):** `Tools.Basic/WholeFileWriteTools.cs` (`WriteFileTool` 30-114, call at 76;
  `DeleteFileTool` 116-165, call at 135; `ApplyDiffTool` 297-585, files-format call at 391) and, if the
  mapping is shared, `Common/ValidateAndApplyHelper.cs` (the `RefusalCode` -> `ResultError` mapping is at
  143-145; chokepoint call at 137).
- **Change:** ~7 edits: pass `StaleReadPolicy.RefuseIfObservedStale` at the 3 call sites, and map
  `result.RefusalCode == ToolErrorCode.StaleRead` to a `ResultError` with code `StaleRead`, the file
  names, and the instruction "re-read <file> with ReadFile, then repeat the edit". Today `WriteFileTool`
  maps any `result.IsError` to `ToolErrorCode.Exception`, and per the survey the other two do not map
  `RefusalCode` either; without this mapping a refusal would surface as a generic exception, which is
  exactly the unactionable error the Mission says to avoid. Not changed: `ApplyUnifiedDiffTool` (call 658)
  and the diff-format call at 524 stay on `None`.
- **Call-site measurement:** 3 opt-in call sites of the 5 in `WholeFileWriteTools.cs`.
- **Tier:** depth 0 (<= 2 files). If it touches more than the two files above, RESCOPE.
- **Done when:** `Build` 0 errors.
- **Out of scope:** `observed:false` result field (step 6), anchor-based and engine tools, narrowing the latch.
- **Test-writer step (T5):** rows N-P in Test strategy; tool-level tests asserting the error code is
  `StaleRead`, not `Exception`, and that the message names the file.

### Step 6 - `observed:false` on results (optional; measure before scheduling)
- Allowed-unobserved writes should be distinguishable to the caller (decision 3). This needs a field on
  `ApplyChangesResult` (43 lines, positional optionals ending in `string? RefusalCode = null`) and on the
  three tools' result payloads. The number of payload types and tests that construct them is NOT measured;
  run `Search(mode: callers/references)` on `ApplyChangesResult` construction before slicing. Likely
  needs its own 2-3 slices or `implementer-senior`. Marked derived, not decided.

### Step 7 - docs (no code)
- **Files (3, Markdown):** `docs/current/references/reference_file_watcher_and_drift_detection.md`,
  `docs/current/reference-code-file-write-paths-v1.md`,
  `docs/current/references/reference_architecture_map.md`.
- **Change:** watcher reference: replace the last Gotcha ("Read-tracking is under design") with the
  ledger, add a short section covering `_observedHashes`, who records, who opts in, the non-latching
  `StaleRead`, and the open debounce / `_knownFileHashes` bug if still unfixed. Write-paths reference: add
  the stale-read step between steps 7 and 8, and update the callers table with which tools opt in.
  Architecture map: its "Read chokepoint (partly built)" section (map lines ~149-155) cites
  "389 call sites in 78 files as of 2026-09-24" for `GetCurrentSolutionAsync`; this plan measured 404
  non-test callers in 70 files for `IWorkspaceReader.GetSolutionAsync` on 2026-10-18 (a different method,
  not directly comparable). Add one sentence that the observed-content ledger is the read-side
  bookkeeping that does not depend on the reader, and re-measure rather than copy numbers.
  The map also lists the chokepoint refusal order; add the stale-read check after pre-image capture.
- **Tier:** docs; main session may edit Markdown. Update `Status:` dates.
- **Done when:** the three docs agree with the source as implemented; this plan moves to IMPLEMENTED.

### Step 8 - deferred follow-ups (do not schedule yet)
- **F1.** Narrow the latch to per-file refusals once the ledger has proven itself. Contradicts
  `external-drift-hard-blocker.md`; needs its own proposal.
- **F2.** Bug, independent of this plan: `OnDebounceTimerElapsed` folds disk text into `CurrentSolution`
  but does not refresh `_knownFileHashes`, so a later event for the same file mismatches the stale baseline
  and is flagged again. Add a `docs/current/TODO.md` line if one is not there; it can be fixed first or
  last.
- **F3.** Opt in anchor-based tools (`ReplaceSnippet`, `ApplyDiff` diff format, `ApplyUnifiedDiff`) and
  engine tools (`Member`, `RenameSymbol`, `MoveMember`, ...). Anchors give partial protection already (a
  stale anchor usually fails to match); engine tools work from the in-memory solution and need a different
  notion of "observed".
- **F4.** Widen recording to `GetFileOutline`, `ListAll`, `InspectSymbol`.
- **F5.** Per-snapshot hashes when staged writes (`proposal_staged_writes.md`) make `Committed` and
  `IncludeStaged` diverge.
- **F6.** A caller/session identity (does not exist on stdio today) would let the ledger be per-agent and
  remove open question 1.

## Test strategy

Role boundaries (per repo convention): the planner (this document) defines what must be covered; a
reviewer confirms the delivered tests actually exercise the specific changed path; the implementer is
responsible only for a green build and the tests it is given. A green run is not evidence of correctness
for any path no listed test touches. Real-`PersistentWorkspaceManager` tests use `TestSolutionFixture`;
fake-backed tests cover the tools' handling only. The two copies of the check (PWM and fake) mean a fake
test NEVER covers the production check.

| Row | Scenario | Path it covers | Needs real PWM |
| --- | --- | --- | --- |
| A | `RecordObserved(X)`, then change the file on disk directly (no waiting for debounce), then chokepoint write with `RefuseIfObservedStale` -> refused, `RefusalCode == StaleRead`, `_sessionHalted` false, file unchanged | disk-differs-from-ledger branch | yes |
| B | In-memory document text differs from the ledger while disk equals it -> refused | in-memory branch. Needs a seam to make memory differ from disk (not measured: check `SetTestSolution` and the document-text update helpers); if none, cover on the fake only and record the gap | yes if a seam exists |
| C | Default policy (`None`) with a stale ledger entry -> write allowed | the 47 production callers' unchanged behavior | yes |
| D | No ledger entry -> write allowed | unobserved-target allowance | yes |
| E | `UndoLastApply` / `exactRestore` after a stale entry -> still works (`UndoLastApplyTests`) | exempt path | yes |
| F | Drift-flagged target AND stale entry -> latch wins (`SessionHalted`), because the drift check (~1542) runs before the stale check | guard ordering | yes |
| G | Read, `WriteFile`, `WriteFile` again without re-read -> second allowed | post-write refresh, step 3b | yes |
| H | G on a mixed-EOL file (EOL-normalized `contentToWrite` vs `newContent`) | refresh hashes what reached disk; guards the EOL/BOM risk | yes |
| I | Delete removes the entry; rollback restores the pre-image hash | delete and rollback sites | yes |
| J | `ReadFile` whole, line-range, member, non-document, no-solution each record the whole-file hash; large-file outline return does NOT | each recording branch in step 4a | no (real tool, fake manager ok) |
| K | After a refusal, `ReadFile` then retry succeeds | "cleared by re-reading" | yes |
| L | `GetMethodSource` records; `GetFileOutline`, `ListAll` do not | step 4b scope boundary | no |
| M | Search records only matching documents, not post-early-exit ones; parallel recording does not throw | step 4b | no |
| N | `WriteFile` / `DeleteFile` / `ApplyDiff` (files) return `ResultError` with code `StaleRead` naming the file | step 5 mapping | no |
| O | `ApplyDiff` diff format, `ApplyUnifiedDiff`, `ReplaceSnippet` do NOT refuse a stale entry in v1 | out-of-scope boundary, so a later widening is a visible diff | no |
| P | Fake and PWM hash the same text identically | shared `ContentFingerprint` | no |

Known gap to name rather than hide: concurrency between a reader and a writer racing across the
`_solutionLock` boundary is not tested by any row above; the ledger itself is thread-safe, the check runs
under the lock, but a read recorded between the check and the write is not modelled.

## Out of scope

- Anchor-based and engine tools (F3), narrowing the latch (F1), the debounce `_knownFileHashes` bug (F2),
  `GetFileOutline`/`ListAll`/`InspectSymbol` recording (F4), staged writes (F5), caller identity (F6).
- A single read chokepoint. This plan does not touch `IWorkspaceReader` or its `ReadSource` design.
- Changes to the drift watcher, `ExternalFileDrift`, or `_sessionHalted` semantics.
- Writing to `docs/current/TODO.md` / `CLOSED.md`: this plan closes nothing tracked.

## Risks and open decisions

Risks:

- **Subagents share one process and one ledger.** Subagents of the same session share the server (no
  caller identity). Agent A's read satisfies agent B's check. The ledger therefore guards out-of-band
  changes (editor, git, a different process, the watcher gap) well and inter-agent overwrites weakly. See
  open question 1.
- **Partial reads treated as whole-file observation.** A line-range, member or single search hit marks
  the whole file observed (decision 2). An agent that read lines 1-20 and writes the whole file passes the
  check even if line 300 changed meanwhile. This is the largest hole in the decision, accepted for v1 because
  per-range tracking multiplies complexity for weak models.
- **Hash cost on large files.** SHA-256 over file text on every recorded read. `ReadFile` has an inline
  budget and returns an outline for large files (not recorded), search records only matched documents, so
  the cost is bounded, but the `memberName` branch now needs an extra whole-file fetch.
- **Hash-domain mismatch (EOL/BOM).** The ledger compares text from three sources (document text, disk
  pre-image via `ReadAllTextIfExistsAsync`, text returned to caller). Verified: the post-write resync
  re-reads disk, so memory equals disk after a write; NOT verified: BOM stripping and EOL handling are
  identical between `ReadAllTextIfExistsAsync` and `ReadAllTextAsync`. A mismatch would produce false
  `StaleRead` refusals. Rows A, G, H exist to catch this; step 3a should add a test before any tool opts
  in.
- **Window behavior.** During the 500 ms debounce window a re-read returns the stale in-memory text, the
  disk pre-image still differs, and the retry is refused again until the sync lands. The refusal message
  should say "re-read; if refused again, wait a moment" so a weak model does not loop.
- **Staged writes.** If `proposal_staged_writes.md` is built, `Committed` and `IncludeStaged` text
  differ and the ledger needs a hash per snapshot (F5).
- **Convention, not enforcement.** A new read tool that forgets `RecordObserved` silently leaves its files
  unprotected (unobserved targets are allowed). No analyzer enforces it; same status as the write-path rule.
- **Recording is a side effect on read paths** that the write-path docs have so far treated as pure.

### Open questions / conflicts

1. **Own-write refresh vs the "blind overwrites" goal.** Decision 4 refreshes the ledger after every
   successful chokepoint write for paths already in the ledger. With a process-wide ledger and no caller
   identity, agent B's `Member`/`ReplaceSnippet` edit refreshes the entry and masks the staleness of agent
   A's earlier read of that file. Two readings: (a) refresh only for opted-in writes, which catches the
   A/B case but refuses an agent after its own anchor edit until it re-reads (nagging, but safe);
   (b) refresh on every write, as decided, which never nags but weakens the inter-agent guarantee. The
   decision stands as written; this is flagged because the stated goal ("blind overwrites") is only
   partly met under (b). Needs an owner decision, or F6.
2. **Conflict with `external-drift-hard-blocker.md`** (superseded): its rationale rejects per-file,
   actionable blocking. Reconciled only for the coexisting design; F1 reopens it.
3. **Tension with `design_read_chokepoint.md`:** explicit `RecordObserved` calls instead of the read gate.
   Is a future read tool expected to be forced through `IWorkspaceReader`? If yes, `RecordObserved` should
   migrate there. Open.
4. **`RecordObserved` placement** (`IObservedContentLedger` vs `IWorkspaceReader`) is my recommendation, not
   a decision. Blast radius is equal (2 implementers); the choice is about cohesion.
5. **Derived, not decided by the brief:** delete removes the ledger entry; rollback restores it;
   whole-batch refusal; a missing disk file with a ledger entry counts as stale; `WriteFile` opts in for
   ReplaceFile only; the new parameter is appended after `exactRestore`; step 3a places the stale check
   after the drift check and pre-image capture (so the drift latch takes precedence); the `observed:false`
   field (step 6). Each should be confirmed at review.
6. **Ledger lifetime on reload.** `LoadSolutionAsync` clears and repopulates `_knownFileHashes`
   (`PopulateKnownFileHashes`, PWM 426-459, called at 634) but the plan does not clear `_observedHashes`.
   Content-hash comparison keeps stale entries safe (they fail the compare), but entries for files that
   leave the solution live for the process lifetime. Acceptable? Open.
7. **Cross-check of survey figures:** `GetSolutionAsync` callers, the interface implementer counts and the
   `ApplyProposedChangesAsync` caller counts above were re-measured on 2026-10-18 with
   `Search(mode: callers/implementations)`. The `reference_architecture_map.md` figure (389 call sites in
   78 files) is for `GetCurrentSolutionAsync` on 2026-09-24 and is not comparable.
