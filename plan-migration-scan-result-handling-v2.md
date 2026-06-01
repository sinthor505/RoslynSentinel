# Plan: Migration Scan Result Handling (spec-migration-scan-result-handling-v2)
<!-- plan-migration-scan-result-handling-v2.md / v2 -->

## TL;DR
Implement the spec's 5 sections in dependency order: MigrationEnvelope.cs keystone first, then fix
filePath bug + scan shape controls, then new get_scan_result tool, then fix
get_async_migration_progress error surfacing, then 9 test cases. All four ambiguities resolved.
One confirm gate before Phase 3d (see C5 below).

---

## Conflicts & Ambiguities

### ✅ C1 — `TotalRecords` / `HasMore` field location — RESOLVED
Add `int? TotalRecords` and `bool HasMore` directly to `MigrationResult<T>`.

### ✅ C2 — Scan file format — RESOLVED
New scan-specific writer writes bare JSON array. OperationBlobWriter is NOT used for scan files.

### ✅ C3 — filePath "zero docs" error detection — RESOLVED
Engine throws `ArgumentException` when filePath filter yields zero documents. Wrapper catches as
`InvalidArgument`. Exception message must name the unmatched path so the error surfaced to the
agent is actionable (e.g. `"filePath 'C:\...\Foo.cs' matched no documents in solution"`).

### ✅ C4 — Pre-confirmed clear: asyncify-agents-v3 directory — RESOLVED
Directory does not exist in the workspace. No callers to update.

### ⚠️ C5 — `Task<object>` return type for summarize union — CONFIRM BEFORE PHASE 3d
Phase 3d uses `Task<object>` as the return type to handle the `summarize` union
(`MigrationResult<MigrationScanSummary>` vs `MigrationResult<List<MigrationCandidateFinding>>`),
noted as mirroring the `GetDiagnostics` pattern.

**Before writing Phase 3d:** open `SentinelWorkspaceTools.cs` line 323 and confirm `GetDiagnostics`
actually returns `Task<object>` and that the MCP serializer handles it correctly at runtime. If
`GetDiagnostics` uses a different approach (two overloads, a discriminated record, etc.), mirror
that pattern exactly — do not assume `Task<object>` works. This is a one-line confirm, not a
blocker for Phases 1–3c.

---

## Implementation Phases

### Phase 1 — Keystone: MigrationEnvelope.cs
**File**: `RoslynSentinel.Server/MigrationEnvelope.cs` (new file)
**Types to create**:
- `MigrationResult<T>`: `Success`, `Data`, `Error`, `LargeResult` + (per C1) `TotalRecords`,
  `HasMore`. `TotalRecords`/`HasMore` are null/false when result is not paged (summarize=true or
  unpaged small result) — this is unambiguous and expected.
- `ResultError`: `ErrorCode`, `Message`, `Detail`
- `LargeResultInfo`: `WrittenToFile`, `FilePath`, `OperationId`, `SizeBytes`, `TotalRecords`.
  Note: `TotalRecords` appears on both `MigrationResult<T>` and `LargeResultInfo`. This is
  intentional — callers reading via `get_scan_result` get the count from `LargeResultInfo` without
  a separate call.
- `MigrationScanSummary`: `TotalCandidates`, `ByPattern`, `ByProject`, `ByScoreBucket`
- Error code string constants: `SolutionNotLoaded`, `FeatureDisabled`, `InvalidArgument`,
  `Exception`

### Phase 2 — filePath matching bug fix (§4) [parallel with Phase 3a/3b]
**File**: `RoslynSentinel.Server/AsyncOptimizationEngine.cs`, line ~1803
1. Normalize both `d.FilePath` and `filePath` input: replace `\` with `/`, `OrdinalIgnoreCase`
2. After building `docs` with filter: if `filePath != null` and `!docs.Any()`, throw
   `ArgumentException("filePath '{filePath}' matched no documents in solution")`
   — include the actual path value in the message so the wrapper surfaces it to the caller.
3. Wrapper catches this and maps to `ErrorCode = "InvalidArgument"` (done in Phase 3d).

### Phase 3a — `summarize` parameter + MigrationScanSummary (§2.1) [parallel with Phase 3b]
**File**: `RoslynSentinel.Server/AsyncOptimizationEngine.cs`
1. Add `bool summarize = false` parameter to `FindMigrationCandidatesAsync`
2. When `summarize = true`, aggregate into `MigrationScanSummary` using F7 score buckets:
   `<0`, `0-25`, `26-50`, `51-75`, `76+`. These are the correct boundaries — score range is
   −20 to ~115; do not substitute 0–100 or any other range.
3. Method can return `object` or use two method paths; wrapper decides based on `summarize`

### Phase 3b — `limit`/`offset` pagination (§2.2) [parallel with Phase 3a]
**File**: `RoslynSentinel.Server/AsyncOptimizationEngine.cs`
1. Add `int limit = 50, int offset = 0` to `FindMigrationCandidatesAsync`
2. Apply `Skip(offset).Take(limit)` to ordered candidate list before returning
3. Return `totalCount` (pre-pagination count) alongside the sliced list — wrapper uses it to set
   `TotalRecords`/`HasMore` on `MigrationResult<T>`
4. Pagination reference: `HealthOrchestrationEngine.cs` — mirror its `HasMore`/`TotalRecords`
   contract exactly. Do not invent a new paging shape.

### Phase 3c — Server-side file-write threshold (§2.3) [depends on 3a/3b]
**File**: New helper in `RoslynSentinel.Server/` or inline in `SentinelQualityTools.cs`

This is a safety net for genuinely large results. The primary fix is `summarize` + pagination
keeping responses small. The value of this path is that `.roslynsentinel/operations/` files are
**structured JSON readable by `get_scan_result`**, unlike the MCP client's opaque text spill.

1. Serialize the candidate page to JSON bytes using `System.Text.Json`. Measure `byte[]` length
   of the **serialized payload only** — not object-graph size, not pre-truncation field count.
2. Threshold: 256KB (configurable via server options if they exist). Do not set this at or near
   9KB — that was the MCP client's spill point, not a server constraint. 9KB appears only in T3
   as a regression assertion (must stay inline).
3. If size > threshold: write bare `List<MigrationCandidateFinding>` JSON (per C2) to
   `.roslynsentinel/operations/scan_{timestamp}_{guid}.json`
   - Use the **path helpers from `OperationBlobWriter.cs`** to resolve the `.roslynsentinel/operations/`
     base directory — do NOT call `OperationBlobWriter.WriteAsync` (C2). Call the path helper
     only, so scan files land in the same `operations/` folder as mutation blobs and
     `get_scan_result`'s glob pattern is reliable.
4. Return `LargeResultInfo` with `FilePath`, `OperationId = guid`, `SizeBytes`, `TotalRecords`
5. If size ≤ threshold: return inline (`Data` non-null, `LargeResult = null`)

### Phase 3d — Update ScanMigrationCandidates wrapper (§2.4) [depends on 1, 2, 3a, 3b, 3c]
**File**: `RoslynSentinel.Server/SentinelQualityTools.cs`, line 239

**⚠️ Confirm C5 before writing this phase.**

1. Convert from expression body to braced method body
2. Add parameters: `bool summarize = false`, `int limit = 50`, `int offset = 0`
3. Solution-loaded guard at top → `ErrorCode = "SolutionNotLoaded"` immediately
4. Wrap engine call in try/catch:
   - `ArgumentException` → `ErrorCode = "InvalidArgument"`
   - Any other exception → `ErrorCode = "Exception"`, `Detail = ex.Message`
5. Return type: `Task<object>` **if and only if** C5 confirms `GetDiagnostics` uses this pattern.
   Otherwise mirror whatever pattern `GetDiagnostics` uses.
   - `summarize=true` → `MigrationResult<MigrationScanSummary>`
   - `summarize=false` → `MigrationResult<List<MigrationCandidateFinding>>`

### Phase 4 — `get_scan_result` new tool (§3) [depends on Phase 1, Phase 3c]
**File**: `RoslynSentinel.Server/SentinelQualityTools.cs` (new `[McpServerTool]` method)
1. Add `GetScanResult(string? changeId = null, string? filePath = null, int limit = 50,
   int offset = 0)`
2. File resolution:
   - If `changeId` supplied: glob `.roslynsentinel/operations/scan_*_{changeId}.json`
   - If `filePath` supplied: verify path matches `scan_` prefix and `.json` suffix in the
     operations directory
   - If neither resolves: return `ErrorCode = "InvalidArgument"`
   - Reject any path NOT matching the `scan_*.json` pattern in the operations dir —
     `ErrorCode = "InvalidArgument"`. This tool does not read arbitrary workspace paths.
3. Deserialize file as bare `List<MigrationCandidateFinding>` (per C2 — bare array, no wrapper
   envelope to unwrap)
4. Apply `Skip(offset).Take(limit)`, return `MigrationResult<List<MigrationCandidateFinding>>`
   with `TotalRecords`/`HasMore`

### Phase 5 — Fix `get_async_migration_progress` (§5) [depends on Phase 1 only]
**File**: `RoslynSentinel.Server/SentinelQualityTools.cs`, line 252
1. Convert from expression body to braced method body
2. Solution-loaded guard at top → `ErrorCode = "SolutionNotLoaded"`
3. Wrap engine call in try/catch: any throw → `ErrorCode = "Exception"`, `Detail = ex.Message`
4. Change return type to `Task<MigrationResult<AsyncMigrationProgressReport>>`

### Phase 6 — Test cases (§6)
**File**: `RoslynSentinel.Tests/MigrationScanResultTests.cs` (new)
**Pattern**: NUnit `[TestFixture]` + `TestSolutionBuilder.CreateSolutionWithProject()` +
`_workspaceManager.SetTestSolution()` per `FlagMigrationCandidateTests.cs` conventions

- T1: `summarize=true` → inline `MigrationScanSummary`, all 5 bucket keys present
- T2: paged scan (page fits) → `Data` non-null, `TotalRecords` set, `LargeResult` null
- T3: ~9KB result (~50–60 candidates) → stays inline, `LargeResult` null (regression anchor —
  if this fails, the threshold is measuring the wrong thing; fix the measurement, not the value)
  **Test scaffolding note:** T3 requires ~50–60 flagged candidates to reach ~9KB. Use
  `TestSolutionBuilder` to generate enough flagged methods — do not write T3 with fewer
  candidates and assume it passes for the right reason.
- T4: result genuinely exceeds threshold → `LargeResult.WrittenToFile=true`, `FilePath`/
  `OperationId` populated, `Data` null
- T5: `get_scan_result` on T4's file → structured `MigrationCandidateFinding` records (not
  preview text), paging works, `TotalRecords` matches T4
- T6: full absolute `filePath` → same records as filename-only input
- T7: `filePath` matching nothing → `Success=false`, `ErrorCode="InvalidArgument"`
- T8: `get_async_migration_progress`, no solution → `ErrorCode="SolutionNotLoaded"`
- T9: `get_async_migration_progress`, forced exception → `ErrorCode="Exception"`,
  `Detail` non-empty

---

## Relevant Files

- `RoslynSentinel.Server/SentinelQualityTools.cs` — `ScanMigrationCandidates` (line 239),
  `GetAsyncMigrationProgress` (line 252)
- `RoslynSentinel.Server/AsyncOptimizationEngine.cs` — `FindMigrationCandidatesAsync`
  (line ~1781)
- `RoslynSentinel.Server/AntiPatternEngine.cs` — `GetAsyncMigrationProgressAsync` (line ~2723)
- `RoslynSentinel.Server/OperationBlobWriter.cs` — path helpers for `.roslynsentinel/operations/`
  base dir only (do NOT call `WriteAsync` for scan files — C2)
- `RoslynSentinel.Server/HealthOrchestrationEngine.cs` — `HasMore`/pagination contract reference
- `RoslynSentinel.Server/SentinelWorkspaceTools.cs` line 323 — `GetDiagnostics` pattern
  reference for C5 confirm (Task<object> vs alternatives)
- `RoslynSentinel.Tests/FlagMigrationCandidateTests.cs` — test structure reference
- `RoslynSentinel.Server/MigrationEnvelope.cs` — NEW FILE

---

## Pre-landing Checklist

- [x] asyncify-agents-v3/ confirmed absent (C4 — pre-confirmed clear)
- [ ] C5 confirmed: `GetDiagnostics` return type pattern verified before Phase 3d
- [ ] All 9 T-cases pass: `dotnet test --filter MigrationScanResultTests`
- [ ] T3 passes inline (threshold not tripped at ~9KB — confirms serialized-byte measurement)
- [ ] T6 passes (full absolute path = filename-only result — confirms matcher fix)
- [ ] JSON shape spot-checked: `{ "success": bool, "data": ..., "error": null/..., "largeResult": null/... }`

<!-- v2 -->
