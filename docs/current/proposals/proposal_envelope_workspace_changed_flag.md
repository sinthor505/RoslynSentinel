# Stamp a top-level editResult field on every response from a tool that can write

**Status:** PROPOSED 2026-11-09. Field shape decided with the owner on 2026-11-09 (`editResult`, replacing the first draft's `workspaceChanged` boolean). Build after the other session releases `SentinelCallToolResult.cs` and finishes its stale-read follow-up work.

## Motivation

Weak models cannot reliably tell from a response whether their edit landed. The only top-level signals
today are `isError`, `workspaceVersion`, `statusMessage`, `findings` and either `successData` or `errorData`
(`RoslynSentinel.Common/SentinelCallToolResult.cs:221-406`). Whether anything was written sits in the
tool-specific payload, for example ReplaceSnippet's `replacementResult.succeededFiles`, `failedFiles` and
`workspaceInSync`. It is shaped differently by every write tool, and some report it only in prose.

Evidence from the 2026-11-09 10-chain runs (`ModelTestingResults/113/Model_AppliesTenChainedRefactors/`):

- **Run `20261109-173949-849` (EditSnippets arm), T22:** after a batch rejected with "every edit matched,
  but ... no changes were written", the model reasoned "I already applied the edits via EditSnippets - the
  tool results say 'no changes were written' because the old content matched". It spent T20-T60 (41 turns)
  re-reading files and never edited again. The arm-A run read "change not applied" correctly (T16).
- **Run `20261109-202110-210`, T23:** after rejected calls at T19 and T22, the model reasoned "The workspace
  version is 11 - my edits went through". It then built `oldContent` from its own rejected `newContent`,
  which caused 4 snippet-not-found failures (T12, T14, T20, T24). Those failures pushed ReplaceSnippet past
  the 5-per-tool error budget, which is the only reason the run failed.
- `workspaceVersion` is an absolute counter
  (`SentinelCallToolResult.cs:386-396`: "Lets a caller compare a version fetched by a read tool against
  one returned by a later write"). A weak model reads a number as confirmation.

Commit `47580ace` reworded six batch-rejection messages to "nothing was changed; every file is exactly as
you last read it". That fixes one family of prose, but about 60 other "nothing was written" messages
remain, and prose is a weaker signal than a field with a fixed name.

## Proposal

### The field (decided 2026-11-09: one computed enum, fixed name)

- `editResult: "applied" | "rejected" | "unchanged" | "partial"` at the top level of the response.
- It is present on every response from a tool that can write, including errors, refusals and `validate`
  or dry-run calls. It is absent on read-only tools, so its presence also marks a write tool.
- The server computes it, so the model never has to combine two signals itself. It reports what happened,
  not what was asked:

  | isError | something was written | editResult | Meaning |
  | --- | --- | --- | --- |
  | true | no | `rejected` | nothing written (the common case to make unmistakable) |
  | false | yes | `applied` | the change is on disk and in the workspace |
  | false | no | `unchanged` | succeeded with no effect (validate, identical content, nothing to do) |
  | true | yes | `partial` | partly applied; the error explains what (rare; must be visible, not hidden) |

- No `workspaceChanged` boolean beside it: it would only add a second signal to cross-check. `isError`
  stays, because the MCP protocol requires it.
- camelCase words, not `No-op`. `unchanged` says what happened to the files.

### Where it is computed (decided: centrally, from the writes that happened)

- **Source of truth:** the write chokepoint records each file it writes during the current call.
  `ToolCallContext` (`Common/ToolCallContext.cs`) is already the per-call ambient set by
  `AddAgentIdentityFilter`; the chokepoint marks it when a write commits.
- **Stamp:** a call-tool filter in the style of `AddHaltStampFilter` and `AddWorkspaceStaleStampFilter`
  (`docs/current/references/reference_tool_response_pipeline.md`, "Call-tool filters" items 5-6). It inserts
  the property into the first text block after `next` returns, and wraps a non-JSON body the way HaltStamp
  does. The position in `CallToolFilterRegistrations` is inside the exception catch-all, so thrown errors
  are stamped too. `ToolTelemetryFilterOrderTests` pins the order.
- **Why not per tool:** copying the payload's value into the envelope in each tool, as first suggested, is
  one line per write tool. That line can drift from what was actually written, and a thrown error never
  reaches it.
- **Why not compare workspace versions:** a before/after version diff is simpler, but another agent writing
  on the same server during the call would make a rejected call look `applied` or `partial`. Multi-agent
  sessions are normal here.

### Which tools count as "can write"

The same classification the mutation breaker or `ActiveToolSurface` already uses for write tools, if one
exists. Otherwise a `[WritesWorkspace]`-style attribute checked by a test that every tool reaching the
write chokepoint carries it. See Open questions.

### Wording that goes with it

- Tool descriptions are not touched: the field name explains itself, and descriptions cost prompt tokens on
  every call.
- Rejection messages may later drop "nothing was changed" prose once the field proves itself in evals. Not
  part of this proposal.

## Alternatives considered, not pursued

- **Per-tool duplicate assignment of the inner value.** Cheapest to start, but it drifts, misses thrown
  errors, and needs every new write tool to remember it.
- **Workspace version before/after.** False positives under concurrent agents (above).
- **Rename or drop `workspaceVersion`.** It has real users (stale line-number detection). Removing the
  misleading reading does not add the missing signal.
- **Prose only (fix 2 of 2026-11-09).** Already done for six messages. Prose varies, and a weak model
  reasons over it, as in run 173949 T22.
- **A `workspaceChanged: true|false` boolean (this proposal's first draft).** The model would have to
  combine it with `isError` to tell `rejected` from `unchanged`. That two-signal inference is exactly what
  weak models get wrong. The self-explanatory enum removes it.

## Open questions

- Whether the write chokepoint can mark `ToolCallContext` for every write path: `ValidateAndApplyHelper`,
  `PersistentWorkspaceManager` writes, whole-file writes, Git operations that change the working tree, and
  undo/retry. Needs a trace before building.
- Whether `ToolCallContext` survives into background or offloaded work started by a call (async-local flow).
- Which existing classification marks write tools (mutation breaker, `ActiveToolSurface`, tool attributes).
  Unverified.
- Whether Git `commit`/`stage`, which change the index but not the files, count as "workspace changed".
  Lean: no, files only.
- Effect on the `EchoReserveChars` budget: about 25 characters per write-tool response.
- Whether the name `editResult` reads well on DeleteFile and on Git working-tree changes. Lean: keep one name
  everywhere rather than per-tool names.

## Cost / risk

- **Touches:** the filter table in `Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, a new filter, the
  write chokepoint, `ToolCallContext`, and tests pinning filter order and the field on representative write
  tools (applied, rejected, validate, thrown error).
- **Risk:** a missed write path reports `rejected` or `unchanged` after a real write. That is worse than no field. Mitigate
  with the "every write path marks the context" trace and a test per write tool class.
- **Coordination:** another session is currently editing `SentinelCallToolResult.cs` and the write path
  (stale-read follow-ups). Build only after it releases those files.

## Related

- `evals/eval_edit_snippets_ab_2026_11_09.md` and the 10-chain runs cited above
- `findings/finding_batch_edit_item_schema_hides_required_fields.md`
- `proposals/proposal_semantic_edit_impact_diagnostics.md` (repeat detection overlaps: "your previous call
  was rejected; the file is unchanged")
- `docs/current/references/reference_tool_response_pipeline.md`
