# `ClaudeLeanModeTests` expect SubAgent on the Basic flavor and in the Tools.Basic assembly scan (3 pre-existing failures)

**Status:** RESOLVED 2026-10-13 (see "Resolution" at the end). Original status: OPEN 2026-10-13. Root cause traced to source by reading (test expectations vs. flavor layering); the three failures were reported by the launching session and not re-run here, and the git history of the introducing commit was not inspected (no Git tool in this session).

## What was being attempted

Not a tool call. While verifying commit a6f2057c ("Ambiguous symbol errors: list candidates...") the launching session ran `RoslynSentinel.Tests.Server` and found three failures in `RoslynSentinel.Tests.Server/ClaudeLeanModeTests.cs`. They fail identically on clean HEAD with and without a6f2057c, so a6f2057c did not cause them. Per the CLAUDE.md convention (compare against the pre-existing baseline) they are written up here rather than fixed in that change.

## The exact symptom

As reported by the launching session (verbatim assertion text was relayed, not re-captured here):

```
Registry_EveryCoreToolName_IsDeclaredByAToolMethod   (ClaudeLeanModeTests.cs:157)
  Assert.That(declared, Is.SupersetOf(CoreTools))
  Missing items: "SubAgent"

ClaudeLean_ExposesExactlyTheTwentyFourCoreTools(False)   (ClaudeLeanModeTests.cs:113)
  Assert.That(names, Has.Length.EqualTo(24))
  Expected: 24  But was: 23        (advanced=false, the Basic server flavor)

Claude_BasicServer_IsTheAdvancedBaselineMinusAdvancedOnlyClasses   (ClaudeLeanModeTests.cs:133)
  Assert.That(names, Is.SupersetOf(CoreTools.Where(t => t != "McpToolsetControl")))
  (Basic server tool list lacks SubAgent)
```

The `advanced=true` variant of the 24-tool test, `Claude_AdvancedServer_StillExposesItsPreExistingToolSet` and `Registry_ListsClaudeLean_InBothServersMaps_AndCoreSetMatches` pass.

## Source trace

Single root cause: **`CoreTools` (ClaudeLeanModeTests.cs:27-35) lists `SubAgent` as a tool every flavor must expose, but `SubAgent` is declared only in `Tools.Advanced`, which the Basic flavor can never register.** The test file was not updated for the Basic/Advanced split when SubAgent was added to the claude-lean allow-list.

Evidence:

1. `SubAgent` is declared at `RoslynSentinel.Tools.Advanced/SubAgentTools.cs:17` (`[McpServerTool(Name = "SubAgent")]` on class `SubAgentTools`, namespace `RoslynSentinel.Tools.Advanced`).
2. `RoslynSentinel.Tools.Basic.csproj:23-24` references only `Common` and `Engines.Basic`, so it cannot see `Tools.Advanced` (CLAUDE.md layering: sibling projects cannot see each other).
3. The only DI block for `SubAgentTools` is `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs:243-246` (`if (activeToolClasses.Contains("SubAgentTools")) { services.AddSingleton<SubAgentTools>(); mcpBuilder.WithSentinelTools<SubAgentTools>(); }`). A text search of `RoslynSentinel.Server.Basic` finds no `SubAgentTools` registration; its only `SubAgent` hits are `ToolClassRegistry.cs` and a comment in `ServerStartupHelpers.cs:122-123`. `RegisterRoslynSentinelToolsBasic` therefore silently registers nothing for a class named in the claude-lean class list.
4. `RoslynSentinel.Server.Basic/ToolClassRegistry.cs` is shared by both flavors and puts SubAgent into the claude-lean surface for both:
   - `ClaudeLeanToolNames` (line 48) includes `"SubAgent"` (the 24-name allow-list).
   - `ClaudeLeanToolClasses` (line 63) includes `"SubAgentTools"`; it is assigned to `BasicModeToToolClasses[ClaudeLeanMode]` (line 92) and `AdvancedModeToToolClasses[ClaudeLeanMode]` (line 148).
   - `ClaudeLeanOnDemandToolClasses` (line 79) includes `"SubAgentTools"`.
   - The XML doc above `ClaudeLeanToolClasses` says the classes are "the same in Basic and Advanced". That is false for `SubAgentTools` (inaccurate comment, not a behaviour bug).
5. Failure 2 (23 vs 24): in Basic claude-lean the allow-list has 24 names but `SubAgentTools` is never registered, so 23 tools are listed. In Advanced claude-lean the Advanced DI block registers it, hence 24 and a pass.
6. Failure 1 (declared scan): `Registry_EveryCoreToolName_IsDeclaredByAToolMethod` calls `McpToolSchemaPatcher.DiscoverAllDeclaredTools(typeof(ServerStatusTools).Assembly)` (ClaudeLeanModeTests.cs:151-153). `ServerStatusTools` is in `RoslynSentinel.Tools.Basic/ServerStatusTools.cs`, so only the Tools.Basic assembly is scanned. `DiscoverAllDeclaredTools` (`RoslynSentinel.Common/McpToolSchemaPatcher.cs:236`) reflects only over the assemblies passed in. `SubAgent` lives in the Tools.Advanced assembly, so it is never found. The test project already references `Server.Advanced` (Tests.Server.csproj:27), so Tools.Advanced is available transitively (hypothesis on the transitive reference; not verified by a build).
7. Failure 3 (Basic `claude` mode): `SubAgentTools` is in neither `BasicModeToToolClasses["Claude"]` nor `AdvancedModeToToolClasses["Claude"]`, and `ToolClassRegistry.cs:139-142` says it is deliberately absent from "Claude" (a child server must not expose it). `ClaudeAdvancedBaseline` (test lines 38-54) correctly has no `SubAgent`, which is why the Advanced `claude` test passes. The Basic `claude` test instead asserts a superset of `CoreTools` (line 133), which now contains `SubAgent`. That assertion can never hold in either flavor's `claude` mode; it only differs in being checked for Basic. Every other `CoreTools` name (except `McpToolsetControl`, already excluded) appears in `ClaudeAdvancedBaseline`, and the preceding length assertion (line 131) is not the reported failure, so `SubAgent` is the only missing name (inferred from the baseline list and the reported failure, not from a re-run).

Prior mention: `docs/current/plans/plan_error_codes_follow_cause.md:203` records a "full-suite baseline note" that these tool-surface tests fail "since 348b03b3 (SubAgent enabled in claude-lean)". That note is stale on the counts (it says 26 vs 25; the lists are now 24 and the Advanced variant passes) but points at the same introducing event. Hypothesis: commit 348b03b3 added `SubAgent` to `CoreTools`/`ClaudeLeanToolNames` without scoping the Basic expectations. Not verified against `git show`. No existing blocker, `TODO.md` item or `CLOSED.md` entry covers these three tests.

## What is and is not confirmed

Confirmed by reading source:
- `SubAgent` is Advanced-only in both declaration (Tools.Advanced) and DI registration (Server.Advanced).
- The declared-tools scan in the test covers the Tools.Basic assembly only.
- `CoreTools` includes `SubAgent`, and the three failing assertions each compare a Basic-flavor or Basic-assembly result to `CoreTools`.
- The `claude` modes never include `SubAgentTools`.

Reported but not re-run in this session: the three failure messages and the pass/fail status of the other `ClaudeLeanModeTests`.

Hypotheses (unverified):
- Introduced by 348b03b3 (per the plan note only).
- Tools.Advanced is reachable from Tests.Server transitively for `typeof(SubAgentTools)`.
- `McpServerStatus` on a Basic claude-lean server may list `SubAgent` as allow-listed/active although it is not registered (not checked). If true, that is a small product-side wrinkle separate from these test failures.
- `ToolsArchitectureDocFreshness` (named in the same stale baseline note) may be a related failure; out of scope here, not checked.

## Why this matters

These are three known-red tests in the tool-surface baseline, so any real regression in the claude-lean surface or tool registration is harder to see (a "new failure" has to be diffed against a noisy baseline). The test expectations also encode an incorrect statement of the architecture (SubAgent exposed on both flavors), contradicting CLAUDE.md "Advanced is additive, not a fork" and the registry's own comment that SubAgent is deliberately kept out of `claude`. A future agent trusting `CoreTools` or the registry comment would conclude Basic is missing a tool, when the Basic flavor simply cannot host it.

## Suggested direction (not implemented)

Recommended (Option A): fix the tests, not the product. Basic cannot expose SubAgent by design.

Options considered:
- **A (recommended): make the test expectations flavor-aware.** No server change; one file.
- **B: remove `"SubAgentTools"` from the Basic claude-lean class list** (split `ClaudeLeanToolClasses` per flavor and give Basic its own `ClaudeLeanToolNames`). More faithful (the Basic surface would stop naming a class it cannot register) but touches the shared registry, McpToolsetControl's on-demand set and `ResolveActiveToolClasses_ClaudeLeanAlone_YieldsFifteenToolClasses` (which currently expects `SubAgentTools` via `BasicModeToToolClasses`), for no behaviour gain. Defer unless the McpServerStatus hypothesis above proves real.
- **C: move SubAgent into Tools.Basic.** Rejected: `SubAgentImpl` and `SubAgentChildServerLauncher` live in Tools.Advanced and belong there.

Suggested slice for Option A (one `implementer`, one file, `test-writer` acceptable):
- Files: `RoslynSentinel.Tests.Server/ClaudeLeanModeTests.cs` (optionally a one-line doc fix in `RoslynSentinel.Server.Basic/ToolClassRegistry.cs` for the inaccurate "same in Basic and Advanced" comment above `ClaudeLeanToolClasses`).
- Symbols: `CoreTools`; `ClaudeLean_ExposesExactlyTheTwentyFourCoreTools`; `Claude_BasicServer_IsTheAdvancedBaselineMinusAdvancedOnlyClasses`; `Registry_EveryCoreToolName_IsDeclaredByAToolMethod`.
- Changes:
  1. Add `private const string AdvancedOnlyCoreTool = "SubAgent";` (or an `AdvancedOnlyCoreTools` array) and a derived `BasicCoreTools = CoreTools.Where(t => t != "SubAgent")`.
  2. `ClaudeLean_ExposesExactlyTheTwentyFourCoreTools`: expected = `advanced ? CoreTools : BasicCoreTools`; assert length 24 vs 23 accordingly (rename to drop "TwentyFour" only if the names/docs elsewhere allow).
  3. `Claude_BasicServer_...` line 133: superset of `BasicCoreTools.Where(t => t != "McpToolsetControl")`.
  4. `Registry_EveryCoreToolName_...`: pass both assemblies, `DiscoverAllDeclaredTools(typeof(ServerStatusTools).Assembly, typeof(SubAgentTools).Assembly)` (add `using RoslynSentinel.Tools.Advanced;`), keeping the superset-of-`CoreTools` assertion. If Tools.Advanced is not transitively visible, add a `ProjectReference` is NOT needed (Server.Advanced already brings it); verify with a build.
  5. Leave `Registry_ListsClaudeLean_InBothServersMaps_AndCoreSetMatches` untouched (`ClaudeLeanToolNames` really has 24 names).
- Call sites: none outside the test file (test-private members).
- Acceptance: `RunTest` filtered to `ClaudeLeanModeTests` passes all cases, including the three above and `ClaudeLean_ExposesExactlyTheTwentyFourCoreTools(True)`; no other test in `RoslynSentinel.Tests.Server` changes status relative to the known baseline.
- Out of scope: `ToolsArchitectureDocFreshness`, McpServerStatus behaviour for unregistered allow-listed tools (open the follow-up only if verified).

When fixed: add a resolution note (root cause, fix, commit hash, tests), move this doc to `docs/current/blockers/resolved/`, and update the stale baseline note in `docs/current/plans/plan_error_codes_follow_cause.md:203` if it is still relevant.

## Resolution

**Resolved 2026-10-13.** Fixed in the commit that moves this doc into `resolved/` (find it with `git log -- docs/current/blockers/resolved/blocking_error_claudeleanmodetests_subagent_expected_in_basic_flavor.md`; a commit cannot record its own hash).

- **Root cause (confirmed):** the test expectations, not the product. `CoreTools` listed `SubAgent` for every flavor, but `SubAgent` is Advanced-only, so the Basic claude-lean flavor correctly exposes 23 tools. The declared-tools scan also covered only the Tools.Basic assembly. Confirmed by re-running `ClaudeLeanModeTests` on a clean HEAD (same 3 failures) before the fix.
- **Fix (Option A):** `RoslynSentinel.Tests.Server/ClaudeLeanModeTests.cs` gained `BasicCoreTools` (`CoreTools` minus `SubAgent`). `ClaudeLean_ExposesExactlyTheTwentyFourCoreTools` was renamed `ClaudeLean_ExposesExactlyTheCoreToolsForItsFlavor` and expects 24 (Advanced) or 23 (Basic). The Basic `claude` superset check uses `BasicCoreTools`. The declared-tools scan covers both the Tools.Basic and Tools.Advanced assemblies. No csproj change was needed (Tools.Advanced arrives transitively via Server.Advanced). `RoslynSentinel.Server.Basic/ToolClassRegistry.cs` had its "same in Basic and Advanced" comment corrected (comment-only).
- **Tests:** `ClaudeLeanModeTests` 14/14 pass; full `RoslynSentinel.Tests.Server` 253 passed, 0 failed, 7 skipped.
- **Not addressed:** hypotheses about `McpServerStatus` listing an unregistered allow-listed `SubAgent` on Basic, and `ToolsArchitectureDocFreshness`, were not investigated.
