# Expose the dormant code-transform engines under a new `Experimental` mode

**Status:** APPROVED 2026-10-10. Phase 1 IMPLEMENTED 2026-10-10 (mode wired, catalogues moved to Common, tests added); Phases 2-3 not started. Design questions 1-3 answered (see "Decisions"). Origin: comparison against the upstream (rhale78/RoslynSentinel) May 2026 `UNFINISHED*.md` docs.

## Motivation

Engine logic for ~63 refactoring/modernization transforms exists and is tested, but no running server
exposes it. Two separate gaps:

**Gap A - the whole `CodeTransformTools` class is dark (55 transforms).**
- `RoslynSentinel.Tools.Experimental/CodeTransformTools.cs` declares four tools (`ApplyFileCodeTransform`,
  `ApplyMethodCodeTransform`, `ApplyClassCodeTransform`, `Generate`) dispatching 25 file, 17 method and
  13 class transforms by name (the `case "..."` labels, lines 85-1242).
- It is registered in no mode. `docs/generated/architecture_tools.md` lists it under "Unregistered tool
  classes".
- The DI block that would host it is commented out and still uses the pre-rename class name:
  `Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs:227-234` (`//services.AddSingleton<CodeTransformationTools>()`).
  `ToolClassRegistry.CodeTransformToolClass` (`ToolClassRegistry.cs:147`) is the same stale string
  `"CodeTransformationTools"`. The class was renamed `CodemodTools` -> `CodeTransformTools` in commit
  `bde1db11`; the `Server.Advanced` project already references `Tools.Experimental` (csproj line 55).
- `DescribeAdvancedToolOptions` entries for `apply_file_codetransform` / `apply_method_codetransform` are
  commented out (`Tools.Advanced/QualityTools.cs:65-66`) because `Tools.Advanced` cannot reference
  `Tools.Experimental`. Yet the tool descriptions tell the model to call
  `DescribeAdvancedToolOptions("apply_file_codetransform")` (`CodeTransformTools.cs:76`), so even if the
  class were registered the catalogue lookup would fail.

**Gap B - eight engine methods have no tool route at all (not even in `CodeTransformTools`).**
Found by text search of `Tools.*` for each name; none is called from any `Tools.*` project:

| Engine method | Location | Note |
| --- | --- | --- |
| `ConvertIndexerToMethodAsync` | `Engines.Basic/BasicRefactoringEngine.cs:913` | gated by feature flag `ConvertIndexerToMethod` (`Common/SentinelConfiguration.cs:23`); only tests call it |
| `UseNullPropagationAsync` | `Engines.Advanced/IDEStyleEngine.cs:196` | |
| `UseSpanForParsingAsync` | `Engines.Advanced/ModernizationEngine.cs:602` | |
| `UseThrowExpressionsAsync` | `Engines.Advanced/ModernizationEngine.cs:664` | `use_exception_expressions` calls a different engine method (`UseExceptionExpressionsAsync`); overlap unverified |
| `ConvertForToForEachAsync` | `Engines.Advanced/LogicOptimizationEngine.cs:1070` | |
| `ConvertWhileToForAsync` | `Engines.Advanced/LogicOptimizationEngine.cs:1143` | |
| `AddRetryPolicyAsync` | `Engines.Advanced/CodeHealingEngine.cs:83` | |
| `RunMicroRefactoringAsync` | `Engines.Advanced/AdvancedStructuralEngine.cs:310` | takes a `refactoringId` string - a sub-dispatcher |

Mission fit: a weak model that wants "add null guards", "for -> foreach" or "lambda null-propagation"
currently has no structured tool and falls back to text edits (CLAUDE.md "gap in the tool surface").

## Proposal

**Decided direction (per request):** put everything under a new `Experimental` mode. No new tool class
is needed - `CodeTransformTools` *is* the Experimental-mode class.

### Phase 1 - register what already exists (Gap A)
1. `ToolClassRegistry.AdvancedModeToToolClasses`: add `["Experimental"] = ["CodeTransformTools"]`.
   Do not add it to `"Claude"` or `claude-lean`.
2. `ServiceRegistrationExtensionsAdvanced`: replace the commented `codeTransformActive` block (lines 227-234)
   with the standard `if (activeToolClasses.Contains("CodeTransformTools")) { AddSingleton; WithSentinelTools }`.
3. Delete `ToolClassRegistry.CodeTransformTriggerModes` and `CodeTransformToolClass`; update
   `ServerStartupHelpers.KnownToolClassNames` (lines ~106-115) which appends the stale constant.
4. Move `ApplyFileCodeTransformOptions` / `ApplyMethodCodeTransformOptions` / `ApplyClassCodeTransformOptions`
   (`CodeTransformTools.cs:1615-1790`) to `Common` (alongside `ToolOptionsResult`) and un-comment the
   `QualityTools.cs:65-66` cases (add the class case). Layering stays one-way (`Tools.Advanced` -> `Common`).
5. Regenerate `docs/generated/` (`ArchitectureDocFreshness`); the "Unregistered" section should empty.
6. Tests: a DI-resolution test that builds the Advanced host with `--mode=Experimental` and resolves
   `CodeTransformTools` (its constructor takes 25 engines); a `ToolClassRegistry` test for the new mode.

### Phase 2 - smoke-test every transform through the tool boundary
The class has not been served since the rename (hypothesis: not run end-to-end since). One NUnit case per
transform (63 after Phase 3), asserting the tool returns without an `Exception` error code on a minimal
fixture and that the unknown-transform error lists the full valid set. Failures become per-transform
blockers/TODO items, not a gate on shipping the mode (it is Experimental).

### Phase 3 - route the Gap B engines
Add as `case` labels in the existing three tools (no new class), plus catalogue entries:

| Tool | New transform | Engine call |
| --- | --- | --- |
| `ApplyFileCodeTransform` | `use_null_propagation` | `UseNullPropagationAsync` |
| `ApplyFileCodeTransform` | `use_throw_expressions` | `UseThrowExpressionsAsync` (decide vs `use_exception_expressions`) |
| `ApplyMethodCodeTransform` | `use_span_for_parsing` | `UseSpanForParsingAsync` |
| `ApplyMethodCodeTransform` | `convert_for_to_foreach` | `ConvertForToForEachAsync` (line-addressed) |
| `ApplyMethodCodeTransform` | `convert_while_to_for` | `ConvertWhileToForAsync` (line-addressed) |
| `ApplyMethodCodeTransform` | `add_retry_policy` | `AddRetryPolicyAsync` (line range + retry count) |
| `ApplyClassCodeTransform` | `convert_indexer_to_methods` | `ConvertIndexerToMethodAsync` (drop the feature-flag gate once solution-wide call-site rewriting is verified) |
| `ApplyFileCodeTransform` | `run_micro_refactoring` | `RunMicroRefactoringAsync` (`refactoringId` param) |

`convert_for_to_foreach`, `convert_while_to_for`, `add_retry_policy` take line numbers, so the new params
need `contextSnippet`-style anchoring like `convert_expression_body` rather than raw line numbers (the
repo's line-number anchors are known-fragile; see memory `project_linenumber_fallback_anchor_idea`).

### Phase 4 (separate, from the same upstream comparison - not part of this proposal's scope)
Genuinely missing features (no engine anywhere): `CopyType` (flag only, `SentinelConfiguration.cs:31`),
`ConvertInterfaceToAbstractClass` (flag only, line 24), `LocalFunctionMigration`. Track in TODO.md.

## Alternatives considered, not pursued

- **New `ExperimentalTools` class holding one `ApplyExperimentalTransform` tool.** Rejected: duplicates the
  dispatch pattern `CodeTransformTools` already has, and splits the transform catalogue in two. Adding
  cases to the existing three tools keeps one place per scope (file/method/class). Revisit only if the four
  existing schemas prove too large for the mode.
- **Fold the transforms into existing tools** (`Inline`, `Introduce`, `Member`). Rejected: those are
  semantically scoped refactorings with solution-wide guarantees; these are heuristic single-file rewrites
  and should not share a name or a promise.
- **Delete the unreferenced engines.** Rejected: they are covered by tests (`StubImplementationTests.cs`,
  `BatteryEightTests.cs`) and implement what upstream still lists as planned work.
- **Reuse the `Refactor`/`Modernize`/`Quality`/`Generation` trigger modes** (the old
  `CodeTransformTriggerModes` rule). Rejected: it attached 4 large schemas to four unrelated modes and
  was the thing that rotted when the class was renamed; one explicit mode is easier to reason about.

## Decisions (2026-10-10, from the reviewer)

1. **Write path:** the transforms are stated to write through the unified write chokepoint (compile gate,
   undo blob). Taken as given, but Phase 2 must assert it per transform (file changed on disk, an
   `UndoLastApply` blob exists) because the code was not traced in this proposal.
2. **`--mode=all` excludes `Experimental`** (and so does `Claude`): add it to `ToolClassRegistry.ExclusiveModes`
   or an equivalent exclusion, so it costs zero tokens unless asked for by name. Add this to Phase 1.
3. **`ConvertIndexerToMethod` call sites:** answer was a bare "yes" to a conditional question. Recorded
   conservatively: it ships only once a test shows `x[i]` callers across files are rewritten. Until then
   the `ConvertIndexerToMethod` feature flag stays off and the transform is omitted from the catalogue.

## Open questions

1. **Are all 25 constructor dependencies registered in the Advanced host?** Spot-checked 10 engines (all
   have `AddSingleton`); the rest unverified. Phase 1's DI-resolution test answers this.
2. **`use_throw_expressions` vs `use_exception_expressions`:** likely overlapping; confirm before adding
   a near-duplicate transform name.
3. **`Generate` tool:** four tools are in scope for registration but only the three `Apply*` tools are
   analysed here; `Generate`'s kinds were not reviewed.

## Cost / risk

- **Prompt tokens:** four tools with large schemas (`CodeTransformTools.cs` is 1791 lines; transform
  catalogues are in the `Description`, not inline) added only when `--mode=Experimental` is active.
- **Correctness:** the transforms are heuristic and unrun through MCP; hence the Experimental label and
  the Phase 2 smoke tests. Risk of silent bad rewrites is mitigated only if Open question 1 resolves well.
- **Touches:** `ToolClassRegistry.cs`, `ServiceRegistrationExtensionsAdvanced.cs`, `ServerStartupHelpers.cs`,
  `QualityTools.cs`, `CodeTransformTools.cs`, a new `Common` file for the catalogues, generated docs.
  No engine changes in Phases 1-2.
- **Maintenance:** 63 transform names to keep in sync across the dispatch switch, the "Unknown transform"
  error string (hand-maintained at `CodeTransformTools.cs:~920`) and the catalogue. Consider deriving the
  error string and catalogue from one table when touching Phase 3.

## Related

- `docs/generated/architecture_tools.md` ("Unregistered tool classes")
- Commit `bde1db11` (CodemodTools -> CodeTransformTools rename), `354158ec` (Tools.Experimental created)
- Memory: `project_write_path_chokepoint_unified`, `project_linenumber_fallback_anchor_idea`,
  `project_caller_fixup_tool_audit_idea`
- CLAUDE.md "Adding a new MCP tool requires three things" (mode dictionary + DI block + class)
