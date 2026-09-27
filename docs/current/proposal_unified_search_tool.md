# A unified `Search` MCP tool with a flat `mode` selector

## Motivation

Weak/small models default to `SearchSolutionText` whenever they want to find something in the
solution, because it is the only registered tool with "Search" in its name
(`RoslynSentinel.Server.Basic/WorkspaceTools.cs:614-627`). This happens even when what the model
actually wants is a symbol declaration lookup (`LocateSymbol`), a references/callers query
(`FindReferences`), or a listing of declarations by kind (`ListAll`). Today, picking the right tool
requires the model to already know the domain split -- text vs. symbol vs. references vs.
declaration-kind listing -- before it can even choose which tool name to reach for. Per this repo's
failure doctrine (`CLAUDE.md`, "Failure doctrine: the environment is responsible"), that
domain-split knowledge should not be a precondition the model is expected to already have; the
environment should make the right choice reachable from the one verb a model already guesses at
("search").

This proposal collapses that decision into one tool, `Search`, with a `mode` parameter that is a
full flat enum -- not a two-tier "pick a family, then pick a sub-kind" schema -- so that a model
whose only instinct is "call Search" is immediately shown every concrete option (`text`, `symbol`,
`references`, `all`, `namespace`, `class`, `interface`, `method`, `property`, `struct`, `record`,
`enum`, `"enum member"`, `constructor`, `field`) in one schema-visible list, and the schema
description for `mode` is what does the work of routing the model to the right choice, rather than
the model needing to already know which existing tool covers which case.

**Important existing-code correction the design below depends on:** there are currently two
`SearchSolutionText`/`ListAll` implementations in the codebase --
`RoslynSentinel.Basic/WorkspaceReadNavigationImpl.cs` (backing the live, MCP-registered tools in
`RoslynSentinel.Server.Basic/WorkspaceTools.cs:604-627`) and a second copy in
`RoslynSentinel.Server.Basic/WorkspaceReadNavigationTools.cs`. That second file's own header
comment (lines 8-26) states plainly that its `[McpServerTool]`-attributed methods are **not
currently reachable over MCP** -- they exist ahead of an in-progress DI split
(`docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md`, Decision 4) that has not
yet wired the fine-grained mode strings needed to register that class. Anyone designing against
this tool family must dispatch to `WorkspaceReadNavigationImpl` (the live implementation), not to
`WorkspaceReadNavigationTools` (the dormant duplicate) -- confusing the two would silently produce
a `Search` tool that calls dead code paths whose behavior can drift from the real one, which is
exactly the dispatch-table-drift failure mode this repo has hit before (see
[[project_member_remove_dispatch_table_drift_pattern]] in memory, and
`docs/current/proposal_universal_symbol_resolver.md`'s Motivation section for the fullest existing
writeup of that pattern). This document is written entirely against the live path.

## Decisions (settled, not open questions)

The shape below reflects choices already talked through and decided. Where an alternative was
seriously considered and rejected, that is recorded in "Alternatives / scope explicitly rejected"
rather than left as an open question.

### 1. No Structured Content unification

`Search`'s output shape is whatever the dispatched-to backing method already returns --
`SentinelCallToolResult<object>` in every case (see dispatch table below), carrying each backing
method's own native success/result shape. `Search` does not normalize `text`/`symbol`/
`references`/declaration-listing results into one common envelope beyond what
`SentinelCallToolResult<object>` already provides today. A caller reading `Search(mode: text)`'s
result sees the same shape it would get from calling `SearchSolutionText` directly today; a caller
reading `Search(mode: class)`'s result sees the same shape `ListAll(kind: class)` already returns.
This is deliberately narrower than `docs/current/proposal_structuredcontent_rollout.md`'s scope --
that proposal is about a different, orthogonal question (whether structured content should be
unified across the whole tool surface) and is not a prerequisite for or blocked by this one.

### 2. Full flat `mode` enum, one axis, no umbrella values

`mode` is a single flat enum with these values: `text`, `symbol`, `references`, `all`, `namespace`,
`class`, `interface`, `method`, `property`, `struct`, `record`, `enum`, `"enum member"`,
`constructor`, `field`.

This merges what was, earlier in design discussion, going to be two separate enums -- a small
custom set (`text`/`symbol`/`references`/`all`) layered in front of the existing `ListAllKind`
enum (`RoslynSentinel.Common/ToolEnums.cs:144-149`: `all, namespace, class, interface, method,
property, struct, record, enum, "enum member" (enumMember), constructor, field`) -- into one flat
list on `Search` itself. A model calling `Search` sees every mode in one schema enum, not a
custom-set value that then requires a second, kind-specific parameter to narrow further.

Two rejected shapes worth naming explicitly:

- **No `member` umbrella value.** A candidate `member` mode (meaning "any member: method, property,
  field, constructor, or enum member") was considered and rejected. `ListAllKind` itself has no such
  umbrella value -- only the specific kinds -- and inventing one on `Search` that `ListAll` doesn't
  have would mean `Search` either silently fans out to several `ListAll` calls internally (adding
  behavior `ListAll` itself doesn't have) or fails to map cleanly onto the existing
  `kind` parameter it passes through to. The model should pick the specific kind it wants, the same
  way it already has to for `ListAll` directly.
- **`all` is not a fan-out across text and declarations.** `all` here is exactly `ListAllKind.all`
  passed straight through -- "every declared symbol in the loaded solution" -- not "search both
  text and every declaration kind and merge the results." Conflating `all` with `text` would mean
  one mode value doing two structurally different searches (a text scan and a declaration listing)
  and merging incompatible result shapes, which section 1 above already rules out doing anywhere in
  this tool. Keeping `all` and `text` as separate, single-purpose axis values avoids that conflation
  entirely; `ListAll`'s own existing `all` already means the sensible, useful thing on its own axis,
  and `Search` just inherits it unchanged.

### 3. Dispatch table

| `mode` value | Backing method | File:line | Params passed through | Notes |
| --- | --- | --- | --- | --- |
| `text` | `WorkspaceReadNavigationImpl.SearchSolutionText` | `RoslynSentinel.Basic/WorkspaceReadNavigationImpl.cs:400-` | `pattern` <- `query`, `fileGlob`, `maxResults` | Live tool wrapper today: `RoslynSentinel.Server.Basic/WorkspaceTools.cs:614-627`. Matches both literal substring and regex in one pass (existing behavior, unchanged). |
| `symbol` | `SymbolNavigationImpl.LocateSymbol` | `RoslynSentinel.Basic/SymbolNavigationImpl.cs:24-` | `symbolName` <- `query`, plus `symbolKind`, `containingType`, `containingNamespace`, `projectName`, `filePath`, `exactMatch` | Live tool wrapper: `RoslynSentinel.Server.Basic/SymbolNavigationTools.cs:19-37`. Only matches declared symbols, not arbitrary text -- same distinction the existing `LocateSymbol` description already draws against `SearchSolutionText`. |
| `references` | `SymbolRelationshipImpl.FindReferences` | `RoslynSentinel.Basic/SymbolRelationshipImpl.cs:219-` | `symbolName` <- `query`, plus `kind` (required), `filePath`, `contextSnippet` (required at runtime, see section 4), `lineBefore`, `lineAfter` | Live tool wrapper: `RoslynSentinel.Server.Basic/SymbolRelationshipTools.cs:63-77`. Real dispatch, not a guidance stub -- `Search(mode: references)` actually calls `FindReferences`'s implementation. |
| `all` | `WorkspaceReadNavigationImpl.ListAll` | `RoslynSentinel.Basic/WorkspaceReadNavigationImpl.cs:330-398` | `kind: ListAllKind.all`, `projectName` | Live tool wrapper: `RoslynSentinel.Server.Basic/WorkspaceTools.cs:604-613`. `query` is unused/ignored. Means "every declared symbol," not a text+declaration merge (see section 2). |
| `namespace` | same as `all` | same | `kind: ListAllKind.namespace`, `projectName` | `query` unused/ignored. |
| `class` | same as `all` | same | `kind: ListAllKind.class`, `projectName` | `query` unused/ignored. |
| `interface` | same as `all` | same | `kind: ListAllKind.interface`, `projectName` | `query` unused/ignored. |
| `method` | same as `all` | same | `kind: ListAllKind.method`, `projectName` | `query` unused/ignored. |
| `property` | same as `all` | same | `kind: ListAllKind.property`, `projectName` | `query` unused/ignored. |
| `struct` | same as `all` | same | `kind: ListAllKind.struct`, `projectName` | `query` unused/ignored. |
| `record` | same as `all` | same | `kind: ListAllKind.record`, `projectName` | `query` unused/ignored. |
| `enum` | same as `all` | same | `kind: ListAllKind.enum`, `projectName` | `query` unused/ignored. |
| `"enum member"` | same as `all` | same | `kind: ListAllKind.enumMember`, `projectName` | Wire value is the string `"enum member"` (`[JsonStringEnumMemberName("enum member")]`, `RoslynSentinel.Common/ToolEnums.cs:147`); `query` unused/ignored. |
| `constructor` | same as `all` | same | `kind: ListAllKind.constructor`, `projectName` | `query` unused/ignored. |
| `field` | same as `all` | same | `kind: ListAllKind.field`, `projectName` | `query` unused/ignored. |

For the `namespace`/`class`/.../`field` rows, `Search`'s `mode` value is passed straight through as
the existing `ListAllKind` enum value it already matches by name -- no translation table, no
re-encoding.

### 4. `query` parameter -- optional, deliberately not conditionally required

`query` is the search pattern for `mode: text` and the symbol name for `mode: symbol` /
`mode: references`. It is ignored by every `ListAll`-backed mode (`all` and every declaration-kind
value). It is left **optional** at the schema level rather than made conditionally required per
mode.

This is an explicit "try it, adjust if testing proves it's a footgun" choice, not a settled
non-issue -- tightening it later (e.g. rejecting a `text`/`symbol`/`references` call with no
`query`) is left open pending observed model behavior, consistent with this repo's general
preference for mandatory params over optional ones that just relocate a failure
(`feedback_prefer_mandatory_params_to_close_footgun_roundtrips` in memory) -- that preference is
noted here as the reason this is flagged for revisiting, not treated as already applied.

### 5. `contextSnippet` -- optional in the schema, required at runtime for `mode: references`

`FindReferences`'s own implementation (`SymbolRelationshipImpl.FindReferences`,
`RoslynSentinel.Basic/SymbolRelationshipImpl.cs:219-227`) takes `contextSnippet` as an optional
`string?` parameter, but its live tool wrapper marks it `[Consumes(DataTag.ContextSnippet,
required: true)]` (`RoslynSentinel.Server.Basic/SymbolRelationshipTools.cs:73`) -- i.e. the existing
`FindReferences` tool itself already carries this exact schema-optional/attribute-required split,
and `Search(mode: references)` inherits the same real constraint rather than inventing a new one.

This repo's MCP schema emission has no mechanism today for expressing "required only when another
parameter has a specific value" -- confirmed no precedent exists anywhere in the current tool
surface. Rather than inventing one for this proposal alone, `Search` validates this at runtime:
if `mode == references` and `contextSnippet` is null or empty, return a guidance `ResultError`
naming `contextSnippet` specifically as the missing parameter (per the failure doctrine in
`CLAUDE.md`: an error must name the exact offending parameter, not just reject the call generically).
`contextSnippet`'s own `<summary>` doc on the `Search` tool must literally state "Required for mode:
references" so the constraint is visible in the schema description even though the schema itself
cannot enforce it structurally.

`references` mode also carries through `FindReferences`'s other parameters unchanged: `kind:
FindReferencesKind` (`callers`/`implementations`/`all`, required by `FindReferences` itself and
therefore required on `Search` whenever `mode: references` is used), plus optional `filePath`,
`lineBefore`, `lineAfter`.

See `docs/current/issue_conditional_required_param_audit_followup.md` and
`docs/current/issue_member_containername_conditional_required_gap.md` for this repo's other known
instances of the same schema-emission gap; this proposal does not attempt to close that gap, only
to apply the same runtime-validation workaround those cases already use.

### 6. `SearchSolutionText` is deleted as a public MCP tool -- hard cutover, not a deprecated alias

The live `SearchSolutionText` tool (`RoslynSentinel.Server.Basic/WorkspaceTools.cs:614-627`) is
removed from the registered tool surface once `Search(mode: text)` exists. Its backing
implementation, `WorkspaceReadNavigationImpl.SearchSolutionText`
(`RoslynSentinel.Basic/WorkspaceReadNavigationImpl.cs:400-`), is reused unchanged as the call
`Search` dispatches to for `mode: text` -- only the MCP-facing tool name is retired, not the
implementation.

This is deliberately a hard cutover rather than keeping `SearchSolutionText` registered as a
deprecated alias alongside `Search`. Two names resolving to the same behavior is exactly the
dispatch-table-drift shape this repo has already been bitten by more than once (see
[[project_member_remove_dispatch_table_drift_pattern]], a four-incident pattern of independently-
maintained "same question, different code path" logic silently disagreeing with itself over time,
and this document's own note above about the dormant `WorkspaceReadNavigationTools.cs` duplicate --
a second concrete instance of the same risk already sitting in this exact area of the codebase). A
hard cutover means there is exactly one call site (`Search`) to keep correct for solution-text
search, instead of two names that could independently drift.

`FindReferences` (`RoslynSentinel.Server.Basic/SymbolRelationshipTools.cs:63-77`) is explicitly
**not** deleted or deprecated by this proposal -- see section 7.

### 7. `FindReferences` remains a separate standalone tool

Unlike `SearchSolutionText`, `FindReferences` is not retired. It is already a well-formed,
symbol-first tool for a caller who already knows exactly what they want (a specific symbol's
callers/implementations). `Search(mode: references)` is an additional entry point for the model
that reaches for "Search" by reflex without first deciding it wants a references query
specifically -- not a replacement for the direct tool. Both remain registered and both dispatch to
the same `SymbolRelationshipImpl.FindReferences` implementation, so there is one implementation
behind two legitimate entry points here (unlike `SearchSolutionText`, where the second entry point
served no purpose `Search` itself doesn't already cover).

## Other tools/engines surveyed and ruled out as mode backers

- **`QuerySymbolRelationships`** (`RoslynSentinel.Server.Basic/SymbolRelationshipTools.cs:19-32`,
  backed by `FindUsagesSearchKind` covering `implementorsOf`/`attributeUsages`/`objectCreations`/
  `extensionsFor`/`typesWithAttribute`/`methodsByReturnType`) -- a different axis entirely
  (type-relationship facts, not a declaration listing or a text/symbol lookup). Left as its own
  tool; folding it into `Search`'s `mode` enum would mix "what does X do/relate to" queries into a
  tool whose other modes are all "find/list a thing," which is a different question shape.
- **`ListSolutionItems`** (live wrapper `RoslynSentinel.Server.Basic/WorkspaceProjectManagementTools.cs:19-31`;
  note the same live/dormant-duplicate split as `SearchSolutionText` exists here too --
  `RoslynSentinel.Server.Basic/WorkspaceTools.cs:90-` also declares a `ListSolutionItems`, so the
  same "verify against the actually-registered wrapper" caution from this proposal's opening section
  applies if this tool is revisited later) -- covers `projects`/`files`/`dependencies`/
  `solutionItems`, i.e. project/solution structural membership, not a symbol or text search. Left
  out of `Search`'s scope.
- **`GetFileOutline`** (`RoslynSentinel.Server.Basic/WorkspaceTools.cs:592-602`) -- single-file only,
  not solution-wide, so it doesn't fit the "search the solution" framing the other modes share.
  Noted as a possible future narrower variant (e.g. a `Search`-adjacent single-file mode) but not
  designed here.
- **`DiscoveryEngine`** and **`SemanticSearchEngine`** -- lower-level engines that are not currently
  exposed as MCP tools in their own right. Not folded into this proposal's scope; flagged as worth a
  future look (they may already contain logic close to what a broader `Search` could use) but out of
  scope for this design.

## Cost / risk

- **New tool surface, but the removal offsets it.** `Search` is a new `[McpServerTool]`-attributed
  method (natural home: alongside `ListAll`/`SearchSolutionText`'s existing live wrappers in
  `RoslynSentinel.Server.Basic/WorkspaceTools.cs`, or a new dedicated `Tools`/`Impl` pair per the
  in-progress DI split convention -- either is consistent with current structure). Net tool count
  goes from `{SearchSolutionText, ListAll, LocateSymbol, FindReferences}` (4, all still needed for
  their backing behavior) to `{Search, ListAll, LocateSymbol, FindReferences}` (still 4 registered
  names -- `SearchSolutionText` is retired, `Search` is added), so registered-tool-count growth is
  effectively zero; the schema-complexity cost is concentrated entirely in `Search`'s own `mode`
  enum and its per-mode parameter set.
- **What breaks:** any existing caller (human, script, or another agent session) that calls
  `SearchSolutionText` by name breaks once it is deregistered. There is no compatibility shim by
  design (section 6) -- this is the accepted cost of the hard-cutover decision, not an oversight.
- **What this doesn't touch:** `LocateSymbol`, `FindReferences`, and `ListAll` all keep their
  existing registered names, signatures, and behavior unchanged; `Search` is purely additive on top
  of them plus one deletion (`SearchSolutionText`).
- **What it makes harder:** a single tool with 15 `mode` values and a param set that's a union of
  four different backing methods' parameters is a wider single schema than any other tool in this
  family today; if a future 16th mode is proposed, it needs the same "does the flat enum still make
  sense, or does this belong in a different tool entirely" scrutiny this proposal itself applied to
  `QuerySymbolRelationships`/`ListSolutionItems`/`GetFileOutline` above, rather than being added by
  default just because `Search` already exists.

## Status

Design proposal only -- talked through and decided as described above, not yet implemented, not yet
scheduled. Implementation is a separate future task: add the `Search` tool per the dispatch table,
retire the registered `SearchSolutionText` tool (keeping its backing `WorkspaceReadNavigationImpl`
method as the reused implementation for `mode: text`), and update any prompt/tool-description text
that currently points models at `SearchSolutionText` by name.

## Related

- [[project_member_remove_dispatch_table_drift_pattern]] -- the precedent this proposal's hard-
  cutover decision (section 6) is explicitly avoiding repeating.
- `docs/current/proposal_universal_symbol_resolver.md` -- a different, independent unification
  effort (five divergent symbol-*lookup* helpers at the `SymbolNavigationEngine` layer) that shares
  this proposal's general "stop maintaining N divergent paths to the same question" motivation, but
  at a lower layer and with no dependency in either direction.
- `docs/current/proposal_structuredcontent_rollout.md` -- a differently-scoped, independent proposal
  about structured-content unification across the tool surface; explicitly not what section 1 above
  is doing.
- `docs/current/issue_conditional_required_param_audit_followup.md`,
  `docs/current/issue_member_containername_conditional_required_gap.md` -- other known instances of
  the schema-cannot-express-conditional-required gap this proposal's `contextSnippet` handling
  (section 5) works around the same way.
- `docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md` -- the in-progress DI split
  whose Decision 4 explains why `WorkspaceReadNavigationTools.cs` currently holds dormant,
  unregistered duplicates of the methods this proposal dispatches to.
