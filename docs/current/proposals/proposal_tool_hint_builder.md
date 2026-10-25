# Build server hints that name a tool through a validated ToolHint helper, enforced by a ratchet

**Status:** DRAFT 2026-10-25. Approved in principle by the user; slices not started.

## Motivation

Every server-emitted hint that names an MCP tool omits at least one required parameter. All 137
`[McpServerTool]` methods require `reason` (`ToolCallReason`, min 10 chars including a space), and no
hint includes it. Many hints also drop a second required parameter: `ReadFile`/`GetFileOutline` without
`filePath`, `Search(mode: symbol)` without `query`, `LoadSolution` without `solutionPath`, a `Member(add)`
shorthand, `WriteFile(operation=...)` written with `=`. A model that follows the hint verbatim fails its
first call. Evidence: a journal entry where `GetLargeResult` was called without `reason` after following
a server hint.

Scale: about 150 hint sites in about 25 files. By tool named:

| Tool named | Sites |
| --- | --- |
| GetFileOutline | ~25 |
| Search | ~25 |
| LoadSolution | ~25 (14 files) |
| ReadFile | ~10 |
| Member | ~12 |
| WriteFile / CreateFile | few |
| ModifyEnum | 4 |
| ProjectDoc | 5 |
| GitRemote / Git | 3 |
| MoveMember | 1 |
| Tools that only lack `reason` (ListAll, Build, RunTest, ExternalFileDrift, McpServerControl, McpToolsetControl, UndoLastApply, GetOperationDetail) | ~30 |

Engine "Next: <Tool> ..." messages account for about 50 of these, in Engines.Basic and Engines.Advanced.

Already shipped for one family: `GetLargeResult` hints now go through `RoslynSentinel.Common/LargeResultHints.cs`
(commit ae34a879), with a reflection guard test
(`RoslynSentinel.Tests.Tools.Basic/LargeResultHintsTests.cs`) that checks required parameters. This
proposal generalises that pattern. The failure is an environment defect (the hint text is the
environment's instruction to the model), so the fix is in how hints are produced, not in the model.

## Proposal

### Decided design: a build-time helper `ToolHint` in RoslynSentinel.Common (not a filter)

- `ToolHint.Call(tool, (param, value)...)` returns a complete call string.
- `ToolHint.Next(tool, message, (param, value)...)` returns `"Next: <call> <message>"`.
- The helper validates tool name, parameter names and enum values against a `ToolSignatureCatalog`,
  quotes values by parameter type, and adds a typed placeholder for every remaining required parameter.
  `reason` gets `<why you call this>`, which passes `ToolCallReason` validation (>= 10 chars, contains a space).
- It never throws at runtime: a bad hint must not turn a tool failure into a crash. Unknown tool falls
  back to the bare tool name; an unknown parameter pair is dropped.
- Placement: Common already references the MCP types and Engines see Common, so every emitting layer
  (Engines, Tools, Server) can call it.

### ToolSignatureCatalog (Common)

- Singleton fed from the live `McpServerOptions.ToolCollection` (`ProtocolTool.InputSchema`: properties,
  required, enum, type). Invalidated on `ToolCollection.Changed`, because claude-lean adds and removes
  tools at runtime (`ToolsetService` / `McpToolSchemaPatcher.CreateSentinelTool`).
- Falls back to reflection discovery (`McpToolSchemaPatcher.DiscoverAllDeclaredTools`) when no server is
  running (unit tests).
- Exposes `IsActive(tool)`. Inactive tools are still rendered; gating stays a separate concern (see
  `WriteToolAdviceHelper.IsExposed`).
- Conditional required parameters (`Search.query`, `Member.memberName`/`containerName`/`newMemberSource`,
  `Git.message`) are not in the schema `required` array. Each hint site names the ones it knows. This is
  the main reason a builder beats a filter.

### Ratchet (keeps future hand-written hints out)

- A syntax-tree scan test over production projects (skipping tests, `ToolHint` itself and `[Description]`
  attributes) finds string literals and interpolated parts that use an active tool name as a call:
  `Name(` or a trigger phrase (Call / Use / Next: / run / retry / via / with) followed by the name. Tool
  names come from `[McpServerTool(Name = ...)]` by reflection.
- A hit outside the baseline file fails the build with "use ToolHint.Call/Next". A baseline entry with no
  matching site also fails, so the baseline only shrinks; each conversion slice removes its entries in the
  same commit.
- Baseline: a text file in the style of `docs/known-failing-tests.*.txt`, one line per site group,
  `file | enclosing member | tool | count`, no line numbers (they churn). Seeded with today's ~150 sites.
- Escape hatch for intentional prose mentions: a `// hint-ok: <reason>` comment.
- A second test checks every `ToolHint.Call` literal (tool, param, enum value) against the catalog, so a
  typo fails CI instead of production.

### Decisions (decided 2026-10-25, revisit if a model pastes the placeholder verbatim)

1. Live catalog with reflection fallback (not reflection only).
2. Silent runtime fallback plus the CI scan; no Debug-build throw.
3. Placeholder text `<why you call this>`.

The user answered "Continue" to the recommended defaults for these three.

### Slices (sequential; Haiku implementer; <= 3 files each)

Foundation:
- F1: `ToolSignatureCatalog` plus tests.
- F2: `ToolHint` plus tests; rebuild `LargeResultHints` on it.
- F3: ratchet test plus seeded baseline.
- F4: startup wiring of the live catalog in `Server.Basic`.

Conversion (each removes its baseline entries in the same commit):
- C1: `LoadSolution` (~25 sites, 14 files).
- C2: engine "Next:" family (GetFileOutline / ReadFile / Search / Build).
- C3: Member / WriteFile / CreateFile / ModifyEnum.
- C4: Git / GitRemote / ProjectDoc / MoveMember.
- C5: tools that only lack `reason`.
- C6: hand-fix the static `[Description]` texts (see out of scope).

Each slice updates the exact-text tests it breaks: `OrientationBreakerFilterTests` (126/224),
`McpToolsetControlTests` (514), `ToolParameterAliasTests` (608), `GenericContainerNameTests`
(520/620/642), `AdminToolsTests` (215), `GetMethodSourceTests` (139), `SolutionNotLoadedMessageTests` (45),
`WriteToolAdviceHelperTests` (42/64). Test harness templates: `RoslynSentinel.Tests/WirePathRewriterTests.cs`
(pure unit) and `RoslynSentinel.Tests.Server/ToolCallEchoFilterTests.cs` (pipeline).

### Out of scope

- Static `[Description]` attributes that name tools (~10 sites): fix by hand in C6, no expansion.
  `ClaudeLeanSchemaBudgetTests` caps the lean schema at 29,000 chars total / 4,500 per tool, so a full
  call with placeholders cannot be inserted there.
- Gating hints by the active tool surface (the catalog exposes `IsActive`, but acting on it is separate).

## Alternatives considered, not pursued

**Runtime filter that rewrites hint text in results** (the `WirePathRewriter` pattern, registered second
in the `WithRequestFilters` chain in `ServiceRegistrationExtensionsBasic.cs`, ~line 104; the rewriter
itself is at `ServiceRegistrationExtensionsBasic.cs:745`). Lost because:
- It cannot know conditional required parameters, so it could only add `reason`; the other omissions stay.
- False-positive hazards in free text: the server analyzes C#, so `Build`, `Search`, `Member`, `Git` and
  `ReadFile` appear as identifiers and prose in results, in echoed user text, and in "do not call X" sentences.
- It changes exact-text of results that tests assert on.
- The Offload threshold is measured before expansion, so rewritten text could push a result over the limit
  after the size decision was made.

Its one advantage is catching future hand-written hints; the ratchet replaces that.

**Per-family helpers like `LargeResultHints` for each tool.** Works, but repeats the schema knowledge in
each helper and has no way to catch a new hand-written hint. Not chosen; `ToolHint` is the generalisation.

## Open questions

- Whether a model pastes `<why you call this>` verbatim instead of writing a real reason (the stated
  revisit trigger for decision 3). Unmeasured.
- Ratchet detection precision: the trigger-phrase heuristic will have false positives and negatives on
  real sources; the first run against the seeded baseline will show how many `// hint-ok:` escapes it needs.
- Whether the live catalog and the reflection fallback can disagree for tools in toolsets not loaded in a
  given mode (affects only `IsActive` and enum lists; hypothesis, not traced).

## Cost / risk

- Touches about 25 production files over the conversion slices plus ~10 exact-text test files; Common
  gains two types and the catalog gains a startup dependency on `McpServerOptions`.
- Hints get longer (placeholders and quoted values), which costs result tokens on every hint-bearing
  response. Not measured.
- Common now carries tool-name string literals in call sites; renaming a tool is caught by the second
  ratchet test rather than the compiler.
- Silent runtime fallback means a typo that slips past CI degrades to a bare tool name instead of failing loudly.
- Makes harder: writing a quick ad hoc hint string. That is intended.

## Related

- `RoslynSentinel.Common/LargeResultHints.cs` and `LargeResultHintsTests.cs` (shipped first family, commit ae34a879).
- `WriteToolAdviceHelper.IsExposed` (gating, kept separate).
- `docs/current/proposals/proposal_reduce_tool_schema_token_cost.md` (schema budget that limits C6).
- CLAUDE.md: "A tool param added to stop a known model failure is mandatory"; failure analysis doctrine.
