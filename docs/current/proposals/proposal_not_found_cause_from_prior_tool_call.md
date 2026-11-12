# Not-found errors name the prior tool call that renamed, moved or removed the target

**Status:** IN PROGRESS 2026-11-12. v1 slices 1-4 done (d5667196, 3a246d60, e000f0cf, a7385d44) and live-checked PASS for rename, dry-run rename and undo; follow-up slices A-D approved by the owner (see "Follow-ups after the live check"), E held. v2 (generic-edit symbol diff) not built.

## Motivation

A weak model that renames `Foo.Bar` to `Foo.Baz` often asks for `Foo.Bar` a few calls later. Today it gets the generic
not-found advice ("typo, renamed, moved or removed"), which leaves it to guess. The server applied the rename itself and knows
the fact; the error can state it: "`Foo.Bar` was not found. Call `a76e9f1c` (RenameSymbol, 3 min ago) renamed `Foo.Bar` to
`Foo.Baz`, now declared at `Foo.cs:42`." Out-of-band edits (editor, shell, git) are out of scope.

What exists (verified 2026-11-12):

- `NotFoundEvidence` (`RoslynSentinel.Common/NotFoundEvidence.cs:14-449`) walks operation blobs newest first and finds the
  first pre-image (`BeforeSource`) that still holds the missing term as a whole word (`SearchBlobs`, lines 122-178). It is text
  evidence only: "`Bar` was in `X.cs` before agent Y changed it via RenameSymbol" (`BlobNote`, 307-322). It cannot tell a
  rename from a remove plus add or a move, so it cannot name the new name or location.
- It returns nothing when the caller made the write (`kind != WriterKind.Foreign`, lines 165-168). The commonest weak-model
  case, forgetting its own rename, is exactly the one suppressed.
- It runs only for tools in `AgentIdentityCapture.MissingTermArgument`
  (`RoslynSentinel.Server.Advanced/AgentIdentityCapture.cs:25-35`), and only when the call has an agent id and a `filePath`
  (`NotFoundEvidence.ForCurrentCall`, 40-50). Symbol lookups with no file path (LocateSymbol, InspectSymbol, RenameSymbol,
  MoveMember, Search symbol mode) never get a note.
- Blobs (`OperationBlobWriter.WriteAsync`, `RoslynSentinel.Common/OperationBlobWriter.cs:121-128`) record `toolName`,
  `changeId`, `generatedUtc` and per-file items (`OperationItemRecord`: file path, full before/after source, outcome, writer
  agent/session/type/reason). They carry no `toolCallId`, no server id and no symbol-level effect.
- `ToolCallContext` (`RoslynSentinel.Common/ToolCallContext.cs`) already carries `ToolCallId` (the id every response echoes as
  `toolCall.toolCallId`), `SessionId`, `MissingTerm` and `MissingTargetKind`.
- The edit attribution ledger (`EditAttributionLedger`, `RoslynSentinel.Common/EditAttributionLedger.cs`) records who wrote
  each file, in memory, 8 entries per file, no symbol data.

## Proposal

### 1. The operation records what it did (decided)

Each covered operation, at the point where it knows the facts, records an effect on the ambient `ToolCallContext`:

```
EditEffect(Kind, SymbolDisplay, SymbolDocId, SimpleName, NewName?, NewContainer?, NewFilePath?,
           ToolCallId?, ToolName, ChangeId?, SessionId?, ServerInstanceId, AgentId?, UtcTime)
EditEffectKind: Renamed | Removed | Moved | Added | SignatureChanged | FileDeleted
```

Effects are pending until the write chokepoint commits the write; a rolled-back or failed write drops them. Engines called
with no context (tests, other hosts) record nothing, as `ToolCallContext.DetachCurrent` does today.

A text diff cannot recover "renamed", so effects are never inferred from before/after source in v1.

### 2. An in-memory effect index, persisted through the blob (decided)

- `EditEffectIndex` in `Common`, one instance per `PersistentWorkspaceManager` (same ownership as the edit attribution
  ledger), keyed by simple name, bounded (oldest dropped first). The not-found path reads it; it parses no JSON.
- The chokepoint appends committed effects to the index. It is fed from the chokepoint, not from blob writes, because some
  write paths write no blob (`docs/current/references/reference_code_file_write_paths.md`).
- The blob payload gains `toolCallId` and `effects` (on-disk only; no tool output changes), so the index can be seeded from
  recent blobs at `LoadSolution` and survives a server restart.
- `UndoLastApply` marks the effects of the undone `changeId` as reverted; a reverted effect produces no note.

### 3. Identity scope (decided)

Match effects on `SessionId`, which is stable across a server restart in the same window. When the call or the effect has no
session id (hosts that send no `_sessionId`, e.g. PlanStepRunner and model-eval), fall back to the server instance id. Effects
from other sessions are excluded; the existing `NotFoundEvidence` text note still covers foreign writers. The caller's own
writes are included (the reverse of today's self-suppression).

### 4. Lookup and note text

- Key: `ToolCallContext.MissingTerm`, reduced to a simple name (last dotted segment, generic arity and parameter list
  stripped). A qualified term must also match the effect's container.
- Follow chains (`Bar` -> `Baz` -> `Qux`) to the newest name. A later `Added` or rename-to of the missing name cancels the note.
- Resolve the end of the chain against the current solution and report where it is declared now; if it no longer resolves,
  say so rather than guess.
- The note states facts side by side and never claims a cause (facts-and-options rule,
  `docs/current/references/reference_tool_error_text_facts_options_policy.md`). Shape:
  "`Foo.Bar` was not found. Call `<toolCallId>` (`<tool>`, `<this agent | agent X>`, `<age>` ago) renamed `Foo.Bar` to
  `Foo.Baz`, declared at `<file>:<line>`." Removed: "... removed `Foo.Bar` from `<file>`." Moved: "... moved `Foo.Bar` to
  `<container>` in `<file>`."
- When an effect note is produced, the text note from `NotFoundEvidence` for the same term is skipped.
- The lookup term map is extended to symbol tools with no `filePath` (exact argument names verified in the slice), and the
  effect lookup does not require `TargetFilePath` or `AgentId`.

### 5. Coverage (decided)

v1, precise tools only: RenameSymbol (`Renamed`), Member remove (`Removed`), Member add (`Added`, for cancellation),
MoveMember (`Moved`), DeleteFile (`FileDeleted`), ChangeSignature (`SignatureChanged`, lowest priority).

v2 (not in this run): generic edits (Member replace, ReplaceSnippet, ApplyDiff) compare declared symbols before and after the
edit and record `Removed`/`Added` facts. That says "removed `Bar`, added `Baz`", never "renamed". The symbol diff from
`proposal_semantic_edit_impact_diagnostics.md` is a candidate to reuse (unverified).

### Slices

1. `EditEffectKind`, `EditEffect`, `EditEffectIndex` (+ provider interface on the workspace manager), `ToolCallContext`
   pending effects, commit at the chokepoint, `toolCallId` + `effects` in the blob. Unit tests.
2. Emitters: RenameSymbol, Member remove/add; `UndoLastApply` reverts. Tests.
3. Lookup and note: `EditEffectEvidence` (chain, cancel, current location, identity scope), wired ahead of
   `NotFoundEvidence.ForCurrentCall` at its call sites (`ToolResultMarshaller.cs:170`, `CallToolFailureHandler.cs:76`);
   lookup-term map extended to symbol tools. Tests, then one live check after a server restart.
4. Emitters: MoveMember, DeleteFile, ChangeSignature; seed the index from blobs at `LoadSolution`. Tests.

## Alternatives considered, not pursued

- **Parse the server log.** Unstructured, format drifts, level-filtered, and may be rotated. The structured channel (context
  and blob) is ours to extend.
- **Infer the effect from blob before/after text.** A text diff cannot distinguish rename, remove plus add, and move, so the
  note would be a guess presented as a fact.
- **Index of blob ids only.** Still parses blobs on every not-found and misses writes that produce no blob; holding the small
  effect record itself is cheaper and complete.
- **Scope by server instance id only.** A restart would drop the history while the agent's transcript still holds the old call
  ids. Kept only as the fallback when no session id exists.
- **Extend `EditAttribution`.** It is per file and capped at 8 entries per file; effects are per symbol and need lookup by name.

## Open questions

- Which hosts send `_sessionId` today, and is it stable across a server restart in one VS Code window? Verify in slice 1; the
  server-instance fallback covers hosts that send none.
- Exact argument names for the lookup-term map on LocateSymbol, InspectSymbol, RenameSymbol, MoveMember and Search (slice 3).
- Should an effect note replace the generic "typo/renamed/moved" advice that `plan_error_policy_pointer` is reworking? v1 adds
  a finding only; replacing the advice is a follow-up coordinated with that plan.
- Index bound and blob seeding window (proposed: 500 effects, 60 minutes, matching `NotFoundEvidence.DefaultWindow`).

## Settled in implementation (2026-11-12)

- `_sessionId` comes only from Claude Code's PreToolUse hook (`agent-identity-hook.exe`, copies the payload `session_id`); it is per
  conversation (a new chat gets a new id), host-side so it survives a server restart. PlanStepRunner and model-eval send none and
  use the server-instance fallback (`ServerInstanceIdentity.Id`, random per process).
- The chokepoint cannot stamp a changeId (callers mint it after the apply). Effects commit with `ChangeId = null` and
  `OperationBlobWriter.WriteAsync` stamps them (`ToolCallContext.StampCommittedEffects`). A write with no blob keeps a null
  ChangeId and `UndoLastApply` cannot revert it.
- Effects commit only when a write stands and at least one file changed; refusals, rollbacks and partial failures drop them.
  Emitters hand effects over through `ValidateAndApplyAsync(recordEffects:)`, which runs only for a real apply that passed the gate.
- Lookup term argument names: LocateSymbol `symbolName`, RenameSymbol `docCommentId`, MoveMember `className`, Search `query`
  (symbol mode only). InspectSymbol has no name argument and is not mapped.
- The note is produced inside `NotFoundEvidence.ForCurrentCall` (the single point both call sites use) and replaces the text note.
- DeleteFile records one `Removed` per top-level non-partial type plus `FileDeleted`. MoveMember records `Moved` only in
  remove mode (a keep mode leaves a forwarder). ChangeSignature records `SignatureChanged`.
- Undo on disk: `UndoLastApply` writes `<blob>.undone` beside the blob; `EditEffectSeeder` skips such blobs. Seeding runs in the
  background after LoadSolution (60 minutes, 200 blobs, 5 s, newest 500 effects), fail-open.
- Index bound 500 and window 60 minutes kept. The generic not-found advice is unchanged (v1 adds a Finding only).
- Known gaps (TODO markers in `AgentIdentityCapture.cs`): InspectSymbol has no name to key on; MoveMember `memberNames` is not read;
  Search symbol mode zero-hit is a success result and never reaches the not-found path. The index is not cleared when another
  solution is loaded into the same manager.

## Follow-ups after the live check (approved 2026-11-12)

Live check at buildSha e7416278: rename note, dry-run rename note and undo (no note afterwards) PASS. Gap: DeleteFile of a file, then
GetMethodSource(filePath: that file) returned FileNotFound with no note, because a file miss keys on the path, not on `MissingTerm`.

- A. A file miss (FileNotFound / file-miss) matches `FileDeleted` and file-move/rename effects by path; RenameSymbol's fileRenamed case records the file move.
- B. Search symbol-mode zero hits (ForZeroHitSearch path) and InspectSymbol get the note.
- C. MoveMember reads `memberNames` for the lookup term.
- D. The effect index is cleared or rescoped when a different solution is loaded into the same manager.
- E. (HELD until the owner confirms) the generic speculative not-found advice is replaced by the fact when an effect note is present;
  overlaps `plan_error_policy_pointer`.

## Cost / risk

- Touches the write chokepoint (`PersistentWorkspaceManager`); read `docs/current/references/reference_architecture_map.md`
  first. Effects must commit only with the write, inside the write lock, or a failed write leaves a false note.
- Blob JSON gains two fields; `NotFoundEvidence` and undo read blobs by property name and ignore unknown fields.
- Runs alongside `plan_error_policy_pointer` (not-found error text, `ToolErrorMapper`, `ToolCallContext.MissingTargetKind`).
  Coordinate file holds with that run.
- A stale note is the main failure mode (an effect the agent already undid or overwrote). Mitigated by revert marking,
  cancellation on `Added`, and resolving the chain end against the current solution.

## Related

- `docs/current/plans/closed/plan_edit_attribution_ledger.md` (edit attribution ledger, blob-based not-found notes)
- `docs/current/proposals/proposal_cross_session_write_journal.md` (cross-window journal; this proposal stays in-session)
- `docs/current/ideas/idea_store_aftersource_in_chokepoint_operation_blobs.md`
- `docs/current/proposals/proposal_semantic_edit_impact_diagnostics.md` (symbol diff, candidate for v2)
- `docs/current/plans/plan_error_policy_pointer.md` (not-found error text, running concurrently)
