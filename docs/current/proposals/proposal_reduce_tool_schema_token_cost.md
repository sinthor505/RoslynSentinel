# Cut the MCP tool-schema token cost by about half

**Status:** PARTLY IMPLEMENTED 2026-10-03.
- Done: gating rule + status-tool fixes (a41e72c7); step 1, no x-tags or `default:null` unless `--emit-datatags` (f90e5bcf); step 3, description diet, -15.2% emitted schema (25b7b8c8).
- Step 2a (lean profile that hides `autoStage`/`returnDiff`/`validateOnApply`/`lineBefore`/`lineAfter`) is built but **opt-in only**: `--schema-profile=lean` or `ROSLYNSENTINEL_SCHEMA_PROFILE=lean`, needs a server restart, and nothing enables it by default. The risk of hiding params that some client or model still sends led to a decision not to hide anything by default.
- Dropped: step 2b (alias hiding) and enabling the lean profile in any launch config.
- Step 4b implemented, opt-in only: stage 1 is the `claude-lean` mode (cdc32ba2); stage 2 is the dynamic `McpToolsetControl` tool, available only in `claude-lean`, which takes the Core set from 25 to 26 tools. See "How to use claude-lean and McpToolsetControl" at the end of this document. `list_changed` was verified against the SDK (see Open questions).
- Step 4a, declaration merge done (slices 4a-1a and 4a-1b): the opt-in `Declaration` tool (operations `modifier`, `accessibility`, `attribute`, `baseType`) delegates to the same Impl methods as ModifyModifier, ChangeAccessibility, ModifyAttribute and ModifyBaseType. It is exposed only in `claude-lean`, via the `declarations` toolset, where it replaces those four; they are unchanged and still registered in `claude` and the other modes. Emitted schema: Declaration 5469 chars vs 8385 for the four originals (-35%). Batch params keep their original names where they do not collide (`edits` for modifier, `batchEdits` for attribute) and `baseTypeEdits` for baseType; `action` is a closed `add|remove|replace` enum, replace being attribute-only. Slice 4a-2 done: the opt-in `ParameterEdit` tool (operations `method`, `constructor`; `action` add|remove|view) delegates to the same Impl methods as MethodSignature and ConstructorParameter, exposed only in `claude-lean` via the `declarations` toolset where it replaces those two (unchanged elsewhere). Emitted schema: ParameterEdit 3615 chars vs 5439 for the two originals (-34%; MethodSignature 2131, ConstructorParameter 3308). It names the missing/foreign params in an InvalidArgument error and keeps `methodName`/`className`/`fieldName`/`callSiteFixups` as distinct params. The `declarations` set is now Declaration, ParameterEdit, ModifyEnum, ChangeSignature, SyncTypeAndFilename (38 catalog tools in all).
- Finding: a validator that rejects unknown params must know about stripped ones; `HiddenSchemaParams` covers this for the lean profile.

## Motivation

The 68 tools active in `claude` mode cost about 38.9k tokens of schema per session. The nine largest
(Git, Member, ConstructorParameter, Search, ModifyAttribute, MethodSignature, ModifyModifier,
ReplaceSnippet, ModifyBaseType) cost about 14.1k, matching the 14.0k reported by the client.

Measured 2026-10-03 by starting a throwaway server (`--mode claude`, no solution loaded), calling
`tools/list`, and measuring the emitted JSON. Calibration: 2.5 chars per token reproduces the
client's per-tool figures within about 3%. Total: 97,261 chars.

| Source | Chars | Share |
| --- | --- | --- |
| Tool-level descriptions | 10.7k | 11% |
| Parameter descriptions | 38.2k | 39% |
| Shared boilerplate params (`reason` x67, `returnDiff` x30, `contextSnippet`, `lineBefore`, `lineAfter`, `dryRun`, `autoStage`) | 24.5k | 25% |
| `x-consumes-tag` / `x-produces-tag` | 7.2k | 7% |
| `"default":null` and `["T","null"]` unions | 4.0k | 4% |
| Four near-identical batch-edit paragraphs (`ToolParams.cs:93-127`) | 4.2k | 4% |

(Rows overlap: boilerplate params include their descriptions.)

Findings behind the numbers:

- The x-tags have no consumer. The only reference to the string is the injector in
  `McpToolSchemaPatcher.cs` (`ApplyConsumesTags`, line 227). `proposal_datatag_chaining_contract.md`
  says the attributes were originally for human organisation.
- Prose that is plainly removable is only about 4.4k of the nine tools' 20.3k description chars
  (alias bookkeeping 1.0k, refusal/error narration 1.6k, maintainer rationale 0.5k, "Not used for"
  complements 0.3k, restated defaults 0.7k, enum copies 0.1k). Wording alone cannot reach 50%.
- Usage, from 16 journal call logs (about 2,100 MCP calls): 35 of 68 tools were never called, and
  ConstructorParameter, ModifyBaseType, ModifyEnum, ChangeSignature and others cost thousands of
  chars each. Caveat: these are dog-fooding sessions by a strong model on this repo only.
- `Member.operation` has no description; its per-operation requirements are scattered over 24
  params as "Required for X / Not used for Y" (about 2.3k chars).
- `McpServerStatus.allDeclaredTools` is emitted on every status call (about 130 entries), contains
  duplicate names (for example `Build` under both `WorkspaceBuildTestTools` and `WorkspaceTools`),
  and lists `McpServerStatus` itself as `activeForThisMode: false` although it is callable.

## Proposal

Steps are cumulative. "Measured" rows were simulated on the real emitted JSON; "estimate" rows were
not measured.

| Step | Nine tools | Whole surface | Basis |
| --- | --- | --- | --- |
| 1. Stop emitting x-tags and `"default":null` (gate tags behind a startup flag, off by default) | -10% | -10% | measured |
| 2. Hide `autoStage`, `returnDiff`, `validateOnApply`, `lineBefore`, `lineAfter` and alias params from the schema, collapse null unions | -25% | -23% | measured |
| 3. Description diet: one per-operation matrix in each `operation` description, delete the removable categories above, one shared batch sentence, shorter shared constants | about -42% | about -33% | estimate |
| 4a. Merge `ConstructorParameter` into `MethodSignature`; merge `ModifyModifier`, `ChangeAccessibility`, `ModifyAttribute`, `ModifyBaseType` into one declaration tool | about -52% | about -40% | estimate |
| 4b. Lean Claude mode (below) | about -65% | up to -79% with 1-3 | estimate |

### Decided

- **Step 1** is zero information loss. Implement as a flag following the startup-arg wiring pattern
  (`LlmOptions.cs`); default off so the DataTag proposal can still opt in.
- **Step 2 is per profile.** A hidden param is unknown to a model unless a description clause or an
  error message names it, and grammar-constrained clients (LM Studio) build the decoder from the
  schema, so they physically cannot emit a hidden param. Therefore:
  - alias params (`files`, `target`, `commitHash`, `attribute`): hide everywhere (canonical param
    stays visible);
  - `autoStage`, `returnDiff`, `validateOnApply`, `lineBefore`, `lineAfter`: hide only in the
    `claude` profile; weak/local profiles keep them;
  - `dryRun` always returns the diff, which makes `returnDiff` redundant;
  - the ambiguous-target error must name `lineBefore`/`lineAfter`.
  - Mechanism: a post-build schema patch like `ApplyConsumesTags` / `ApplyReplaceSnippetLimits`
    (`McpToolSchemaPatcher.cs:227`, `:291`) that removes properties marked hidden; the C# method keeps
    the parameters, so runtime binding is unchanged.
- **Step 3 keep list:** per-operation required params (once, in `operation`), non-guessable formats
  (`callSiteFixups` keys, CSV-or-array `paths`, `position: "after:X"`), hard limits (ReplaceSnippet
  300 lines / 4000 chars), the `reason` rule (an empty description was tried and models omitted the
  field; see the comment at `ToolParams.cs:90`), one batch-semantics sentence (edits resolve against
  the original file, apply atomically), and the "use `nullDefault`, not the string null" clause.
- **Step 4b proposed Claude-mode toolsets** (sizes pre-diet):
  - Core, always on, 25 tools, 35.3k chars: LoadSolution, ReadFile, GetFileOutline, GetMethodSource,
    GetLargeResult, Search, FindReferences, InspectSymbol, LocateSymbol, GetDiagnostics, Build,
    RunTest, Git, ReplaceSnippet, Member, UsingDirective, RenameSymbol, WriteFile, CreateFile,
    DeleteFile, UndoLastApply, McpServerControl, McpServerStatus, AcknowledgeExternalFileChanges,
    ListExternalDiskChanges.
  - Declarations (on demand): merged declaration tool, merged parameter tool, ModifyEnum,
    ChangeSignature, SyncTypeAndFilename.
  - Move/Extract (on demand): MoveMember, MoveType, MoveAllTypesToFiles, Extract*, Inline*,
    Introduce*, WrapRange, InvertAssignments, ConvertAnonymousToNamed, SyncInterface, SummaryComment,
    SafeDeleteUnusedSymbol, PreviewRenameImpact, ApplyDiff, ApplyUnifiedDiff.
  - Project/Admin (on demand): CreateProject, SplitProjectByFolder, ListSolutionItems,
    ListWorkspaceSolutions, ListProjectFrameworkTargets, Features, ProjectDoc, GetWorkspaceHealth,
    GetOperationDetail, RetryFailedChanges, IsSessionHalted, GetTypeInfo, QuerySymbolRelationships,
    GetBestInsertionPoint.
  - Stage 1 (no new code): a static `claude` mode via the existing mode registry and
    include/exclude-tools options. Stage 2: dynamic `McpToolsetControl(toolSet, on|off)`.

### Gating policy and status-tool fixes (being built first)

- CLAUDE.md states that tools are intentionally gated to reduce schema tokens; check the status
  tool's inactive list before concluding a tool does not exist; if a disabled tool would make the
  task materially easier, stop and report (name, how to enable) instead of working around it. A gated
  tool is a configuration decision, not a tool failure, so it does not get a blocker doc.
- `McpServerStatus` fixes: make `allDeclaredTools` opt-in/filterable, dedupe by tool name reporting
  the class that backs the active tool, add an `enabledBy` hint per tool, correct the wrong
  `McpServerStatus` entry. No separate `ListAllPossibleTools` tool: `allDeclaredTools` already
  exists (`project_mcpserverstatus_alldeclaredtools_added`, commit 7116d434).

## Alternatives considered, not pursued

- **One MCP server per toolset, started by a `McpToolsetControl`.** Rejected. Claude Code owns server
  lifecycle: the documented controls are the `/mcp` panel toggle and `claude mcp add/remove`, and the
  docs do not say `.mcp.json` edits hot-reload, so a tool cannot make the client connect a new
  server. Worse, each process holds its own Roslyn workspace, drift detector, undo ledger and halt
  latch: N servers means N solution loads, and an edit through one looks like an external write to
  the others, tripping `SessionHalted`.
- **Dynamic tool list inside one process via `tools/list_changed`.** Kept as stage 2 of step 4b.
  Claude Code documents that it re-fetches the list on this notification in interactive sessions
  (and refreshes the tool list in `-p`/Agent SDK mode).
- **Batch-only edit tools (drop singular params).** Would roughly halve ModifyAttribute,
  ModifyModifier and ModifyBaseType, but array-of-object shapes are riskier for small models.
- **Drop `reason` on read-only tools.** Saves about 2.7k chars; it is a product decision about
  journaling and call-log value, not a token fix. Not proposed.
- **Collapse `["T","null"]` to `"T"`.** Included in step 2 as measured, but only after testing that
  weak models sending explicit nulls still work.

## Open questions

- Does the SDK's tool collection raise `tools/list_changed` by itself when tools are added or
  removed at runtime? **Verified (SDK 2.2.0, 2026-10-03): yes, with one protocol caveat.**
  `McpServerImpl` subscribes `ToolCollection.Changed` to `SendListChangedNotificationAsync` for
  stateful transports (stdio and stream transports; not stateless Streamable HTTP) and advertises
  `tools.listChanged`. tools/list and tools/call read the live collection, so a tool enabled at
  runtime is callable even if the client never re-lists. Caveat: a client that negotiated protocol
  2026-07-28 or later is notified only over a `subscriptions/listen` stream it opened (including
  `toolsListChanged`); clients on the initialize-handshake protocols get the broadcast. Whether
  Claude Code subscribes is not verified; if it does not, enabling a set works (callable by name,
  and a re-list shows the tools) but the list does not refresh by itself. Both paths are covered by
  `McpToolsetControlTests`.
- Do LM Studio and other local clients honour `list_changed`? If not, they stay on static modes.
- Is the x-tag emission needed by any external consumer? In-repo search found none.
- Dog-fooding: CLAUDE.md names Member, MethodSignature, ModifyModifier, ApplyDiff and RenameSymbol as
  the Edit/Write replacements, so sessions on this repo should enable Declarations and Move/Extract;
  the lean list is the default for other repos. Confirm the intended default.
- The usage counts cover about 16 sessions of one workflow; "never called" means "not needed by
  default", not "unused".

## Cost / risk

- Step 2 and 4a change what weak models see; run ModelEval before and after, weak profile included.
- 4a renames tools: needs registry entries and DI blocks (CLAUDE.md "Adding a new MCP tool") and
  updates to prompts, docs and tests that name the old tools.
- Dynamic toolsets add a meta tool and a round trip, and a model must know when to enable a set;
  the toolset names go in the `McpToolsetControl` description.
- Schema-diet changes are invisible to a running server: restart it after each change.

## How to use claude-lean and McpToolsetControl

- Start the server with `--mode claude-lean` (or the equivalent `ROSLYNSENTINEL_*` environment
  variable). It exposes the 26 core tools only; no other mode includes `McpToolsetControl`, and
  `--include-tools` cannot add it elsewhere.
- Call `McpToolsetControl(reason, toolSet: declarations | moveExtract | projectAdmin, enabled: true | false)`.
  The result lists the tools added or removed, any that are unavailable on this server flavor (the
  Basic flavor has no `AdvancedRefactoringTools`), and the now-active tool list. Repeating a call is
  a no-op. Sets: `declarations` (9 tools), `moveExtract` (19), `projectAdmin` (14).
- Enabled tools are patched exactly like startup tools and the server sends `tools/list_changed`
  (subject to the protocol caveat above). They are callable by name even if the client has not
  refreshed its list. An existing tool is never replaced; a name already present is reported as unavailable.
- `McpServerStatus(toolListing: inactive)` shows `enabledBy: McpToolsetControl(toolSet: X, enabled: true)`
  for every tool in a set, and reports enabled ones as active.
- Dog-fooding sessions on this repo should enable `declarations` and `moveExtract`.
- State lives in one `ToolsetService` singleton per server process. On a stateful HTTP server the
  collection is shared across sessions, so a toggle affects all of them.

## Related

- `proposal_datatag_chaining_contract.md` (owner of the x-tag emission decision)
- `issue_member_containername_conditional_required_gap.md` (the Member requirements gap)
- Memories: `project_mcpserverstatus_alldeclaredtools_added`, `project_startup_arg_wiring_pattern`,
  `project_server_flavors_and_build_configs`
