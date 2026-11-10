# Plan: extend the stale-read guard (F3 anchor and engine tools, F4 outline/InspectSymbol), replace the drift latch with per-file protection (F1), add a cross-process write lock and fold-on-read (F7); F5 blocked

**Status:** IN PROGRESS 2026-11-09. Steps 1-6 and 8a-8d done (20440d38, a10b26ee, e95c2b15, 458f2fa3, d5fe3d6b, 96996473, f45f07c8, 4a73f333, 088220fb, a170f724, 46f61b62, 333a0740, 233a04ab, fe1afbee, b57208a7, 5a635e5e, d914fa1b, 374c2636, 3e4ed6fa, 1c9cb4db); Step 7 merged into 8e. Batched run with the owner present; F5 stays blocked on staged writes. Builds on [plan_read_ledger_stale_read_guard.md](closed/plan_read_ledger_stale_read_guard.md) (IMPLEMENTED 2026-11-09, 7b703f18..5d9ee8d2). Per-file attribution of refusals is deferred to [proposal_cross_session_write_journal.md](../proposals/proposal_cross_session_write_journal.md) ("Revision 2026-11-09"); this plan leaves it a lock, a holder record and a message-builder hook.

## Problem

The stale-read guard shipped for three tools only. A write that reaches the chokepoint with the default
policy is never compared to what the writing agent last read, so the most-used edit tools can still write a
whole file built from text the agent (or the in-memory workspace) no longer shares with disk:

- `ReplaceSnippet` single and batch call `ApplyProposedChangesAsync(..., validateChanges: validateOnApply)` with no policy
  (`Tools.Basic/WorkspaceFileEditImpl.cs:787`, `:1047`).
- `ApplyDiff` diff format and `ApplyUnifiedDiff` likewise (`Tools.Basic/WholeFileWriteTools.cs:562`, `:697`).
- Every engine tool that writes through `ValidateAndApplyHelper` gets `None`: the helper's one chokepoint call
  (`Common/ValidateAndApplyHelper.cs:194`) passes no policy. That is `Member`, `RenameSymbol`, `MoveMember`,
  `ModifyModifier`, `ChangeSignature`, `ExtractMethod`, `SemanticFindReplace` and the rest (55 tool-level call sites, measured below).
- The Asyncify, Commenting, format and `DeleteUnusedSymbol` tools call the chokepoint directly with `None` (34 sites).

Mechanism (from the closed plan, Problem item 1, not re-derived here): an anchor or engine tool computes the
whole-file text from the in-memory document and the chokepoint writes it; inside the 500 ms debounce window
the in-memory text is older than disk, so the write overwrites an external change. The closed plan's claim that
anchors "give partial protection (a stale anchor usually fails to match)" covers a changed anchor line, not a change elsewhere in the
file. `reference_file_watcher_and_drift_detection.md` (Gotchas, last two lines of the stale-read bullet) records the same scope limit.

A second defect makes opting in unsafe without work: the three anchor/diff call sites above have no branch for a
non-validation refusal. `ApplyDiff` diff format (`WholeFileWriteTools.cs:562-566`), `ApplyUnifiedDiff` (`:697-701`),
`ReplaceSnippet` (`WorkspaceFileEditImpl.cs:787-`) and `ReplaceSnippetBatch` (`:1047-`) test only
`result.IsError && result.ValidationResult != null`; anything else falls through to `IsError = false`. The files-format branch had the same
hole and was fixed in step 5 of the closed plan (`WholeFileWriteTools.cs:425-438`). Passing the policy at these sites without a mapping would report a
`StaleRead` refusal as success. That is an environment defect (the tool surface hides a refusal from a weak model), not a model error.

### Scope extension 2026-11-09: the drift latch and the non-atomic write (F1, F7)

Two further defects were raised after the first draft; both are in this plan now.

- **The drift latch punishes the wrong party (F1).** `ApplyProposedChangesAsync` sets `_sessionHalted = true` and throws `SessionHaltedException` when ANY target is in `_externalChanges`
  (`Common/PersistentWorkspaceManager.cs:1958-1964`, setter `:1962`). That halts every later write by every agent in the session, including agents that never read or wrote the drifted file. Since the stale-read guard
  shipped, drift on a file an agent read or edited is already caught per agent by `StaleRead`; the latch is now the blunt second layer. The owner decision (option d) is that drift only matters for files the agent read or edited.
- **Writes are neither atomic nor cross-process exclusive (F7).** `FileIoHelper.WriteAllTextAsync` is `File.WriteAllTextAsync` (`Common/FileIoHelper.cs:52`), which truncates and then writes; the only exclusion is the in-process `FilePathLock`.
  Two server processes on one solution (peer sessions on one working tree; one stdio process per window) can interleave a truncate with another process's pre-image read, and the debounce fold reads disk with no exclusion at all.
  Removing the drift halt (F1) removes the only thing that stopped a writer after such a torn event, so F1 and F7 are one safety story: F1 narrows the blast radius, F7 closes the race window F1 stops covering.

## Decision

Owner decisions of 2026-11-09 (first set and the second set the same day). Everything not marked "Open" is decided.

**F3 - anchor and engine tools opt in.**
- `ReplaceSnippet` (single and batch), `ApplyDiff` diff format, `ApplyUnifiedDiff`, and every engine write tool
  (`Member`, `RenameSymbol`, `MoveMember` and all other tools that write through `ValidateAndApplyHelper` or call
  `ApplyProposedChangesAsync` directly) pass a `StaleReadPolicy` that refuses. A target the writer read and that changed
  since is refused with `ToolErrorCode.StaleRead`, whole batch, non-latching (existing chokepoint behavior).
- Engine tools do NOT emit the "not read first" finding (noise on multi-file edits). Anchor tools keep emitting it,
  as `WriteFile` does.
- `UndoLastApply` / `exactRestore` and `CreateFile` stay exempt.
- Each tool family maps the refusal to a `ResultError` with code `StaleRead`. The mapping helper moves one layer down
  (to `Common`) because `Tools.Advanced` cannot see `Tools.Basic` (layering: Common <- Engines.* <- Tools.* <- Server.*; siblings cannot see each other).

**F4 - recording widens.** `GetFileOutline` records its file's whole-text hash. `InspectSymbol` records the declaring
file of the returned symbol (whole-file hash). `ListAll` does NOT record. `InspectSymbol` in `blastRadius` mode records nothing (accepted recommendation, was open decision 2).

**F5 - blocked** on staged writes ([proposal_staged_writes.md](../proposals/proposal_staged_writes.md), not built). See Out of scope.

**Accepted recommendations (owner, 2026-11-09):**
1. Second enum value `StaleReadPolicy.RefuseIfObservedStaleQuiet` for quiet mode (not a bool, not stripping the finding after the fact). Measured cost: 3 production declarations already carry the parameter and 2 equality tests gate on it
   (`PersistentWorkspaceManager.cs:2046` region, `FakeWorkspaceManager.cs:200`) plus the finding add at the `unobservedTargets` block; about 5 edits in 3 files.
2. `ValidateAndApplyHelper` opts in by default (`staleReadPolicy = RefuseIfObservedStaleQuiet`), with a code comment explaining the deviation from the chokepoint's default-`None` pattern. 3 edits in 1 file; 55 tool-level sites reached with no edit.
3. The ratchet test is built (Step 5e is no longer a recommendation). Precedent: `BareFileWriteRatchetTests`.
4. Asyncify and `AsyncBatchEngine` batch tools carry the `StaleRead` refusal in their summary text (no per-candidate typed code), plus the typed code where a site already forwards `RefusalCode` (was open decision 3).
5. `ReplaceSnippetBatch` stays non-quiet (keeps the "not read first" finding like the other anchor tools; was open decision 6).

**F1 - decided 2026-11-09, option (d): drift only matters for files the agent read or edited.**
- Writes CREATE the writer's observed entry. This reverses decision 4 of the closed plan (a write only refreshed an EXISTING entry; `StaleReadLedgerRefreshTests.AgentWithNoEntry_StaysUnobservedAcrossWrites`, `:128`, pins the old behavior).
  Engine tools create entries for the files they wrote. Whether a delete creates anything is Open (coordinator recommends no; Step 8a states the same).
- The external-drift check in `ApplyProposedChangesAsync` no longer sets `_sessionHalted` and no longer throws `SessionHaltedException`. Drift on a file the agent read or edited is caught per agent by `StaleRead`. A drifted file the writer never touched is written without a refusal.
- New non-latching debounce-window guard for every policy except `exactRestore`: if a target's disk pre-image differs from its in-memory document text, refuse the whole batch with a new code (proposed `ToolErrorCode.FoldPending`) and the message
  "<file> is changing on disk; retry in a moment". Uses the in-memory lookup of the stale-read check. A new file has no document and is not checked.
- Session halt stays for the unrecoverable breaker (`:1888`). `_externalChanges` stays for `ExternalFileDrift(List/Status)` visibility. `Acknowledge` becomes a manual override (what it still overrides: Open, see Risks and open decisions 1-2). Docs, tool descriptions and prompt text that tell agents to acknowledge on halt are updated (Step 8d, 8e).
- Existing tests that expect `SessionHalted` from drift are updated or inverted (named in Step 8c). Per-file attribution comes later from the journal proposal: Step 2 leaves a `// TODO` hook in the message builder; no attribution step here.

**F7 - decided 2026-11-09: cross-process write lock, lock in the debounce fold, fold-on-read.**
- Lock: an OS-held exclusive `FileStream` (`FileShare.None`) on `<solution root>/.roslynsentinel/write.lock`, holder metadata (session, agent, tool, reason, UTC since) outside the locked file (Step 9a explains why a sidecar). Taken INSIDE `_solutionLock`, always in that order.
  Held from pre-image capture through file writes and in-memory resync. Never held during compile validation. Bounded wait (10 s suggested, configurable), then a non-latching `WriteLockBusy` refusal naming the holder. Tests use per-temp-root locks.
- Debounce fold (`OnDebounceTimerElapsed`) takes the lock with a short bounded wait before reading disk. NOT temp-file-plus-rename: the watcher treats `Renamed` as drift.
- Fold-on-read: `ReadFile`, `GetMethodSource` and `Search` text mode synchronously fold the one pending or differing file first (under `_solutionLock`, briefly under the write lock), then record.
- Ordered after F1 (reason in Execution rules).

**Owner, 2026-11-09 (second round):**
- **FoldPending is bounded: refuse once per fold.** Refuse, enqueue the file for a fold; the next attempt after
  that fold proceeds. Owner asked for a normalized-comparison fallback; adopted as a ladder evaluated before
  refusing: (1) exact text equal -> proceed; (2) equal after stripping a BOM, normalizing EOLs and trimming
  trailing whitespace per line -> treat as converged, proceed with an Info finding; (3) otherwise refuse once
  per fold, then proceed with a Finding pointing at `LoadSolution(forceReload: true)`. NOT a Roslyn
  `IsEquivalentTo`/syntax-equivalence compare: it ignores all trivia including comments, so an external
  comment edit would count as "equal" and be silently overwritten by the in-memory-based write.
- **Acknowledge after 8c clears the visibility list only.** Descriptions, docs and the peer-coordination skill
  stop telling agents to acknowledge on a halt.
- **CLAUDE.md may be edited in step 8e** (replace the drift-halt recovery line; run
  `scripts/Test-GuidanceHygiene.ps1`).
- **Accepted defaults:** no halt on watcher overflow (overflow already forces a full reload; the earlier brief's
  premise was wrong); the up-to-10 s lock wait inside `_solutionLock` is accepted for v1; the blob write stays
  outside the lock (accepted gap, the journal proposal closes it).
- **9f replaced:** apply pending reloads (`_pendingChanges`) before `Search`, `ReadFile` and `GetMethodSource`,
  instead of folding each returned Search document (see Step 9f).

**Owner, 2026-11-09 (third round, after the pre-implementation review):**
- **Leftovers accepted as recommended:** open decisions 4 (no FoldPending check for a target with no document or
  no disk pre-image), 5 (release the write lock before a full reload), 7 (holder = `AgentId`, `SessionId`,
  `ToolName`, `Reason` from `ToolCallContext`, all verified to exist, `ToolCallContext.cs`, built at
  `Server.Advanced/AgentIdentityCapture.cs:53`; may be null; the debounce thread uses a synthetic holder; 10 s default
  in an options record), 8 (cap the stale list at 10 plus "and N more", full list in `StructuredDetail`), 9 (leave
  the `// TODO` at the four anchor sites). A delete creates no ledger entry (current removal kept).
- **8a rollback hole (review defect 4):** the success refresh at PWM `:2221` runs inside the write loop before any
  rollback, so a rolled-back batch would keep a created entry. Decision: create-on-write is deferred until after the
  rollback decision (batch known to have succeeded); inside the loop only existing entries are updated.
- **8b self-heal:** fold the refused files inline, under the `_solutionLock` the chokepoint already holds, at the
  moment of refusal (not enqueue plus timer: that never completes for `SetTestSolution` managers, whose debounce
  handler returns early, and makes refuse-once timing-dependent). "Refuse once per fold" state lives beside
  `_unfoldedFiles` and is cleared in `NoteFileFolded`. Reuse `WorkspaceStaleText.Recovery` for the
  `LoadSolution(forceReload: true)` hint.
- **Step 5 reshaped (review defects 1-3):** 5d runs FIRST and is rewritten (see Step 5d). The three Asyncify sites that
  ignore `IsError` (`AsyncifyTools.cs` `:1741` AddCancellationToken, `:3632` HandlerToAsync, `:3848` ExtractEventHandlers)
  already misreport validation failures and rollbacks as success today; they are fixed to respect `IsError`.
  `MsToolAugmentEngine` is fixed too (owner chose "fix Ms too"): the sort-usings path stops discarding the apply
  result and `UsingsCleanupResult` gains a way to report a refused write (Step 5c-b, DTO change).
- **9h:** the "Added, not loaded yet" note reads the named file from disk (foreign blob under 15 s old) instead of
  `AfterSource`, which chokepoint blobs never store. 9h-2 is two slices: text mode, then a zero-result post-process in
  `DispatchSearch` for the other modes. Storing `AfterSource` in blobs is a separate idea doc, not this plan.
- **Run:** owner present, Batched mode.

**Owner, 2026-11-09 (fourth round, after B6):** FoldPending does NOT refuse when the proposed content already equals
the disk pre-image (a no-op write): fold inline and proceed. A refusal there costs a weak model a round-trip for
nothing. Slice 8b-3 (start of B7) makes the change and reverts fixture edits that existed only for this case; it also
adds the missing test for the persisting-mismatch Warning path. Also standing from this run: per slice filtered test
runs, one full run per batch, code and tests in one dispatch when they fit (`dispatch-implementer`, "Tests and test runs").

**Open, with my recommendation (not decided):** none; Risks and open decisions 1-10 are all settled above.

## Execution rules

- Follow `templates/plan.md` order. One slice at a time, compile-green after each. Dispatch sequentially, never in parallel, for C# edits.
  One `Build` / `RunTest` at a time.
- Limits read from `.claude/implementer-tiers.json` on 2026-11-09: depth lane (`implementer`) tier 1 = files <= 3, edits <= 15, acceptance <= 1, tool calls <= 35
  ("D1"); sweep lane (`implementer-sweep`) tier 0 = 1 operation, files <= 8, call sites <= 25, projects <= 2, tool calls <= 30 ("S0"). Each step states its measured shape.
- Migration-first: new enum value, new mapper and new helper beside the old; `WholeFileWriteTools.StaleReadError` is kept as a thin delegate until the new mapper is proven, then its three call sites are moved (Step 2b), `// TODO` marker until then.
- Test baseline is zero known failures (`docs/known-failing-tests.*`); any failing test is new. Slices that flip behavior name the tests they invert; the implementer is responsible only for a green build and the tests it is given,
  so the brief must list those tests. A coverage gap that survives an all-green run is a planner gap (this plan did not specify the test) or a reviewer gap (the delivered test does not cover the changed path), not an implementer failure.
- Exclude `*/Worktree/` folders from searches and reviews. Line numbers below are as of 2026-11-09; the source wins if they differ (the Step 1-5 numbers were taken before this revision and the PWM chokepoint lines shifted by about 80 between the two measurements).
- Do not edit `docs/current/TODO.md` or `CLAUDE.md` from this plan; the owner adds the F5 TODO line, and CLAUDE.md carries a drift-halt recovery line that Step 8e reports instead of editing (Out of scope).
- **Order: F3/F4 (Steps 1-7), then F1 (Step 8), then F7 (Step 9).** Why F7 after F1: (1) Step 8b's in-memory-versus-disk comparison is the helper that 9c (debounce fold) and 9d (fold-on-read) reuse; building F7 first would duplicate it.
  (2) The `WriteRefusal` mapper (Step 2) must carry `FoldPending` and `WriteLockBusy` together, so both codes land in tool results through one mapping. (3) F1 is independently shippable and fully reversible; F7 adds a cross-process dependency (a lock file, bounded waits)
  that should land on a quiet chokepoint. F7 does not need to wait for F1 to compile; the order is for reuse and risk. Accepted exposure between 8c and 9b: with the halt gone and no lock, a torn cross-process write can be seen by another process's pre-image read; `FoldPending` (8b) catches the in-memory-differs-from-disk case but not a half-written file that happens to equal the in-memory text. Keep 8c and 9b close together in time.
- **Lock-order and re-entrancy rules for F7** (every F7 slice brief must restate these): (a) always `_solutionLock` first, then the OS write lock; never the reverse. (b) The OS lock is a `FileShare.None` `FileStream`: it is NOT re-entrant, a second open from the same process fails like another process's. So the lock is acquired only at entry points
  (chokepoint, debounce handler, fold-on-read), and `ApplyInMemoryDocumentUpdatesAsync` NEVER acquires it; its two callers (`:1455` debounce, `:2316` chokepoint) already run under `_solutionLock` and own the lock decision. (c) Never held across compile validation or a full MSBuild reload.

- **Batches (review 2026-11-09), one orchestrator each, in order:** B1 Steps 1, 2, 2b, 3 (full `Tests.Basic` + `Tests.Tools.Basic`
  after 3); B2 Step 4; B3 Steps 5d, 5a, 5b, 5e (5d first); B4 Steps 5c-a, 5c-b; B5 Steps 6a, 6b, then Step 7 docs (main session);
  B6 Steps 8a, 8b-1, 8b-2, 8c, 8d, then 8e docs; B7 Steps 9a, 9b, 9c, 9d + 9f-1, 9e + 9f-2; B8 Steps 9h-1, 9h-2a, 9h-2b, then 9g docs.

## Call-site measurement (2026-11-09, `Search` text/callers, production projects only)

`ApplyProposedChangesAsync(` text hits: 50 in 11 files (+96 in test projects: Tests 11, Tests.Basic 62, Battery.Basic 10, Tools.Basic 13, hidden unless `includeTests`).

| Class of hit | Count | Where |
| --- | --- | --- |
| Declarations / internal | 4 | `IWorkspaceMutator.cs:24`, `PersistentWorkspaceManager.cs:1854`, PWM `RetryFailedChangesAsync` `:2941` (default policy, cache replay), `ValidateAndApplyHelper.cs:194` |
| Dead code inside a block comment | 3 | `Tools.Basic/WorkspaceTools.cs:214, 279, 387` (block comment `:184`-`:455`, the retired `ApplyDiffWithConfirmationCodeTool`); NOT call sites |
| Direct, already opted in (closed plan) | 3 | `WholeFileWriteTools.cs` WriteFile `:76`, DeleteFile `:140`, ApplyDiff files `:425` |
| Direct, exempt (stay on `None`) | 2 | `WorkspaceFileEditImpl.cs` UndoLastApply `:142`, CreateFile `:1164` |
| Direct, anchor tools (Step 4) | 4 | `WorkspaceFileEditImpl.cs:787` ReplaceSnippet, `:1047` ReplaceSnippetBatch; `WholeFileWriteTools.cs:562` ApplyDiff diff, `:697` ApplyUnifiedDiff |
| Direct, remaining (Step 5) | 34 | `Tools.Advanced/AsyncifyTools.cs` 18 (project Tools.Advanced); `Engines.Advanced/AsyncBatchEngine.cs` 11 (Engines.Advanced); `Tools.Advanced/CommentingTools.cs` 2; `Engines.Basic/MsToolAugmentEngine.cs` 2 (`:623` SortAndDeduplicateUsings, `:768` FormatDocumentSafeAsync); `Tools.Basic/WorkspaceProjectManagementImpl.cs:837` DeleteUnusedSymbol 1 |

Check: 50 - 4 - 3 = 43 live direct sites; 43 - 3 - 2 - 4 = 34.

`ValidateAndApplyHelper.ValidateAndApplyAsync(` text hits: 65 in 10 files (+11 in tests: Tests.Basic 7, Battery.Basic 4). 1 helper declaration; 5 private per-class wrappers
(`Tools.Advanced/AdvancedRefactoringTools.cs:97`, `GenerationTools.cs:60`; `Tools.Basic/RefactoringExtractionDocsImpl.cs:42`, `RefactoringSignatureImpl.cs:46`, `RefactoringStructuralImpl.cs:55`),
each with one body call to the helper; 51 call sites of the wrappers (AdvancedRefactoringTools 26, RefactoringStructuralImpl 16, RefactoringExtractionDocsImpl 4,
RefactoringSignatureImpl 4, GenerationTools 1); 4 direct helper callers (`Engines.Basic/TestCategoryApplyEngine.cs:147`, `Tools.Basic/NamedArgumentsTools.cs:136`,
`QualifyCrefsImpl.cs:176`, `SemanticFindReplaceTools.cs:220`). Tool-level sites reached by one helper edit: 55, in 3 projects (Tools.Advanced, Tools.Basic, Engines.Basic).
The helper maps any chokepoint `RefusalCode` generically (`new ResultError(applyResult.RefusalCode, applyResult.Summary)`, `ValidateAndApplyHelper.cs` ~197-211), but that message
names only the count and the first file (PWM `:2087` region), unlike `StaleReadError` which lists every file. The `dryRun` branch returns before the chokepoint
(`ValidateAndApplyHelper.cs` ~157-162), so a dry run is never stale-checked.

### F1 / F7 measurement (2026-11-09)

| Fact | Evidence |
| --- | --- |
| Only setter of `_sessionHalted` is the drift check | `PersistentWorkspaceManager.cs:1962`; only other writer is `ClearSessionHalt` `:511`; reads at `:42-45`, `:502` (`IsSessionHalted`), `:1874-1876` (entry throw) |
| The unrecoverable breaker halts through a separate throw, not `_sessionHalted` | `PersistentWorkspaceManager.cs:1888` `throw new SessionHaltedException(unrecoverableHalt)`; pinned by `Tests.Battery.Basic/UnrecoverableBreakerTests.cs:117` |
| Watcher overflow does NOT halt the session | `_watcherOverflowed` forces a full reload only (no `_sessionHalted` write exists outside `:1962`). The brief's "halt stays for watcher overflow" has nothing to preserve; see open decision 1 |
| Surfaces that read the drift halt | `IsSessionHalted` is read by `Common/HaltInfo.cs:62`, `Common/IdleRestartPolicy.cs:77-79` (guard `sessionHalted`), `Server.Advanced/IdleRestartMonitor.cs:81`, `Server.Advanced/HaltStamp.cs` (stamps `isSessionHalted`), `Tools.Basic/ServerStatusTools.cs` (2 hits). After Step 8c, `_sessionHalted` can never become true, so all of them report false for the drift kind (breaker uses its own path) |
| Observed-ledger writers | `RecordObserved` `:524` (upsert); `RefreshObservedForWriter` `:540-545` (update-only: `TryGetValue` then `TryUpdate`), called at `:2221` (after a successful write) and `:2284` (rollback restore, hash of `original`); `RemoveObservedForAllAgents` `:552-554` |
| Tests that call `AddExternalChangeForTest` | `Tests.Basic/StaleReadGuardTests.cs:236` only (1 test, `DriftFlaggedTarget_WithStaleLedgerEntry_LatchWins_ThrowsSessionHalted`; the neighboring `SessionHaltTripped_WinsOverStaleLedgerEntry_ThrowsSessionHaltedException`, `:199-212`, trips the halt another way and is also in scope). Seam defined `PersistentWorkspaceManager.cs:430` |
| Tests that assert `SessionHalted` text or code | `Tests.Tools.Basic/TestCategoryApplyTests.cs:265-273` (fake throws `SessionHaltedException`; stays valid, breaker still exists); `CreateProjectOwnWritesTests.cs:46` (comment and `DoesNotThrowAsync` for own-writes-are-not-drift; stays valid, the test becomes weaker evidence). Not measured: the `DeleteFile_DriftedFile` test in `CreateFileDeleteFileTests.cs` (~`:385-432` per the earlier reading) uses a seam other than `AddExternalChangeForTest`; re-measure at the Step 8c dispatch |
| Acknowledge / halt-recovery text outside code | docs: `reference_file_watcher_and_drift_detection.md`, `reference_code_file_write_paths.md`, `reference_architecture_map.md`, `reference_changing_claude_lean_tools.md`, `reference_tool_terminology_refinement.md`, `proposal_create_project_register_own_writes.md`, `proposal_cross_session_write_journal.md`; agent-facing: `CLAUDE.md` (the "If halted: ExternalFileDrift(operation: List) then Acknowledge" line under tracked-file deletes), `.claude/skills/peer-coordination/SKILL.md`. C#: `DriftMessages.cs`, `ToolException.cs`, `AdminTools.cs` (`:41`, `:44`, `:46`), `WorkspaceTools.cs`, tests `AdminToolsTests.cs`, `AdminToolsLedgerTests.cs`, `BatteryTwentyTests.cs`, `ConvertedToolHintTests.cs`, `CreateFileDeleteFileTests.cs` |
| Solution root for the lock | `PersistentWorkspaceManager.GetSolutionRoot()` (`:1570`). A `SetTestSolution` workspace has `CurrentSolution` but no path, so `GetSolutionRoot()` returns null (`:3063-3064`): the ~675 `SetTestSolution` usages of the earlier measurement would have no lock location. Rule in Step 9b: null root = no OS lock (in-process `_solutionLock` only); lock tests inject a root through a new internal seam |
| `.roslynsentinel/` precedent | `largeresults` already live under `<solution root>/.roslynsentinel/` and are swept at load (`PersistentWorkspaceManager.cs:955`, `LargeResultHelper.SweepExpired`) |
| Writers outside the chokepoint | `FileIoHelper.WriteAllTextAsync` `FileIoHelper.cs:52` (in-process `FilePathLock` only); `EolFileWriter` (`Common/EolFileWriter.cs`), called from `MsToolAugmentEngine.FormatDocumentSafeAsync` `:782` (`EolFileWriter.WriteAllTextResolvedAsync`, the no-solution-loaded fallback of the format engine); `OperationBlobWriter` `:132`, `:192` (blob records, `ValidateAndApplyHelper.cs:214`); `LargeResultHelper:84`, `MigrationLedger:197`. Only the first two write source files |
| Debounce handler shape | `OnDebounceTimerElapsed` `:1353-1535`: takes `_solutionLock` `:1366`, folds with `ApplyInMemoryDocumentUpdatesAsync` `:1455`, releases `:1535`. The chokepoint calls the same fold at `:2316` under its own `_solutionLock` (`:1985`, release `:2380`) |
| Chokepoint order (relevant slice) | after `_solutionLock` `:1985`: concurrent-edit check, `RevalidateAfterForeignWriteAsync`, pre-image capture, stale-read check (`:2046` region), EOL pre-pass, deletes, writes, `RefreshObservedForWriter` `:2221`, rollback `:2284`, resync `:2316`, finally release `:2380` |

## Steps

Order: policy value, shared mapper, helper (engine tools), anchor tools, direct callers, ratchet, recording, F3/F4 docs (Steps 1-7); then F1 (Step 8); then F7 (Step 9). Steps 1-3 land before any tool is behaviorally
changed except Step 2's pure move.

### Step 1 - `StaleReadPolicy.RefuseIfObservedStaleQuiet` in both managers
- Done: 20440d38
- Files (3): `RoslynSentinel.Common/IWorkspaceMutator.cs`, `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`.
- Symbols: enum `StaleReadPolicy` (`IWorkspaceMutator.cs:4`); PWM `ApplyProposedChangesAsync` stale block (`:2046` region) and unobserved add (`:2054-2056` region); fake `ApplyProposedChangesAsync` (`:200`).
- Change (~5 edits): add the enum value with a doc comment ("same refusal as RefuseIfObservedStale; does not report unobserved targets; for engine tools that edit many files");
  PWM and fake test `staleReadPolicy != StaleReadPolicy.None && !exactRestore`; PWM adds to `unobservedTargets` only when the policy is `RefuseIfObservedStale`. The fake emits no
  unobserved finding today, so it needs no second change.
- Call-site measurement: 0 callers change (new value, existing parameter). The only equality tests on the enum are the 2 listed (text search `StaleReadPolicy|staleReadPolicy` 2026-11-09: 26 hits in 6 files, 17 in tests).
- Tier: D1 (3 files, ~5 edits, 1 acceptance).
- Done when: full-solution `Build` 0 errors; all existing `StaleReadGuardTests` / `StaleReadLedgerRefreshTests` pass unchanged (they use only `None` and `RefuseIfObservedStale`).
- Out of scope: any tool passing the new value; mapping.
- Test-writer step (T1): rows A, B in Test strategy. Real PWM for A; the fake copy of the check is separate and a fake test does not cover production.

### Step 2 - shared non-latching refusal mapper `WriteRefusal` in `Common`
- Done: a10b26ee, e95c2b15 (tool-hint ratchet fix)
Generalized from the first draft's `StaleReadRefusal`: one mapper for every non-latching chokepoint refusal, so the later codes (`FoldPending`, `WriteLockBusy`) reach every tool through the same call and add no tool-site edits.
- Files (2): `RoslynSentinel.Common/WriteRefusal.cs` (new), `RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs`.
- Symbols: new `public static class WriteRefusal { public static bool TryMap(string toolName, ApplyChangesResult result, out ResultError error) }`. This step maps only `ToolErrorCode.StaleRead`; later steps add `FoldPending` (8b) and `WriteLockBusy` (9a) as further cases of the same switch.
  Existing `WholeFileWriteTools.StaleReadError` (`:309-315`) becomes a one-line delegate.
- Change (~4 edits): move the body of `StaleReadError` verbatim (message names every file in `result.FailedFiles`, the `ToolCallHint.Call("ReadFile", ...)` instruction, "if refused again, wait a moment");
  guard an empty `FailedFiles` (today `.Keys.First()` throws on an empty dictionary). Leave a `// TODO per-file attribution: name the writer that changed each file once proposal_cross_session_write_journal.md lands` at the message builder (owner decision: attribution later, hook now).
  Recommended, not decided (open decision 8): when more than 10 files are stale, list the first 10 plus "and N more" and put the full list in `StructuredDetail`, because a project-wide rename can name hundreds of files.
- Call-site measurement: `StaleReadError` has 3 callers, all in `WholeFileWriteTools.cs` (`:78`, `:146`, `:427`); they stay unchanged this slice.
- Tier: D1 (2 files, ~4 edits).
- Done when: `Build` 0 errors; `StaleReadWriteToolsTests` rows N still pass (message still contains the file name, `ReadFile`, `wait a moment`).
- Out of scope: moving the 3 callers (Step 2b); any opt-in.
- Review 2026-11-09: `TryMap` returns true only when `result.IsError` and the code is mapped (today's call sites test only `RefusalCode`). The cap (decision 8) is adopted.
- Test-writer step (T2): mapper unit tests: single file, many files (cap if adopted), empty `FailedFiles` does not throw, code is `StaleRead` not `Exception`, an unmapped code returns false.

### Step 2b - move the three `WholeFileWriteTools` callers to `WriteRefusal.TryMap`
- Done: 458f2fa3
- Files (1): `RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs`.
- Change (~6 edits): at `:78` (WriteFile), `:146` (DeleteFile), `:427` (ApplyDiff files format) replace the `StaleRead`-specific branch with `if (WriteRefusal.TryMap("<ToolName>", result, out var refusal))` returning the same result type; delete `StaleReadError`. Reason this is required, not cosmetic: without it, a `FoldPending` or `WriteLockBusy` refusal reaching `WriteFile` falls to whatever generic branch the site has today (not measured; the files-format fix at `:425-438` handled only `StaleRead`).
- Call-site measurement: 3 callers, 1 file, 1 project. Tier: D1. Done when: `Build` 0 errors; `StaleReadWriteToolsTests` pass unchanged. Out of scope: other tools.
- Test-writer step (T2b): none beyond the existing `StaleReadWriteToolsTests`; they exercise all three moved sites (WriteFile, DeleteFile, ApplyDiff files). Say so in the brief.

### Step 3 - engine tools opt in once, through `ValidateAndApplyHelper`
- Done: d5fe3d6b. Known and accepted: the helper's compile gate runs before the chokepoint, so a stale AND compile-breaking edit reports the compile error, not `StaleRead`.
- Files (1): `RoslynSentinel.Common/ValidateAndApplyHelper.cs`.
- Symbols: `ValidateAndApplyHelper.ValidateAndApplyAsync` (declaration `:78`, chokepoint call `:194`, refusal mapping ~`:197-211`).
- Change (~3 edits): add optional last parameter `StaleReadPolicy staleReadPolicy = StaleReadPolicy.RefuseIfObservedStaleQuiet` with a comment that the default deviates from
  the chokepoint's default-`None` pattern on purpose (owner decision 2026-11-09: engine tools opt in); pass it at `:194`; in the `applyResult.IsError` branch, when
  `WriteRefusal.TryMap(operationName, applyResult, out var refusal)` is true use `refusal` instead of the generic
  `new ResultError(applyResult.RefusalCode, applyResult.Summary)` so every stale file is named (and, later, `FoldPending` / `WriteLockBusy` need no edit here). The existing `refusalFindings` attribution lookup keeps working.
- Call-site measurement: 0 call sites edited, 55 tool-level sites in 3 projects behave as opted in (5 wrapper bodies + 4 direct helper callers + the 51 wrapper callers).
  Compile effect: none (optional last parameter). Behavioral effect: every one of the 55 sites now refuses a stale observed target, including bulk tools
  (`SemanticFindReplace`, `QualifyCrefs`, `NamedArguments`, `TestCategoryApply`, `RenameSymbol`); see Risks.
- Tier: D1 (1 file, ~3 edits).
- Done when: `Build` 0 errors; `RunTest` of `Tests.Basic` and `Tests.Tools.Basic` shows no new failure against the baseline (this step flips behavior for 55 sites, so a green build alone is not evidence).
- Out of scope: dry run (never reaches the chokepoint), `UndoLastApply`, any direct caller.
- Test-writer step (T3): rows C, D, E, N, K (helper path) in Test strategy.

### Step 4 - anchor tools opt in: `ReplaceSnippet` (single, batch), `ApplyDiff` diff format, `ApplyUnifiedDiff`
- Done: 96996473
- Files (2): `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`, `RoslynSentinel.Tools.Basic/WholeFileWriteTools.cs`.
- Symbols: `ReplaceSnippet` call `:787` and `ReplaceSnippetBatch` call `:1047` (`SentinelCallToolResult<ReplaceSnippetResult>`); `ApplyDiffTool` diff branch `:562`; `ApplyUnifiedDiffTool` `:697`.
- Change (~8 edits): at each of the 4 calls pass `staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale` (non-quiet: anchor tools keep the "not read first" finding, per decision; `ReplaceSnippetBatch` included, accepted);
  directly after each call, before the `ValidationResult != null` check, add
  `if (result.IsError && WriteRefusal.TryMap("<ToolName>", result, out var refusal)) return ... ErrorData = refusal` in each tool's own result type
  (same ordering rule as step 5 of the closed plan: the refusal can carry a `ValidationResult`). Because the call is `TryMap`, these four sites also handle `FoldPending` and `WriteLockBusy` when Steps 8b and 9a add them, with no second edit.
  Leave a `// TODO` at each site for the pre-existing fall-through of other non-validation refusals to `IsError = false` (not fixed here; open decision 9).
- Call-site measurement: 4 of the 4 anchor sites (2 files, 1 project). `UndoLastApply` (`:142`) and `CreateFile` (`:1164`) are in the same file and are not touched.
- Tier: D1 (2 files, ~8 edits, 1 acceptance).
- Done when: `Build` 0 errors; the tests named in T4 that assert the old boundary are inverted in the same dispatch (brief must name them) and `RunTest` of
  `Tests.Tools.Basic` shows no other new failure. If the implementer is given no test list, its code is correct from its vantage point while existing tests fail: that is a planner gap, so the brief lists them.
- Out of scope: the generic `result.IsError` mapping for non-mapped refusals; engine tools; direct callers.
- Test-writer step (T4): rows F, G, K (anchor path). Invert `StaleReadWriteToolsTests.ApplyDiffDiffFormat_StaleEntry_IsNotRefusedAsStaleReadAsync` (`:180-206`) and
  `ApplyUnifiedDiff_StaleEntry_IsNotRefusedAsStaleReadAsync` (`:208-234`) to assert `StaleRead`. Fix `CreateFileDeleteFileTests.ApplyUnifiedDiff_MixedPreImageUnderCrlf_...` (`:1223`,
  `Findings.Count(Source == "WriteChokepoint") == 1`): the test never reads the file, so the new "read them first" finding makes the count 2; filter it as the two EOL tests at `:1108`/`:1133` already do.

### Step 5 - direct chokepoint callers (34 sites): policy `RefuseIfObservedStaleQuiet`
Four independent slices plus the ratchet; any order after Step 1. Each passes `staleReadPolicy: StaleReadPolicy.RefuseIfObservedStaleQuiet` and adds no other behavior.
The sites surface a refusal as text, not a typed code (measured below), so Step 5d handles the exceptions. Accepted: batch tools carry the refusal in summary text; typed code only where a site already forwards `RefusalCode`.

#### Step 5a - `AsyncifyTools.cs` (S0, mechanical)
- Done: 4a73f333 (carries the T5d tests, which need the policy to fire)
- Files (1): `RoslynSentinel.Tools.Advanced/AsyncifyTools.cs`.
- Symbols: 18 `ApplyProposedChangesAsync` calls, `:489`, `:1479`, `:1524`, `:1741`, `:2129`, `:2274`, `:2568`, `:2634`, `:2796`, `:3122`, `:3232`, `:3268`, `:3301`, `:3310`, `:3341`, `:3350`, `:3632`, `:3848`.
- Change: one operation, "add the named argument to every `ApplyProposedChangesAsync` call", 18 sites.
- Call-site measurement: 18 sites, 1 file, 1 project (S0 limit 25 sites, 8 files, 2 projects).
- Tier: S0.
- Done when: `Build` 0 errors; the Step 5e ratchet shows 0 unlisted sites in this file (once built).
- Out of scope: changing any site's error handling.
- Test-writer step (T5a): row H (Asyncify family) plus the ratchet (row L).

#### Step 5b - `AsyncBatchEngine.cs` and `CommentingTools.cs` (S0, mechanical)
- Done: 088220fb. Gap: the BulkComment test covers the seed-phase refusal only, not the per-file "write refused:" branch (follow-up test in B4).
- Files (2): `RoslynSentinel.Engines.Advanced/AsyncBatchEngine.cs` (11 calls: `:566`, `:621`, `:675`, `:724`, `:756`, `:846`, `:882`, `:1218`, `:1925`, `:1953`, `:2099`), `RoslynSentinel.Tools.Advanced/CommentingTools.cs` (2 calls: `:146`, `:307`).
- Change: same single operation, 13 sites.
- Call-site measurement: 13 sites, 2 files, 2 projects (S0 limits met).
- Tier: S0.
- Done when: `Build` 0 errors.
- Out of scope: error handling. Note: of the 36 failure-handling lines measured in `AsyncBatchEngine.cs`, the sites embed `ctApply.Summary` etc. in "Write refused ..." text and ledger records (`_ledger.Record(..., "BridgeApplyFail", ...Summary)`); the PWM `Summary` already begins `StaleRead:` and carries the re-read instruction, so the refusal is visible as text. Sites that forward `RefusalCode` (`:851`, `:1227`, `:1964`, `:2104`) pass `StaleRead` through.
- Test-writer step (T5b): row H (batch-engine family) plus the ratchet.

#### Step 5c - `MsToolAugmentEngine.cs` and `DeleteUnusedSymbol` (D1, one hand edit)
- Done: 5c-a a170f724, 5c-b 46f61b62 (`UsingsCleanupResult.Error`/`ErrorCode`, omitted from JSON when null); BulkComment per-file refusal test b57208a7
- **Review 2026-11-09, split (owner: fix Ms too):** the hypothesis below is false. `MsToolAugmentEngine.cs:623` discards the
  `ApplyChangesResult` and returns `WrittenToDisk: writeToFile`, so a refused sort-usings write reports as written.
  `FormatDocumentSafeAsync` (`:768`) already returns `MsAugmentResult.Fail`, and `MsAugmentResult` has an `ErrorCode`. Callers are
  `Tools.Experimental/CodeTransformTools.cs` `:203`, `:324` (Experimental mode only), which wrap the result in `IsError = false`.
  - **5c-a:** `DeleteUnusedSymbol` only (`WorkspaceProjectManagementImpl.cs:837`): policy plus `TryMap` BEFORE the
    `ValidationResult is not null` check (a StaleRead refusal carries a non-null `ValidationResult`). D1.
  - **5c-b:** Ms engine: policy at both calls; the usings path checks `IsError` and reports the refusal (add an error
    path to `UsingsCleanupResult`: load `dto-conventions`, reuse-first); the format path forwards the refusal code in
    `MsAugmentResult.ErrorCode`; `CodeTransformTools` reports both as tool errors. Measure at dispatch; ~3 files, ~8 edits, D1.
- Files (2): `RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs` (`:623`, `:768`), `RoslynSentinel.Tools.Basic/WorkspaceProjectManagementImpl.cs` (`:837`).
- Change (~4 edits): pass the policy at the 3 calls; in `DeleteUnusedSymbol.ApplyAndRespondAsync` (`:~838-846`) map with `WriteRefusal.TryMap("DeleteUnusedSymbol", apply, out var refusal)`
  before the existing `ToolErrorCode.Exception` branch (today every refusal becomes `Exception` with "failed to write to disk").
- Call-site measurement: 3 sites, 2 files, 2 projects. How `MsToolAugmentEngine` callers surface a refused write is NOT measured; hypothesis: `FormatDocumentSafeAsync` and `SortAndDeduplicateUsingsAsync` return a result the format/usings tools report as a failed apply. Verify before dispatch.
  Note `FormatDocumentSafeAsync` also has a no-solution-loaded fallback that writes with `EolFileWriter` (`:782`); that path never reaches the chokepoint and is not touched here (see Step 9b exclusions).
- Tier: D1.
- Done when: `Build` 0 errors; `RunTest` `Tests.Tools.Basic` shows no new failure (`DeleteUnusedSymbol_MixedPreImageUnderCrlf_FindingReachesToolResponseAsync` in `CreateFileDeleteFileTests.cs:~1180-1195` uses `Any(Source == "WriteChokepoint")` and is unaffected by the quiet policy).
- Out of scope: the other direct callers.
- Test-writer step (T5c): row H (DeleteUnusedSymbol returns code `StaleRead`).

#### Step 5d - refusal surfacing at throw sites (D1, after 5a)
- Done: f45f07c8
- **Review 2026-11-09, rewritten; runs BEFORE 5a and 5b.** The PWM StaleRead refusal returns a non-null `ValidationResult`
  (PWM `:2091`) when `validateChanges: true`, so sites keyed on `ValidationResult == null` print "Validation: 0 error(s)".
  Scope (1-2 files, ~10 edits, D1): (1) `DescribeRefusedApply` (`AsyncifyTools.cs:2086-2096`, used by `:2129`, `:2274`, `:2634`,
  presumably `:2796`) checks `RefusalCode` first; (2) the throw sites `:3129`, `:3239` likewise; (3) the three sites that ignore
  `IsError` (`:1741` AddCancellationToken: on `IsError` flip every `Succeeded` item to `Failed` with the summary, nothing
  was written, adjust counts, no blob for unwritten items; `:3632` HandlerToAsync and `:3848` ExtractEventHandlers: record
  the item `Failed` with the summary); (4) `CommentingTools.cs` `:146` and `:307` report the refusal summary instead of
  "validation found 0 error(s)". These are pre-existing defects (validation failures and rollbacks are already reported as success).
  The text below is superseded where it differs.
- Files (1): `RoslynSentinel.Tools.Advanced/AsyncifyTools.cs`.
- Change: measured sites that turn a refusal into `throw new InvalidOperationException($"Write refused: {applyResult3a.Summary}")` (`:3129`, `:3239`): the typed code is lost to the
  `ToolErrorMapper` `Exception` mapping. Per the accepted decision the text carries the refusal (it already contains the `StaleRead:` prefix and the re-read call); the step only checks each throw site keeps the summary text and forwards the typed code where the site already forwards `RefusalCode` (`:490-496`, `:2573`). Other Asyncify sites were not
  read. ~4 edits expected, to be re-measured at dispatch.
- Tier: D1. Done when: `Build` 0 errors. Out of scope: AsyncBatchEngine. Test-writer step (T5d): one Asyncify tool test through a real PWM asserting the refusal text contains `StaleRead`.

#### Step 5e - ratchet test (test-writer only; accepted)
- Done: 333a0740 (ran after 5c so the Ms sites were opted in first)
- Files (1, test): `RoslynSentinel.Tests.Basic/StaleReadPolicyRatchetTests.cs` (new).
- Change: syntax scan of production projects (precedent `BareFileWriteRatchetTests`): every `ApplyProposedChangesAsync(` invocation outside `Common` must pass a `staleReadPolicy:` argument or be allowlisted as
  `<file>::<member>` with a reason (`WorkspaceFileEditImpl::UndoLastApply` exactRestore, `WorkspaceFileEditImpl::CreateFile`). The count may only shrink. Block-commented code (`WorkspaceTools.cs:184-455`) must not count; a syntax scan ignores comments.
- Done when: the test passes after 5a-5c and fails if a site is reverted.

### Step 6 - F4: record from `GetFileOutline` and `InspectSymbol`
#### Step 6a - shared whole-file recorder
- Done: 233a04ab
- Files (2): `RoslynSentinel.Common/ObservedContentExtensions.cs` (new), `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`.
- Symbols: extension `RecordWholeFileObservedAsync(this IWorkspaceManager, FilePathWrapper, CancellationToken)` in `Common`; `WorkspaceFileEditImpl.RecordWholeFileObservedAsync` (`:348-366`, private) becomes a delegate.
- Change (~3 edits): move the body verbatim (resolve the document through `DocumentLookup.FindDocumentOrNull`, else `FileIoHelper.ReadAllTextIfExistsAsync`, then `RecordObserved`). `DocumentLookup` and `FileIoHelper` are already in `Common`.
  Reason for the move: `InspectSymbol` is in a different class; `Tools.Advanced` may need it later.
- Call-site measurement: 1 existing caller (`ReadFile`, `:395`). Tier: D1. Done when: `Build` 0 errors, existing `ReadFile` recording tests pass.
- Test-writer step (T6a): none beyond the existing `ReadFile` recording tests (they exercise the moved body); say so in the brief.

#### Step 6b - the two recording calls
- Done: fe1afbee (`SymbolNavigationImpl` now takes `IWorkspaceManager` instead of `ISolutionProvider`; the old `GetFileOutline_SolutionDocument_RecordsNothing` test was removed as it pinned the reversed behavior)
- Files (2): `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`, `RoslynSentinel.Tools.Basic/SymbolNavigationImpl.cs`.
- Symbols: `WorkspaceReadNavigationImpl.GetFileOutline` (`:372-425`); `SymbolNavigationImpl.InspectSymbol` (`:101-174`); `SymbolHoverInfo.DefinedInFile` (set from `symbol.Locations.FirstOrDefault(l => l.IsInSource).SourceTree.FilePath` in `SymbolNavigationEngine.GetSymbolInfoAsync`, `:405-599`).
- Change (~3 edits): `GetFileOutline`: after the document is found and `fileText` is read (the text is already materialized for the byte count), call `_workspaceManager.RecordObserved(filePathResolved, fileText.ToString())` on the success path only, with the same key shape `GetMethodSource` uses (`:336`).
  `InspectSymbol`, `aspect == info` success branch: when `symbolInfo.DefinedInFile` is non-null, `await _workspaceManager.RecordWholeFileObservedAsync(_workspaceManager.ResolveFromWire(symbolInfo.DefinedInFile), ct)`. A metadata symbol has no source location (`DefinedInFile` null) and records nothing. Failure paths record nothing.
- `ListAll` is deliberately untouched (decision). `blastRadius` records nothing (accepted): `ImpactReport` (`ImpactAnalyzer.cs:28-125`) carries no declaring file, so there is nothing to record without a DTO change.
- Call-site measurement: 2 read-tool entry points, no callers changed. Tier: D1. Done when: `Build` 0 errors.
- Out of scope: `blastRadius`, `ListAll`, `FindReferences` and other symbol reads.
- Test-writer step (T6b): rows I, J, plus one assertion that `InspectSymbol(blastRadius)` records nothing (row J).

### Step 7 - docs for F3/F4 (Markdown, main session)
- 2026-11-09: merged into Step 8e (F1 lands in the same run; one doc pass over the same three references).
- Files (3): `docs/current/references/reference_file_watcher_and_drift_detection.md` (section 5 "Checked by" list and the Gotcha stating that anchor and engine tools do not check),
  `docs/current/references/reference_code_file_write_paths.md` (step 7b "Opted in" list), `docs/current/references/reference_architecture_map.md` (the "Stale-read guard" paragraph, ~`:144`).
- Change: list the new opted-in families, the `Quiet` value, the helper default, the recorded reads (outline, InspectSymbol info) and the non-recorded one (`ListAll`, `blastRadius`). Move the closed plan's "follow-ups F3-F4" to done; keep F5 open (F1 is Step 8, F7 is Step 9).
- Done when: the three docs match the source as implemented. Steps 8e and 9g edit the same three docs again; the two doc passes may be merged into one if F1 lands within the same session.

### Step 8 - F1: replace the drift session latch with per-file protection (decided, option d)
Order inside the group: 8a, 8b (both additive), then 8c (the behavior flip), then 8d and 8e (text). The guard (8b) lands BEFORE the halt is removed (8c) so there is never a state where neither protection exists.

#### Step 8a - writes create the writer's observed entry
- Done: 5a635e5e
- Files (2 production + tests): `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`.
- Symbols: `RefreshObservedForWriter` (`PersistentWorkspaceManager.cs:540-545`, update-only today); its two callers `:2221` (after a successful write) and `:2284` (rollback restore); fake `RefreshObservedForWriter` (`FakeWorkspaceManager.cs:34-36`).
- **Review 2026-11-09 (decided):** the success refresh `:2221` runs inside the write loop, before rollback, so the
  rule below cannot hold as written. Instead: inside the loop keep the update-only refresh; after the loop, once the
  batch is known not to have rolled back, create entries for the written targets that have none. Only inversion
  needed: `StaleReadLedgerRefreshTests.cs:128` (no test asserts "read them first" on a second write).
  `RemoveObservedForAllAgents` (private, PWM `:550`; callers `:2131` delete success, `:2278` rollback of a new file) is unchanged.
- Change (~4 edits): give `RefreshObservedForWriter` a `createIfMissing` argument (upsert when true: `_observedHashes[key] = hash`). The success call `:2221` passes true. The rollback call `:2284` stays update-only: a rolled-back batch must not create an entry whose hash describes content the agent never saw (a restore writes the pre-image back; creating an entry there would
  claim the agent observed it). Mirror both in the fake. Because every writer (anchor, engine, direct) reaches the chokepoint, engine tools get entries with no tool edit. Delete: a delete creates nothing (Open, coordinator recommends no; an existing entry for a deleted path is left to its current behavior, to be confirmed when `RemoveObservedForAllAgents` callers are re-measured at dispatch).
- Call-site measurement: 2 callers of `RefreshObservedForWriter` in PWM (+ the fake's own copy); 0 tool call sites change. NOT measured: existing tests that assert the "read them first" finding on a SECOND write by an agent with no entry (the finding stops after the first write); re-measure at dispatch with a text search for `read them first` in tests.
- Tier: D1 (2 files, ~4 edits).
- Done when: `Build` 0 errors; the tests in T8a are inverted in the same dispatch and `RunTest` of `Tests.Basic` shows no other new failure.
- Out of scope: the drift check; the guard; deletes.
- Test-writer step (T8a): rows O, P. Invert `StaleReadLedgerRefreshTests.AgentWithNoEntry_StaysUnobservedAcrossWrites` (`:128`) to assert the agent now HAS an entry after its own write and a later external change makes its next write `StaleRead`; add the rollback case (no entry created when a batch rolls back).

#### Step 8b - `FoldPending` guard (non-latching, every policy except exactRestore)
- Done: 8b-1 d914fa1b, 8b-2 374c2636 (20 tests in `FoldPendingGuardTests.cs`; gap: the persisting-mismatch Warning path is untested). Fixtures adjusted: 2 in `StaleReadGuardTests`, `StaleReadWriteToolsTests`, `BatteryTwentyTests`, `WorkspaceRefreshTests` (Tests), `BulkComment_File_NeverObservedThenChangedOnDisk_SeedsNormally` (Tests.Advanced: one FoldPending, then a normal seed on retry).
- **Review 2026-11-09, split; supersedes the self-heal and lookup text below.** `preImages` (PWM ~`:2020-2037`) is reusable;
  the in-memory lookup (~`:2063-2072`) is inside the policy-gated, observed-only stale block and hashes, so it is not:
  add a separate loop over all targets (also under policy `None`) or extract a helper returning raw text.
  - **8b-1:** `FoldComparison` helper in `Common` (the owner's ladder: exact equal -> proceed; equal after BOM strip,
    EOL normalization and per-line trailing-whitespace trim -> proceed with an Info finding; else mismatch),
    `ToolErrorCode.FoldPending` (`SentinelCallToolResult.cs`, beside `StaleRead` `:181`), `WriteRefusal` case. 3 files, ~7 edits.
  - **8b-2:** PWM wiring: the comparison loop after the stale check; refuse once per path per fold (state beside
    `_unfoldedFiles` `:173`, cleared in `NoteFileFolded` `:217-226`); on refusal fold the files INLINE under the held
    `_solutionLock` (decided); after a fold, a persisting mismatch proceeds with a Finding built from
    `WorkspaceStaleText.Recovery` (`LoadSolution(forceReload: true)`). 1 file, ~10-12 edits.
  - `StaleReadGuardTests` fixtures (`:60-76`) deliberately diverge disk from memory with `File.WriteAllText`; expect
    FoldPending to change their outcomes and list them in the 8b-2 brief after a measuring run.
- Files (3): `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `RoslynSentinel.Common/SentinelCallToolResult.cs` (new `ToolErrorCode.FoldPending`, beside `StaleRead` `:178`), `RoslynSentinel.Common/WriteRefusal.cs` (new `FoldPending` case).
- Symbols: PWM chokepoint, directly AFTER the stale-read check (`:2046` region) so `StaleRead` wins for an agent that is stale (the agent really read old text), and BEFORE the EOL pre-pass and any write.
- Change (~8 edits): for every target (writes; deletes included, recommended) that has an in-memory document, compare the disk pre-image text already captured for the write with the document text from the stale-read check's lookup. If they differ and the call is not `exactRestore`:
  refuse the whole batch with `RefusalCode = FoldPending`, `FailedFiles` naming each file, message "`<file>` is changing on disk; retry in a moment", no latch, nothing written. A target with no document (a new file) or no disk pre-image is not checked (rule that protects the test fixtures; see open decision 4).
  Self-heal: on refusal, enqueue the named files into `_pendingChanges` and re-arm the debounce timer, so the fold runs even if the watcher event was lost (otherwise a lost event gives a permanent `FoldPending`). `WriteRefusal.TryMap` gets the `FoldPending` case, so every site that already calls it (Steps 2b, 3, 4, 5c) maps it with no further edit.
- Call-site measurement: 1 chokepoint, 3 files, 1 project; 0 tool call sites edited. Behavioral effect on tools that do NOT call `TryMap` (the 34 direct sites of Step 5a/5b, which surface refusals as `Summary` text): they report "Write refused: ..." text containing the `FoldPending` message; acceptable under the Step 5 decision.
- Tier: D1 (3 files, ~8 edits, 1 acceptance).
- Done when: `Build` 0 errors; `RunTest` `Tests.Basic` and `Tests.Tools.Basic` no new failure (the guard affects every real-PWM write test whose document text differs from disk; the `SetTestSolution` fixtures create documents from the files they write, so a mismatch is the exception; verify by the run, not by assumption).
- Out of scope: removing the halt (8c); persistent-mismatch escape hatch (open decision 3).
- Test-writer step (T8b): row S (mismatch refused, exactRestore exempt, new file unchecked, no-document and no-disk unchecked, self-heal enqueue observed, no `_sessionHalted`).

#### Step 8c - remove the drift halt from the chokepoint
- Done: 3e4ed6fa (`DeleteFile_DriftedFile` split into two deterministic tests; dead-latch `// TODO` left)
- Files (1 production + tests): `RoslynSentinel.Common/PersistentWorkspaceManager.cs`.
- Symbols: the block at `:1958-1964` (`drift` set, `driftedTargets`, `_sessionHalted = true`, `throw new SessionHaltedException(DriftMessages.DriftHaltMessage)`).
- Change (~2 edits): delete the throw and the latch set, leaving a comment that drift on a read or edited file is caught per agent by `StaleRead` and that `_externalChanges` is kept for `ExternalFileDrift` visibility; keep `_externalChanges`, `GetExternalFileChanges`, `ClearSessionHalt`, `IsSessionHalted`, `SessionHaltedException` and the breaker throw `:1888` untouched.
  Leave `// TODO _sessionHalted can no longer be set; remove the dead latch and its readers (HaltInfo, IdleRestartPolicy, HaltStamp) in a cleanup slice` (dead-latch cleanup is out of scope, see below).
- Call-site measurement: 1 block, 1 file; readers of `IsSessionHalted` (5 files, measured above) now see false for the drift kind.
- Tier: D1 (1 file, ~2 edits).
- Done when: `Build` 0 errors; the tests in T8c are inverted in the same dispatch; `RunTest` `Tests.Basic`, `Tests.Tools.Basic` and `Tests.Battery.Basic` show no other new failure (`UnrecoverableBreakerTests.cs:117` must still pass: the breaker still halts).
- Out of scope: `Acknowledge` semantics (8d); removing the dead latch; the overflow halt (open decision 1).
- Review 2026-11-09: the drift test is `StaleReadGuardTests.cs:225-249`; `:199-212` trips the halt through `IUnrecoverableBreaker.Trip` and is unchanged. `DeleteFile_DriftedFile_RefusesDeleteAsync` (`CreateFileDeleteFileTests.cs:394-428`) uses the real watcher through `TestSolutionFixture` and would race after 8b+8c: rewrite it deterministically (assert the new behavior, no polling for drift).
- Test-writer step (T8c): rows Q, R, T, U. Invert `StaleReadGuardTests.DriftFlaggedTarget_WithStaleLedgerEntry_LatchWins_ThrowsSessionHalted` (`:236-238`, the row F test of the closed plan): drift plus a stale ledger entry now yields `StaleRead`, not `SessionHalted`. Re-read `SessionHaltTripped_WinsOverStaleLedgerEntry_ThrowsSessionHaltedException` (`:199-212`): if it trips the halt through the drift path it is inverted too; if it sets `_sessionHalted` by reflection it is unchanged.
  Re-measure and, if it depends on the drift halt, invert the `DeleteFile_DriftedFile` test in `CreateFileDeleteFileTests.cs` (~`:385-432`). Add: drift on a never-touched file is written without refusal; drift on a read file refuses with `StaleRead`; re-read then retry succeeds; `ExternalFileDrift(List)` still lists the file.

#### Step 8d - tool descriptions and runtime messages
- Done: 1c9cb4db (`AcknowledgeDriftResult` still calls the now no-op `ClearSessionHalt()`; left for the dead-latch cleanup)
- Files (3): `RoslynSentinel.Common/DriftMessages.cs`, `RoslynSentinel.Tools.Basic/AdminTools.cs`, `RoslynSentinel.Tools.Basic/WorkspaceTools.cs` (only if the `Acknowledge` hit is outside the block comment `:184-:455`; verify).
- Symbols: `DriftHaltReason` / `DriftHaltRecovery` / `DriftHaltMessage` (`DriftMessages.cs:6-20`; still used by nothing after 8c except possibly the breaker text: verify), `BuildHint` (`:65`); `ExternalFileDrift` tool description (`AdminTools.cs:41`), operation description (`:44`), `:46`.
- Change (~6 edits): rewrite the text so it no longer says drift halts the session or that `Acknowledge` is required to resume writes; say that drift is listed for visibility, that a read or edited file that drifted is refused with `StaleRead` until re-read, and what `Acknowledge` does now (Open decision 2; recommended: only clears the visibility list). State the workflow positively (CLAUDE.md rule: never name a gated-off tool).
  Update the tests that assert the old strings: `AdminToolsTests.cs`, `AdminToolsLedgerTests.cs`, `BatteryTwentyTests.cs`, `ConvertedToolHintTests.cs`, `CreateFileDeleteFileTests.cs` (all contain `Acknowledge`; the exact assertions are NOT measured; re-measure at dispatch, expect most to be text asserts).
- Tier: D1 (3 files; the test updates are a separate test-writer dispatch because they touch 5 more files).
- Done when: `Build` 0 errors; `RunTest` `Tests.Tools.Basic` no new failure after the test dispatch.
- Out of scope: behavior of `Acknowledge` (a code change, only if open decision 2 chooses one); CLAUDE.md and skill text (8e report).
- Test-writer step (T8d): update the five text-asserting test files; add none unless 8d changes `Acknowledge` behavior.

#### Step 8e - docs and the stale agent-facing text (Markdown, main session)
- Files: `reference_file_watcher_and_drift_detection.md` (sections 4 latch, 5, Gotchas), `reference_code_file_write_paths.md`, `reference_architecture_map.md`, `reference_changing_claude_lean_tools.md`, `reference_tool_terminology_refinement.md`, `proposal_create_project_register_own_writes.md` (its premise, "own writes must not trip the drift halt", is largely moot after 8c; mark it, do not delete), `proposal_cross_session_write_journal.md` (point its attribution hook at Step 2's `// TODO`).
- Change: describe the new model (per-agent ledger protection, create-on-write, `FoldPending` guard, halt only for the breaker, `Acknowledge` as manual override). Report, do not edit: `CLAUDE.md` line "If halted: `ExternalFileDrift(operation: List)` then `Acknowledge`" (under tracked-file deletes) and `.claude/skills/peer-coordination/SKILL.md` mention `Acknowledge`; after 8c a shell delete of a tracked file no longer halts mutating tools unless the agent writes that path. The owner decides the CLAUDE.md edit.
- Done when: docs match the source as implemented; the TODO.md drift-latch entries (owner) move to `CLOSED.md`.

### Step 9 - F7: cross-process write lock, debounce lock, fold-on-read (decided)
Restate the lock-order and re-entrancy rules (Execution rules) in every brief.

#### Step 9a - `CrossProcessWriteLock` and the `WriteLockBusy` code
- Files (3): `RoslynSentinel.Common/CrossProcessWriteLock.cs` (new), `RoslynSentinel.Common/SentinelCallToolResult.cs` (new `ToolErrorCode.WriteLockBusy`), `RoslynSentinel.Common/WriteRefusal.cs` (new `WriteLockBusy` case).
- Symbols: new `CrossProcessWriteLock` with `AcquireAsync(string solutionRoot, WriteLockHolder holder, TimeSpan wait, CancellationToken ct)` returning a lease (`IAsyncDisposable`) or a busy result carrying the holder; new record `WriteLockHolder(string SessionId, string AgentId, string Tool, string Reason, DateTime SinceUtc)`.
- Change (~10 edits): open `<solutionRoot>/.roslynsentinel/write.lock` with `FileMode.OpenOrCreate`, `FileAccess.ReadWrite`, `FileShare.None`, poll every ~50 ms until the wait elapses (there is no blocking primitive for this share mode; a sharing violation is an `IOException`), honoring `ct`. After acquire write the holder JSON to a SIDECAR `write.lock.holder.json`
  and delete it on release: the holder has exclusive access so a waiter cannot read the lock file itself, which is why the metadata cannot live inside it. A waiter reads the sidecar best-effort; missing or unparsable = "another session (holder unknown)". After a crash the OS releases the lock, the next acquirer overwrites the stale sidecar, so a stale sidecar is only visible in the short window between acquire and sidecar write.
  Busy refusal: `WriteLockBusy`, non-latching, message "another session (<agent>, <tool>, since <time>) is writing; retry in a moment". Default wait 10 s as an options-record default (configurable via the startup-arg pattern is a later slice; `// TODO wire startup arg per the LlmOptions pattern`; open decision 7).
  Create `.roslynsentinel/` if missing (precedent: `largeresults`).
- Call-site measurement: 0 callers (new type); `WriteRefusal.TryMap` gains a case and so reaches every existing `TryMap` caller (Steps 2b, 3, 4, 5c) with no edit. Sites that do not call `TryMap` (the 34 direct sites) show the text.
- Tier: D1 (3 files, ~10 edits, 1 acceptance).
- Done when: `Build` 0 errors; `RunTest` of the new lock tests (T9a).
- Out of scope: using the lock anywhere (9b-9e).
- Test-writer step (T9a): row W (exclusivity, timeout with holder, release on dispose, release on exception, sidecar lifecycle). Tests use a per-test temp root, never the repo root.

#### Step 9b - acquire the lock at the chokepoint
- Files (3): `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `RoslynSentinel.Common/EolFileWriter.cs`, `RoslynSentinel.Engines.Basic/MsToolAugmentEngine.cs` (the last two get a `// TODO` only).
- Symbols: PWM `ApplyProposedChangesAsync`: acquire AFTER `RevalidateAfterForeignWriteAsync` and BEFORE pre-image capture (compile validation and revalidation are finished and outside the lock), under `_solutionLock` (`:1985`); release in the existing `finally` (`:2380` region) AFTER the in-memory resync (`:2316`) and BEFORE `_solutionLock.Release()`. Also: new internal test seam `SetTestSolutionRoot(string)` beside `SetTestSolution` (`:1768`).
- Change (~8 edits): `GetSolutionRoot()` null = no OS lock (in-process `_solutionLock` only; this protects the ~675 `SetTestSolution` usages); non-null = acquire with the holder built from `ToolCallContext.Current` (agent) and the chokepoint's operation name and reason (which of session id, tool name and reason the context already carries is NOT measured; open decision 7) and a busy result returns `ApplyChangesResult` with `RefusalCode = WriteLockBusy`, nothing written, no latch.
  A refusal path that returns before the write (stale-read, FoldPending, validation) releases the lock through the same `finally`. The blob write (`ValidateAndApplyHelper.cs:214`, `OperationBlobWriter`) happens AFTER the chokepoint returns, so it is outside the lock: accepted gap (it is written by this process only, is a record of the operation and not a source file, and a torn blob cannot cause a lost source edit; whether the watcher ignores `.roslynsentinel/` is NOT measured, verify before relying on that).
  Explicit exclusions, with a `// TODO` at each: `EolFileWriter` and the `MsToolAugmentEngine.FormatDocumentSafeAsync` no-solution-loaded fallback (`:782`) have no solution root and never reach the chokepoint; writers outside the chokepoint (`FileIoHelper.WriteAllTextAsync` callers that bypass it, `BareFileWriteRatchetTests` allowlist) keep in-process exclusion only.
- Call-site measurement: 1 chokepoint, 3 files (2 comment-only), 1 production behavior change; the lock covers all 3 + 2 + 4 + 34 + 55 callers (every write that reaches the chokepoint) with no tool edit. Measured stall risk: see Risks (wait inside `_solutionLock`).
- Tier: D1 (3 files, ~8 edits).
- Done when: `Build` 0 errors; `RunTest` `Tests.Basic`, `Tests.Tools.Basic`, `Tests.Battery.Basic` show no new failure (a null-root PWM must behave exactly as before).
- Out of scope: debounce, read paths, startup-arg wiring.
- Test-writer step (T9b): rows X (held across write and resync; released on refusal and on exception; busy returns `WriteLockBusy` naming a holder; a long compile validation does not hold the lock, shown by acquiring it from a second lease while validation is gated; null root = no lock).

#### Step 9c - debounce fold takes the lock
- Files (1): `RoslynSentinel.Common/PersistentWorkspaceManager.cs`.
- Symbols: `OnDebounceTimerElapsed` (`:1353-1535`): around the fold `ApplyInMemoryDocumentUpdatesAsync` (`:1455`), inside `_solutionLock` (`:1366`).
- Change (~6 edits): acquire with a short bounded wait (default 2 s, a named constant) before the fold reads disk; on busy, re-enqueue the changes into `_pendingChanges`, re-arm the timer and return WITHOUT reading disk or latching; release right after the fold and BEFORE any full reload (`needsReloadAfterAll`): a full MSBuild reload reads many files for seconds and must not hold other processes' writers off (open decision 5).
  `ApplyInMemoryDocumentUpdatesAsync` itself is NOT changed and never acquires the lock (rule b).
- Call-site measurement: 1 handler, 1 file. Tier: D1 (1 file, ~6 edits).
- Done when: `Build` 0 errors; `RunTest` `Tests.Basic` no new failure. Review 2026-11-09: watcher/debounce tests use `TestSolutionFixture` (real MSBuild load, real solution root), so they DO take the OS lock; the run must show they still pass. `.roslynsentinel/` is not watched (`IsWatchedExtension`, PWM `:1020-1026`).
- Out of scope: the full-reload path; fold-on-read.
- Test-writer step (T9c): row Y (lock busy: no disk read, changes re-queued, no halt; lock free: fold as before; lock released before a reload).

#### Step 9d - `SyncFileFromDiskIfStaleAsync` (the fold-on-read primitive)
- Files (3): `RoslynSentinel.Common/IWorkspaceMutator.cs` (or the interface that `ReadFile` already uses; confirm at dispatch which one declares `RecordObserved`), `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`.
- Symbols: new `Task SyncFileFromDiskIfStaleAsync(FilePathWrapper path, CancellationToken ct)`.
- Change (~7 edits): PWM: under `_solutionLock`, find the document; read disk; if disk text equals the document text, return (cheap path: one file read and one string compare); if it differs, take the write lock with a short bounded wait (default 1 s; reuse 8b's comparison helper), fold that one path with `ApplyInMemoryDocumentUpdatesAsync`, and remove it from `_pendingChanges`
  (idempotent if left; confirm at dispatch). If the lock is busy or the fold needs a full reload, return without folding (fail open: the read returns in-memory text and records the matching hash; a later write is refused `FoldPending` if disk still differs, which is safe and self-healing through 8b). Fake: a no-op.
  Never throws on IO errors (read tools must not fail on a vanished file).
- Cost: one disk read per read-tool call plus a string compare. NOT measured as latency; the Search text-mode corpus gives the size bound (p95 about 47 KB per file in this solution, measured earlier in this plan's analysis). Row Z includes a timing assertion on a 1 MB file to catch a regression; no budget is claimed beyond that.
- Call-site measurement: 0 callers yet (9e). Tier: D1 (3 files, ~7 edits).
- Done when: `Build` 0 errors; `RunTest` of the new tests (T9d).
- Out of scope: wiring into any tool.
- Test-writer step (T9d): row Z (differing file folded; equal file untouched; lock busy fails open; no throw on a deleted file).

#### Step 9e - `ReadFile` and `GetMethodSource` fold first, then record
- Files (2): `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs` (`ReadFile`, around the `RecordWholeFileObservedAsync` call `:395`), `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs` (`GetMethodSource`, record at `:336`).
- Review 2026-11-09: `ReadFile:395` is only the `memberName` path; the line-range path records at `:451` and the whole-file path at `:493`. All three get the fold. Batched with 9f-2 (same files).
- Change (~4 edits): call `SyncFileFromDiskIfStaleAsync` before reading the document text, then record as today. `GetFileOutline` (Step 6b) is NOT given fold-on-read (decision lists three tools); mention in the brief so it is not added by analogy.
- Call-site measurement: 2 read tools, 2 files, 1 project. Tier: D1 (2 files, ~4 edits).
- Done when: `Build` 0 errors; `RunTest` `Tests.Tools.Basic` no new failure; the existing `ReadFile` recording tests still pass.
- Out of scope: Search (9f); `GetFileOutline`; `InspectSymbol`.
- Test-writer step (T9e): row Z (tool level): `ReadFile` on a file changed on disk inside the window returns the new text, records its hash, and a following `WriteFile` is not refused (no `StaleRead`, no `FoldPending`).

#### Step 9f - apply pending reloads before `Search`, `ReadFile` and `GetMethodSource` (owner decision 2026-11-09)
- Replaces the earlier "Search folds returned documents" design (up to 200 disk reads per call, and it could not
  fix a MISSED match: a file whose stale in-memory text has no hit is never returned, so never folded).
- Change: before these three tools read, fold the paths currently queued in `_pendingChanges` (the watcher's
  pending set the debounce timer would fold ~500 ms later) synchronously, under `_solutionLock` and briefly under
  the write lock (9c's bounded wait; busy lock fails open: proceed on the in-memory text). Cost scales with the
  number of pending changes (usually 0), not with hits or solution size. A pending `.sln`/`.csproj` or a watcher
  overflow keeps the existing reload path (do not run a full reload inline; proceed and let the debounce handle it).
- `ReadFile`/`GetMethodSource` keep 9d/9e's single-file disk compare as well (one read per call; catches a change
  the watcher has not delivered yet).
- Files: `RoslynSentinel.Common/PersistentWorkspaceManager.cs` (a `FlushPendingFoldsAsync` beside the debounce
  handler, sharing its fold code), `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`,
  `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`. Measure the debounce handler before slicing; split PWM
  and the two tools if over D1.
- Remaining gap, named: the milliseconds before the OS delivers the watcher event.
- Review 2026-11-09, split: the fold is one call (`ApplyInMemoryDocumentUpdatesAsync`) plus `_workspaceVersion` increment; the
  shareable part is the structural-file classification loop (`:1383-1403`), extract as a helper. **9f-1:** PWM + interface +
  fake (`FlushPendingFoldsAsync`), dispatched with 9d (same 3 files). **9f-2:** `ReadFile`, `GetMethodSource`, `SearchSolutionText`
  call it, dispatched with 9e (same files).
- Test-writer step (T9f): row Z (Search variant): an external write queued but not yet debounced; Search finds
  the new text immediately; with nothing pending, no fold runs.

#### Step 9g - docs for F7 (Markdown, main session)
- Files: `reference_file_watcher_and_drift_detection.md` (write path, debounce fold, lock), `reference_code_file_write_paths.md` (the lock step and the excluded writers), `reference_architecture_map.md` (chokepoint order, lock order), `proposal_cross_session_write_journal.md` (the lock and holder sidecar are now the attribution hook for `WriteLockBusy`; link this plan).
- Done when: docs match the source; the lock-order and re-entrancy rules appear in the architecture map where the next change to the chokepoint will read them.
- Also document Step 9h (zero-hit Search hint) in the watcher reference.

#### Step 9h - zero-hit Search hint from recent operation blobs (owner decision 2026-11-09)
Runs after 9f; independent of the lock. Two slices.
- **9h-1, cross-file blob evidence in `Common`.** Files: `RoslynSentinel.Common/NotFoundEvidence.cs` (+ a test
  file). Extract from `SearchBlobs` (`NotFoundEvidence.cs:79`) a variant with no `filePath` filter that walks
  recent blobs newest first (same `MaxBlobsOpened` cap, same `Classify` self/foreign rule, skips the caller's own
  writes and `Skipped`/`Failed`/`RolledBack` items) and returns at most a few notes of two kinds:
  - **Removed** (window: the existing `DefaultWindow`, 60 min): `BeforeSource` holds the term and `AfterSource`
    does not -> "'<term>' was removed from <file> by <agent> (<other session>) via <tool> <age> ago: <reason>".
    Same wording builder as `BlobNote`.
  - **Added, not loaded yet** (window: 15 s): `AfterSource` holds the term -> "'<term>' appears in <file> as
    written by <agent> <age> ago; the server has not loaded it yet - retry". The caller confirms the in-memory
    text lacks the term (it just searched it).
  - **Review 2026-11-09 (decided):** chokepoint blobs never set `AfterSource` (only 3 Asyncify sites do). Removed uses
    `BeforeSource` alone (the zero-hit Search already proves memory lacks the term). Added: for a foreign blob under 15 s
    old, read the named file from DISK and check it holds the term; no blob schema change. Storing `AfterSource` is a separate idea doc.
  - Term matching: literal via the existing `Holds` for text/symbol names; for a regex query, match the regex
    (with a timeout) against the blob text. Respect `MinNameLength`; clip reason with `MaxReasonChars`.
  - Never throws (same catch-and-empty policy as `Find`); the single-file `Find` keeps working unchanged.
  - Tier: D1 (1-2 files). Done when: `Build` 0 errors; T9h-1 green.
- **9h-2, call it from Search on zero results.** Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`
  (text mode `SearchSolutionText`; symbol/declaration modes where a zero-result return is built). When the
  result has zero matches, add the notes as Info `Findings` (review 2026-11-09: two slices. **9h-2a** text mode, the one
  zero-result return ~`:1196-1250`, merging into the existing `Findings = justTripped ? [...] : []`; **9h-2b** the other modes,
  which dispatch from `DispatchSearchCore` (`WorkspaceReadNavigationTools.cs:171-195`) to LocateSymbol, FindReferences,
  QuerySymbolRelationships and ListAll: one post-process on `TotalRecords == 0`, verify the seam at dispatch) (source as in `NotFoundEvidence.FindingSource`). No
  change when there are hits. Re-measure the zero-result return points first; if they span more than one file,
  split. Tier: D1.
- **Limits, named:** no hint does not mean nothing changed (editors, git and shell writes leave no blob; 5 chokepoint
  paths write none, see the journal proposal's blob-coverage note). The write journal stores hashes only and
  cannot replace this term search; 9h stays blob-based.
- **Test-writer steps:** T9h-1 (unit, temp `.roslynsentinel/operations` fixture): foreign removal inside 60 min
  -> Removed note; foreign add inside 15 s -> Added note; add older than 15 s -> no Added note; own-agent write
  -> nothing; regex query; short term skipped; corrupt blob ignored. T9h-2 (tool): Search with zero hits and a
  matching foreign blob returns the Finding; Search with hits returns none.

## Test strategy

Role boundaries: this plan (planner) defines what must be covered; a reviewer confirms the delivered tests exercise the specific changed path and not a shared helper; the implementer is
responsible only for a green build and the tests it is given. Real-PWM tests use the harness in `Tests.Tools.Basic/StaleReadWriteToolsTests.cs` (`NewManager` with `SetTestSolution` on an `AdhocWorkspace`, `RecordObserved` under
`AsAgent(...)`, then `File.WriteAllText` to change disk; no watcher, so no drift latch; drift is simulated with `AddExternalChangeForTest`, `PersistentWorkspaceManager.cs:430`). The fake (`FakeWorkspaceManager`) holds a separate copy of the check (memory only, no disk, no unobserved finding), so a fake test never covers the production path.

| Row | Scenario | Path it covers | Real PWM |
| --- | --- | --- | --- |
| A | Policy `RefuseIfObservedStaleQuiet`: observed-then-changed target is refused (`StaleRead`, nothing written, no halt); unobserved existing target is written and `Findings` has NO "read them first"; the same unobserved write under `RefuseIfObservedStale` still has exactly one | quiet value, both branches (Step 1) | yes |
| B | Fake with `Quiet` refuses a stale entry | fake copy of the check (Step 1) | no (fake only; says nothing about PWM) |
| C | An engine tool through the helper (`Member` or `ModifyModifier`, plus `RenameSymbol` for a multi-file batch) on an observed-then-changed file returns `ErrorData.ErrorCode == StaleRead`, message names every stale file, file unchanged | helper opt-in and mapping (Step 3) | yes |
| D | Engine tool on a never-read existing file succeeds and has no "read them first" finding | quiet default through the helper (Step 3) | yes |
| E | `dryRun: true` on a stale file is not refused (documents that the dry run never reaches the chokepoint) | boundary (Step 3) | yes |
| F | `ReplaceSnippet` single, `ReplaceSnippetBatch`, `ApplyDiff` diff format, `ApplyUnifiedDiff` each refuse a stale entry with `StaleRead`, name the file, write nothing (the two existing tests at `StaleReadWriteToolsTests.cs:180`, `:208` are inverted) | anchor opt-in and mapping (Step 4) | yes |
| G | Anchor tool on a never-read existing file still reports the "read them first" finding; `CreateFileDeleteFileTests.cs:1223` updated so the `WriteChokepoint` count ignores that finding | non-quiet anchor behavior, existing-test fix (Step 4) | yes |
| H | One refusal test per direct family: `DeleteUnusedSymbol` returns `StaleRead`; one Asyncify tool; one `AsyncBatchEngine` path; `CommentingTools`; `MsToolAugmentEngine` format. Gap: not one test per mechanical site; the ratchet (row L) stands in for the rest | Step 5 | yes |
| I | `GetFileOutline` on a file, then change it, then `WriteFile`/`ReplaceSnippet` -> `StaleRead`; `ListAll` over the same file records nothing (a following `WriteFile` carries the "read them first" finding) | outline records, `ListAll` does not (Step 6b) | yes |
| J | `InspectSymbol(info)` on a snippet whose symbol is declared in ANOTHER file records the DECLARING file (change that file -> `StaleRead`) and not the file holding the snippet; a metadata symbol records nothing; `blastRadius` records nothing | declaring-file rule (Step 6b) | yes |
| K | Own-write refresh: `ReadFile` -> `ReplaceSnippet` -> `ReplaceSnippet` again without re-read succeeds; `ReadFile` -> `Member` edit -> `ReplaceSnippet` succeeds; `ReadFile` -> `ReplaceSnippet` -> `Member` succeeds (each path separately: anchor and helper) | refresh after a successful write still prevents self-refusal for the newly opted-in tools (Steps 3, 4) | yes |
| L | Ratchet: no production `ApplyProposedChangesAsync(` call without `staleReadPolicy:` except the allowlist | silent non-opt-in at any of the 34 sites (Step 5e) | n/a (syntax scan) |
| M | `UndoLastApply` and `CreateFile` are not refused after a stale entry (existing `ExactRestore_IsExemptFromStaleCheck`, `UndoLastApplyTests`); add a `CreateFile` case | exemptions | yes |
| N | A helper-routed `StaleRead` refusal does not set `_sessionHalted` and a re-read then retry succeeds | non-latching, "cleared by re-reading" (Step 3) | yes |
| O | An agent with NO prior entry writes file X through an anchor tool; another actor changes X on disk; the same agent's next write to X is refused `StaleRead` (inverts `StaleReadLedgerRefreshTests.AgentWithNoEntry_StaysUnobservedAcrossWrites`, `:128`); a batch that rolls back creates no entry | create-on-write and the update-only rollback call (Step 8a) | yes |
| P | The same as O through an engine tool (`Member`, helper path) and through a direct caller | create-on-write reaches engine and direct writers with no tool edit (Step 8a) | yes |
| Q | A target flagged by `AddExternalChangeForTest` that the agent never read or wrote is written; no `_sessionHalted`, no `SessionHaltedException`, `IsSessionHalted() == false` | drift halt removed for untouched files (Step 8c) | yes |
| R | Drift flagged target WITH a stale ledger entry -> `StaleRead` (inverts `StaleReadGuardTests.DriftFlaggedTarget_WithStaleLedgerEntry_LatchWins_ThrowsSessionHalted`, `:236-238`); after `ReadFile` the retry succeeds | per-agent protection replaces the latch (Step 8c) | yes |
| S | Target whose document text differs from the disk pre-image: whole batch refused `FoldPending`, nothing written, no latch, files enqueued for fold; `exactRestore` exempt; new file (no document) and no-disk-pre-image targets unchecked; a `StaleRead` target reports `StaleRead` not `FoldPending` | the guard and its precedence (Step 8b) | yes |
| T | Breaker still halts: `UnrecoverableBreakerTests.Tripped_RefusesEveryWriteAtTheChokepointAsync` unchanged and green | halt retained for the breaker (Step 8c) | yes |
| U | `ExternalFileDrift(List)` still lists a drifted path; `Acknowledge` clears the list; the tool description and `DriftMessages` text contain no instruction to acknowledge in order to resume writes | visibility kept, text updated (Steps 8c, 8d) | yes (text: asserts) |
| W | `CrossProcessWriteLock` on a temp root: second `AcquireAsync` times out as busy with the holder from the sidecar; released on dispose and on exception; sidecar removed; stale sidecar after a simulated crash is overwritten. Gap: two leases in one process prove the OS `FileShare.None` behavior but not a second process; one child-process test is optional and would need a helper executable (not scoped) | the lock type (Step 9a) | n/a (unit) |
| X | Chokepoint: lock held from pre-image capture through resync, released on success, refusal and exception; busy returns `WriteLockBusy`; a second lease cannot be taken while a write is in flight but CAN be taken while compile validation is gated; null root takes no lock | acquisition placement and order (Step 9b) | yes (with `SetTestSolutionRoot` on a temp root) |
| Y | Debounce fold with the lock held elsewhere: no disk read, changes re-queued, timer re-armed, no halt; free lock: fold as before; lock released before a full reload | debounce lock (Step 9c) | yes |
| Z | Fold-on-read: `ReadFile` and `GetMethodSource` on a file changed on disk inside the window return the disk text, record its hash, and a following `WriteFile` is accepted; equal file untouched; busy lock fails open (in-memory text returned, later write `FoldPending`); deleted file does not throw; timing assertion on a 1 MB file | the primitive and both tools (Steps 9d, 9e) | yes |

Existing tests that could break, measured by text search 2026-11-09:
`CreateFileDeleteFileTests.cs:1223` (breaks under Step 4, ApplyUnifiedDiff does not read first; fix named in T4); `StaleReadWriteToolsTests.cs:180`, `:208` (invert on purpose, Step 4); `StaleReadLedgerRefreshTests.cs:128` (inverts on purpose, Step 8a);
`StaleReadGuardTests.cs:236-238`, possibly `:199-212` (Step 8c); the text-asserting `Acknowledge` tests (Step 8d, not individually measured).
Not affected: `EolChokepointConversionTests.cs:202-278` and the rest of `StaleReadGuardTests.cs` call the chokepoint directly with the default or explicit policy and are unchanged by Steps 1-5;
`CreateFileDeleteFileTests.cs:1108/1133` already filter the finding; `:1173` (Member), `:1193` (DeleteUnusedSymbol), `:1245` (ApplyDiff diff) use `Any(...)` and the engine paths are quiet; `TestCategoryApplyTests.cs:265-273` (fake `ApplyException`) and `UnrecoverableBreakerTests.cs:117` (breaker) are unaffected by 8c.
NOT measured: existing tests that `ReadFile`/`GetMethodSource`/`Search` a file, change it on disk, then drive an engine or anchor tool (Steps 3, 4 require a full `Tests.Basic` and `Tests.Tools.Basic` run for that reason); existing tests whose in-memory document text differs from the disk text at write time (Step 8b); existing tests that rely on a second write still reporting "read them first" (Step 8a).

Known gaps to name: a read recorded between the check and the write (reader/writer race across `_solutionLock`) is untested, as in the closed plan; the 34 direct sites are covered by the
ratchet (presence of the argument) and one behavioral test per family, not per site; no test exercises two PROCESSES contending for the lock (row W); the blob write is outside the lock by decision.

## Out of scope

- **F5** (per-snapshot hashes when staged writes make `Committed` and `IncludeStaged` diverge): BLOCKED on [proposal_staged_writes.md](../proposals/proposal_staged_writes.md), which is not built. A `docs/current/TODO.md` line
  will be added by the owner to track it; this plan does not edit `TODO.md`. Until staged writes land, `RecordObserved` and the check use the committed snapshot only.
- `ListAll` recording (decided no); `blastRadius` recording (decided no).
- `PersistentWorkspaceManager.RetryFailedChangesAsync` (`:2941`) replays cached content of a failed write with the default policy; left on `None` (cache replay, not a fresh agent edit). Add a `// TODO` when touching it.
- Exempt paths: `UndoLastApply` / `exactRestore`, `CreateFile`.
- The pre-existing fall-through of non-mapped, non-validation refusals to `IsError = false` in the four anchor call sites (Step 4 leaves `// TODO`; open decision 9).
- Removing the retired block-commented tool in `WorkspaceTools.cs:184-455`.
- **Dead-latch cleanup after Step 8c:** removing `_sessionHalted`, `ClearSessionHalt`, `IsSessionHalted` readers (`HaltInfo`, `IdleRestartPolicy` guard `sessionHalted`, `IdleRestartMonitor`, `HaltStamp`, `ServerStatusTools`) and `DriftMessages` halt text once nothing sets them. Separate slice with its own measurement; `// TODO` left at the block in 8c.
- **Temp-file-plus-rename atomic writes** (the watcher treats `Renamed` as drift; owner decision).
- **Per-file attribution of who changed a file** (journal proposal); Step 2 leaves the hook only.
- **Edits to `CLAUDE.md` and the peer-coordination skill** for the stale drift-halt recovery text: reported in Step 8e, the owner decides.
- Caller identity, a single read chokepoint, watcher changes other than the debounce lock (unchanged from the closed plan).
- Cross-process coverage of writers outside the chokepoint (`EolFileWriter`, the format engine's no-solution fallback, `FileIoHelper` callers that bypass it): excluded with `// TODO` markers (Step 9b).

## Risks and open decisions

Open decisions (not decided; do not route around).

1. **Watcher overflow does not halt today.** The F1 decision says "session halt stays for watcher overflow"; the code has nothing to keep: `_watcherOverflowed` forces a full reload and the only `_sessionHalted` write is the drift check (`PersistentWorkspaceManager.cs:1962`). After Step 8c nothing sets the halt except the breaker (separate throw).
   Options: (i) accept; overflow already triggers a full reload that resyncs memory, and per-agent `StaleRead` plus `FoldPending` cover the affected files; (ii) add an overflow halt. Recommend (i); choose (ii) only if overflow is observed to leave memory wrong after the reload.
2. **What `Acknowledge` means after 8c.** With no drift halt there is nothing to override. Options: keep it as "clear the visibility list" (recommended, no behavior change, the operation stays for compatibility) or give it a real override (for example suppress `FoldPending` for a named file). A real override adds a way to write over unfolded disk changes; recommend not until a concrete need exists.
3. **`FoldPending` persistent mismatch.** If the in-memory text keeps differing from disk after a fold has run (for example a normalization difference between document text and disk text, or a file the workspace failed to refresh), every write to that file is refused forever. Recommend: refuse at most once per path per completed fold; if the mismatch persists after a fold, proceed and add a finding, or refuse with a message that names the existing full-reload operation (verify the tool name at dispatch). The comparison must use the same normalization as the stale-read check (it does today for `StaleRead`; Step 8b reuses that lookup).
4. **Unchecked-target rule for `FoldPending`:** a target with no document or no readable disk pre-image is not checked. Protects the ~675 `SetTestSolution` usages and new files; the cost is that a file missing from the workspace (not yet added) is never guarded. Recommend accept.
5. **Full-reload locking:** 9c releases the write lock before a full reload. If a reload can race a cross-process writer, `FoldPending` and the ledger still catch the files the agent touched. Recommend accept; revisit only with an observed torn reload.
6. **`Search` fold-on-read (Step 9f):** SETTLED 2026-11-09: replaced by "apply pending reloads before Search, ReadFile and GetMethodSource".
7. **Lock configuration and holder fields:** the 10 s wait is a default in an options record; wiring a startup argument follows the `LlmOptions` pattern and is not measured here. Which of session id, tool name and reason `ToolCallContext` already carries is not measured; the holder record may start with agent id plus operation name and gain fields later.
8. **`WriteRefusal` message cap:** list at most 10 stale files plus "and N more" with the full list in `StructuredDetail` (Step 2). Recommended, not decided.
9. **Generic non-mapped refusal at the four anchor sites** (for example `ConcurrentEditRefused`) still falls through to `IsError = false`: fix in Step 4 or leave the `// TODO`. Plan leaves the `// TODO`; fixing changes behavior for disk-write failures and needs its own tests.
10. **Wait inside `_solutionLock` (the lock-order rule):** 9b waits up to the bound (10 s) for the OS lock while holding `_solutionLock`; in-process reads that call `GetCurrentSolutionAsync` (`:1697`) queue behind it. Recommend accept for the first version because the OS lock is normally held for tens of milliseconds (no compile validation under it).
    Alternative: try-acquire quickly, and on busy release `_solutionLock`, wait outside, retry; more complex and breaks the "always in that order" rule only for waiting. Revisit if a stall is observed.

Risks:

- **Bulk and project-wide tools refuse on an incidentally stale file.** `SemanticFindReplace`, `QualifyCrefs`, `NamedArguments`, `TestCategoryApply`, `RenameSymbol` can touch many files; if the agent read any of them earlier and another agent, `Git`
  checkout or an editor changed it, the whole batch is refused until that file is re-read. Engine tools compute edits from the current in-memory text, so for them the refusal is conservative (the edit would not have used the agent's old copy), which is the owner's chosen trade-off.
  Create-on-write (8a) widens this: an engine tool's own earlier write now also creates an entry, so a later external change to that file refuses the next batch. Mitigation: the message names every stale file (capped list, Step 2). Not measured: how often this fires in real runs (no telemetry for `StaleRead`; see `project_tool_error_code_taxonomy_proposal` for the unbuilt telemetry).
- **Behavior flips for 55 helper-routed sites in one 3-edit change.** That is the point of threading once, and the reason Step 3 requires a full test run, not only a build.
- **A weak model may loop on "re-read, then repeat" during the 500 ms debounce window.** The refusal text already says "if refused again, wait a moment" (closed plan, Window behavior); the engine and anchor paths now reach that text too, and `FoldPending` adds a second retry message with the same shape. Fold-on-read (9e) removes the loop for `ReadFile` and `GetMethodSource` (a re-read inside the window now returns disk text); `GetFileOutline`, `Search` and `InspectSymbol` can still record in-memory text inside the window.
- **Removing the drift halt (8c) before the lock (9b):** see the accepted exposure in Execution rules.
- **The lock is a new failure surface.** A hung peer process holding the lock for the full 10 s refuses every other session's writes with `WriteLockBusy`; a crashed holder releases the lock (the OS closes the handle). A lock file on a network share or a filesystem without `FileShare.None` semantics is NOT measured; the repo and `.roslynsentinel/` precedent are local.
- **Recording is wider than the content shown.** `GetFileOutline` returns structure, `InspectSymbol` returns one symbol's metadata, yet each marks the whole file observed (decided). An agent that outlined a file and then writes it passes the check even if the body changed between outline and write. Same class as the closed plan's accepted partial-read hole, now with weaker evidence of having seen the text.
- **Dry run is not checked** (Step 3); a stale file previews fine and is refused on the real apply.
- **Convention, not enforcement** for new read tools remains (a new read tool must still call `RecordObserved` and, after F7, `SyncFileFromDiskIfStaleAsync`); new writers are covered by the ratchet.
- **Test coverage:** passing tests will not prove the 34 mechanically edited sites each opt in; only the ratchet does (row L). The inverted tests (rows O, R) prove the new semantics only if the implementer is given the list in the brief; an all-green run with an un-inverted test left behind would mean the brief omitted it, a planner gap. No test covers two processes contending for the lock (row W gap).
- **Fake/PWM divergence** (fake: memory-only check, no unobserved finding, no lock, no `FoldPending`) can make a fake-backed tool test pass while the production path differs; rows A, C, D, F, K, O-S, X-Z require real PWM.
- Line numbers in this plan are 2026-11-09 and shift as slices land (the PWM chokepoint lines already shifted by about 80 between Steps 1-5's measurement and the F1/F7 measurement); the source wins.
