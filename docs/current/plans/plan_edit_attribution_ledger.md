# Plan: Edit attribution ledger naming the agent behind each write, with a same-file conflict refusal and a fixed per-process operating mode

**Status:** APPROVED 2026-11-05. Design rulings from the owner are recorded under Decision; Steps 0-11 done (Step 0 results under that step); the one open item is the Step 9 hook helper republish, an owner action listed in that step, after which this plan closes; the hook is installed and live: writes are recorded, the filter sets identity, the chokepoint refuses same-file conflicts between two known agents and `UndoLastApply` refuses an undo over another agent's later write. Step 8 live check: a restarted server latched `MultiAgent` ("auto: first call carried an agent id") with `CallerAgentId: main`, and two concurrent subagents each reported their own distinct id; the Step 4 refusal naming a subagent was not reproduced live (it needs a write landing inside another agent's in-flight call) and is covered by tests only. A missing hook exe fails open (checked with `claude -p` in a scratch folder, both command forms). Citations checked at commit b3b90a26; the request pipeline, `AsyncLocal` and telemetry points were rechecked at bab9599b, which added per-tool telemetry. Between those commits `PersistentWorkspaceManager.cs`, `ToolArgumentValidator.cs`, `ValidateAndApplyHelper.cs` and `AdminTools.cs` did not change, so their line citations still hold.

## Problem

Several Claude subagents in one session share one RoslynSentinel server instance. The server cannot
tell them apart, and its write path assumes a single actor:

- **Silent lost updates.** `ApplyProposedChangesAsync` takes full file text
  (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:1703`), computed by the engine from an earlier
  `CurrentSolution` snapshot. `_solutionLock` is taken only for the write (`:1819`), and nothing checks
  that the snapshot is still current. If agent A reads a file, agent B writes it and then A writes, B's
  edit is overwritten with no error. The drift check (`:1795`) covers only out-of-process writes.
- **Two green edits can make a red tree.** Validation runs outside the write lock (`:1807`). A can remove
  a method while B adds a call to it in another file, and both pass.
- **Errors without provenance.** The compile gate reports only errors an edit introduces, so A's existing
  errors neither block B nor reach B through it. B meets them through `Build` or `GetDiagnostics`, with no
  cause attached. `git status` lists dirty files with no writer or reason.
- **The scoped operation ledger assumes one actor.** It was built for multi-step edits, not parallel
  agents. There is one instance per `PersistentWorkspaceManager` (`:288`), with one open slot and no caller
  identity. An open one refuses B's unrelated writes (`:1765`, `ScopedLedgerBlocked`) without saying who
  opened it, and the refusal offers B `LedgerRelease`, which calls `ForceRelease()` with no ownership check
  (`RoslynSentinel.Tools.Basic/AdminTools.cs`, `LedgerReleaseResult`).
- **Undo is server-wide.** `UndoLastApply` restores `BeforeSource` from a blob selected by `changeId`, with
  no check of later writes (`RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs:73-177`). It can overwrite
  a sibling's later edit.

External drift is out of scope. Agents in one instance share `_knownFileHashes`, so they do not trip the
watcher on each other, and the dog-fooding hook blocks out-of-band `.cs` edits. The remaining sources are
sibling server instances and editor writes, covered by
[proposal_cross_session_write_journal.md](../proposals/proposal_cross_session_write_journal.md). This plan
is that proposal's in-process first phase and shares its `ToolCallContext` plumbing.

Naming: always say which ledger. The **scoped operation ledger** is the existing `IScopedOperationLedger`
for multi-step edits. The **edit attribution ledger** is the new one here. The existing
`LedgerList`/`LedgerFix`/`LedgerRelease` operations and `ScopedLedgerBlocked` all mean the scoped operation
ledger.

## Decision

1. **Identity comes from a Claude Code hook, not from the model.** Per
   https://code.claude.com/docs/en/hooks, PreToolUse input carries `agent_id` and `agent_type` inside a
   subagent, and `hookSpecificOutput.updatedInput` replaces the whole tool input. A PreToolUse hook on
   RoslynSentinel MCP tools adds `_agentId` (`main` when `agent_id` is absent) and `_agentType`.
2. **A new outermost request filter strips them** from `context.Params.Arguments` before
   `ToolArgumentValidator` would refuse them as unknown
   (`RoslynSentinel.Server.Advanced/ToolArgumentValidator.cs:1235-1243`), and sets an ambient
   `AsyncLocal<ToolCallContext?>`. This is the second `AsyncLocal` in the codebase. Follow the first,
   `RoslynSentinel.Common/ToolGuidanceCollector.cs`: the slot holds a mutable, thread-safe object, so a value set
   deep in a call (`GatePassed`, Step 10) is visible to later code in that call, because the reference flows and no
   slot assignment has to flow back up.
   - **Placement: first, ahead of `ToolCallEcho`, making 13 filters.** The echo snapshots arguments before calling
     `next`, so only a filter outside it keeps `_agentId` out of the echoed `toolCall.arguments`. An `AsyncLocal` set
     by an inner filter is not visible to an outer filter's code after `next` returns, so being outermost also lets
     `ToolTelemetry` and the echo read the context.
   - **This filter must never throw.** It sits outside the echo stamp and the telemetry count, so a failure here
     would surface unstamped and uncounted. Catch, log and call `next` with a null context.
3. **Operating mode is fixed for the process (owner ruling 2026-11-05).** A mode that can change mid-session
   is the risk to avoid, so there is no toggle tool.
   - Startup arg `--operating-mode=auto|single-agent|multi-agent`, falling back to env var
     `ROSLYNSENTINEL_OPERATING_MODE`. Default `auto`. Parsing follows
     `RoslynSentinel.Common/EolPolicyOptions.cs` and never throws; an unrecognised value means `auto`.
   - `auto` resolves once, on the first tool call of the process, and latches: the call carries `_agentId`
     means `MultiAgent`, otherwise `SingleAgent`. It is never re-evaluated, so a later call cannot change the
     mode. With the hook installed the first call already carries an id; hookless clients (weak or
     self-hosted models, PlanStepRunner, model-eval) resolve to `SingleAgent` and behave as they do today.
   - `McpServerStatus` reports the effective mode and how it was chosen, so the main session can check it.
     Because that call also passes through the filter, status never shows an unresolved mode.
4. **What the mode changes:**

   | Behaviour | `SingleAgent` | `MultiAgent` |
   | --- | --- | --- |
   | `MoveMember` passes the compile gate with unresolved call sites | Applies and opens a scoped operation ledger (today) | Refused before anything is written, with `UnresolvedCallSites` and the `callSiteFixups` retry |
   | Scoped operation ledger can open | Yes | No (`TryOpen` also rejects, so a future opener cannot bypass the rule) |
   | Different known agent writes while a scoped operation ledger is open | Refused for that agent only | Not applicable |
   | Same-file conflict refusal (Decision 6) | Active when both ids are known | Active when both ids are known |

   The second-agent case is a refusal, not `_sessionHalted`: a latch would also stop the first agent while it
   is fixing the problem. A "build is red" trigger is dropped, because the server has no such state and the
   gate only compares errors per edit.
5. **An in-memory `EditAttributionLedger` per server.** Each `EditAttribution` holds `Seq`, `AgentId`,
   `AgentType`, `Tool`, `Reason` (capped to 200 chars, ASCII-sanitised), `UtcTime`, `Op`
   (write|create|delete) and `HashAfter`. Keyed by path, last 8 per file, global monotonic `Seq`. Appended
   inside `_solutionLock` from the post-rollback succeeded and deleted lists, minus no-ops: `succeeded` includes
   files whose content did not change, and recording those would make a later writer conflict with a write that never
   happened. Not persisted. Any failure
   degrades to a logged warning and never blocks a write.
6. **Conflict refusal at the chokepoint, inside the lock.** If a target's latest entry has
   `Seq > ctx.StartSeq` and came from a different known agent, refuse the whole batch. Refuse only when both
   ids are non-null and differ, so unknown or same-agent writes keep today's behaviour.
   - Set `RefusalCode: ToolErrorCode.ConcurrentEditRefused`; without one `Code` reads `Exception`
     (`RoslynSentinel.Common/ApplyChangesResult.cs:41`).
   - Name the writer (agent id and type, tool, UTC time, reason) in `Summary` as well as `FailedFiles`, since
     `ValidateAndApplyHelper` builds its error from `RefusalCode` and `Summary`
     (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:133-134`).
   - State the next step: re-read and retry; if the change conflicts with your slice, stop and report
     `MULTI-AGENT:` to your dispatcher. Build any named call with `ToolCallHint.Call`.
7. **Recovery goes through the dispatcher, not peer-to-peer messaging.** The error names the agent; the main
   session, which owns slicing and can `SendMessage` either agent, resolves it.
8. **Reporting uses the existing `Findings` list and adds no input schema.** The claude-lean schema measured 28305
   of 28500 at W13 (`docs/current/decisions_autonomous_run_2026-11-05.md`), so there is no new tool or operation.
   Re-measure with `ClaudeLeanSchemaBudgetTests` before Step 6; other approved work may spend the same headroom.

**DTO notes** (per the `dto-conventions` skill; repeat these in the briefs and tool-call `reason`s):

```
NEW-DTO: EditAttribution (Item, Common.Workspace)
Evaluated: OperationItemRecord - blob item holding full source, no agent or sequence; LedgerEntryBase - scoped operation ledger call-site tracking, no writer or hash
Wire impact: none
```

`ToolCallContext` is an ambient per-call holder, not a DTO (`DTO-EXEMPT`). `McpServerStatusResult` is extended
in place. Findings reuse `Finding`.

## Execution rules

- Implementation is dispatched (load `dispatch-implementer` first). The main session reviews, builds and
  commits. Each step builds green on its own.
- `Build(level: fullBuild)` after each step, then `RunTest` on the affected test projects. Report only new
  failures against the baselines. One build or test at a time.
- Steps 3, 4 and 10 change the request pipeline or the chokepoint: read
  `docs/current/references/reference_architecture_map.md` first and restart the instance before live-testing.
- Test chokepoint refusals on the real `PersistentWorkspaceManager`. `FakeWorkspaceManager` mirrors the scoped
  operation ledger refusal by hand, so a test through the fake proves nothing unless the fake is updated too.
- Commits carry `Plan: plan_edit_attribution_ledger`; the last one carries `Closes:`.

## Steps

### Step 0 - Spike: confirm the hook payload

- Files: a temporary log-only PreToolUse hook under `.claude/hooks/` (removed after the spike).
- Change: log the input for one main-session call and one subagent call. Return `updatedInput` adding a dummy
  `_probe` key and confirm the server refuses it as an unknown parameter (`ToolArgumentValidator.cs:1243`).
- Done when recorded in this plan: `agent_id` present for subagents and absent for main; whether it equals the
  `SendMessage` id; `updatedInput` reaches the server; how it combines with `enforce-dogfood.ps1`; hook latency
  per call.

**Results (2026-11-05; probe hook in `settings.local.json`, removed afterwards):**

- `agent_id` and `agent_type` are present on a subagent call (`explore-tier1`) and absent on main-session calls.
- `agent_id` equals the id the Agent tool returned, which is the id `SendMessage` addresses.
- `updatedInput` reaches the server with no `permissionDecision`. The injected key produced `Unknown parameter '_probe'`
  from the validator and appeared in the echoed `toolCall.arguments`, which confirms the filter placement in Decision 2.
  Tested only with bypass permissions active; other permission modes are untested.
- On `Git` the probe ran alongside `enforce-dogfood.ps1` and the injection still arrived. No existing hook returns
  `updatedInput` (no match under `.claude/`), so there is no merge conflict to resolve.
- Latency of the hook process per call, 10 runs: `powershell.exe` median 369 ms (347-383); `pwsh` median 518 ms (506-543).
- **Hook settings hot-reload.** Editing a settings file took effect in the running session with no restart. See Step 8.

### Step 1 - Context and operating mode option

- Files: new `RoslynSentinel.Common/ToolCallContext.cs`; new `RoslynSentinel.Common/AgentOperatingModeOptions.cs`;
  `RoslynSentinel.Server.Advanced/ServerStdio.cs:47` and `ServerHttp.cs:35` (call `Configure` beside
  `EolPolicyOptions.Configure`).
- Change: `ToolCallContext` (agent id and type, tool name, reason, `StartSeq`, `GatePassed`) with a static
  accessor wrapping the `AsyncLocal`, with a comment on why ambient (roughly 50 chokepoint call sites).
  `AgentOperatingModeOptions`: `Configured`, `Effective`, `Source`, `ResolveOnce(bool hasAgentId)` using
  `Interlocked.CompareExchange`. `Configure` resets the latch, so tests reset with `Configure([])`.
  The C# enum is `AgentOperatingMode`, because `RoslynSentinel.Common.OperatingMode` already exists (Production/Testing,
  from `--testing`). The flag, env var and `McpServerStatus` field keep the `OperatingMode` name; none of them collide.
- Done when tests show: the context flows across `await` and `Task.Run`; arg beats env var; explicit modes never
  change; `auto` latches on the first call and ignores later calls; concurrent first calls agree.

### Step 2 - EditAttributionLedger and recording

- Files: new `RoslynSentinel.Common/EditAttributionLedger.cs`; new `IEditAttributionLedgerProvider`;
  `PersistentWorkspaceManager.cs` (append after the rollback block, before the summary, near `:2105`, inside the
  lock).
- Change: bounded per-file history, `Interlocked` sequence, `CurrentSeq`, `GetLatest(path)`, `GetHistory(path)`.
  The manager owns the single instance and exposes it through the provider interface, read with an `as` cast as
  `IWorkspaceReloadFailureProvider` is (`ServerStatusTools.cs`). Do not also register it in DI: the
  `ScopedOperationLedgerEngine` in DI is a different object from the live one (comment in `AdminTools.cs`), and
  this plan must not repeat that split. Tests swap in a throwing ledger with `SetTestEditAttributionLedger`, beside
  `SetTestSolution`, so the constructor and DI stay unchanged.
- Done when tests show: a write records the expected entry; a rolled-back write records nothing; a thrown ledger
  failure does not fail the apply.

### Step 3 - Identity filter (outermost)

- Files: new `RoslynSentinel.Server.Advanced/AgentIdentityCapture.cs` (the filter's logic, testable without an
  `McpRequestContext`, as `ToolTelemetryRecorder` is);
  `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs` (`CallToolFilterRegistrations`; add
  `("AgentIdentity", AddAgentIdentityFilter)` as the first row, ahead of `ToolCallEcho`);
  `RoslynSentinel.Tests.Server/ToolTelemetryFilterOrderTests.cs`.
- Change: read and remove `_agentId`/`_agentType`, read `reason` raw, call
  `AgentOperatingModeOptions.ResolveOnce`, set the context with `StartSeq = CurrentSeq` (0 when there is no provider),
  then call `next`. Update what the new position makes false: the `names[0]`/`names[1]` asserts in
  `CallToolFilters_AreRegisteredInTheDeclaredOrder_WithTelemetrySecond` (rename it; AgentIdentity, then echo, then
  telemetry), the "must be registered first" and "must be registered second" doc comments on
  `AddToolCallEchoFilter` and `AddToolTelemetryFilter`, and the summary on `CallToolFilterRegistrations`. Telemetry
  still wraps every filter that can refuse, because the new filter never refuses.
- Done when tests show: `_agentId` no longer triggers the unknown-parameter refusal; the echoed `toolCall.arguments`
  omits it; the context is visible inside a tool; a call without the key gets `AgentId = null`; a failure inside the
  filter still runs the tool; the pinned order is AgentIdentity, ToolCallEcho, ToolTelemetry.

### Step 4 - Conflict refusal in the chokepoint

- Files: `PersistentWorkspaceManager.cs` (inside `_solutionLock`, right after `:1819`, before pre-image capture);
  `RoslynSentinel.Common/SentinelCallToolResult.cs` (`ToolErrorCode.ConcurrentEditRefused`).
- Change: apply Decision 6. Inside the lock so the check is atomic with the write. Skip when `exactRestore` is set
  (Step 7 handles undo).
- Done when tests show: different ids where B wrote after A's `StartSeq` refuse A, name B and set the code; same id
  and null ids are not refused; a refused batch writes nothing.

### Step 5 - Mode behaviour for the scoped operation ledger

- Files: `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs` (`MoveMemberTool`; reuse the
  `UnresolvedCallSites` error at `:497-509`; today's opener is at `:541-544`);
  `PersistentWorkspaceManager.cs` (`TryOpen` at `:2694`, and the block check at `:1765`).
- Change: in `MultiAgent`, a real apply with pending entries returns `UnresolvedCallSites` before anything is
  written; dry run is unchanged. `TryOpen` rejects in `MultiAgent` as a backstop for future openers. In
  `SingleAgent`, record the opening agent id and refuse a write from a different known agent while the scoped
  operation ledger is open, naming the opener. `ScopedOperationLedgerEngine` itself stays unchanged.
- Done when tests cover both modes: refusal with nothing written; `TryOpen` rejected; the second agent refused while
  the first still writes; null ids behave as today.

### Step 6 - McpServerStatus reports the mode

- Files: `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`; `McpServerStatusResult`.
- Change: add `OperatingMode` (effective), `OperatingModeSource` (`startup arg or env var`,
  `auto: first call carried an agent id`, `auto: first call had no agent id`) and `CallerAgentId`, which lets the
  main session confirm the hook is working. Add one short clause to the tool description and check the schema budget.
- Done when a test covers each source value and the schema budget test passes.

### Step 7 - Undo guard

- Files: `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs` (`UndoLastApply`).
- Change: before the apply, compare each file's current hash with the after-state of the change being undone. On any
  mismatch, refuse the whole undo with `IsError = true`, naming the latest writer, and return before the apply and
  before `RecordUndo` (`:156`). A per-file refusal would not work: the method reports failed reverts with
  `IsError = false` and calls `RecordUndo` unconditionally (existing TODO at `:151`).
- Done when tests show undo refused after a second agent edits the file, nothing written and no `RecordUndo`; undo
  still works when the file is unchanged.

### Step 8 - Hook

- Files: `.claude/settings.json` (PreToolUse matcher on the RoslynSentinel MCP tool prefix, command
  `.claude/hooks/bin/agent-identity-hook.exe`). The helper is already built:
  `RoslynSentinel.Utilities.AgentIdentityHook`, published by `scripts/publish-agent-identity-hook.ps1`. The exe is a local
  build output, not committed, so the docs in Step 11 must name the publish script as a setup step.
- Change: the helper returns `updatedInput` as the full original `tool_input` plus `_agentId` and `_agentType`, and fails
  open on any problem. Folding into `enforce-dogfood.ps1` would not save the cost, because that hook's matcher covers only
  some MCP tools.
- Before saving the hook, confirm a missing exe fails open: Claude Code must run the tool call when the command cannot
  start, on a clone that has not run the publish script.
- **Rollout order: this is the last server-affecting change to go live.** The hook reaches every running session as soon
  as the settings file is saved, and any server without the Step 3 filter refuses every call with `Unknown parameter
  '_agentId'`. Merge Steps 1-7 first, restart every window so each instance serves a build with the filter, confirm with
  `McpServerStatus`, and only then save the hook.
- Done when, live with two subagents: `McpServerStatus` shows `MultiAgent` and `CallerAgentId: main`, and the Step 4
  refusal names the right subagent.

### Step 9 - Reporting surfaces

- Files: `RoslynSentinel.Common/EditAttributionFindings.cs` (new shared helper); `RoslynSentinel.Tools.Basic/WorkspaceBuildTestImpl.cs`
  (`Build`, `GetDiagnostics`); `RoslynSentinel.Common/ValidateAndApplyHelper.cs`; `RoslynSentinel.Common/SentinelCallToolResult.cs`;
  `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs` (`MoveMemberTool` error branch);
  `RoslynSentinel.Tools.Basic/GitTools.cs`; for "not found" errors `RoslynSentinel.Common/ToolResultMarshaller.cs`,
  `RoslynSentinel.Server.Advanced/AgentIdentityCapture.cs` and `RoslynSentinel.Common/ToolCallContext.cs`.
- Change: one shared helper turns a set of paths into `Finding`s, reading "last changed by `<agent>` via `<tool>`,
  `<age>`: `<reason>`". Emit only for a different agent. Apply to: files carrying errors in `Build`, `GetDiagnostics`
  and compile-gate failures; dirty files in `Git(status)`.
- Failed edits (owner ruling, 2026-11-06): about 55 sites turn a failed `ApplyOutcome` into a result with
  `ErrorData = apply.Error` and drop its `Findings`. So `ResultError` carries a JSON-ignored `Findings`, set in
  `ValidateAndApplyHelper`, and `SentinelCallToolResult.Findings` falls back to it after explicit findings and
  `AppliedChangeSummary`. The 55 sites do not change. `MoveMemberTool` builds new errors in its error branch and copies
  the `Findings` across.
- "Not found" errors (owner ruling, 2026-11-06). Purpose: a reasonably confident signal, so an agent's own typo or guess
  is not blamed on another agent or on the brief. The agent still investigates, so the note states evidence and not
  a cause. `RoslynSentinel.Common/NotFoundEvidence.cs` holds the search. It runs in `ToolResultMarshaller`, where the typed
  result is in hand, so there is no JSON rewrite and no new filter. A typed throw (`ToolNotFoundException`, including a
  `ReplaceSnippet` no-match) never reaches the marshaller, so `CallToolFailureHandler.ToFailureResult` adds the same notes.
  `AgentIdentityCapture` copies the `filePath`, the missing term and the solution root onto `ToolCallContext`, from a
  per-tool map of argument names (`methodName`, `memberName`, `oldContent` and so on). Unmapped tools get no note. The codes
  are `NotFound`, `MethodNotFound` and `FileNotFound`. Match the term exactly, normalizing line endings for snippets and
  using word boundaries for names; a name under 3 characters gets no note. Evidence, in order:
  1. Operation blobs, searched directly (owner ruling, 2026-11-06), not through the edit attribution ledger, which is per
     server. The blob folder is per solution root, so every window writes to it. Filter by filename timestamp to the last
     60 minutes (`DefaultWindow`), newest first, at most 200 blobs, open each and match the item's `FilePath`. Newest first,
     the first `BeforeSource` that holds the term belongs to the write that removed it. If that writer is foreign:
     "`Bar` was in `Foo.cs` before agent `b7` changed it 20s ago via RenameSymbol: `<reason>`", saying when the writer is
     another session. A writer is foreign when its session differs from the caller's, or the session matches and its agent
     id differs. Blobs exist only for successful applies. Without a session id on both sides, an item counts as foreign only
     when its agent id differs and is not `main`, because every window's main session is called `main`.
  2. Git fallback: if `git show HEAD:<path>` holds the term and the file on disk does not, and no blob in the window shows
     a write by this caller: "`Bar` is in `Foo.cs` at HEAD but not on disk: it was changed since the last commit, writer
     unknown". This now only has to cover edits made outside any server and writes older than the window. `git log -S`
     is not used, because old removals are not a sign of a live conflict.
  3. Neither: no note, so a typo stays silent.
- Session id: the hook helper adds `_sessionId` from the hook input's `session_id`. `AgentIdentityCapture` strips it onto
  `ToolCallContext`, and `OperationBlobWriter.WriteAsync` stamps `AppliedBySessionId` beside `AppliedByAgentId` (add
  `AppliedByAgentType` and `AppliedByReason` too, for the note). **Rollout order, same trap as Step 8:** a server that does
  not strip `_sessionId` refuses every call as an unknown parameter, and the hook's settings hot-reload. So commit the
  server side first, restart every window, confirm with `McpServerStatus`, and only then republish the helper with
  `scripts/publish-agent-identity-hook.ps1`.
- Known gaps: `GetDiagnostics` checks only errors inside `maxDetails` (TODO.md); `Git(status)` checks the 10-per-list
  sample when totals exceed `maxEntries`; a name mentioned only in a comment can still match; `batchEdits` paths are not
  covered at first.
- Known gaps, not-found notes: blob order rests on the write time in the blob's file name (taken inside the write lock, to
  100 ns), so a system clock adjustment can misorder writes and two servers' writes to one file are not serialized; a bare
  file name that the tool resolved elsewhere in the solution gets a blob note but no Git note, because Git looks under the
  solution root.
- Not verified: that every `ReplaceSnippet` and batch blob carries `BeforeSource`.
- Live check at 9c27ce7b (restarted server, a subagent created a scratch file and renamed a method): `GetMethodSource`,
  `ReadFile(memberName)` and a `ReplaceSnippet` no-match each carried the blob note naming the subagent, with tool, age and
  reason; a typo got no note. The note shows the blob's operation name (`replace_snippet`), not always the tool's name.
  Tests only: the Git note, and another session as writer, which needs the republished helper.
- **Remaining rollout, owner action.** When the helper was due, three other servers ran older builds (`0022bcdb`, `090b4b16`
  twice), so it was not republished. Restart those windows, confirm no server runs a build older than b6ccd5e5, then run
  `scripts/publish-agent-identity-hook.ps1`. Until then the search uses agent ids alone, so it cannot tell one window's `main`
  from another's.
- Done when tests cover each surface and show no output when there is no foreign entry; a typo gets no note; a term removed
  by another agent gets the blob note; a term removed outside the server gets the Git note. A call with no agent id
  is unchanged.

### Step 10 - Re-validate after a sibling's write

- Files: `PersistentWorkspaceManager.cs` (inside the lock, after Step 4); `ValidateAndApplyHelper.cs`.
- Change: `ValidateAndApplyHelper` sets `ctx.GatePassed` when its gate passes, and the chokepoint treats
  `validateChanges` the same way. Under the lock, if the gate passed and any entry from a different known agent has
  `Seq > ctx.StartSeq`, re-run `ValidationEngine.ValidateChangesAsync(CurrentSolution, changes, ctx.GateRemovePaths)`, the
  static overload meant for use inside the chokepoint (`ValidationEngine.cs:136`). `GateRemovePaths` holds what the gate left
  out of its candidate, because a rename's old path would otherwise read as a duplicate declaration, and the chokepoint is not
  given `removePaths`. Refuse on new errors with `ConcurrentEditRefused`, naming the agent.
- Done when a test reproduces the race (A removes a method, B adds a call, both validated first) and the later apply is
  refused; a call with no foreign write does not re-validate.

### Step 11 - Guidance and docs

- Files: `docs/current/references/reference_architecture_map.md` (edit attribution ledger, refusals, mode, hook setup and
  `--operating-mode`); `docs/current/references/reference_tool_response_pipeline.md` (filter 1, filter 8, marshaller order; the
  count was already 13); memory `reference_central_mcp_request_filter_pipeline` no longer exists;
  `.claude/skills/dispatch-implementer/SKILL.md` ("Concurrent C# dispatches (trial)": the refusal and the
  `MULTI-AGENT:` report); `.mcp.json` or launch script docs for `--operating-mode`.
- Done when the docs match the source.

## Out of scope

- Persisting the edit attribution ledger, or attributing writes across server instances (cross-session journal proposal).
  One exception by owner ruling, 2026-11-06: the Step 9 "not found" note may name a writer from another window by reading
  the shared operation blobs. No refusal depends on it.
- Any change to external-drift detection or `SessionHalted`.
- A runtime mode toggle, or any new tool or tool operation.
- Ownership rules on `LedgerFix`/`LedgerRelease`, and changes to `ScopedOperationLedgerEngine`.
- Automated agent-to-agent messaging, and one server instance per worktree or subagent.
- Per-agent telemetry. Guidance-adherence counts are approximate today because interleaved agents can consume each
  other's suggestions (`ToolGuidanceCollector` class doc). `ToolCallContext.AgentId` makes keying them by agent a small
  follow-up, as long as `SingleAgent` and hookless output stay unchanged. Record it as a `// TODO` in `ToolTelemetry`
  during Step 3.

## Risks and open decisions

- **Hook latency (decided: compiled helper).** A `powershell.exe` hook measured about 370 ms per RoslynSentinel call, or
  roughly 18 s over a 50-call subagent run. The NativeAOT helper measured median 25 ms spawned directly (20-35, 20 runs) and
  50 ms through `cmd` (45-58), or about 2.5 s over the same run. Publishing it needs the VS C++ build tools.
- **A server older than Step 3 behind the hook.** Once the hook is live, a launcher fallback to an older build
  (`lastLaunch.buildFailed`) or a window not yet restarted refuses every call as an unknown parameter. The failure is loud
  and names `_agentId`; the recovery is to rebuild or restart that window.
- **Permission modes other than bypass.** `updatedInput` was only exercised under bypass permissions. Re-check under the
  default mode in Step 8; the hook must not grant permissions (`permissionDecision: allow`) to make it work.
- **`auto` resolves on one observation.** If the first call lacks an id (hook added or broken at that moment), the
  process stays `SingleAgent` until restart. That is the deliberate cost of never changing mid-session; status shows it,
  and an explicit arg overrides it.
- **Explicit `multi-agent` with no hook.** All ids are null, so the conflict refusal never fires, while partial `MoveMember`
  applies are still refused. `CallerAgentId: null` in status is how to spot it.
- **`SingleAgent` with several agents.** `LedgerFix`/`LedgerRelease` remain unguarded, so a second agent can still abandon the
  first agent's scoped operation ledger. Accepted as out of scope; that configuration is opt-in.
- **Lone main session under the hook.** It gets `MultiAgent`, so `MoveMember` refuses rather than opening a scoped operation
  ledger. It can retry with `callSiteFixups` or start with `--operating-mode=single-agent`.
- **False-positive conflict refusals.** `StartSeq` is taken at call start, but an engine may read `CurrentSolution` after the
  sibling's write, compute correct text and still be refused. Conservative: the retry succeeds. Measure before refining.
- **Step 10 holds the write lock during a compile.** It runs only when a foreign write raced this call, but it blocks other
  writers meanwhile. It also relies on `ValidateAndApplyHelper` being the main gate path; other gate paths were not traced.
- **Batch tools lose the name.** `AsyncBatchEngine` surfaces only `RefusalCode` (`AsyncBatchEngine.cs:836`), so those agents see
  the code without the writer. Known limitation. Multi-apply tools can also be refused part-way; check whether they roll back.
- **Undo after-state (Step 7) - resolved.** Checked while building Step 7: nothing ever writes `OperationItemRecord.AfterSource`, so the after-state compare was dropped. The owner chose an applier id instead: `OperationBlobWriter.WriteAsync` stamps `AppliedByAgentId` on each item, and the undo is refused when the edit attribution ledger's latest writer is a different known agent. (Original note: assumed after-content or a hash per item. If absent,
  the fallback was the edit attribution ledger's latest `HashAfter`, which a restart clears.) Blobs written without an agent id carry no applier id, so their undo is never refused.
- **Writes outside the chokepoint** (`docs/current/reference-code-file-write-paths-v1.md:122-128`) and Git mutations are not
  attributed. Whether `Git` writes the working tree was not checked.
- **`LaunchTestInstance` children.** Whether they inherit `ROSLYNSENTINEL_OPERATING_MODE` or receive the arg was not checked.
