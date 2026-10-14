# Reference: changing the claude-lean tool surface (and the tests that pin it)

**Status:** CURRENT 2026-10-14. Verified by a throwaway trial (adding `BridgeAsyncMethods` to the Advanced claude-lean surface, since reverted) and by implementing the streamlining changes listed at the end.

## Purpose
Consult this before adding, removing, renaming or re-describing a tool in `--mode claude-lean`, or changing a tool's schema or behaviour. It lists the edit points, the tests that fail when they are missed, and the fast loop to run them.

## How it works

### 1. First, which surface are you changing?
claude-lean is an exclusive mode with a per-tool allow-list. A tool you know from another mode may not be in it, or may be there under a merged name.

| Question | Where to look |
| --- | --- |
| Is the tool in claude-lean at all? | `McpServerStatus(toolListing: inactive, toolNameFilter: <name>)`. `enabledBy` says why it is gated. |
| Core (always on) | `ClaudeLeanToolNames` in `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`. 24 names in Advanced, 23 in Basic (no `SubAgent`). Do not trust counts written in prose; the tests derive them. |
| On demand (hidden until `McpToolsetControl(toolSet: ..., enabled: true)`) | `ToolsetCatalog.ToolsBySet` / `Summaries` in `RoslynSentinel.Common/ToolsetCatalog.cs`; classes listed in `ClaudeLeanOnDemandToolClasses`. |
| Merged replacements | In claude-lean `MethodSignature` and `ConstructorParameter` do not exist. `ParameterEdit` (`Tools.Basic/ParameterEditTools.cs`) replaces them, in the `declarations` toolset. `Declaration` likewise replaces ModifyModifier, ChangeAccessibility, ModifyAttribute and ModifyBaseType. |

Behaviour changes go in the shared impl or engine (for MethodSignature: `Tools.Basic/RefactoringSignatureImpl.cs`), and every mode sees them. `ParameterEditTools` delegates to the same impl.

### 2. Edit points by kind of change

**A. Change a tool's behaviour (all modes).** Edit the impl or engine. No registry edits. Run the behaviour tests (section 3, rows marked B).

**B. Change a tool's description or parameter schema.**
- Text duplicated between a merged tool and its source tools lives in shared constants in `RoslynSentinel.Common/ToolParams.cs` (the "Merged-tool shared text" block). Edit the constant once; both tools change.
- Wrapper text that differs on purpose (per-tool examples) stays on each tool.
- `MergedToolDescriptionSyncTests` fails if a listed parameter description or shared clause differs between a merged tool and a source tool. Its failure message names the `ToolParams` constant. If you introduce a new duplicated clause, add it to that test's table by hand.
- `ParameterEdit_EmittedSchema_IsSmallerThanTheTwoToolsItMerges` fails if the merged schema grows past its sources.
- `ClaudeLeanSchemaBudgetTests` fails if the core surface grows past its absolute budget (see Gotchas).
- Regenerate the docs (step 4 below).

**C. Add an existing tool to claude-lean (core).**
1. Add the tool name to `ClaudeLeanToolNames`.
2. Add the same name to `ClaudeLeanModeTests.CoreTools`, the one explicit oracle list. Counts are derived from it. If the tool lives in `Tools.Advanced`, also exclude it from `BasicCoreTools` (as `SubAgent` is).
3. Make sure its class is in the claude-lean class set for each flavour that can host it:
   - Class in `Tools.Basic`: it is already in or must join `ClaudeLeanToolClasses`.
   - Class in `Tools.Advanced` (or `Tools.Experimental`): Basic cannot host it. Use an Advanced-only array in `ToolClassRegistry` and point `AdvancedModeToToolClasses[claude-lean]` at it. Adding it to the shared `ClaudeLeanToolClasses` would grow Basic's class set and break `ResolveActiveToolClasses_ClaudeLeanAlone_YieldsExactlyTheRegistryClaudeLeanClasses`.
4. No DI edit is needed when the class's existing `if (activeToolClasses.Contains(...))` block already exists. Other tools on the same class stay hidden by the allow-list (`McpToolSchemaPatcher.WithSentinelTools`).
5. The allow-list is shared by both flavours, so an Advanced-only name also appears in Basic's list. It is harmless there because no Basic class declares it.
6. Regenerate the docs, check `ClaudeLeanSchemaBudgetTests`.

**D. Add a brand-new tool.** Do the three registration steps in CLAUDE.md (tool class, `ToolClassRegistry` dictionary entry, DI block), then C above.

**E. Move a tool to or from an on-demand toolset.** Edit `ToolsetCatalog` (`ToolsBySet`, `Summaries`) and the hand-listed names in the `McpToolsetControl` `[Description]` (`ToolsetControlTools.cs`), then the toolset tests in section 3.

### 3. Tests that pin the surface

All are in `RoslynSentinel.Tests.Server` unless noted. Rows marked B pin behaviour, not the surface.

| Test | Pins | Typical edit |
| --- | --- | --- |
| `ClaudeLeanModeTests`: `ClaudeLean_ExposesExactlyTheCoreToolsForItsFlavor` | The exposed tool set and count per flavour, derived from `CoreTools` / `BasicCoreTools` | Edit `CoreTools` |
| `ClaudeLeanModeTests`: `Registry_ListsClaudeLean_InBothServersMaps_AndCoreSetMatches` | Registry allow-list equals `CoreTools` | Add the name to both. The message says so. |
| `ClaudeLeanModeTests`: class-set tests | Basic's claude-lean class set equals the registry's | Only when the class set changes |
| `McpToolsetControlTests`: `LeanHost_StartsWithNoDynamicToolsAndTheExactCoreSurface` | Host-start surface equals `CoreTools` | None beyond `CoreTools` |
| `McpToolsetControlTests`: disjointness, "every set described", "description names every tool" | The on-demand toolset catalogue | Only when `ToolsetCatalog` changes |
| `McpToolsetControlTests`: ParameterEdit and Declaration tests | Merged-tool errors and schema-size check | Text edits |
| `MergedToolDescriptionSyncTests` | Shared text between merged tools and sources | Edit the `ToolParams` constant |
| `ClaudeLeanSchemaBudgetTests` | Absolute serialized schema size of the core surface, total and per tool, per flavour | Raise the constants if growth is intended; say why in the commit message |
| `ArchitectureDocFreshnessTests` | `docs/generated/architecture_*.md` equals fresh output. On failure the message lists the first differing lines and the regen command. | Regenerate (step 4) |
| `ArchitectureDocFreshnessTests` `*_RegeneratesFile` twins | Regeneration only | Skipped unless `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS` is set |
| `McpServerStatusToolListingTests.ClaudeLeanAllowList...` (`Tests.Tools.Basic`) | Inactive-tool hints | Rarely |
| `ParameterEditToolTests`, `BatteryTwentyFourTests` (B) | MethodSignature/ParameterEdit behaviour | Behaviour changes only |
| `SchemaLeanProfileTests` | `--schema-profile=lean` stripping. Not the claude-lean mode. | Usually none |

### 4. Procedure
1. Identify the surface (section 1) and the kind of change (section 2).
2. Make the code edit. In this repo implementation is dispatched to `implementer`/`implementer-senior`; the brief must name the registry and test files.
3. Run the fast loop: `.\scripts\Test-ClaudeLeanSurface.ps1`. Expect any first failure to name what to edit.
4. Regenerate the docs: `.\scripts\Test-ClaudeLeanSurface.ps1 -Regenerate` (or `.\scripts\Generate-ArchitectureMap.ps1`). Review the diff of `docs/generated/architecture_tools.md`, then commit it with the change.
5. `Build(fullBuild)` with 0 errors, then `RunTest` on `RoslynSentinel.Tests.Server`. Add `Tests.Tools.Basic` / `Tests.Tools.Advanced` for behaviour or description changes. A whole-solution run takes about 5.5 minutes and is only needed for wide changes.
6. Compare failures with the baseline (no known failures per `reference_known_failing_tests`). Before blaming your change, check `git status` for uncommitted work from another session.
7. A live check needs a server restart: `McpServerControl(StopServer)`, then `LoadSolution`. `Build(fullBuild)` makes the running binary stale.
8. Commit only the files you changed, including `docs/generated/*`.

### 5. Trial run evidence (2026-10-14, reverted)
Change: `BridgeAsyncMethods` (`Tools.Advanced/AsyncifyTools.cs:576`) made a core Advanced claude-lean tool. Files edited: `ToolClassRegistry.cs`, `ClaudeLeanModeTests.cs`, `McpToolsetControlTests.cs`, `docs/generated/architecture_tools.md`. No DI edit. The trial was thrown away after the process was documented.

With the old hand-copied counts, one `RunTest` on `Tests.Server` after the registry edit alone gave 4 failures: two count assertions (25 vs 24), the registry-vs-`CoreTools` set check, and the generated-doc freshness check. After the streamlining below, the same change needs the allow-list, `CoreTools` (and `BasicCoreTools` for Advanced-only tools) and a regeneration.

## Usage
Fast loop for any claude-lean surface change:

```
.\scripts\Test-ClaudeLeanSurface.ps1 [-Regenerate] [-NoBuild]
```

It runs `RoslynSentinel.Tests.Server` filtered to ClaudeLean, ToolsetControl, ArchitectureDocFreshness, MergedToolDescriptionSync and ClaudeLeanSchemaBudget (about 20 s), and exits non-zero on failure. `-Regenerate` sets `ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1` for the run.

Live surface check: `McpServerStatus(toolListing: inactive, toolNameFilter: <tool>)`.

## Gotchas
- A tool can be declared, registered in DI and still invisible: the allow-list hides it. `enabledBy` in `McpServerStatus` says which switch is missing.
- The generated Modes column is per tool for claude-lean: `claude-lean` means core, `claude-lean (on demand)` means a toolset member. Classes that are only in `ClaudeLeanOnDemandToolClasses` also get the on-demand label on their toolset tools.
- `ClaudeLeanSchemaBudgetTests` budgets are the measured size rounded up to the next 500 characters (2026-10-14: Advanced 42186 of 42500 total, Basic 40216 of 40500, per-tool 7500 against `Git` at 7415). Headroom is small on purpose; a small `Git` description edit can trip the per-tool limit. Raise the constant and say why in the commit message.
- The `*_RegeneratesFile` tests are skipped unless the env var is set, so a green run does not mean docs were regenerated. The freshness test is the check.
- The freshness diff is positional. An inserted or removed row shifts every later line; read the first differing line.
- `--schema-profile=lean` is unrelated to `--mode claude-lean`.
- `ClaudeLeanSchemaBudgetTests` copies the host construction from `McpToolsetControlTests.BuildHost` (private). A shared helper would remove the duplication.
- Editing a `.cs` file outside the server (for example `git restore` in a shell) trips `SessionHalted: externalDrift` for the next tool call. Use `ExternalFileDrift(List)` then `Acknowledge` for your own files only.

## Streamlining changes (implemented 2026-10-14)
1. Test counts derived from `ClaudeLeanModeTests.CoreTools`, with failure messages naming the list to edit.
2. Merged-tool text single-sourced in `ToolParams`, with `MergedToolDescriptionSyncTests`.
3. Generated Modes column made per tool for claude-lean.
4. Doc-freshness failure message shows the differing lines.
5. Absolute schema size budget (`ClaudeLeanSchemaBudgetTests`).
6. `*_RegeneratesFile` tests skip when the env var is unset.
7. Stale names, counts and comments fixed (test name, comments, `ClaudeLeanToolNames` doc comment, "Phase 2" in the generator script, counts in `proposal_reduce_tool_schema_token_cost.md`).
8. `scripts/Test-ClaudeLeanSurface.ps1` fast loop.

Remaining ideas: share the host builder between `McpToolsetControlTests` and `ClaudeLeanSchemaBudgetTests`; extend the sync test to derive its parameter list instead of a hand table.
