# Plan: one ExternalFileDrift tool, reworded halt message, and halt state stamped onto responses

**Status:** DRAFT 2026-10-09. Implements owner decisions 1 and 2 of `docs/current/proposals/proposal_journal_digest_followup_decisions.md`; nothing built yet.

## Problem

When a confirmed external-drift hit trips the session-halt latch, the agent gets this text from the
write chokepoint (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1281` and `:1337`, identical
strings): "Session halted: external file drift was detected on a tracked file. This session cannot safely
continue. Stop and report to the user/operator."

That text is stale and misleading on four counts:

1. It says the session cannot continue, but recovery exists (`AcknowledgeExternalFileChanges` clears the latch).
2. It names no recovery call, so an agent that does not already know the three admin tools stops.
3. It does not say that read-only tools still work.
4. Discovering recovery takes three differently named tools (`ListExternalDiskChanges`, `IsSessionHalted`,
   `AcknowledgeExternalFileChanges`, all in `RoslynSentinel.Tools.Basic/AdminTools.cs`), and a halted
   response elsewhere (an unrelated tool, `Git`, a breaker refusal) carries no pointer to them.

Further defects found while tracing:

- A partial acknowledge never clears the latch while any entry remains flagged
  (`AdminTools.cs` around line 117, comment "DEFERRED DECISION ... plan_session_halt_recovery_and_git_gaps.md,
  Risks 1"). The owner has now decided the policy (decision 2), so the comment and the "Session latch still set"
  message are obsolete.
- `AdminTools.cs` lines 6-14 (header comment) still describe a retired "Admin mode" gate.
- `SessionHaltedException`'s doc comment in `RoslynSentinel.Common/ToolException.cs` calls the latch "terminal
  and non-actionable"; that is no longer true for the drift latch.
- `DriftMessages.BuildHint` (`RoslynSentinel.Common/DriftMessages.cs:49`) tells the agent to run
  `ListExternalDiskChanges`, a name this plan removes.

Journal evidence (journal-digest entries, session:line): `a08be84f:L53`, `a08be84f:L48`,
`47b2c93d:L16`, `96b0b939:L17`, `0017ac93:L6`, `50e0e6ac:L29`. Per CLAUDE.md these are impressions; the
claims above were each traced to the cited source lines. The `ok:false` result recorded for
`ListExternalDiskChanges` in one journal entry was not traced (the method at `AdminTools.cs:29` has no failing
branch in source); it may be a stale-binary artefact.

## Decision

1. **One tool.** `ExternalFileDrift(operation: Status | List | Acknowledge, files?)`, hosted by the existing
   `AdminTools` class (so no new class, no `ToolClassRegistry` mode entry, no DI block). The three old tools
   are deleted outright (no aliases).
   - `Status`: reports whether the drift latch is set and how many external changes are tracked.
   - `List`: full list of tracked external changes.
   - `Acknowledge`: with `files` clears the named entries, without `files` clears all. In both cases it **clears
     the session-halt latch** (decision 2). Entries not named stay flagged, so the next write that touches one
     re-trips the halt. The reply says which files remain flagged and that they re-trip the halt.
   - Returns a `string` (as `AcknowledgeExternalFileChanges` and `McpServerControl` do today), so the three
     operations share one return type.
   - Carries `[UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]` (the golden set stays at 8 names:
     `ExternalFileDrift` replaces `IsSessionHalted`).
   - Enum values are PascalCase, matching `McpServerControlOperation`; decision 4 (case repair) will make
     lowercase `status | list | acknowledge` accepted too.
2. **Reworded halt message** (single constant, `DriftMessages.DriftHaltMessage`, used at both throw sites):
   > Session halted: a write was refused because a file changed on disk outside this server (external file
   > drift). Read-only tools (ReadFile, GetFileOutline, Search, GetMethodSource, Git status/log/diff) still work.
   > To resume writing: call ExternalFileDrift(operation: List) to see the files, review them, then
   > ExternalFileDrift(operation: Acknowledge). Files you do not acknowledge stay flagged and halt the
   > session again if a write touches them.
3. **Halt stamping filter.** A new request filter stamps four top-level properties into the first text block of
   every tool response while the session is halted, so recovery needs no discovery call:
   ```json
   { "isSessionHalted": true,
     "sessionHaltKind": "externalDrift",
     "sessionHaltReason": "<one sentence, same wording source as the halt message>",
     "sessionHaltRecovery": "<one sentence naming the exact call>", ...original body... }
   ```
   - Source of truth: `HaltInfo.From(IWorkspaceHealthReporter, IUnrecoverableBreaker)` in `Common` (new). Kinds
     in v1: `unrecoverable` (takes precedence) and `externalDrift`. The recovery text for `externalDrift` is
     `ExternalFileDrift(operation: List)` then `ExternalFileDrift(operation: Acknowledge)`; for `unrecoverable` it
     says there is no reset and to stop and report, with read-only tools still available.
   - Position: registered **second-to-first, right after `AddArgumentValidationFilter`, before
     `AddToolErrorFilter`** (`ServiceRegistrationExtensionsBasic.cs:100-110`). It is therefore outer to the
     ToolError, drift, offload and both breaker filters (it sees breaker refusals, which are plain text, and
     post-offload stubs) and inner to the Echo filter (Echo still stamps last and sees the final body).
     Advanced reuses this chain (`ServiceRegistrationExtensionsAdvanced.cs:172` calls
     `AddRoslynSentinelToolsBasic`), so one registration serves both servers.
   - Skipped for `ExternalFileDrift` itself (its own reply carries the state) and for any body that already
     contains an `isSessionHalted` key.
   - Mechanics follow `ToolCallEcho.Stamp` (`RoslynSentinel.Server.Basic/ToolCallEcho.cs`): operate on the first
     text block; if it parses as a JSON object insert the four properties at index 0; otherwise wrap as
     `{"isSessionHalted":...,"message":"<original text>"}`; use `SharedJsonOptions.Compact`; any failure is
     swallowed (a diagnostic aid never breaks a call). Never stamp when no halt is active (zero cost).
   - Cost: roughly 400 characters per response while halted, nothing otherwise.

## Execution rules

- Every step is one compile-green slice for a Haiku-tier implementer: at most 3 files, one acceptance check,
  edit nothing outside the named symbols; if a test seems to need another change, reply `RESCOPE:`.
- Use `ReplaceSnippet` with `batchEdits` (definitions before call sites) wherever a step lists more than one
  edit; never pass `filePath` together with `batchEdits`. No `Edit`/`Write` on `.cs`.
- ASCII-only punctuation in everything written.
- Tests are NUnit 5: `Assert.ThrowsAsync`/`CatchAsync` must be `await`ed.
- Step 4 leaves three Server test fixtures red at runtime (still compiling); step 5 fixes them. Do not run the
  Server fixtures between those two steps.
- Doc steps (7-10) edit Markdown/JSON with the normal file tools; no Build needed, the check is a grep for the
  old names returning nothing in the named files.

## Steps

### Step 1 - Common: HaltInfo, halt text constants, BuildHint rename
- Files: `RoslynSentinel.Common/HaltInfo.cs` (new), `RoslynSentinel.Common/DriftMessages.cs`,
  `RoslynSentinel.Tests/DriftMessagesTests.cs`
- Change:
  - `HaltInfo.cs`: `public sealed record HaltInfo(string Kind, string Reason, string Recovery)` with
    `public static HaltInfo? From(IWorkspaceHealthReporter health, IUnrecoverableBreaker unrecoverable)`:
    if `unrecoverable.IsTripped()` return `Kind "unrecoverable"`, `Reason = unrecoverable.StateMessage() ?? <fallback>`,
    `Recovery` = no reset exists, stop and report to the operator, read-only tools still work; else if
    `health.IsSessionHalted()` return `Kind "externalDrift"`, `Reason = DriftMessages.DriftHaltReason`,
    `Recovery = DriftMessages.DriftHaltRecovery`; else `null`.
  - `DriftMessages.cs`: add `public const string DriftHaltReason` (first two sentences of the Decision-2
    message), `public const string DriftHaltRecovery` (the "To resume writing ..." sentences), and
    `public const string DriftHaltMessage = DriftHaltReason + " " + DriftHaltRecovery`. Change the text at line
    49 in `BuildHint` from `ListExternalDiskChanges` to `ExternalFileDrift(operation: List)`.
  - `DriftMessagesTests.cs` (test at line 157, `BuildHint_NonEmpty_ContainsListExternalDiskChanges`, assertion at
    161): rename to `BuildHint_NonEmpty_NamesExternalFileDrift` and assert `Does.Contain("ExternalFileDrift")`. Add
    `DriftHaltMessage_NamesRecoveryCallAndReadOnlyTools` asserting it contains `ExternalFileDrift`, `Acknowledge`,
    and `Read-only`.
  - Apply as one `ReplaceSnippet` batch for the two existing files; create `HaltInfo.cs` with `CreateFile`
    first (definition before use).
- Call sites: `BuildHint` callers are untouched (string only changes). No signature changes.
- Done when: `RunTest` on `DriftMessagesTests` passes (and `Build` has 0 errors).

### Step 2 - Manager: use the reworded message at both throw sites
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
- Call sites: none (string constants only).
- Done when: `RunTest` on `CreateFileDeleteFileTests.DeleteFile_DriftedFile_RefusesDeleteAsync` passes.

### Step 3 - AdminTools: add ExternalFileDrift, remove the three old tools, update tests
- Files: `RoslynSentinel.Tools.Basic/AdminTools.cs`, `RoslynSentinel.Tests.Tools.Basic/AdminToolsTests.cs`
- Change (one `ReplaceSnippet` with `batchEdits`; definitions first, removals after, tests last):
  1. In `AdminTools`, add nested `public enum ExternalFileDriftOperation { Status, List, Acknowledge }` beside
     `McpServerControlOperation`.
  2. Add `public static string AcknowledgeDrift(IWorkspaceHealthReporter health, string? files)` containing the
     logic currently inlined in `AcknowledgeExternalFileChanges` (cases A and B), changed so that: the latch is
     cleared whenever at least one entry was cleared or `files` is empty (i.e. every successful acknowledge);
     an unmatched or empty selection still returns "Nothing cleared ..." and does NOT clear the latch; messages
     say `ExternalFileDrift(operation: List)` where they currently say "Call ListExternalDiskChanges"; the
     partial-success message ends "Session-halt latch cleared. N file(s) remain flagged: <summary>. A write
     that touches one of them will halt the session again; acknowledge them too once reviewed."; delete the
     `DEFERRED DECISION` comment and the "Session latch still set while other changes remain flagged" branch.
     Static + interface parameter so tests can drive it with a stub (the manager cannot be seeded with drift
     without a real file watcher round trip).
  3. Add instance method `ExternalFileDrift(ToolCallReason reason, ExternalFileDriftOperation operation, string? files = null, CancellationToken cancellationToken = default)`:
     `[UnrecoverableBreaker(UnrecoverableBreakerAccess.Allowed)]`, `[McpServerTool(Name = "ExternalFileDrift")]`,
     `[Produces(DataTag.ResultOnly)]`, `[Description]` stating: what drift is; the three operations; that
     Acknowledge clears the halt latch and that files not named stay flagged and re-trip the halt on the next
     write touching them; that `files` is only used by Acknowledge (CSV or JSON array; omit for all); that
     read-only tools work while halted. `Status` returns `"SessionHalted=<bool>; tracked external changes: <n> (<summary>)."`
     (breaker states stay in `McpServerStatus`); `List` returns the full-path list or "No tracked external
     file changes."; `Acknowledge` calls `AcknowledgeDrift(_workspaceManager, files)`. Do not accept `files`
     for Status/List silently: if `files` is non-empty with those operations, return
     `"files is only used with operation: Acknowledge; nothing changed."`.
  4. Delete methods `ListExternalDiskChanges` (lines 26-35), `IsSessionHalted` (37-47) and
     `AcknowledgeExternalFileChanges` (49-138) and their attributes.
  5. Rewrite the header comment (lines 6-14) to: "Operator-adjacent tools: ExternalFileDrift (status/list/acknowledge
     external-change state and the session-halt latch) and McpServerControl (process lifecycle). Always visible
     in claude-lean (ToolClassRegistry.ClaudeLeanToolNames)."
  6. `AdminToolsTests.cs`: replace the five old tests at lines 30-62 with: `ExternalFileDrift_Status_ReportsNotHaltedOnFreshManager`,
     `ExternalFileDrift_List_OnFreshManager_SaysNoChanges`, `ExternalFileDrift_FilesWithStatus_IsRefused`,
     `Acknowledge_UnknownFile_ReturnsNothingClearedNamingFile` (port of line 43-48),
     `Acknowledge_MalformedJsonArray_ReturnsParserError` (port of 57-63), and, against a private nested
     `StubHealth : IWorkspaceHealthReporter` (three drift paths `A.cs`, `B.cs`, `C.cs`; records
     `ClearSessionHalt` calls; other members `throw new NotImplementedException()`):
     `Acknowledge_Partial_ClearsLatchAndNamesRemainingFiles` (clears A, asserts latch cleared once, text contains
     `B.cs`, `C.cs` and "halt the session again") and `Acknowledge_AllWithoutFiles_ClearsLatch`.
- Call sites (measured with `FindReferences`): `ListExternalDiskChanges` 1 (`AdminToolsTests.cs:32`),
  `AcknowledgeExternalFileChanges` 4 (`AdminToolsTests.cs:39,45,53,60`), tool-method `IsSessionHalted` 0. Manager
  `IsSessionHalted()` stays (still called by `ServerStatusTools.cs:88`). Name-only (string/comment) mentions
  are handled in steps 4-10.
- Done when: `RunTest` on `AdminToolsTests` passes (and `Build` has 0 errors).

### Step 4 - Registrations: claude-lean allow-list, toolset catalog, toolset description
- Files: `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`, `RoslynSentinel.Common/ToolsetCatalog.cs`,
  `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs`
- Change (one `ReplaceSnippet` batch):
  - `ToolClassRegistry.cs:37` (`ClaudeLeanToolNames`): replace `"AcknowledgeExternalFileChanges"` and
    `"ListExternalDiskChanges"` with the single entry `"ExternalFileDrift"`. The Core set goes from 26 to 25
    names. No change to the `AdminTools` class entries (lines 51, 67, 77, 120, 136): the class and DI block
    are unchanged.
  - `ToolsetCatalog.cs:52` (`projectAdmin`): remove `"IsSessionHalted"`. (`ExternalFileDrift` is Core, not
    on-demand, so it does not belong in `ToolsBySet`.)
  - `ToolsetControlTools.cs:27`: remove `IsSessionHalted` from the description text listing the `projectAdmin` tools.
- Call sites: string literals only; no symbol references. Known consumers that will fail at runtime until
  step 5: `ClaudeLeanModeTests`, `McpToolsetControlTests`, `UnrecoverableBreakerPolicyTests`.
- Done when: `Build` 0 errors.

### Step 5 - Server tests: allow-list, toolset and golden set
- Files: `RoslynSentinel.Tests.Server/ClaudeLeanModeTests.cs`, `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs`,
  `RoslynSentinel.Tests.Server/UnrecoverableBreakerPolicyTests.cs`
- Change (one `ReplaceSnippet` batch):
  - `ClaudeLeanModeTests.cs`: in `CoreTools` (line 33) replace the two old names with `"ExternalFileDrift"`; in
    `ClaudeAdvancedBaseline` remove `"AcknowledgeExternalFileChanges"` (line 40), `"IsSessionHalted"` and
    `"ListExternalDiskChanges"` (line 46) and add `"ExternalFileDrift"` in alphabetical position; the
    `Length 26` assertion (lines 109-113) becomes 25.
  - `McpToolsetControlTests.cs`: line 416 delete `Assert.That(during, Does.Contain("IsSessionHalted"));`
    (the next line, `GetWorkspaceHealth`, keeps the test's meaning); lines 474-478
    (`EnabledTool_IsCallable_WithoutRelisting`): delete the `IsSessionHalted` call and its two-line comment, keep
    the `GetWorkspaceHealth` call; line 493 (`DisabledTool_IsNotCallable`): call `GetWorkspaceHealth` instead
    of `IsSessionHalted`; line 539 `Has.Count.EqualTo(26)` becomes 25.
  - `UnrecoverableBreakerPolicyTests.cs`: line 39 `"IsSessionHalted"` becomes `"ExternalFileDrift"`; line 76
    `[TestCase("IsSessionHalted")]` becomes `[TestCase("ExternalFileDrift")]`. The set size stays 8.
- Call sites: none beyond the named lines.
- Done when: `RunTest` filtered to the three fixtures passes: `ClaudeLeanModeTests`, `McpToolsetControlTests`,
  `UnrecoverableBreakerPolicyTests`.

### Step 6 - Halt-stamping filter
- Files: `RoslynSentinel.Server.Basic/HaltStamp.cs` (new), `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`,
  `RoslynSentinel.Tests.Server/HaltStampFilterTests.cs` (new)
- Change:
  - `HaltStamp.cs`: `internal static class HaltStamp` with `public static void Stamp(CallToolResult result, HaltInfo info)`,
    modelled on `ToolCallEcho.Stamp` (first text block; JSON-object body -> insert `isSessionHalted:true`,
    `sessionHaltKind`, `sessionHaltReason`, `sessionHaltRecovery` at index 0; any other text -> wrap
    `{"isSessionHalted":true,...,"message":"<text>"}`; skip when the body already has `isSessionHalted`;
    `SharedJsonOptions.Compact`). No-op when `result.Content` has no text block.
  - `ServiceRegistrationExtensionsBasic.cs`: add `private static void AddHaltStampFilter(IMcpRequestFilterBuilder filters)`
    next to `AddUnrecoverableBreakerFilter` (~line 859). Body: `AddCallToolFilter`; call `next` first; if
    `context.Params?.Name == "ExternalFileDrift"` return the result untouched; resolve
    `context.Server.Services?.GetService<PersistentWorkspaceManager>()` exactly as the unrecoverable filter does
    (line ~885), call `HaltInfo.From(manager, manager)` (the manager implements both interfaces; cast through
    `IWorkspaceHealthReporter` and `IUnrecoverableBreaker` explicitly because of the explicit-interface breaker
    members, see `PersistentWorkspaceManager.cs:1995-2001`), and `HaltStamp.Stamp` in a try/catch that only
    `Debug.WriteLine`s. Register it in `AddRoslynSentinelToolsBasic` between `AddArgumentValidationFilter(filters);`
    and `AddToolErrorFilter(filters);` (lines 100-103). Definition (`AddHaltStampFilter`) is added before the
    registration line in the same batch.
  - `HaltStampFilterTests.cs`: model on `RoslynSentinel.Tests.Server/ToolCallEchoFilterTests.cs` (in-process
    host). Tests: (a) no halt -> response body has no `isSessionHalted`; (b) trip the `IUnrecoverableBreaker`
    (resolve from the host's `PersistentWorkspaceManager`, call `Trip("T", "id", "diag")`) -> a read-only allowed
    call (`GetWorkspaceHealth`) is stamped with `sessionHaltKind == "unrecoverable"`; (c) a refused call (a
    non-allowed tool) has a stamped wrapper whose `message` holds the original refusal text; (d) a call to
    `ExternalFileDrift` is not stamped. The `externalDrift` kind is covered by `HaltInfo` unit logic in step 1
    plus the live check in the final step (real drift cannot be seeded cheaply); add a `HaltInfo.From` test
    with two small stubs if the test file stays under ~150 lines.
- Call sites: new private methods only; one registration line added. No existing symbol changes.
- Done when: `RunTest` on `HaltStampFilterTests` passes (and `Build` has 0 errors).

### Step 7 - Docs/config: allow-list and agent docs
- Files: `.claude/settings.json`, `CLAUDE.md`, `.claude/agents/orchestrator.md`
- Change:
  - `.claude/settings.json` lines 158, 181, 183: remove the entries `...AcknowledgeExternalFileChanges`,
    `...IsSessionHalted`, `...ListExternalDiskChanges`; add one `"mcp__root_roslyn_sentinel_advanced_stdio__ExternalFileDrift"`
    (alphabetical position, after `DeleteFile`). Keep valid JSON.
  - `CLAUDE.md:178`: replace the sentence naming `ListExternalDiskChanges()` then `AcknowledgeExternalFileChanges()`
    with `ExternalFileDrift(operation: List)` then `ExternalFileDrift(operation: Acknowledge)`.
  - `.claude/agents/orchestrator.md:120`: replace `ListExternalDiskChanges` in the read-only tool list with `ExternalFileDrift`.
- Call sites: none (text). Hooks and `.ps1` scripts were searched and contain none of the three names.
- Done when: a text search for the three old names over these three files returns no match.

### Step 8 - Docs: architecture map, write-path reference, superseded idea
- Files: `docs/current/references/reference_architecture_map.md`, `docs/current/reference-code-file-write-paths-v1.md`,
  `docs/current/ideas/external-drift-hard-blocker.md`
- Change:
  - `reference_architecture_map.md`: (a) in "Request pipeline" insert the halt-stamp filter after
    `AddArgumentValidationFilter` and renumber, describing the four stamped properties, its skip rule for
    `ExternalFileDrift`, and "non-JSON refusal text is wrapped into `{..., message}`"; (b) line 87 (allow-list
    sentence): `IsSessionHalted` becomes `ExternalFileDrift`; (c) in "Write chokepoint" add a short
    "Session halt and acknowledge" paragraph: the drift latch is `_sessionHalted`; `ExternalFileDrift(operation:
    Acknowledge)` clears the latch even when only some files are named; files not named stay in
    `_externalChanges` and re-trip the halt on the next write that touches them; `LoadSolution(forceReload)` does
    not drain `_externalChanges` (comment at `PersistentWorkspaceManager.cs` ~511); the unrecoverable halt has no
    reset; (d) update the "Status" line date.
  - `reference-code-file-write-paths-v1.md:29`: replace the old tool names with `ExternalFileDrift`.
  - `external-drift-hard-blocker.md`: add a top banner "Superseded 2026-10-09 by plan_external_file_drift_tool_and_halt_stamping.md;
    the Admin-mode gating described here was retired and the recovery tools merged into ExternalFileDrift".
- Done when: a text search for `ListExternalDiskChanges|AcknowledgeExternalFileChanges|IsSessionHalted` over
  `reference_architecture_map.md` and `reference-code-file-write-paths-v1.md` returns no match.

### Step 9 - Docs: TODO and sibling plans
- Files: `docs/current/TODO.md`, `docs/current/plans/plan_session_halt_recovery_and_git_gaps.md`,
  `docs/current/plans/plan_scoped_operation_ledger.md`
- Change:
  - `TODO.md` (~line 458 and ~598-624): replace old tool names with `ExternalFileDrift`; replace the open item
    "halt wording / latch policy" with a one-line follow-up "Revisit partial-acknowledge policy after real use
    (decision 2, proposal_journal_digest_followup_decisions.md)". Resolved entries move to `CLOSED.md` per
    the TODO rule; do not delete.
  - `plan_session_halt_recovery_and_git_gaps.md`: in the Status line and Risks 1, note that the latch policy was
    decided 2026-10-09 and implemented by this plan; its deferred Step 2 (halt wording) is covered by step 2 here.
  - `plan_scoped_operation_ledger.md` lines 49, 103, 105: replace old tool names with `ExternalFileDrift`.
- Done when: a text search for the three old names over these three files returns only explicitly historical
  mentions (for example "formerly ...").

### Step 10 - Docs: schema-cost proposal
- Files: `docs/current/proposals/proposal_reduce_tool_schema_token_cost.md`
- Change: lines 87-96 list `ListExternalDiskChanges`, `AcknowledgeExternalFileChanges` and `IsSessionHalted`
  as separate schema-cost items; replace with the single `ExternalFileDrift` and adjust any total in that
  paragraph (3 tools become 1).
- Done when: a text search for the three old names over the file returns no match.

### Step 11 - Regenerate architecture docs
- Files: `docs/generated/architecture_tools.md` (generated; lines 66-68 list the old tools) and any other file
  the script rewrites.
- Change: run `scripts/Generate-ArchitectureMap.ps1` (sets `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1`). Do not
  hand-edit generated files. Expected diff: three rows replaced by one `ExternalFileDrift` row; claude-lean
  tool counts decrease by 2 (3 tools removed, 1 added); the unrecoverable-allowed list shows `ExternalFileDrift`.
- Done when: `RunTest` on `ArchitectureDocFreshnessTests` passes.

### Step 12 - Final verification
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
     `ExternalFileDrift(operation: List)` and `(operation: Acknowledge, files: <one of two drifted files>)`
     clears the latch while naming the remaining file; a write touching the remaining file halts again.
- Done when: all four items above hold.

## Out of scope

- Stamping the orientation (automatic) breaker or the mutation (manual) breaker. The orientation breaker is a
  soft pacing guard with its own message; the recovery path for the mutation breaker was not traced (see Risks).
- Auto-clearing the drift latch on `LoadSolution(forceReload)` when content hashes match disk.
- Per-file or per-project halt granularity (the latch stays session-wide).
- Case-insensitive enum repair in argument validation (owner decision 4, separate plan).
- Any change to `McpServerControl` or `McpServerStatus` (no folding into either; rejected in the proposal).
- The `Read`-block hook (decision 3), scripts and retention items (other plans).
- Renaming the `AdminTools` class or moving it.

## Risks and open decisions

1. **Mutation and orientation breakers are not stamped (needs owner call).** The brief says "any breaker", but
   stamping only helps if the stamp can name a recovery call. For drift the recovery is `ExternalFileDrift`; for
   the unrecoverable breaker it is "stop and report"; for the mutation breaker (`IManualCircuitBreaker`) I did
   not trace a model-reachable reset, so I would be inventing guidance. `HaltInfo.From` is the single extension
   point once the mutation-breaker recovery is confirmed.
2. **Acknowledge during an unrecoverable halt.** The attribute is per method, so allowing `ExternalFileDrift`
   (needed for `Status`) also allows `Acknowledge` during an unrecoverable halt. It clears only drift state; the
   write chokepoint still refuses every write on the unrecoverable breaker (`PersistentWorkspaceManager.cs:1284-1295`),
   so no harm results. If the owner wants Status-only during that halt, the tool needs a runtime check, which
   requires `AdminTools` to take `IUnrecoverableBreaker` (constructor change; DI already resolves it).
3. **Acknowledge with no `files` clears everything with no confirmation.** Same as today. Decision 2 does not
   change it. A confirm token (like `McpServerStopConfirmation`) is possible; tradeoff: one more round trip for
   an agent that has just reviewed the list.
4. **Un-acknowledged entries survive `LoadSolution(forceReload)`** (`PersistentWorkspaceManager.cs` ~511) and
   will re-trip on the next touching write; documented in step 8, behaviour unchanged.
5. **Return type is a string, not a record.** Matches existing admin tools and keeps one return type across the
   operations; a record would be friendlier to machine parsing but needs a check of how records serialize
   through `[Produces(DataTag.ResultOnly)]`. Revisit only if journals show agents mis-parsing the text.
6. **Enum casing.** PascalCase now; the proposal text writes lowercase. Decision 4 makes lowercase work.
7. **Stamping changes the shape of plain-text refusals** while halted (wrapped into a JSON object with
   `message`). The Echo filter already does the same wrap, so clients that parse bodies are already exposed.
8. **Token cost** is ~400 characters per response while halted; none otherwise.
9. **Untraced:** the `ok:false` journal entry for `ListExternalDiskChanges` (no failing branch in source; likely
   stale binary or retracted); the exact line numbers in `TODO.md` and the generated-doc row counts, which were
   taken from the earlier survey and must be re-found by search in steps 9 and 11.
10. **Live check 3 in step 12 trips the real latch** in the verifying session; use a throwaway scratch file and
    acknowledge afterwards, or the session cannot write.
