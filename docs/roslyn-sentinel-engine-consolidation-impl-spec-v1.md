# RoslynSentinel — Engine Consolidation Implementation Spec
<!-- v1 -->
**Target server:** RoslynSentinel (rhale78) — C# / Microsoft.CodeAnalysis
**Date:** 2026-06-04
**For:** an implementing coding agent (Copilot/Claude in VS Code) working against the RoslynSentinel repo
**Outcome:** reduce the internal engine layer from **62 engine classes** (336 non-boilerplate methods) to **~38 engines** by resolving 9 duplicated method names, relocating ~16 mis-homed methods, and collapsing fragment/single-method engines into their domain owners.

## 0. Read first — scope and rules

This is an **implementation-layer** refactor. It does **not** change the public tool surface (93 tools per `tool_list_all.json`, 2026-06-04). The tool→engine wiring stays valid; only the engine that *hosts* a given method changes. After each move, update the dispatch/DI registration so the owning tool still resolves its handler.

This spec has three workstreams, done in the order in §F:
**(A)** resolve duplicate method names → **(B)** relocate mis-homed methods → **(C)** collapse fragment engines (small/single-method) and the async cluster.

**Consolidation rule (do not violate):** merge engine B into engine A only when B's methods share A's domain *responsibility*, not merely a coarse category. Do **not** merge two large cohesive engines just because both are "analysis." The two largest engines (`RefactoringEngine` 42, `AnalysisEngine` 31) are **not** merge targets — `AnalysisEngine` is a *source* of moves, not a sink.

**Verify-before-delete (critical):** this spec is derived from method **names and signatures only** — no bodies were read. Where two engines share a method name, confirm the bodies are actually duplicates (not divergent overloads — e.g. file-scope vs project-scope) before deleting either. One pair below (`FindCircularDependenciesAsync`) is a **known false duplicate** — do not delete it. **Take a backup / branch before applying; this touches DI wiring across the solution.**

**C# style for this repo:** explicit types, always braces, no expression-bodied members.

---

## A. Duplicate method names across engines — resolve 9

The same method name is registered in two engines. Each is either a dead duplicate (delete the non-canonical copy) or a divergent pair that needs renaming. **Canonical owner in bold.**

| # | Method | Engines | Action |
|---|---|---|---|
| A1 | `DetectInefficientStringComparisonsAsync` | AnalysisEngine, **PerformanceEngine** | Delete the AnalysisEngine copy; keep PerformanceEngine. |
| A2 | `FindBoxingAllocationsAsync` | AnalysisEngine, **PerformanceEngine** | Delete the AnalysisEngine copy; keep PerformanceEngine. |
| A3 | `OptimizeResourceDisposalAsync` | AnalysisEngine, **PerformanceEngine** | Delete the AnalysisEngine copy; keep PerformanceEngine. |
| A4 | `FindUnsafeLazyInitAsync` | AsyncSafetyEngine, **ThreadSafetyEngine** | Delete the AsyncSafetyEngine copy; lazy-init safety is a thread-safety concern. Keep ThreadSafetyEngine. |
| A5 | `GetPublicApiSurfaceAsync` | **BreakingChangeEngine**, DiscoveryEngine | Keep the BreakingChangeEngine copy (it owns the baseline/snapshot path the `get_public_api_surface` tool folds together); delete the DiscoveryEngine copy. |
| A6 | `PullUpMemberAsync` | RefinementEngine, **StructuralRefinementEngine** | Delete the RefinementEngine copy (RefinementEngine is dissolved in C2). |
| A7 | `SafeDeleteSymbolAsync` | **RefactoringEngine**, StructuralRefinementEngine | Keep RefactoringEngine (canonical per tool spec A2 `safe_delete`); delete the StructuralRefinementEngine copy. |
| A8 | `UpgradePatternMatchingAsync` | ModernizationUpgradeEngine, **SyntaxUpgradeEngine** | Delete the ModernizationUpgradeEngine copy (that engine is dissolved in C4). Keep SyntaxUpgradeEngine. |
| A9 | `FindCircularDependenciesAsync` | AnalysisEngine, ArchitecturalEngine | **NOT a duplicate — do not delete.** AnalysisEngine detects *type* cycles; ArchitecturalEngine detects *project-reference* cycles (matches tool spec §A3). **Rename** for clarity: AnalysisEngine → `FindCircularTypeReferencesAsync` (note: AnalysisEngine already has a method by that name — see §A note), ArchitecturalEngine → `FindCircularProjectReferencesAsync`. Verify which detector each backs before renaming. |

**§A9 note:** `AnalysisEngine` already contains both `FindCircularDependenciesAsync` **and** `FindCircularTypeReferencesAsync`. Determine whether these are two genuinely different detectors or a leftover dup *within the same engine*; if the latter, collapse to one and give the project-ref detector to ArchitecturalEngine as `FindCircularProjectReferencesAsync`. This is the one item requiring body inspection before any rename.

---

## B. Mis-homed methods — relocate ~16

`AnalysisEngine` and `AntiPatternEngine` have become catch-all classes; most strays originate there. Moves are pure relocations (no behaviour change) — move the method, update DI/dispatch, leave the tool definition untouched.

| # | Method | From | To | Rationale |
|---|---|---|---|---|
| B1 | `DetectMemoryLeaksAsync` | AnalysisEngine | PerformanceEngine | Memory/perf, not structural analysis. |
| B2 | `AnalyzeSemaphoreUsageAsync` | AnalysisEngine | ThreadSafetyEngine | Concurrency primitive analysis. |
| B3 | `DetectMismatchedAwaitAsync` | AnalysisEngine | AsyncSafetyEngine | Async correctness. |
| B4 | `GenerateEqualityOverridesAsync` | AnalysisEngine | CodeGenerationEngine | Generates code — wrong engine entirely. |
| B5 | `GenerateCallTreeAsync` | AnalysisEngine | SymbolNavigationEngine | Call-graph family lives there (`GetCallGraphAsync`, `GetReverseCallGraphAsync`). |
| B6 | `FindMissingCancellationTokensAsync` | AntiPatternEngine | AsyncOptimizationEngine | Belongs with the rest of the CT family. |
| B7 | `FindInconsistentAsyncSuffixAsync` | AntiPatternEngine | AsyncSafetyEngine | Async naming/correctness. |
| B8 | `GetAsyncMigrationProgressAsync` | AntiPatternEngine | AsyncBatchEngine | Migration reporting, not anti-pattern detection — clearly misfiled. |
| B9 | `DetectJsonAntiPatternsAsync` | SecurityEngine | AntiPatternEngine | An anti-pattern detector, not security. |
| B10 | `GetTestCoverageMapAsync` | ControlFlowEngine | TestingEngine | Test concern, not control-flow analysis. |
| B11 | `FindNonExhaustiveEnumSwitchesAsync` | ControlFlowEngine | AnalysisEngine | Correctness sweep, not deep flow analysis. (AnalysisEngine is a valid sink for sweeps; it is only mis-homed *generators* and *perf/async* methods we pull out.) |
| B12 | `FindReadonlyFieldCandidatesAsync` | SymbolNavigationEngine | AnalysisEngine | Code-quality sweep, not symbol navigation. |
| B13 | `FindUseFrozenCollectionsAsync` | CodeStyleEngine | PerformanceEngine | Frozen collections are a perf optimization. |

**Net effect on AnalysisEngine:** loses A1–A3 (dup deletes) + B1, B2, B3, B4, B5 = 8 removed; gains B11, B12 = 2. Drops from 31 → 25, and is no longer a dumping ground for perf/async/codegen.

**Net effect on AntiPatternEngine:** loses B6, B7, B8 = 3; gains B9 = 1. Drops 13 → 11, scoped back to actual anti-pattern detection.

---

## C. Engine consolidation — collapse fragments and the async cluster

### C1. Async cluster → `AsyncEngine` (highest-value merge)

Merge **`AsyncSafetyEngine`** (19), **`AsyncOptimizationEngine`** (16), **`AsyncBatchEngine`** (4) into one **`AsyncEngine`**. Batch is the multi-target variant of Optimization; Safety is the detection half. After A4 (−1 from Safety), B3/B6/B7/B8 (+3 incoming), the merged engine is ~41 methods — large but cohesive and backs a single `--mode async` tool group.

If 41 methods is judged too large for one class, split along the existing seam: `AsyncEngine` (detection/optimization) + keep `AsyncBatchEngine` (batch orchestration) separate. Do **not** keep all three.

### C2. `RefinementEngine` → `StructuralRefinementEngine`

`RefinementEngine` (2: `PullUpMemberAsync`, `InlineMethodAsync`) fully overlaps StructuralRefinementEngine. `PullUpMemberAsync` is the A6 dup. Move `InlineMethodAsync` over, delete `RefinementEngine`.

### C3. Style cluster → one `CodeStyleEngine`

Merge **`CodeStyleAnalysisEngine`** (1: `FindMutablePublicCollectionPropertiesAsync`) and **`CodeSmellAndStyleEngine`** (2: `ScanForSmellsAsync`, `UseSwitchExpressionAsync`) into **`CodeStyleEngine`** (8, −1 after B13 = 7 → 10). Three "Style"-named engines, two of them fragments.

### C4. Modernization/upgrade cluster → `SyntaxUpgradeEngine`

Merge **`ModernizationUpgradeEngine`** (3, −1 after A8 = 2: `UseSpanForParsingAsync`, `UseThrowExpressionsAsync`) and **`ModernLoggingEngine`** (1: `ConvertToSourceGeneratedLoggingAsync`) into **`SyntaxUpgradeEngine`** (11). Leave `ModernizationEngine` (4: record↔class, expression-body, pattern) separate **or** fold it too if bodies show overlap — verify. All are "rewrite to newer C# syntax."

### C5. Logic/control-structure cluster → `ControlStructureEngine`

Merge **`AdvancedLogicEngine`** (8), **`LogicOptimizationEngine`** (4), **`StandardRefactoringEngine`** (3) into one **`ControlStructureEngine`** (~15). All perform control-structure rewrites (if→switch, for↔foreach, boolean inversion, null-coalescing, guard clauses). Note the near-dup pair `InvertBooleanLogicAsync` (AdvancedLogicEngine) / `InvertBooleanAsync` (StandardRefactoringEngine) — verify and collapse to one during the merge.

### C6. Single-method engines → fold by domain

Each one-method engine is a class for a maintenance cost of one method. Fold:

| Engine (method) | Fold into |
|---|---|
| `StackOverflowEngine` (`AnalyzeStackOverflowRisksAsync`) | SecurityAndSafetyEngine |
| `CodeFlowEngine` (`ReduceBlockDepthAsync`) | RefactoringEngine or ControlStructureEngine (C5) |
| `ImmutabilityEngine` (`MakeClassImmutableAsync`) | AdvancedStructuralEngine |
| `OutParamRefactoringEngine` (`ConvertOutParamsToValueTupleAsync`) | GranularRefactoringEngine |
| `PathDrivenTestEngine` (`GeneratePathDrivenTestsAsync`) | TestingEngine |
| `ApiAutomationEngine` (`GenerateHttpClientForControllerAsync`) | CodeGenerationEngine |
| `ApiIntegrationEngine` (`AddValidationToPocoAsync`) | CodeGenerationEngine or DocumentationEngine |
| `HealthOrchestrationEngine` (`GenerateComprehensiveHealthReportAsync`) | MetricsEngine (keep as orchestration entry if it fans out to other engines — verify dependency direction first) |

### C7. `ExhaustiveAnalyzerEngine` + `MassiveAnalyzerEngine` — verify then merge

`ExhaustiveAnalyzerEngine` (`RunDiagnosticRuleAsync`) and `MassiveAnalyzerEngine` (`RunSpecificRuleAsync`) look like the same "run one analyzer rule" capability under two names. **Read both bodies.** If equivalent, merge into one `AnalyzerRuleEngine` and delete the other; if they back distinct tools with distinct semantics, leave them but rename for clarity.

### Do NOT merge

- **`RefactoringEngine`** (42) — core edit primitives, cohesive. Leave as-is.
- **`AnalysisEngine`** (→25 after A/B) — a source of moves, not a sink. Do not merge it with PerformanceEngine; that would rebuild the god-class.
- **`PerformanceEngine`** (→14 after incoming perf moves) — keep distinct from AnalysisEngine.
- **`MsToolAugmentEngine`** (12) — the MS-bug-fix augment family; distinct provenance, leave intact.

---

## D. Weakest moves — verify, revert if bodies disagree

- **§A9 `FindCircularDependenciesAsync`** — the only item that is *not* a delete. Requires body inspection to confirm type-cycle vs project-cycle split before renaming. Get this one wrong and you delete a live detector.
- **§C4 `ModernizationEngine` fold** — left optional pending body overlap check; don't force it.
- **§C6 `HealthOrchestrationEngine`** — if it orchestrates other engines (calls into them), folding it into MetricsEngine may invert a dependency. Check call direction first; if it's a top-level fan-out, leave it standalone.
- **§A duplicate deletes generally** — every "delete the copy" in §A assumes the copy is dead-identical. If a copy has diverged, the tool that calls it may depend on the divergence. Spot-check the calling tool's expected output after each delete.

---

## E. Resulting engine landscape (target)

Starting: **62 engines / 336 methods.** Method total is unchanged by relocation; the count drops only from the §A duplicate deletes (8 deletes: A1–A8; A9 is a rename) → **328 methods**.

Engines removed (folded/dissolved): AsyncSafetyEngine, AsyncOptimizationEngine, AsyncBatchEngine (→ AsyncEngine; or two if split), RefinementEngine, CodeStyleAnalysisEngine, CodeSmellAndStyleEngine, ModernizationUpgradeEngine, ModernLoggingEngine, AdvancedLogicEngine, LogicOptimizationEngine, StandardRefactoringEngine, StackOverflowEngine, CodeFlowEngine, ImmutabilityEngine, OutParamRefactoringEngine, PathDrivenTestEngine, ApiAutomationEngine, ApiIntegrationEngine, HealthOrchestrationEngine, MassiveAnalyzerEngine (or ExhaustiveAnalyzerEngine).

New engines: `AsyncEngine`, `ControlStructureEngine`, (optional) `AnalyzerRuleEngine`.

**Target: ~38–40 engines** (62 − ~23 removed + 2–3 new), down ~37%. No tool-surface change.

---

## F. Implementation order

Lowest-risk first; matches the repo's guard→read→write→complex convention.

1. **A. Resolve duplicates.** For each §A pair: read both bodies, confirm dead-identical, delete the non-canonical copy, repoint DI/dispatch to the survivor, build. Do §A9 (rename, not delete) **last** in this group and only after confirming the type-vs-project split.
2. **B. Relocate mis-homed methods.** Pure moves; build + spot-check each owning tool's output after the move. No behaviour change expected.
3. **C2–C7. Collapse fragment engines.** Fold single-method and style/modernization/logic clusters. One engine at a time; build between each.
4. **C1. Async cluster merge.** Largest surface; do last. Decide one-engine vs split-at-batch before starting. Validate the full `--mode async` tool group end-to-end.

---

## G. Acceptance criteria

- [ ] No method name is registered in more than one engine, **except** the intentionally-renamed circular-dependency pair (now `FindCircularTypeReferencesAsync` / `FindCircularProjectReferencesAsync`).
- [ ] `AnalysisEngine` contains no code-generation, perf-sweep, or async-detection methods.
- [ ] `AntiPatternEngine` contains only anti-pattern detectors (incl. `DetectJsonAntiPatternsAsync`).
- [ ] Engine count ≤ 40.
- [ ] Every tool in `tool_list_all.json` still resolves to a live handler (no orphaned dispatch).
- [ ] Each relocated method reproduces its prior tool output (spot-check one tool per move).
- [ ] Build passes; no orphaned engine references in DI registration.

*Generated 2026-06-04 — v1*
