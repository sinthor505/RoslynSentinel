# Plan: one ExternalFileDrift tool, reworded halt message, and halt state stamped onto responses

**Status:** READY 2026-10-09 (amended per round-3 decisions). Implements owner decisions 1, 1b and 2 of `docs/current/proposals/proposal_journal_digest_followup_decisions.md` plus round-3 decisions D-1, D-2, D-3 and D-21; nothing built yet.

## Problem

When a confirmed external-drift hit trips the session-halt latch, the agent gets this text from the
write chokepoint (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1281` and `:1337`, identical
strings, re-measured 2026-10-09): "Session halted: external file drift was detected on a tracked file. This
session cannot safely continue. Stop and report to the user/operator."

That text is stale and misleading on four counts:

1. It says the session cannot continue, but recovery exists (`AcknowledgeExternalFileChanges` clears the latch).
2. It names no recovery call, so an agent that does not already know the three admin tools stops.
3. It does not say that read-only tools still work.
4. Discovering recovery takes three differently named tools (`ListExternalDiskChanges`, `IsSessionHalted`,
   `AcknowledgeExternalFileChanges`, all in `RoslynSentinel.Tools.Basic/AdminTools.cs`), and a halted
   response elsewhere (an unrelated tool, `Git`, a breaker refusal) carries no pointer to them.

Further defects found while tracing (all re-verified in source 2026-10-09):

- A partial acknowledge never clears the latch while any entry remains flagged
  (`AdminTools.cs:112-116`, comment "DEFERRED DECISION ... plan_session_halt_recovery_and_git_gaps.md,
  Risks 1"). The owner has decided the policy (decision 2), so the comment and the "Session latch still set"
  message (`AdminTools.cs:131-134`) are obsolete.
- `acknowledge` with no `files` clears everything with no confirmation (`AdminTools.cs`, the "Case A" branch, lines ~60-77). D-2 closes this.
- `AdminTools.cs` lines 7-14 (header comment) still describe a retired "Admin mode" gate.
- `SessionHaltedException`'s doc comment in `RoslynSentinel.Common/ToolException.cs` calls the latch "terminal
  and non-actionable"; that is no longer true for the drift latch.
- `DriftMessages.BuildHint` (`RoslynSentinel.Common/DriftMessages.cs:49`) tells the agent to run
  `ListExternalDiskChanges`, a name this plan removes.
- The mutation breaker's own directive text (`MutationCircuitBreaker.cs`, `CheckBreaker` and
  `ComputeDirectiveUnlocked`) says "until reset_breaker is called by the user"; no tool has that name (the tool
  is `ResetMutationBreaker`, `RoslynSentinel.Tools.Advanced/AsyncifyTools.cs:3822`). The stamp must not copy it.

Journal evidence (journal-digest entries, session:line): `a08be84f:L53`, `a08be84f:L48`,
`47b2c93d:L16`, `96b0b939:L17`, `0017ac93:L6`, `50e0e6ac:L29`. Per CLAUDE.md these are impressions; the
claims above were each traced to the cited source lines. The `ok:false` result recorded for
`ListExternalDiskChanges` in one journal entry was not traced (the method at `AdminTools.cs:26` has no failing
branch in source); it may be a stale-binary artefact.

## Decision

1. **One tool.** `ExternalFileDrift(operation: Status | List | Acknowledge, files?, acknowledgeScope?)`, hosted by
   the existing `AdminTools` class (so no new class, no `ToolClassRegistry` mode entry, no DI block). The three
   old tools are deleted outright (no aliases).
   - `Status`: reports whether the drift latch is set and how many external changes are tracked.
   - `List`: full list of tracked external changes.
   - `Acknowledge`: declares tracked changes reviewed and **clears the session-halt latch** (decision 2). The
     second enum parameter `acknowledgeScope` (D-2) makes the choice explicit:
     - `ConfirmWithListedFiles` (default): requires `files`; clears exactly those entries.
     - `ConfirmAll`: requires `files` to be empty; clears every tracked entry.
     - `Acknowledge` with no `files` and the default scope is **refused**, clearing nothing, with a message that
       names both ways forward (see step 1 for the exact text). `ConfirmAll` together with `files` is also refused.
     - Entries not named (listed-files case) stay flagged, so the next write that touches one re-trips the halt.
       The reply says which files remain flagged and that they re-trip the halt.
   - Parameter name `acknowledgeScope` was chosen over the bare `scope` because the tool has other
     parameters and operations, and the value only matters for `Acknowledge`; the enum is
     `ExternalFileDriftAcknowledgeScope`. Precedent for a defaulted enum parameter on a tool:
     `McpServerStatus(toolListing = none)` (`ServerStatusTools.cs`).
   - Returns a `string` (as `AcknowledgeExternalFileChanges` and `McpServerControl` do today), so the three
     operations share one return type.
   - Carries `[UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]` (the golden set stays at 8 names:
     `ExternalFileDrift` replaces `IsSessionHalted`). D-1: this also lets `Acknowledge` run during an
     unrecoverable halt; it clears only drift state and the write chokepoint still refuses every write
     (`PersistentWorkspaceManager.cs:1284-1295`).
   - Enum values are PascalCase, matching `McpServerControlOperation`. Enum case repair has landed (commit
     `98299ed`, `ToolArgumentValidator.NormalizeEnumCase`), so lowercase `status | list | acknowledge` is accepted too.
   - **Descriptions (D-2).** The tool `[Description]` and each parameter description are rewritten, not just
     extended; exact text in step 1.
2. **Reworded halt message** (single constant, `DriftMessages.DriftHaltMessage`, used at both throw sites). It
   lands **after** the tool merge (D-21: steps 5-7 follow steps 1-4):
   > Session halted: a write was refused because a file changed on disk outside this server (external file
   > drift). Read-only tools (ReadFile, GetFileOutline, Search, GetMethodSource, Git status/log/diff) still work.
   > To resume writing: call ExternalFileDrift(operation: List) to see the files, review them, then
   > ExternalFileDrift(operation: Acknowledge, files: <the files you reviewed>). To clear every tracked change at
   > once pass acknowledgeScope: ConfirmAll instead of files. Files you do not acknowledge stay flagged and halt
   > the session again if a write touches them.
3. **Halt stamping filter, all breakers (decision 1b, D-3).** A new request filter stamps four top-level
   properties into the first text block of every tool response while any breaker is tripped or the drift latch is
   set, so recovery needs no discovery call:
   ```json
   { "isSessionHalted": true,
     "sessionHaltKind": "externalDrift",
     "sessionHaltReason": "<one sentence>",
     "sessionHaltRecovery": "<one sentence naming the exact call>", ...original body... }
   ```
   - Source of truth: `HaltInfo.From(...)` in `Common` (new). Kinds, in precedence order (first match wins; the
     others show once it clears):

     | Kind | Active when | `sessionHaltReason` | `sessionHaltRecovery` |
     | --- | --- | --- | --- |
     | `unrecoverable` | `IUnrecoverableBreaker.IsTripped()` | `StateMessage()` verbatim (already says no reset exists, stop, restart, operator review) | one fixed sentence: no reset exists; stop and report to the operator; read-only tools still work |
     | `externalDrift` | `IWorkspaceHealthReporter.IsSessionHalted()` | `DriftMessages.DriftHaltReason` | `DriftMessages.DriftHaltRecovery` |
     | `orientation` | `IAutomaticCircuitBreaker.IsTripped()` | fixed sentence: "The orientation breaker is tripped: repeated Search(mode: text) calls returned no matches." | `IAutomaticCircuitBreaker.StateMessage()` verbatim (D-3: reuse the existing text; it names ListAll/ListSolutionItems and says the breaker clears itself) |
     | `mutation` | `IManualCircuitBreaker.IsTripped()` | fixed sentence: "The batch-mutation breaker is tripped after repeated failed batches; Asyncify/BulkComment-style batch tools are disabled." | if `resetMutationBreakerActive`: "Review why the batches failed, fix the cause, then call ResetMutationBreaker." else: "Stop and report to the user/operator; the tool that resets this breaker is not available in this tool set." |

     `orientation` ranks above `mutation` because it restricts every non-orienting tool, while the mutation
     breaker only gates the Asyncify/BulkComment batch tools (`AsyncifyTools.cs`, `CommentingTools.cs`, via
     `CheckBreaker()`).
   - **How the stamp knows whether `ResetMutationBreaker` is active (D-3).** `ResetMutationBreaker` lives in
     `AsyncifyTools` (`RoslynSentinel.Tools.Advanced/AsyncifyTools.cs:3822`), which is not in the claude-lean
     allow-list. The authoritative live check is the server's tool collection, the same collection
     `ToolsetService` mutates (`RoslynSentinel.Common/ToolsetService.cs:104-121`, via
     `McpServerOptions.ToolCollection` and `McpServerPrimitiveCollection<McpServerTool>.TryGetPrimitive(name, out _)`).
     The filter resolves `IOptions<McpServerOptions>` from `context.Server.Services` (it is registered; `ToolsetService`
     takes it through its constructor, `ToolsetService.cs:60-65`) and asks `TryGetPrimitive("ResetMutationBreaker", out _)`.
     This is correct for static modes, claude-lean allow-lists and tools switched on at runtime by
     `McpToolsetControl`, none of which `ActiveToolSurface` alone captures (`ActiveToolSurface.cs:46-48` only knows
     classes plus the startup allow-list). Failure to resolve the collection counts as "not active" (the safe
     answer is "stop and report"). The result is passed into `HaltInfo.From` as a plain `bool`, so `Common` stays
     free of filter plumbing and the unit tests need no host.
   - Position: registered **second-to-first, right after `AddArgumentValidationFilter`, before
     `AddToolErrorFilter`** (`ServiceRegistrationExtensionsBasic.cs:102-110`, re-measured: Echo 102, ArgumentValidation
     103, ToolError 104, WorkspaceNotLoaded 105, Drift 106, LargeResultOffload 107, WirePathRelativize 108,
     Orientation 109, Unrecoverable 110). It is therefore outer to the ToolError, workspace-not-loaded, drift,
     offload, relativize and both breaker filters (it sees breaker refusals, which are plain text, and post-offload
     stubs) and inner to the Echo filter (Echo still stamps last and sees the final body). The state is read
     **after** `next` returns, so an orientation breaker that an inner filter just reset is not stamped. Advanced
     reuses this chain (`ServiceRegistrationExtensionsAdvanced.cs` calls `AddRoslynSentinelToolsBasic`), so one
     registration serves both servers.
   - Skipped for a call to `ExternalFileDrift` when the active kind is `externalDrift` (its own reply carries the
     state), and for any body that already contains an `isSessionHalted` key.
   - Mechanics follow `ToolCallEcho.Stamp` (`RoslynSentinel.Server.Basic/ToolCallEcho.cs:79`): operate on the first
     text block; if it parses as a JSON object insert the four properties at index 0; otherwise wrap as
     `{"isSessionHalted":...,"message":"<original text>"}`; use `SharedJsonOptions.Compact`; any failure is
     swallowed (a diagnostic aid never breaks a call). Never stamp when nothing is tripped (zero cost).
   - Cost: roughly 400 characters per response while a breaker is tripped, nothing otherwise.
   - The property is named `isSessionHalted` for all four kinds (decision 1 wording). For `orientation` and
     `mutation` that slightly overstates (the session can still run other tools); `sessionHaltKind` and the reason
     text carry the precision. See Risks.

## Execution rules

- Every step is one compile-green slice for a Haiku-tier implementer: at most 3 files, one acceptance check,
  edit nothing outside the named symbols; if a test seems to need another change, reply `RESCOPE:`. No
  Write/Edit/shell writes on `.cs` files.
- Use `ReplaceSnippet` with `batchEdits` (definitions before call sites) wherever a step lists more than one
  edit; never pass `filePath` together with `batchEdits`. No `Edit`/`Write` on `.cs`.
- ASCII-only punctuation in everything written.
- Tests are NUnit 5: `Assert.ThrowsAsync`/`CatchAsync` must be `await`ed.
- **Ordering (D-21):** steps 1-4 are the tool merge; steps 5-7 (halt wording, `HaltInfo`, stamping) land only after
  step 4 is green. Step 2 leaves three Server test fixtures red at runtime (still compiling); step 3 fixes them. Do
  not run the Server fixtures between those two steps.
- Doc steps (8-11) edit Markdown/JSON with the normal file tools; no Build needed, the check is a grep for the
  old names returning nothing in the named files.

## Steps

### Step 1 - AdminTools: add ExternalFileDrift, remove the three old tools, update tests
- Files: `RoslynSentinel.Tools.Basic/AdminTools.cs`, `RoslynSentinel.Tests.Tools.Basic/AdminToolsTests.cs`
- Symbols: `AdminTools.ListExternalDiskChanges` (lines 26-35), `AdminTools.IsSessionHalted` (37-47),
  `AdminTools.AcknowledgeExternalFileChanges` (49-138), header comment (7-14); new
  `AdminTools.ExternalFileDriftOperation`, `AdminTools.ExternalFileDriftAcknowledgeScope`,
  `AdminTools.AcknowledgeDrift`, `AdminTools.ExternalFileDrift`; tests in `AdminToolsTests` (old tests at lines 30-63).
- Change (one `ReplaceSnippet` with `batchEdits`; definitions first, removals after, tests last):
  1. Add nested `public enum ExternalFileDriftOperation { Status, List, Acknowledge }` and
     `public enum ExternalFileDriftAcknowledgeScope { ConfirmWithListedFiles, ConfirmAll }` beside
     `McpServerControlOperation` (line 140).
  2. Add `public static string AcknowledgeDrift(IWorkspaceHealthReporter health, string? files, ExternalFileDriftAcknowledgeScope scope)`
     containing the logic currently inlined in `AcknowledgeExternalFileChanges` (Case B parse/resolve), with this
     behaviour:
     - `scope == ConfirmAll` and `files` non-blank: refuse, clear nothing: "Nothing cleared: acknowledgeScope
       ConfirmAll clears every tracked external change and cannot be combined with files. Either omit files (to
       clear all) or pass files with acknowledgeScope ConfirmWithListedFiles (to clear only those)."
     - `scope == ConfirmAll`, no files: `ClearExternalFileChanges()` + `ClearSessionHalt()`; reply "Cleared N
       tracked external file change(s): <summary>. Session-halt latch cleared." (keep the current Case A wording
       for the count/summary).
     - `scope == ConfirmWithListedFiles` and `files` blank: refuse, clear nothing: "Nothing cleared:
       operation Acknowledge needs one of two things. (1) files = the files you reviewed with ExternalFileDrift(operation:
       List), keeping acknowledgeScope at its default ConfirmWithListedFiles, to clear only those files; or
       (2) acknowledgeScope = ConfirmAll with no files, to clear every tracked change. Tracked changes: <summary>."
       (This message names both options and both parameters, per D-2.)
     - `scope == ConfirmWithListedFiles` and `files` given: parse (reuse `DelimitedListParser` error returns),
       resolve with `DriftMessages.ResolveSelection`; unmatched or empty selection returns "Nothing cleared ..." and
       does NOT clear the latch; messages say `ExternalFileDrift(operation: List)` where they currently say "Call
       ListExternalDiskChanges"; on success clear the matched entries and **always clear the latch** (decision 2);
       the reply ends "Session-halt latch cleared. N file(s) remain flagged: <summary>. A write that touches one
       of them will halt the session again; acknowledge them too once reviewed." (omit the remaining-files
       sentence when none remain).
     - Delete the `DEFERRED DECISION` comment and the "Session latch still set while other changes remain
       flagged" branch.
     - Static + interface parameter so tests can drive it with a stub (the manager cannot be seeded with drift
       without a real file watcher round trip).
  3. Add instance method `ExternalFileDrift(ToolCallReason reason, ExternalFileDriftOperation operation, string? files = null, ExternalFileDriftAcknowledgeScope acknowledgeScope = ExternalFileDriftAcknowledgeScope.ConfirmWithListedFiles, CancellationToken cancellationToken = default)`
     with `[UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]`, `[McpServerTool(Name = "ExternalFileDrift")]`,
     `[Produces(DataTag.ResultOnly)]` and these descriptions (copy verbatim):
     - Tool: "Inspect and resolve external file drift: a tracked file that changed on disk outside this server (an
       editor, git, a build step). A write that touches such a file is refused and halts the session until the drift
       is acknowledged; read-only tools keep working meanwhile. operation Status = is the session halted and how many
       external changes are tracked. operation List = full paths of the tracked changes; review these first.
       operation Acknowledge = declare changes reviewed and clear the session halt; it needs either files (names or
       relative paths from List) with acknowledgeScope ConfirmWithListedFiles (the default), or acknowledgeScope
       ConfirmAll with no files to clear every tracked change. Files you did not name stay flagged and halt the
       session again if a later write touches them."
     - `operation`: "Status, List or Acknowledge. After a write was refused with 'Session halted', call List first."
     - `files`: "Only for operation Acknowledge with acknowledgeScope ConfirmWithListedFiles. File names or relative
       paths (CSV or JSON array) taken from operation List. Leave empty when acknowledgeScope is ConfirmAll."
     - `acknowledgeScope`: "Only for operation Acknowledge. ConfirmWithListedFiles (default): clear only the
       files named in files; refused when files is empty. ConfirmAll: clear every tracked external change; pass no
       files."
     Behaviour: `Status` returns `"SessionHalted=<bool>; tracked external changes: <n> (<summary>)."` (breaker
     states stay in `McpServerStatus`); `List` returns the full-path list or "No tracked external file changes.";
     `Acknowledge` calls `AcknowledgeDrift(_workspaceManager, files, acknowledgeScope)`. If `files` is non-blank, or
     `acknowledgeScope` is `ConfirmAll`, with `Status`/`List`, return `"files and acknowledgeScope are only used with operation: Acknowledge; nothing changed."`.
  4. Delete methods `ListExternalDiskChanges`, `IsSessionHalted` and `AcknowledgeExternalFileChanges` and their attributes.
  5. Rewrite the header comment (lines 7-14) to: "Operator-adjacent tools: ExternalFileDrift (status/list/acknowledge
     external-change state and the session-halt latch) and McpServerControl (process lifecycle). Always visible
     in claude-lean (ToolClassRegistry.ClaudeLeanToolNames)."
  6. `AdminToolsTests.cs`: replace the five old tests at lines 30-63 with: `ExternalFileDrift_Status_ReportsNotHaltedOnFreshManager`,
     `ExternalFileDrift_List_OnFreshManager_SaysNoChanges`, `ExternalFileDrift_FilesWithStatus_IsRefused`,
     `Acknowledge_UnknownFile_ReturnsNothingClearedNamingFile` (port of the test at line 43),
     `Acknowledge_MalformedJsonArray_ReturnsParserError` (port of line 58) and, against a private nested
     `StubHealth : IWorkspaceHealthReporter` (three drift paths `A.cs`, `B.cs`, `C.cs`; records
     `ClearSessionHalt` calls; other members `throw new NotImplementedException()`):
     `Acknowledge_Partial_ClearsLatchAndNamesRemainingFiles` (clears A, asserts latch cleared once, text contains
     `B.cs`, `C.cs` and "halt the session again"),
     `Acknowledge_NoFilesDefaultScope_RefusesNamingBothOptions` (asserts nothing cleared, latch untouched, text
     contains `files`, `ConfirmWithListedFiles` and `ConfirmAll`; replaces the old
     `..._NoFilesArgument_ReportsZeroCleared` at line 51),
     `Acknowledge_ConfirmAll_ClearsEverythingAndLatch` and
     `Acknowledge_ConfirmAllWithFiles_IsRefusedAndClearsNothing`.
- Call sites (measured 2026-10-09 with a text search): `ListExternalDiskChanges` 1 (`AdminToolsTests.cs:32`),
  `AcknowledgeExternalFileChanges` 4 (`AdminToolsTests.cs:39,45,53,60`), tool-method `IsSessionHalted` 0. Manager
  `IsSessionHalted()` stays (still called by `ServerStatusTools.cs:88`; declared `IWorkspaceHealthReporter.cs:25`).
  Name-only (string/comment) mentions are handled in later steps; two historical comments
  (`WorkspaceTools.cs:133`, `BatteryTwentyTests.cs:759`, both "moved to AdminTools") are left alone.
- Acceptance: `RunTest` on `AdminToolsTests` passes (and `Build` has 0 errors).
- Out of scope: registrations, Server tests, `DriftMessages`, halt wording, any change to `McpServerControl`.

### Step 2 - Registrations: claude-lean allow-list, toolset catalog, toolset description
- Files: `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`, `RoslynSentinel.Common/ToolsetCatalog.cs`,
  `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs`
- Symbols: `ToolClassRegistry.ClaudeLeanToolNames` (line 37), `ToolsetCatalog.ToolsBySet` (line 52), `McpToolsetControl` description (`ToolsetControlTools.cs:27`).
- Change (one `ReplaceSnippet` batch):
  - `ToolClassRegistry.cs:37`: replace `"AcknowledgeExternalFileChanges"` and `"ListExternalDiskChanges"` with the
    single entry `"ExternalFileDrift"`. The Core set goes from 26 to 25 names. No change to the `AdminTools` class
    entries (the class and DI block are unchanged).
  - `ToolsetCatalog.cs:52` (`projectAdmin`): remove `"IsSessionHalted"`. (`ExternalFileDrift` is Core, not
    on-demand, so it does not belong in `ToolsBySet`.)
  - `ToolsetControlTools.cs:27`: remove `IsSessionHalted` from the description text listing the `projectAdmin` tools.
- Call sites: string literals only; no symbol references. Known consumers that will fail at runtime until
  step 3: `ClaudeLeanModeTests`, `McpToolsetControlTests`, `UnrecoverableBreakerPolicyTests`.
- Acceptance: `Build` 0 errors.
- Out of scope: tests (step 3).

### Step 3 - Server tests: allow-list, toolset and golden set
- Files: `RoslynSentinel.Tests.Server/ClaudeLeanModeTests.cs`, `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs`,
  `RoslynSentinel.Tests.Server/UnrecoverableBreakerPolicyTests.cs`
- Change (one `ReplaceSnippet` batch):
  - `ClaudeLeanModeTests.cs`: in `CoreTools` (line 33) replace the two old names with `"ExternalFileDrift"`; in
    `ClaudeAdvancedBaseline` remove `"AcknowledgeExternalFileChanges"` (line 40), `"IsSessionHalted"` and
    `"ListExternalDiskChanges"` (line 46) and add `"ExternalFileDrift"` in alphabetical position; the
    `Has.Length.EqualTo(26)` assertion (line 113) becomes 25 and the test
    `ClaudeLean_ExposesExactlyTheTwentySixCoreTools` is renamed `..._TwentyFiveCoreTools` (use `RenameSymbol`, it has
    no other callers).
  - `McpToolsetControlTests.cs`: line 416 delete `Assert.That(during, Does.Contain("IsSessionHalted"));`
    (the next line, `GetWorkspaceHealth`, keeps the test's meaning); lines 474-478
    (`EnabledTool_IsCallable_WithoutRelisting`): delete the `IsSessionHalted` call and its comment, keep
    the `GetWorkspaceHealth` call; line 493 (`DisabledTool_IsNotCallable`): call `GetWorkspaceHealth` instead
    of `IsSessionHalted`; line 539 `Has.Count.EqualTo(26)` becomes 25.
  - `UnrecoverableBreakerPolicyTests.cs`: line 39 `"IsSessionHalted"` becomes `"ExternalFileDrift"`; line 76
    `[TestCase("IsSessionHalted")]` becomes `[TestCase("ExternalFileDrift")]`. The set size stays 8.
- Call sites: none beyond the named lines (re-measured 2026-10-09).
- Acceptance: `RunTest` filtered to the three fixtures passes: `ClaudeLeanModeTests`, `McpToolsetControlTests`,
  `UnrecoverableBreakerPolicyTests`.
- Out of scope: any production file.

### Step 4 - BuildHint names the new tool
- Files: `RoslynSentinel.Common/DriftMessages.cs`, `RoslynSentinel.Tests/DriftMessagesTests.cs`
- Symbols: `DriftMessages.BuildHint` (line 49 text), test `BuildHint_NonEmpty_ContainsListExternalDiskChanges` (line 157, assertion line 161).
- Change (one `ReplaceSnippet` batch): in `BuildHint` replace `run ListExternalDiskChanges` with `run ExternalFileDrift(operation: List)`;
  rename the test to `BuildHint_NonEmpty_NamesExternalFileDrift` and assert `Does.Contain("ExternalFileDrift")`.
- Call sites: `BuildHint` callers unchanged (string only changes).
- Acceptance: `RunTest` on `DriftMessagesTests` passes.
- Out of scope: the halt message (step 5). This is the last step of the merge group (D-21 gate).

### Step 5 - Common: halt text constants and HaltInfo (after the merge, D-21)
- Files: `RoslynSentinel.Common/HaltInfo.cs` (new), `RoslynSentinel.Common/DriftMessages.cs`,
  `RoslynSentinel.Tests/HaltInfoTests.cs` (new)
- Symbols: new `HaltInfo`; new `DriftMessages.DriftHaltReason`, `DriftHaltRecovery`, `DriftHaltMessage`.
- Change:
  - `DriftMessages.cs`: add `public const string DriftHaltReason` (the first two sentences of the Decision-2
    message), `public const string DriftHaltRecovery` (the "To resume writing ..." sentences, including the
    `acknowledgeScope: ConfirmAll` alternative), and `public const string DriftHaltMessage = DriftHaltReason + " " + DriftHaltRecovery`.
  - `HaltInfo.cs` (create first; definition before use): `public sealed record HaltInfo(string Kind, string Reason, string Recovery)`
    with `public static HaltInfo? From(IWorkspaceHealthReporter health, IUnrecoverableBreaker unrecoverable, IAutomaticCircuitBreaker orientation, IManualCircuitBreaker mutation, bool resetMutationBreakerActive)`
    implementing the kind table in "Decision" item 3 (precedence unrecoverable, externalDrift, orientation,
    mutation; `null` when none). All four breaker interfaces and `IWorkspaceHealthReporter` are in `Common`
    (`ICircuitBreaker.cs`, `IWorkspaceHealthReporter.cs`).
  - `HaltInfoTests.cs`: use the real breaker classes with `NullLogger.Instance` (no stubs for the breakers):
    `UnrecoverableCircuitBreaker` (`Trip("T","id","diag")`), `OrientationCircuitBreaker(logger, tripThreshold: 2)` then
    `RecordSearchOutcome(0)` twice, `MutationCircuitBreaker` then `RecordBatchOutcome(0,1,0,0)` eight times (streak
    threshold 8, `MutationCircuitBreaker.cs`), and a small private stub of `IWorkspaceHealthReporter` whose
    `IsSessionHalted()` is settable (other members `throw new NotImplementedException()`). Tests: none tripped ->
    `null`; each kind alone yields its `Kind`; precedence (unrecoverable over drift over orientation over mutation);
    mutation recovery contains `ResetMutationBreaker` when `resetMutationBreakerActive` is true and contains
    "Stop and report" and not `ResetMutationBreaker` when false; orientation `Recovery` equals the breaker's
    `StateMessage()`; `DriftHaltMessage` contains `ExternalFileDrift`, `Acknowledge`, `ConfirmAll` and `Read-only`.
- Call sites: none (new members).
- Acceptance: `RunTest` on `HaltInfoTests` passes (and `Build` has 0 errors).
- Out of scope: the manager throw sites (step 6), the filter (step 7).

### Step 6 - Manager: use the reworded message at both throw sites
- Files: `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `RoslynSentinel.Common/ToolException.cs`,
  `RoslynSentinel.Tests.Tools.Basic/CreateFileDeleteFileTests.cs`
- Change:
  - `PersistentWorkspaceManager.cs`: replace the string literals at the two drift throw sites (lines 1281 inside
    `if (_sessionHalted)` and 1337 inside `if (driftedTargets.Count > 0)`) with `DriftMessages.DriftHaltMessage`.
    Do NOT touch the unrecoverable throw at ~1293 (`throw new SessionHaltedException(unrecoverableHalt)`).
    Update the comment at ~1274-1277 only to drop "terminal"/"unconditionally" if it contradicts recovery;
    behaviour is unchanged.
  - `ToolException.cs`: edit the `SessionHaltedException` XML doc to say the drift latch is cleared by
    `ExternalFileDrift(operation: Acknowledge)` and the unrecoverable halt has no reset.
  - `CreateFileDeleteFileTests.cs`: in `DeleteFile_DriftedFile_RefusesDeleteAsync` (real-drift pattern at ~298-323)
    additionally assert the refusal text contains `ExternalFileDrift`. If that test asserts on a different
    exception text shape, add the assertion to the existing message check rather than a new test.
- Call sites: none (string constants only). `TestCategoryApplyTests.cs:265` feeds the old literal into a fake as
  input data only; it asserts nothing about the text and is left alone.
- Acceptance: `RunTest` on `CreateFileDeleteFileTests.DeleteFile_DriftedFile_RefusesDeleteAsync` passes.
- Out of scope: the filter.

### Step 7 - Halt-stamping filter (all breakers)
- Files: `RoslynSentinel.Server.Basic/HaltStamp.cs` (new), `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`,
  `RoslynSentinel.Tests.Server/HaltStampFilterTests.cs` (new)
- Change:
  - `HaltStamp.cs`: `internal static class HaltStamp` with `public static void Stamp(CallToolResult result, HaltInfo info)`,
    modelled on `ToolCallEcho.Stamp` (first text block; JSON-object body -> insert `isSessionHalted:true`,
    `sessionHaltKind`, `sessionHaltReason`, `sessionHaltRecovery` at index 0; any other text -> wrap
    `{"isSessionHalted":true,...,"message":"<text>"}`; skip when the body already has `isSessionHalted`;
    `SharedJsonOptions.Compact`). No-op when `result.Content` has no text block.
  - `ServiceRegistrationExtensionsBasic.cs`: add `private static void AddHaltStampFilter(IMcpRequestFilterBuilder filters)`
    next to `AddUnrecoverableBreakerFilter`. Body: `AddCallToolFilter`; call `next` first; resolve
    `context.Server.Services?.GetService<PersistentWorkspaceManager>()` exactly as the unrecoverable and orientation
    filters do; compute `resetActive` as
    `context.Server.Services?.GetService<IOptions<McpServerOptions>>()?.Value.ToolCollection is { } c && c.TryGetPrimitive("ResetMutationBreaker", out _)`
    (any exception or null -> `false`); call `HaltInfo.From((IWorkspaceHealthReporter)manager, (IUnrecoverableBreaker)manager, (IAutomaticCircuitBreaker)manager, (IManualCircuitBreaker)manager, resetActive)`
    (explicit casts are required because the manager implements the breaker members explicitly,
    `PersistentWorkspaceManager.cs:1995-2049`); if `context.Params?.Name == "ExternalFileDrift"` and `info.Kind == "externalDrift"`
    return the result untouched; else `HaltStamp.Stamp` in a try/catch that only `Debug.WriteLine`s. Register it in
    `AddRoslynSentinelToolsBasic` between `AddArgumentValidationFilter(filters);` and `AddToolErrorFilter(filters);`
    (lines 103-104). Definition (`AddHaltStampFilter`) is added before the registration line in the same batch.
  - `HaltStampFilterTests.cs`: model on `RoslynSentinel.Tests.Server/ToolCallEchoFilterTests.cs` (in-process host, `SetUp`
    at line 44; mode `Workspace`). Tests: (a) nothing tripped -> response body has no `isSessionHalted`; (b) trip the
    unrecoverable breaker (resolve `PersistentWorkspaceManager` from the host, `((IUnrecoverableBreaker)m).Trip("T","id","diag")`)
    -> an allowed call (`GetWorkspaceHealth`) is stamped with `sessionHaltKind == "unrecoverable"`; (c) a refused call
    (a non-allowed tool) has a stamped wrapper whose `message` holds the original refusal text; (d) a call to
    `ExternalFileDrift` during an *unrecoverable* halt is stamped (the skip rule applies only to the `externalDrift`
    kind); (e) mutation breaker tripped (`((IManualCircuitBreaker)m).RecordBatchOutcome(0,1,0,0)` x8) in the default
    host (no `ResetMutationBreaker` registered) -> `sessionHaltKind == "mutation"` and `sessionHaltRecovery` contains
    "Stop and report" and not `ResetMutationBreaker`; (f) the same in a second host that also registers a probe tool named
    `ResetMutationBreaker` (`mcpBuilder.WithTools<ProbeTool>()`, as `CaseCollisionProbeTool` is registered in the echo
    tests) -> `sessionHaltRecovery` contains `ResetMutationBreaker`; (g) orientation breaker tripped (10 zero-match
    `RecordSearchOutcome(0)`) -> `sessionHaltKind == "orientation"` and `sessionHaltRecovery` equals the breaker's `StateMessage()`.
    The `externalDrift` kind is covered by `HaltInfoTests` (step 5) plus the live check in the final step (real drift
    cannot be seeded cheaply).
- Call sites: new private methods only; one registration line added. No existing symbol changes.
- Acceptance: `RunTest` on `HaltStampFilterTests` passes (and `Build` has 0 errors).
- Out of scope: changing any breaker's own `StateMessage()` text (including the stale `reset_breaker` wording in
  `MutationCircuitBreaker`; the stamp composes its own mutation text).

### Step 8 - Docs/config: allow-list and agent docs
- Files: `.claude/settings.json`, `CLAUDE.md`, `.claude/agents/orchestrator.md`
- Change:
  - `.claude/settings.json`: remove the entries `...AcknowledgeExternalFileChanges`, `...IsSessionHalted`,
    `...ListExternalDiskChanges` (locate by search; lines were 158, 181, 183); add one
    `"mcp__root_roslyn_sentinel_advanced_stdio__ExternalFileDrift"` (alphabetical position, after `DeleteFile`).
    Keep valid JSON.
  - `CLAUDE.md:178`: replace the sentence naming `ListExternalDiskChanges()` then `AcknowledgeExternalFileChanges()`
    with `ExternalFileDrift(operation: List)` then `ExternalFileDrift(operation: Acknowledge, files: ...)`.
  - `.claude/agents/orchestrator.md:120`: replace `ListExternalDiskChanges` in the read-only tool list with `ExternalFileDrift`.
- Call sites: none (text). Hooks and `.ps1` scripts were searched and contain none of the three names.
- Acceptance: a text search for the three old names over these three files returns no match.

### Step 9 - Docs: architecture map, write-path reference, superseded idea
- Files: `docs/current/references/reference_architecture_map.md`, `docs/current/reference-code-file-write-paths-v1.md`,
  `docs/current/ideas/external-drift-hard-blocker.md`
- Change:
  - `reference_architecture_map.md`: (a) in "Request pipeline" insert the halt-stamp filter after
    `AddArgumentValidationFilter` and renumber, describing the four stamped properties, the four kinds, its skip rule
    for `ExternalFileDrift`, the live `ResetMutationBreaker` check, and "non-JSON refusal text is wrapped into
    `{..., message}`"; also add the workspace-not-loaded and wire-path-relativize filters if still absent from that
    list; (b) the allow-list sentence: `IsSessionHalted` becomes `ExternalFileDrift`; (c) in "Write chokepoint" add a
    short "Session halt and acknowledge" paragraph: the drift latch is `_sessionHalted`; `ExternalFileDrift(operation:
    Acknowledge)` clears the latch even when only some files are named; files not named stay in `_externalChanges`
    and re-trip the halt on the next write that touches them; clearing everything needs `acknowledgeScope: ConfirmAll`;
    `LoadSolution(forceReload)` does not drain `_externalChanges` (comment at `PersistentWorkspaceManager.cs` ~511);
    the unrecoverable halt has no reset (but `ExternalFileDrift` is allowed during it, D-1); (d) update the "Status" line date.
  - `reference-code-file-write-paths-v1.md:29`: replace the old tool names with `ExternalFileDrift`.
  - `external-drift-hard-blocker.md`: add a top banner "Superseded 2026-10-09 by plan_external_file_drift_tool_and_halt_stamping.md;
    the Admin-mode gating described here was retired and the recovery tools merged into ExternalFileDrift".
- Acceptance: a text search for `ListExternalDiskChanges|AcknowledgeExternalFileChanges|IsSessionHalted` over
  `reference_architecture_map.md` and `reference-code-file-write-paths-v1.md` returns no match.

### Step 10 - Docs: TODO and sibling plans
- Files: `docs/current/TODO.md`, `docs/current/plans/plan_session_halt_recovery_and_git_gaps.md`,
  `docs/current/plans/plan_scoped_operation_ledger.md`
- Change:
  - `TODO.md` (~line 458 and ~598-624; re-find by search): replace old tool names with `ExternalFileDrift`; replace the
    open item "halt wording / latch policy" with a one-line follow-up "Revisit partial-acknowledge policy after real use
    (decision 2, proposal_journal_digest_followup_decisions.md)". Resolved entries move to `CLOSED.md` per
    the TODO rule; do not delete.
  - `plan_session_halt_recovery_and_git_gaps.md`: in the Status line and Risks 1, note that the latch policy was
    decided 2026-10-09 and implemented by this plan; its deferred Step 2 (halt wording) is covered by step 6 here.
  - `plan_scoped_operation_ledger.md` lines 49, 103, 105: replace old tool names with `ExternalFileDrift`.
- Acceptance: a text search for the three old names over these three files returns only explicitly historical
  mentions (for example "formerly ...").

### Step 11 - Docs: schema-cost proposal
- Files: `docs/current/proposals/proposal_reduce_tool_schema_token_cost.md`
- Change: lines 87-96 list `ListExternalDiskChanges`, `AcknowledgeExternalFileChanges` and `IsSessionHalted`
  as separate schema-cost items; replace with the single `ExternalFileDrift` and adjust any total in that
  paragraph (3 tools become 1; note the new tool carries one extra enum parameter).
- Acceptance: a text search for the three old names over the file returns no match.

### Step 12 - Regenerate architecture docs
- Files: `docs/generated/architecture_tools.md` (generated; lines 66-68 list the old tools) and any other file
  the script rewrites.
- Change: run `scripts/Generate-ArchitectureMap.ps1` (sets `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1`). Do not
  hand-edit generated files. Expected diff: three rows replaced by one `ExternalFileDrift` row; claude-lean
  tool counts decrease by 2 (3 tools removed, 1 added); the unrecoverable-allowed list shows `ExternalFileDrift`.
- Acceptance: `RunTest` on `ArchitectureDocFreshnessTests` passes.

### Step 13 - Final verification
- `Build` solution: 0 errors.
- `RunTest` at solution scope; compare with the known-failure baseline (memory `reference_known_failing_tests`
  and `docs/current`); report only new failures.
- `McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`, wait a few seconds,
  reconnect, `LoadSolution`. Then verify live on the new binary:
  1. `McpServerStatus(toolListing: inactive, toolNameFilter: "ExternalFileDrift")` shows it active in claude-lean
     and the three old names absent.
  2. `ExternalFileDrift(operation: Status)` returns `SessionHalted=False` on a fresh session.
  3. Provoke drift: edit a tracked `.cs` outside the server (a scratch file in a throwaway project is safest),
     then attempt a `ReplaceSnippet` on it; expect the reworded halt message; any following call (for example
     `GetWorkspaceHealth`) shows the stamped `isSessionHalted`/`sessionHaltRecovery` properties; then
     `ExternalFileDrift(operation: List)`; `(operation: Acknowledge)` with no files is refused naming both options;
     `(operation: Acknowledge, files: <one of two drifted files>)` clears the latch while naming the remaining file; a
     write touching the remaining file halts again; `(operation: Acknowledge, acknowledgeScope: ConfirmAll)` clears all.
- Acceptance: all three items above hold.

## Out of scope

- Auto-clearing the drift latch on `LoadSolution(forceReload)` when content hashes match disk.
- Per-file or per-project halt granularity (the latch stays session-wide).
- Case-insensitive enum repair in argument validation (landed separately, commit `98299ed`).
- Any change to `McpServerControl` or `McpServerStatus` (no folding into either; rejected in the proposal).
- Fixing the stale `reset_breaker` wording inside `MutationCircuitBreaker` directive text (the stamp does not use it).
- The `Read`-block hook (decision 3), scripts and retention items (other plans).
- Renaming the `AdminTools` class or moving it.

## Decided

- **D-1** `ExternalFileDrift` (including `Acknowledge`) is allowed during an unrecoverable halt; it clears only drift
  state and the write chokepoint still refuses writes. Replaces former Risk 2.
- **D-2** Second enum parameter `acknowledgeScope` (`ConfirmWithListedFiles` default / `ConfirmAll`); `Acknowledge`
  with no files and the default scope is refused with a message naming both options; descriptions rewritten (step 1).
  Replaces former Risk 3.
- **D-3** All breakers are stamped (also decision 1b). The mutation stamp names `ResetMutationBreaker` only when the live
  tool collection contains it, else "stop and report"; the orientation stamp reuses the breaker's existing text.
  Replaces former Risk 1.
- **D-21** The halt-wording step (5-6) lands after the tool merge (1-4).
- Decision 4 (enum case repair) is already built; the enum casing risk is closed.

## Risks and open decisions

1. **`isSessionHalted` is slightly overstated for `orientation` and `mutation`.** The session can still run other
   tools; only the property name is shared (owner's decision 1 wording). `sessionHaltKind` and the reason text carry
   the precision. If journals show agents stopping on an orientation stamp, split the name (for example
   `breakerTripped`) in a follow-up; not planned.
2. **Un-acknowledged entries survive `LoadSolution(forceReload)`** (`PersistentWorkspaceManager.cs` ~511) and
   will re-trip on the next touching write; documented in step 9, behaviour unchanged.
3. **Return type is a string, not a record.** Matches existing admin tools and keeps one return type across the
   operations; a record would be friendlier to machine parsing but needs a check of how records serialize
   through `[Produces(DataTag.ResultOnly)]`. Revisit only if journals show agents mis-parsing the text.
4. **Stamping changes the shape of plain-text refusals** while a breaker is tripped (wrapped into a JSON object with
   `message`). The Echo filter already does the same wrap, so clients that parse bodies are already exposed.
   For an orientation refusal the breaker text now appears twice (in `message` and as `sessionHaltRecovery`);
   accepted for simplicity.
5. **Token cost** is ~400 characters per response while a breaker is tripped; none otherwise.
6. **Only one breaker is stamped at a time** (precedence unrecoverable, externalDrift, orientation, mutation). A second
   tripped breaker becomes visible when the first clears. `McpServerStatus` still lists all of them.
7. **`ToolCollection` lookup relies on `IOptions<McpServerOptions>` being resolvable from `context.Server.Services`.**
   Traced: `ToolsetService` receives exactly that through DI (`ServiceRegistrationExtensionsBasic.cs:197-198`). Not run
   end to end; step 7 test (f) and the live check cover it. If it were unavailable the stamp degrades to "stop and report".
8. **Untraced:** the `ok:false` journal entry for `ListExternalDiskChanges` (no failing branch in source; likely
   stale binary or retracted); the exact line numbers in `TODO.md` and the generated-doc row counts, which were
   taken from the earlier survey and must be re-found by search in steps 10 and 12.
9. **Live check 3 in step 13 trips the real latch** in the verifying session; use a throwaway scratch file and
   acknowledge afterwards, or the session cannot write.
