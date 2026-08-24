# RoslynSentinel — Batch-First Implementation Plan
<!-- Derived from spec-batch-first-tool-architecture-v1.md -->

**Date:** 2026-05-30
**Spec:** [spec-batch-first-tool-architecture-v1.md](spec-batch-first-tool-architecture-v1.md)
**Active host:** `RoslynSentinel.Server.dll` (stdio, per `.vscode/mcp.json`)

---

## Context

- **200+ tools** currently registered — manifest bloat is the primary cost target.
- **Rate limiter** exists in `PersistentWorkspaceManager`; **no circuit breaker** yet.
- **Batch shape proven:** `PropagateCtBatchInput` with `List<PropagateCtFileTarget>` (contains `string[]? MethodNames`) ships in production — nested schema serializes correctly.
- **Source reading tools missing** (`get_method_source`, `get_file_outline`, `search_solution_text`) — flagged as Phase 1 blockers.
- **No `asyncify` macro** yet.
- **Tool naming:** `find_*` / `detect_*` prefixes throughout — rename pass needed (Phase 7).

---

## Phase 0 — Schema Verification + Startup Dump ✅ DONE

**Goal:** Write `tool_list.json` (full MCP payload) and `tool_list_simple.json` (names only) on every startup. Reads `McpServerTool.ProtocolTool` from the DI container — the exact schema the MCP layer emits.

**Files changed:**
- `RoslynSentinel.Server/SentinelConsoleMode.cs` — added `WriteStartupDump(IServiceProvider, string)`
- `RoslynSentinel.Server/Program.cs` — calls `WriteStartupDump` after `WarmupAndAutoLoad`

**Output location:** server binary directory (`AppDomain.CurrentDomain.BaseDirectory`)
- `tool_list.json` — full payload: name + description + inputSchema (as MCP emits it), metadata header with tool count + char estimate + timestamp
- `tool_list_simple.json` — names only, sorted, for human readability

**Verified 2026-05-30:** 319 tools resolved via `GetServices<McpServerTool>()`. `propagate_cancellation_token_batch.inputSchema` renders correctly — `targets` → `{type:"array", items:{type:"object", properties:{filePath, methodNames}}}`, `methodNames` → `{type:["array","null"], items:{type:"string"}}`. Nested array-of-objects schema serializes correctly end-to-end. `BatchTargetInput` canonical shape is safe to build on.

---

## Phase 1 — Low-Level Fallback Tools
*Parallel with Phase 2. Unblocks sealed-tank model and asyncify macro.*

3. **`get_method_source`** — `filePath + methodName` → full source text. Back via `InventoryEngine` → `SyntaxNode.ToFullString()`.
4. **`get_file_outline`** — `filePath` → all namespaces/classes/interfaces/methods/properties with line ranges, no bodies.
5. **`search_solution_text`** — `pattern, isRegex, fileGlob?` → `(filePath, line, column, preview)` list, solution-scoped.
6. **`undo_last_apply(changeId)`** — symmetric reversal of a committed apply; reads pre-apply source from forensic blob (Phase 2 dependency).

**File:** `RoslynSentinel.Server/SentinelWorkspaceTools.cs`

---

## Phase 2 — BatchResultSummary + Blob Persistence Infrastructure
*Parallel with Phase 1. All Phase 4+ tools depend on this.*

7. **Shared types** (`RoslynSentinel.Server/BatchTypes.cs`, new):
   - `BatchTargetInput` (`List<BatchTarget>`, `DryRun`, `MaxItems`)
   - `BatchTarget` (`FilePath`, `string[]? MethodNames`)
   - `BatchResultSummary` (`ChangeId`, `BlobName`, `Succeeded`, `Skipped`, `RolledBack`, `Failed`, `Attempted`, `List<FailureDetail> Failures` capped 15, `string Severity` "ok"/"caution"/"halt", `string Directive`, `bool BreakerOpen`)
   - `FailureDetail`, `OperationItemRecord` (forensic blob per-item)

8. **`OperationBlobWriter.cs`** (new) — direct `File.WriteAllTextAsync`, no DocPathGuard, no rate limit. Output: `.roslynsentinel/operations/<toolName>_<utcTimestamp>_<changeId>.json` under solution root.

9. **`get_operation_detail`** tool — `changeId, filter?, maxItems=50` → slice of blob. Never returns full document.

**Files:** `RoslynSentinel.Server/BatchTypes.cs` (new), `RoslynSentinel.Server/OperationBlobWriter.cs` (new), `RoslynSentinel.Server/SentinelWorkspaceTools.cs`

---

## Phase 3 — Circuit Breaker ✅ DONE
*Depends on Phase 2 (BatchResultSummary/Severity).*

10. **`BreakerState` in `PersistentWorkspaceManager`:**
    - Fields: `bool _breakerOpen`, `int _consecutiveFailureStreak`, `int _totalAttempts`, `int _weightedRollbackScore`
    - `RecordBatchOutcome(succeeded, failed, rolledBack, skipped)` — resets streak on any success; rollbacks weighted 2×
    - Trip thresholds (generous, tune on real data): streak ≥ 8, OR rate > 30% after ≥ 20 attempts, OR rollback score > 20
    - Severity ladder: streak < 4 AND rate < 15% → "ok"; approach threshold → "caution"; tripped → "halt"
    - `CheckBreaker()` returns structured refusal or null; `ResetBreaker()` clears all state

11. **`reset_breaker`** tool in `SentinelWorkspaceTools.cs`

---

## Phase 4 — Batch Collapse (§7.1 of spec) ✅ DONE — commit `6be58b9`
*Depends on Phases 2+3. Steps within are independent.*

Each unified tool: check breaker → execute → `RecordBatchOutcome` → write blob → return `BatchResultSummary`. Old names become thin aliases with deprecation note.

12. **`propagate_cancellation_token`** — absorbs `_in_method`, `_in_file`, `_batch` ✅
13. **`convert_to_async_bridge`** — absorbs single-method (explicit targets); `run_bridge_batch` auto-discovery not yet absorbed ✅
14. **`add_cancellation_token`** — absorbs `add_cancellation_token_to_method` + `apply_cancellation_token_to_file` ✅
15. **`run_uplift`** — absorbs `run_uplift_batch` + `run_uplift_batch_multi` ✅
16. **`flag_migration_candidates`** — absorbs single-item + batch + project-scan via `scope: "targets"|"project"` ✅

**File:** `RoslynSentinel.Server/SentinelQualityTools.cs`

---

## Phase 5 — Query/Graph Consolidation (§7.3 of spec)

17. **`scan_task_api_usage`** — absorbs 6 `find_task_*` tools; `api` param filter
18. **`scan_unsafe_lazy_init`** — absorbs `find_unsafe_lazy_init` variants; `checkThreadSafety: bool`
19. **`scan_mutable_public_properties`** — absorbs both property-mutation tools; `collectionsOnly: bool`
20. **`get_relationship_graph`** — exposes `ImpactAnalyzer` as MCP tool (`direction`, `depth`, `edgeKinds`)
21. **`apply_modernization`** — `ruleId: string` + `BatchTargetInput`; batch-first Roslyn code-fix applier; enforces breaker

---

## Phase 6 — asyncify Macro Workflow ✅ DONE — commit `cba4e3a`
*Depends on all Phase 4 tools.*

22. **`asyncify(targets, exclusions?, dryRun, propagateCancellationTokens=true)`** — fixed internal sequence: scan → flag → convert_to_async_bridge → run_uplift → propagate_cancellation_token. Server owns the sequence; agent only selects targets/params. One blob per run. Returns `BatchResultSummary`.

**Implemented:** `AsyncifyInput` type in `BatchTypes.cs`; `Asyncify` tool in `SentinelQualityTools.cs` — 4 phases (Flag, Bridge, UpliftMulti, PropagateCtAsync), handles both autonomous discovery (project scan) and explicit `MethodTargets`. Breaker-checked, blob-persisted.

---

## Phase 7 — Tool Naming Rename Pass ✅ DONE — commit `cba4e3a`
*Coordinate with Phase 4's compatibility break — one hit.*

23. Apply §12.2 prefix test to every `find_*`/`detect_*` tool → `scan_*` or `get_*`. Add old names as aliases with deprecation note. Update `DefaultRateLimits` for renamed keys. Update documentation.

**Implemented:** ~97 methods renamed across `SentinelQualityTools.cs`, `SentinelIntelligenceTools.cs`, `SentinelModernizationTools.cs`. Legacy thin-alias methods appended to each file (correct return types, `[McpServerTool]` + `[Description("Deprecated: use scan_*/get_* instead...")]`). Rename helper script at `scripts/phase7_rename.ps1`.

---

## Key Decisions

| Decision | Value |
|---|---|
| Breaker thresholds | streak=8, rate=30% after ≥20 attempts, rollback-weight=2× |
| Failure cap in `BatchResultSummary.Failures` | 15 items |
| Blob location | `.roslynsentinel/operations/` under solution root |
| `undo_last_apply` | After Phase 2 (needs blob for pre-apply source) |
| `describe_tool` lookup | Explicitly rejected (spec §10.1) |
| Breaker reset | Manual only — `reset_breaker` tool |
| Blob cleanup | Deferred — no auto-expire |
| Rename aliases | Remove after one full migration cycle |

---

## Relevant Files

| File | Phases |
|---|---|
| `RoslynSentinel.Server/SentinelConsoleMode.cs` | 0 |
| `RoslynSentinel.Server/Program.cs` | 0 |
| `RoslynSentinel.Server/PersistentWorkspaceManager.cs` | 3 |
| `RoslynSentinel.Server/SentinelQualityTools.cs` | 4, 5, 6 |
| `RoslynSentinel.Server/SentinelWorkspaceTools.cs` | 1, 2, 3 |
| `RoslynSentinel.Server/BatchTypes.cs` *(new)* | 2 |
| `RoslynSentinel.Server/OperationBlobWriter.cs` *(new)* | 2 |
